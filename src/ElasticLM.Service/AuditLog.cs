using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ElasticLM.Service;

/// <summary>
/// One administration operation to record (design §18.2).
/// </summary>
/// <param name="ActorType"><c>apikey</c>, <c>oidc</c> (M5), or <c>system</c>.</param>
/// <param name="ActorId">The API key ID, the OIDC issuer and subject, or for <c>system</c> the operation's name (<c>setup</c>, <c>auth</c>).</param>
/// <param name="Action">The operation, such as <c>apikey.create</c>.</param>
/// <param name="Target">What it acted on, such as an API key ID, or <see langword="null"/>.</param>
/// <param name="Result">The result code, such as <c>ELM_OK</c>.</param>
/// <param name="Ip">The source address, or <see langword="null"/>.</param>
/// <param name="Detail">A compact JSON object with the operation's details, never a secret, or <see langword="null"/>.</param>
internal sealed record AuditEntry(string ActorType, string? ActorId, string Action, string? Target, string Result, string? Ip, string? Detail);

/// <summary>
/// A stored audit row.
/// </summary>
internal sealed record AuditRow(long N, long Ts, AuditEntry Entry, byte[] Prev, byte[] Hash);

/// <summary>
/// The result of checking the chain.
/// </summary>
/// <param name="Checked">The rows read.</param>
/// <param name="FirstBroken">The number of the first row that does not follow, or <see langword="null"/>.</param>
internal sealed record AuditCheck(long Checked, long? FirstBroken);

/// <summary>
/// The audit log (design §18.2): an append-only table whose rows are chained by SHA-256. A row's hash covers ten columns in a
/// fixed order, each written as a 4-byte big-endian length and its UTF-8 bytes (numbers in decimal, <c>prev</c> as its 32
/// bytes, <c>NULL</c> as the length <c>0xFFFFFFFF</c>), so another tool can compute the same hash from the design alone
/// (decision 0.77). Appends lock the one row of <c>audit_state</c>, so the chain does not fork.
/// </summary>
internal static class AuditLog
{
    /// <summary>The page size when none is given, and the largest (design §14.5).</summary>
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    private const int HashLength = 32;

    private const string InsertSql =
        "INSERT INTO audit_log (n, ts, actor_type, actor_id, action, target, result, ip, detail, prev, hash) "
        + "VALUES (@n, @ts, @actorType, @actorId, @action, @target, @result, @ip, @detail, @prev, @hash)";

    /// <summary>
    /// Returns the bytes a row's hash covers.
    /// </summary>
    public static byte[] RowBytes(long n, long ts, AuditEntry entry, ReadOnlySpan<byte> prev)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (prev.Length != HashLength)
        {
            throw new ArgumentException("The previous hash is 32 bytes.", nameof(prev));
        }

