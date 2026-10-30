using AuraORM;
using static AuraORM.Sql;

namespace ElasticLM.Service;

/// <content>
/// The admin UI's sessions and the Data Protection keys (M5b).
/// </content>
internal sealed partial class ServiceStore<TDialect>
{
    // The advisory lock that one node at a time holds to add Data Protection keys (design §15.3). Any fixed number serves.
    private const long DataProtectionLock = 0x454C_4D44_5000_0001;

    public override Task<SessionCreated> CreateSessionAsync(
        NewSession session,
        Func<SessionCreated, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(audit);
        long seconds = now.ToUnixTimeSeconds();
        long idleBefore = seconds - (long)session.Idle.TotalSeconds;
        return _db.InTransactionAsync(
            async tx =>
            {
                Guid? replaced = null;
                if (session.Presented is byte[] presented)
                {
                    // A list, not SingleOrDefault: the default of a Guid is Guid.Empty, not "no row".
                    List<Guid> removed = await tx.Delete(AdminSessionRow.Table)
                        .Where(s => Eq(s.Hash, presented))
                        .Returning(s => s.Id)
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    replaced = removed.Count > 0 ? removed[0] : null;
                }

                // design §15.3: at most the limit of live sessions per administrator; the oldest one makes room.
                List<AdminSessionRow> live = await tx.From(AdminSessionRow.Table)
                    .Where(s => And(Eq(s.AdminId, session.AdminId), Gt(s.ExpiresAt, seconds), Ge(s.LastSeenAt, idleBefore)))
                    .Select(s => s)
                    .OrderBy(s => [Asc(s.CreatedAt), Asc(s.Id)])
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                Guid? evicted = null;
                if (live.Count >= session.Limit)
                {
                    evicted = live[0].Id;
                    await tx.Delete(AdminSessionRow.Table).Where(s => Eq(s.Id, live[0].Id)).ExecuteAsync(cancellationToken).ConfigureAwait(false);
                }

                var created = new SessionCreated(Guid.CreateVersion7(now), replaced, evicted);
                await tx.Insert(AdminSessionRow.Table)
                    .Values(
                        new AdminSessionRow.Insert
                        {
                            Id = created.Id,
                            AdminId = session.AdminId,
                            Hash = session.Hash,
                            CreatedAt = seconds,
                            LastSeenAt = seconds,
                            ExpiresAt = seconds + (long)session.Lifetime.TotalSeconds,
                            Ip = session.Ip,
                        }
                    )
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                await AppendAsync(tx, audit(created), now, cancellationToken).ConfigureAwait(false);
                return created;
            },
            cancellationToken
        );
    }

    public override async Task<SessionHolder?> FindSessionAsync(byte[] hash, CancellationToken cancellationToken)
    {
        SessionHolderRow? row = await _db.From(AdminSessionRow.Table)
            .InnerJoin(AdminRow.Table, (s, a) => Eq(a.Id, s.AdminId))
            .Where((s, a) => Eq(s.Hash, hash))
            .Select(
                (s, a) =>
                    new SessionHolderRow.RowProjection
                    {
                        Id = s.Id,
                        AdminId = s.AdminId,
                        CreatedAt = s.CreatedAt,
                        LastSeenAt = s.LastSeenAt,
                        ExpiresAt = s.ExpiresAt,
                        Role = a.Role,
                        Enabled = a.Enabled,
                    }
            )
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : new SessionHolder(row.Id, row.AdminId, row.CreatedAt, row.LastSeenAt, row.ExpiresAt, row.Role, row.Enabled);
    }

    public override async Task RemoveSessionAsync(Guid id, CancellationToken cancellationToken) =>
        _ = await _db.Delete(AdminSessionRow.Table).Where(s => Eq(s.Id, id)).ExecuteAsync(cancellationToken).ConfigureAwait(false);

    public override async Task TouchSessionAsync(Guid id, long now, long before, CancellationToken cancellationToken) =>
        _ = await _db.Update(AdminSessionRow.Table)
            .Set(s => [Set(s.LastSeenAt, now)])
            .Where(s => And(Eq(s.Id, id), Le(s.LastSeenAt, before)))
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

