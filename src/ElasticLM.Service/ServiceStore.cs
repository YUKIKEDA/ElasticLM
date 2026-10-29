using System.Data;
using System.Security.Cryptography;
using AuraORM;
using static AuraORM.Sql;

namespace ElasticLM.Service;

/// <summary>
/// The database operations, written once with AuraORM for SQLite and PostgreSQL (design §14.2, decision 0.81). Writes run in
/// the provider's default transaction: <c>BEGIN IMMEDIATE</c> on SQLite, which takes the write lock at once, and read
/// committed on PostgreSQL, where every audit append locks the one <c>audit_state</c> row first.
/// </summary>
/// <typeparam name="TDialect">The engine's dialect.</typeparam>
internal sealed partial class ServiceStore<TDialect> : Database
    where TDialect : IUpdateReturningDialect, IDeleteReturningDialect, IOnConflictDialect
{
    private readonly Database<TDialect> _db;

    // The isolation level of one consistent read: a deferred transaction on SQLite (its snapshot starts at the first read and
    // lasts to the end), repeatable read on PostgreSQL. In Microsoft.Data.Sqlite, ReadUncommitted is the only level that
    // starts with a deferred BEGIN; every other level is BEGIN IMMEDIATE. Its read_uncommitted pragma acts only on a shared
    // cache, which the service does not use, so uncommitted rows are never read.
    private readonly IsolationLevel _snapshot;

    public ServiceStore(DatabaseProvider provider, System.Data.Common.DbDataSource source)
        : base(provider, source)
    {
        _db = new Database<TDialect>(source);
        _snapshot = provider == DatabaseProvider.Sqlite ? IsolationLevel.ReadUncommitted : IsolationLevel.RepeatableRead;
    }

    public override async Task PingAsync(CancellationToken cancellationToken) =>
        _ = await _db.QueryScalarAsync<int>($"SELECT 1", cancellationToken).ConfigureAwait(false);

    public override Task<ApiKeyRecord> SetupKeyAsync(
        AdminIdentity owner,
        NewApiKey key,
        Func<ApiKeyRecord, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        return _db.InTransactionAsync(
            async tx =>
            {
                // Two setups at once both try to add the owner; the second insert does nothing and both read the same row.
                await tx.Insert(AdminRow.Table)
                    .Values(
                        new AdminRow.Insert
                        {
                            Id = Guid.CreateVersion7(now),
                            Issuer = owner.Issuer,
                            Subject = owner.Subject,
                            Role = Permissions.RoleOwner,
                            Enabled = true,
                            CreatedAt = now.ToUnixTimeSeconds(),
                        }
                    )
                    .OnConflict(a => [a.Issuer, a.Subject])
                    .DoNothing()
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                AdminRow admin = await tx.From(AdminRow.Table)
                    .Where(a => And(Eq(a.Issuer, owner.Issuer), Eq(a.Subject, owner.Subject)))
                    .Select(a => a)
                    .SingleAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (admin.Role != Permissions.RoleOwner || !admin.Enabled)
                {
                    throw new ServiceStartException("The bootstrap owner is disabled or no longer an owner; setup does not change that.");
                }

                return await InsertKeyAsync(tx, admin.Id, key, audit, now, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken
        );
    }

    public override Task<ApiKeyRecord> CreateKeyAsync(
        Guid adminId,
        NewApiKey key,
        Func<ApiKeyRecord, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    ) => _db.InTransactionAsync(tx => InsertKeyAsync(tx, adminId, key, audit, now, cancellationToken), cancellationToken);

    public override async Task<KeyHolder?> FindKeyAsync(byte[] hash, CancellationToken cancellationToken)
    {
        KeyHolderRow? row = await _db.From(ApiKeyRow.Table)
            .InnerJoin(AdminRow.Table, (k, a) => Eq(a.Id, k.AdminId))
            .Where((k, a) => Eq(k.Hash, hash))
            .Select(
                (k, a) =>
                    new KeyHolderRow.RowProjection
                    {
                        Id = k.Id,
                        AdminId = k.AdminId,
                        Name = k.Name,
                        Prefix = k.Prefix,
                        Permissions = k.Permissions,
                        CreatedAt = k.CreatedAt,
                        ExpiresAt = k.ExpiresAt,
                        RevokedAt = k.RevokedAt,
                        Role = a.Role,
                        Enabled = a.Enabled,
                        AdminCreatedAt = a.CreatedAt,
                        LastLoginAt = a.LastLoginAt,
                    }
            )
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? null
            : new KeyHolder(
                new ApiKeyRecord(
                    row.Id,
                    row.AdminId,
                    row.Name,
                    row.Prefix,
                    Permissions.Split(row.Permissions),
                    row.CreatedAt,
                    row.ExpiresAt,
                    row.RevokedAt
                ),
                row.Role,
                row.Enabled,
                row.AdminCreatedAt,
                row.LastLoginAt
            );
    }

    public override async Task<IReadOnlyList<ApiKeyRecord>> ListKeysAsync(Guid? after, int limit, CancellationToken cancellationToken)
    {
        List<ApiKeyRow> rows = await _db.From(ApiKeyRow.Table)
            .Where(k => And([after is Guid id ? Gt(k.Id, id) : null]))
            .Select(k => k)
            .OrderBy(k => [Asc(k.Id)])
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(ApiKeys.FromRow)];
    }

    public override Task<bool> RevokeKeyAsync(Guid id, AuditEntry audit, DateTimeOffset now, CancellationToken cancellationToken) =>
        _db.InTransactionAsync(
            async tx =>
            {
                long found = await tx.From(ApiKeyRow.Table)
                    .Where(k => Eq(k.Id, id))
                    .Select(k => Count())
                    .SingleAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (found == 0)
                {
                    return false;
                }

                await tx.Update(ApiKeyRow.Table)
                    .Set(k => [Set(k.RevokedAt, (long?)now.ToUnixTimeSeconds())])
                    .Where(k => And(Eq(k.Id, id), IsNull(k.RevokedAt)))
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                await AppendAsync(tx, audit, now, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken
        );

    public override Task<long> AppendAuditAsync(AuditEntry entry, DateTimeOffset now, CancellationToken cancellationToken) =>
        _db.InTransactionAsync(tx => AppendAsync(tx, entry, now, cancellationToken), cancellationToken);

    public override async Task<IReadOnlyList<AuditRow>> ListAuditAsync(long after, int limit, CancellationToken cancellationToken)
    {
        List<AuditLogRow> rows = await _db.From(AuditLogRow.Table)
            .Where(r => Gt(r.N, after))
            .Select(r => r)
            .OrderBy(r => [Asc(r.N)])
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(AuditLog.FromRow)];
    }

    public override async Task<AuditCheck> VerifyAuditAsync(CancellationToken cancellationToken)
    {
        // One snapshot: an append that commits while the rows are read does not show as a row past the head.
        Transaction<TDialect> tx = await _db.BeginTransactionAsync(_snapshot, cancellationToken).ConfigureAwait(false);
        await using (tx.ConfigureAwait(false))
        {
            AuditStateRow? state = await tx.From(AuditStateRow.Table)
                .Where(s => Eq(s.Id, 1))
                .Select(s => s)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (state is null)
            {
                return new AuditCheck(0, 1);
            }

            long expected = state.AnchorN + 1;
            byte[] prev = state.AnchorHash;
            long count = 0;
            await foreach (
                AuditLogRow row in tx.From(AuditLogRow.Table)
                    .Select(r => r)
                    .OrderBy(r => [Asc(r.N)])
                    .AsAsyncEnumerable(cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                count++;
                AuditRow read = AuditLog.FromRow(row);
                bool follows =
                    read.N == expected
                    && CryptographicOperations.FixedTimeEquals(read.Prev, prev)
                    && CryptographicOperations.FixedTimeEquals(read.Hash, AuditLog.Hash(read.N, read.Ts, read.Entry, read.Prev));
                if (!follows)
                {
                    // A row removed in the middle shows as the next row's number; a changed row as its own.
                    return new AuditCheck(count, Math.Min(read.N, expected));
                }

                prev = read.Hash;
                expected++;
            }

            // A removed last row, or a row added past the recorded head, does not match the head.
            bool headMatches = expected - 1 == state.HeadN && CryptographicOperations.FixedTimeEquals(prev, state.HeadHash);
            return new AuditCheck(count, headMatches ? null : expected);
        }
    }

    public override Task<long> RemoveAuditOlderThanAsync(DateTimeOffset before, CancellationToken cancellationToken) =>
        _db.InTransactionAsync(
            async tx =>
            {
                AuditStateRow state = await LockStateAsync(tx, cancellationToken).ConfigureAwait(false);

                // The rows to remove are the oldest ones up to the first row at or after the time, so a clock that moved back
                // does not leave a hole in the middle.
                long cutoff = before.ToUnixTimeSeconds();
                long? firstKept = await tx.From(AuditLogRow.Table)
                    .Where(r => Ge(r.Ts, cutoff))
                    .Select(r => Min(r.N))
                    .SingleAsync(cancellationToken)
                    .ConfigureAwait(false);
                long last = (firstKept ?? state.HeadN + 1) - 1;
                if (last <= state.AnchorN)
                {
                    return 0L;
                }

                byte[]? lastHash = await tx.From(AuditLogRow.Table)
                    .Where(r => Eq(r.N, last))
                    .Select(r => r.Hash)
                    .SingleOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (lastHash is null)
                {
                    // The row the anchor would move to is gone; the chain check reports it, and retention waits.
                    return 0L;
                }

                long removed = await tx.Delete(AuditLogRow.Table).Where(r => Le(r.N, last)).ExecuteAsync(cancellationToken).ConfigureAwait(false);
                await tx.Update(AuditStateRow.Table)
                    .Set(s => [Set(s.AnchorN, last), Set(s.AnchorHash, lastHash)])
                    .Where(s => Eq(s.Id, 1))
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                return removed;
            },
            cancellationToken
        );

    // Locks the chain's state row and reads it. An UPDATE that sets a column to itself takes the row lock on PostgreSQL as
    // SELECT … FOR UPDATE would, in the query builder; on SQLite the transaction already holds the write lock.
    private static Task<AuditStateRow> LockStateAsync(Transaction<TDialect> tx, CancellationToken cancellationToken) =>
        tx.Update(AuditStateRow.Table).Set(s => [Set(s.HeadN, s.HeadN)]).Where(s => Eq(s.Id, 1)).Returning(s => s).SingleAsync(cancellationToken);

    private static async Task<long> AppendAsync(Transaction<TDialect> tx, AuditEntry entry, DateTimeOffset now, CancellationToken cancellationToken)
    {
        AuditStateRow state = await LockStateAsync(tx, cancellationToken).ConfigureAwait(false);
        long n = state.HeadN + 1;
        long ts = now.ToUnixTimeSeconds();
        byte[] hash = AuditLog.Hash(n, ts, entry, state.HeadHash);
        await tx.Insert(AuditLogRow.Table)
            .Values(
                new AuditLogRow.Insert
                {
                    N = n,
                    Ts = ts,
                    ActorType = entry.ActorType,
                    ActorId = entry.ActorId,
                    Action = entry.Action,
                    Target = entry.Target,
                    Result = entry.Result,
                    Ip = entry.Ip,
                    Detail = entry.Detail,
                    Prev = state.HeadHash,
                    Hash = hash,
                }
            )
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        await tx.Update(AuditStateRow.Table)
            .Set(s => [Set(s.HeadN, n), Set(s.HeadHash, hash)])
            .Where(s => Eq(s.Id, 1))
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        return n;
    }

    private static async Task<ApiKeyRecord> InsertKeyAsync(
        Transaction<TDialect> tx,
        Guid adminId,
        NewApiKey key,
        Func<ApiKeyRecord, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        ApiKeyRecord record = ApiKeys.NewRecord(adminId, key, now);
        await tx.Insert(ApiKeyRow.Table)
            .Values(
                new ApiKeyRow.Insert
                {
                    Id = record.Id,
                    AdminId = adminId,
                    Name = record.Name,
                    Prefix = record.Prefix,
                    Hash = ApiKeys.HashOf(key.Key),
                    Permissions = Permissions.Join(record.Permissions),
                    CreatedAt = record.CreatedAt,
                    ExpiresAt = record.ExpiresAt,
                    RevokedAt = null,
                }
            )
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        await AppendAsync(tx, audit(record), now, cancellationToken).ConfigureAwait(false);
        return record;
    }
}