        using var buffer = new MemoryStream();
        void Write(ReadOnlySpan<byte> bytes)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
            buffer.Write(length);
            buffer.Write(bytes);
        }

        void WriteText(string? text)
        {
            if (text is null)
            {
                Span<byte> none = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(none, uint.MaxValue);
                buffer.Write(none);
                return;
            }

            Write(Encoding.UTF8.GetBytes(text));
        }

        WriteText(n.ToString(CultureInfo.InvariantCulture));
        WriteText(ts.ToString(CultureInfo.InvariantCulture));
        WriteText(entry.ActorType);
        WriteText(entry.ActorId);
        WriteText(entry.Action);
        WriteText(entry.Target);
        WriteText(entry.Result);
        WriteText(entry.Ip);
        WriteText(entry.Detail);
        Write(prev);
        return buffer.ToArray();
    }

    /// <summary>Returns a row's hash.</summary>
    public static byte[] Hash(long n, long ts, AuditEntry entry, ReadOnlySpan<byte> prev) => SHA256.HashData(RowBytes(n, ts, entry, prev));

    /// <summary>
    /// Writes a compact JSON object for <see cref="AuditEntry.Detail"/>.
    /// </summary>
    public static string Detail(Action<Utf8JsonWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Appends a row inside the caller's write transaction, so the operation and its record commit together.
    /// </summary>
    /// <returns>The row's number.</returns>
    public static async Task<long> AppendAsync(
        Database database,
        DbConnection connection,
        DbTransaction transaction,
        AuditEntry entry,
        DateTimeOffset now,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(entry);
        string lockClause = database.Provider == DatabaseProvider.Postgres ? " FOR UPDATE" : string.Empty;
        long head;
        byte[] prev;
        await using (
            DbCommand state = Database.Command(connection, transaction, "SELECT head_n, head_hash FROM audit_state WHERE id = 1" + lockClause)
        )
        await using (DbDataReader reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The audit_state row is missing; the schema was not migrated.");
            }

            head = reader.GetInt64(0);
            prev = Database.ReadBytes(reader, 1);
        }

        long n = head + 1;
        long ts = now.ToUnixTimeSeconds();
        byte[] hash = Hash(n, ts, entry, prev);
        await using (
            DbCommand insert = Database.Command(
                connection,
                transaction,
                InsertSql,
                ("n", n),
                ("ts", ts),
                ("actorType", entry.ActorType),
                ("actorId", entry.ActorId),
                ("action", entry.Action),
                ("target", entry.Target),
                ("result", entry.Result),
                ("ip", entry.Ip),
                ("detail", entry.Detail),
                ("prev", prev),
                ("hash", hash)
            )
        )
        {
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (
            DbCommand update = Database.Command(
                connection,
                transaction,
                "UPDATE audit_state SET head_n = @n, head_hash = @hash WHERE id = 1",
                ("n", n),
                ("hash", hash)
            )
        )
        {
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return n;
    }

    /// <summary>
    /// Appends a row in a transaction of its own.
    /// </summary>
    public static async Task<long> AppendAsync(Database database, AuditEntry entry, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        await using DbConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await Database.BeginWriteAsync(connection, cancellationToken).ConfigureAwait(false);
        long n = await AppendAsync(database, connection, transaction, entry, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return n;
    }

    /// <summary>
    /// Returns rows after a row number, oldest first.
    /// </summary>
    public static async Task<IReadOnlyList<AuditRow>> ListAsync(
        Database database,
        long after,
        int limit,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(database);
        await using DbConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbCommand select = Database.Command(
            connection,
            null,
            "SELECT n, ts, actor_type, actor_id, action, target, result, ip, detail, prev, hash FROM audit_log WHERE n > @after ORDER BY n LIMIT @limit",
            ("after", after),
            ("limit", limit)
        );
        await using DbDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<AuditRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    /// <summary>
    /// Checks the chain from the anchor to the head: the numbers follow one another, each <c>prev</c> is the hash of the row
    /// before, each hash is the hash of its row, and the last row is the recorded head.
    /// </summary>
    public static async Task<AuditCheck> VerifyAsync(Database database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        await using DbConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        long expected;
        byte[] prev;
        long headN;
        byte[] headHash;
        await using (
            DbCommand state = Database.Command(connection, null, "SELECT anchor_n, anchor_hash, head_n, head_hash FROM audit_state WHERE id = 1")
        )
        await using (DbDataReader reader = await state.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new AuditCheck(0, 1);
            }

            expected = reader.GetInt64(0) + 1;
            prev = Database.ReadBytes(reader, 1);
            headN = reader.GetInt64(2);
            headHash = Database.ReadBytes(reader, 3);
        }

        long count = 0;
        await using (
            DbCommand select = Database.Command(
                connection,
                null,
                "SELECT n, ts, actor_type, actor_id, action, target, result, ip, detail, prev, hash FROM audit_log ORDER BY n"
            )
        )
        await using (DbDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                AuditRow row = ReadRow(reader);
                count++;
                bool follows =
                    row.N == expected
                    && CryptographicOperations.FixedTimeEquals(row.Prev, prev)
                    && CryptographicOperations.FixedTimeEquals(row.Hash, Hash(row.N, row.Ts, row.Entry, row.Prev));
                if (!follows)
                {
                    // A row removed in the middle shows as the next row's number; a changed row as its own.
                    return new AuditCheck(count, Math.Min(row.N, expected));
                }

                prev = row.Hash;
                expected++;
            }
        }

        bool headMatches = expected - 1 == headN && CryptographicOperations.FixedTimeEquals(prev, headHash);
        return new AuditCheck(count, headMatches ? null : expected);
    }

    /// <summary>
    /// Removes the oldest rows written before a time, from the anchor on, and moves the anchor to the last row removed, so
    /// the rows left still check (design §18.2).
    /// </summary>
    /// <returns>The rows removed.</returns>
    public static async Task<long> RemoveOlderThanAsync(Database database, DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        await using DbConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await Database.BeginWriteAsync(connection, cancellationToken).ConfigureAwait(false);
        string lockClause = database.Provider == DatabaseProvider.Postgres ? " FOR UPDATE" : string.Empty;
        await using (DbCommand state = Database.Command(connection, transaction, "SELECT head_n FROM audit_state WHERE id = 1" + lockClause))
        {
            _ = await state.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        // The rows to remove are the oldest ones up to the first row at or after the time, so a clock that moved back does not
        // leave a hole in the middle.
        long? last = null;
        byte[]? lastHash = null;
        await using (DbCommand select = Database.Command(connection, transaction, "SELECT n, ts, hash FROM audit_log ORDER BY n"))
        await using (DbDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            long cutoff = before.ToUnixTimeSeconds();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.GetInt64(1) < cutoff)
            {
                last = reader.GetInt64(0);
                lastHash = Database.ReadBytes(reader, 2);
            }
        }

        if (last is not long removedUpTo)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        long removed;
        await using (DbCommand delete = Database.Command(connection, transaction, "DELETE FROM audit_log WHERE n <= @n", ("n", removedUpTo)))
        {
            removed = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (
            DbCommand anchor = Database.Command(
                connection,
                transaction,
                "UPDATE audit_state SET anchor_n = @n, anchor_hash = @hash WHERE id = 1",
                ("n", removedUpTo),
                ("hash", lastHash)
            )
        )
        {
            await anchor.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    private static AuditRow ReadRow(DbDataReader reader)
    {
        var entry = new AuditEntry(
            reader.GetString(2),
            Database.ReadText(reader, 3),
            reader.GetString(4),
            Database.ReadText(reader, 5),
            reader.GetString(6),
            Database.ReadText(reader, 7),
            Database.ReadText(reader, 8)
        );
        return new AuditRow(reader.GetInt64(0), reader.GetInt64(1), entry, Database.ReadBytes(reader, 9), Database.ReadBytes(reader, 10));
    }
}