    public override async Task<IReadOnlyList<AdminSessionRow>> ListSessionsAsync(
        Guid? after,
        Guid? adminId,
        long now,
        long idleSeconds,
        int limit,
        CancellationToken cancellationToken
    )
    {
        long idleBefore = now - idleSeconds;
        return await _db.From(AdminSessionRow.Table)
            .Where(s =>
                And([
                    Gt(s.ExpiresAt, now),
                    Ge(s.LastSeenAt, idleBefore),
                    after is Guid id ? Gt(s.Id, id) : null,
                    adminId is Guid admin ? Eq(s.AdminId, admin) : null,
                ])
            )
            .Select(s => s)
            .OrderBy(s => [Asc(s.Id)])
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public override Task<Guid?> RevokeSessionAsync(
        Guid id,
        long idleSeconds,
        Func<Guid, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(audit);
        long seconds = now.ToUnixTimeSeconds();
        long idleBefore = seconds - idleSeconds;
        return _db.InTransactionAsync(
            async tx =>
            {
                List<Guid> admins = await tx.Delete(AdminSessionRow.Table)
                    .Where(s => And(Eq(s.Id, id), Gt(s.ExpiresAt, seconds), Ge(s.LastSeenAt, idleBefore)))
                    .Returning(s => s.AdminId)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (admins.Count == 0)
                {
                    return (Guid?)null;
                }

                await AppendAsync(tx, audit(admins[0]), now, cancellationToken).ConfigureAwait(false);
                return admins[0];
            },
            cancellationToken
        );
    }

    public override Task<bool> LogoutSessionAsync(
        byte[] hash,
        Func<Guid, Guid, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(audit);
        return _db.InTransactionAsync(
            async tx =>
            {
                AdminSessionRow? removed = await tx.Delete(AdminSessionRow.Table)
                    .Where(s => Eq(s.Hash, hash))
                    .Returning(s => s)
                    .SingleOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (removed is null)
                {
                    return false;
                }

                await AppendAsync(tx, audit(removed.Id, removed.AdminId), now, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken
        );
    }

    public override async Task<long> RemoveOldSessionsAsync(long now, long idleSeconds, CancellationToken cancellationToken)
    {
        long idleBefore = now - idleSeconds;
        return await _db.Delete(AdminSessionRow.Table)
            .Where(s => Or(Le(s.ExpiresAt, now), Lt(s.LastSeenAt, idleBefore)))
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<DataProtectionKeyRow>> ListDataProtectionKeysAsync(CancellationToken cancellationToken) =>
        await _db.From(DataProtectionKeyRow.Table).Select(k => k).OrderBy(k => [Asc(k.Name)]).ToListAsync(cancellationToken).ConfigureAwait(false);

    public override Task<int> AddDataProtectionKeysAsync(
        Func<IReadOnlyList<DataProtectionKeyRow>, IReadOnlyList<DataProtectionKeyRow>> decide,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(decide);
        return _db.InTransactionAsync(
            async tx =>
            {
                // SQLite's BEGIN IMMEDIATE already holds the write lock; PostgreSQL takes the advisory lock for the transaction.
                if (Provider == DatabaseProvider.Postgres)
                {
                    _ = await tx.ExecuteAsync($"SELECT pg_advisory_xact_lock({DataProtectionLock})", cancellationToken).ConfigureAwait(false);
                }

                List<DataProtectionKeyRow> rows = await tx.From(DataProtectionKeyRow.Table)
                    .Select(k => k)
                    .OrderBy(k => [Asc(k.Name)])
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                IReadOnlyList<DataProtectionKeyRow> added = decide(rows);
                foreach (DataProtectionKeyRow row in added)
                {
                    await tx.Insert(DataProtectionKeyRow.Table)
                        .Values(
                            new DataProtectionKeyRow.Insert
                            {
                                Name = row.Name,
                                Nonce = row.Nonce,
                                Ciphertext = row.Ciphertext,
                                Tag = row.Tag,
                                CreatedAt = row.CreatedAt,
                            }
                        )
                        .ExecuteAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                return added.Count;
            },
            cancellationToken
        );
    }
}
