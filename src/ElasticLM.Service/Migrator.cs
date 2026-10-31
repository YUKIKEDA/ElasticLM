using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ElasticLM.Service;

/// <summary>
/// One schema version: a numbered SQL file embedded in the executable (design §14.2, decision 0.69).
/// </summary>
/// <param name="Version">The number, from 1, without gaps.</param>
/// <param name="Name">The file name, such as <c>0001_foundation.sql</c>.</param>
/// <param name="Sql">The SQL text.</param>
internal sealed record Migration(int Version, string Name, string Sql)
{
    /// <summary>Gets the SHA-256 of the file's UTF-8 bytes, recorded when the version is applied. It is computed from
    /// <see cref="Sql"/> each time, so a copy with other text (<c>with</c>) has its own hash.</summary>
    public byte[] Hash => SHA256.HashData(Encoding.UTF8.GetBytes(Sql));
}

/// <summary>
/// Applies the schema versions at start (design §14.2, decision 0.69): each version once, in order, in its own transaction,
/// recorded in <c>schema_migrations</c> with its SHA-256. A recorded version whose file changed, or that this build does not
/// have, stops the start. On PostgreSQL an advisory lock lets one node apply them while others wait; on SQLite each version is
/// checked again inside its write transaction, so two processes starting at once apply it once.
/// </summary>
internal static partial class Migrator
{
    // An arbitrary 64-bit number that names this lock among the database's advisory locks ("elm-migr" in ASCII).
    private const long AdvisoryLockId = 0x656C_6D2D_6D69_6772;

    // design §14.2: a SQLite migration waits this long for another writer, not the 5 seconds of a request. Two processes that
    // migrate one empty file at once take turns per version, and the one that waits can wait for all the other's versions: on
    // CI's Windows runner that took more than 5 seconds (Issue #158).
    private static readonly TimeSpan SqliteWriterWait = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Returns the migrations embedded for an engine, in order.
    /// </summary>
    /// <exception cref="ServiceStartException">A file name is not <c>NNNN_name.sql</c>, or the numbers have a gap.</exception>
    public static IReadOnlyList<Migration> Embedded(DatabaseProvider provider)
    {
        string folder = provider == DatabaseProvider.Sqlite ? "migrations/sqlite/" : "migrations/postgres/";
        Assembly assembly = typeof(Migrator).Assembly;
        var migrations = new List<Migration>();
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            string name = resource.Replace('\\', '/');
            if (!name.StartsWith(folder, StringComparison.Ordinal))
            {
                continue;
            }

            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            migrations.Add(Parse(name[folder.Length..], reader.ReadToEnd()));
        }

        return Order(migrations);
    }

    /// <summary>
    /// Checks a migration's file name and returns it with its number.
    /// </summary>
    /// <exception cref="ServiceStartException">The name is not <c>NNNN_name.sql</c>.</exception>
    public static Migration Parse(string fileName, string sql)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        Match match = FileNamePattern().Match(fileName);
        if (!match.Success)
        {
            throw new ServiceStartException($"The migration '{fileName}' is not named NNNN_name.sql.");
        }

        return new Migration(int.Parse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture), fileName, sql);
    }

    /// <summary>
    /// Sorts migrations and checks that their numbers run 1, 2, 3, … without a gap or a repeat.
    /// </summary>
    /// <exception cref="ServiceStartException">A number is missing or repeated.</exception>
    public static IReadOnlyList<Migration> Order(IEnumerable<Migration> migrations)
    {
        Migration[] ordered = [.. migrations.OrderBy(m => m.Version)];
        for (int i = 0; i < ordered.Length; i++)
        {
            if (ordered[i].Version != i + 1)
            {
                throw new ServiceStartException($"The migrations must be numbered from 1 without gaps; '{ordered[i].Name}' is out of place.");
            }
        }

        return ordered;
    }

    /// <summary>
    /// Brings the database up to the last migration.
    /// </summary>
    /// <returns>The versions applied now (none when the database was up to date).</returns>
    /// <exception cref="ServiceStartException">A recorded version changed or is unknown to this build.</exception>
    public static async Task<IReadOnlyList<int>> RunAsync(
        Database database,
        IReadOnlyList<Migration> migrations,
        TimeProvider time,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentNullException.ThrowIfNull(time);
        await using DbConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        bool postgres = database.Provider == DatabaseProvider.Postgres;
        if (!postgres)
        {
            Database.WaitForWriters(connection, SqliteWriterWait);

            // design §14.2: write-ahead logging lets readers go on while one writer writes. The mode is kept in the file.
            await using DbCommand wal = Database.Command(connection, null, "PRAGMA journal_mode = WAL");
            await wal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using DbCommand acquire = Database.Command(connection, null, "SELECT pg_advisory_lock(@id)", ("id", AdvisoryLockId));
            await acquire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            string blob = postgres ? "bytea" : "BLOB";
            await using (
                DbCommand create = Database.Command(
                    connection,
                    null,
                    $"CREATE TABLE IF NOT EXISTS schema_migrations (version integer PRIMARY KEY, name text NOT NULL, sha256 {blob} NOT NULL, applied_at bigint NOT NULL)"
                )
            )
            {
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            Dictionary<int, byte[]> applied = await ReadAppliedAsync(connection, cancellationToken).ConfigureAwait(false);
            foreach ((int version, byte[] hash) in applied)
            {
                Migration? known = migrations.FirstOrDefault(m => m.Version == version);
                if (known is null)
                {
                    throw new ServiceStartException(
                        $"The database has schema version {version}, which this elm-service does not know; it is older than the database."
                    );
                }

                if (!CryptographicOperations.FixedTimeEquals(known.Hash, hash))
                {
                    throw new ServiceStartException($"The migration '{known.Name}' changed after it was applied; restore the file as it was.");
                }
            }

            var done = new List<int>();
            foreach (Migration migration in migrations.Where(m => !applied.ContainsKey(m.Version)))
            {
                await using DbTransaction transaction = await Database.BeginWriteAsync(connection, cancellationToken).ConfigureAwait(false);

                // SQLite has no advisory lock: another process may have applied the version since it was read. The write
                // transaction holds the database's write lock, so the check here cannot be overtaken.
                await using (
                    DbCommand recorded = Database.Command(
                        connection,
                        transaction,
                        "SELECT sha256 FROM schema_migrations WHERE version = @version",
                        ("version", migration.Version)
                    )
                )
                {
                    if (await recorded.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is byte[] recordedHash)
                    {
                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        if (!CryptographicOperations.FixedTimeEquals(migration.Hash, recordedHash))
                        {
                            throw new ServiceStartException($"The migration '{migration.Name}' was applied by another process with other text.");
                        }

                        continue;
                    }
                }

                await using (DbCommand script = Database.Command(connection, transaction, migration.Sql))
                {
                    await script.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (
                    DbCommand record = Database.Command(
                        connection,
                        transaction,
                        "INSERT INTO schema_migrations (version, name, sha256, applied_at) VALUES (@version, @name, @sha256, @at)",
                        ("version", migration.Version),
                        ("name", migration.Name),
                        ("sha256", migration.Hash),
                        ("at", time.GetUtcNow().ToUnixTimeSeconds())
                    )
                )
                {
                    await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                done.Add(migration.Version);
            }

            return done;
        }
        finally
        {
            if (postgres)
            {
                await using DbCommand release = Database.Command(connection, null, "SELECT pg_advisory_unlock(@id)", ("id", AdvisoryLockId));
                await release.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task<Dictionary<int, byte[]>> ReadAppliedAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var applied = new Dictionary<int, byte[]>();
        await using DbCommand select = Database.Command(connection, null, "SELECT version, sha256 FROM schema_migrations");
        await using DbDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied[reader.GetInt32(0)] = (byte[])reader.GetValue(1);
        }

        return applied;
    }

    [GeneratedRegex(@"^([0-9]{4})_[a-z0-9_]+\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();
}
