using System.Data.Common;
using AuraORM;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace ElasticLM.Service;

/// <summary>
/// An API key and what its administrator allows, as read for authentication.
/// </summary>
/// <param name="Key">The key.</param>
/// <param name="Role">The administrator's role.</param>
/// <param name="Enabled">Whether the administrator is enabled.</param>
/// <param name="AdminCreatedAt">When the administrator was added, in UNIX seconds.</param>
/// <param name="LastLoginAt">The administrator's last OIDC login in UNIX seconds, or <see langword="null"/> (M5a).</param>
internal sealed record KeyHolder(ApiKeyRecord Key, string Role, bool Enabled, long AdminCreatedAt, long? LastLoginAt);

/// <summary>
/// The service's database (design §14.2): the operations the service needs, each in the transaction it needs. Statements are
/// built with AuraORM (decision 0.81) by <see cref="ServiceStore{TDialect}"/>, one implementation for both engines; only the
/// migrations are SQL files run as they are.
/// </summary>
internal abstract partial class Database : IAsyncDisposable
{
    private static readonly Lock SqliteInitLock = new();
    private static bool _sqliteReady;

    private protected Database(DatabaseProvider provider, DbDataSource source)
    {
        Provider = provider;
        Source = source;
    }

    public DatabaseProvider Provider { get; }

    private protected DbDataSource Source { get; }

    /// <summary>
    /// Creates the database for a connection string. Nothing is opened yet.
    /// </summary>
    /// <exception cref="ServiceStartException">The connection string is not valid for the engine.</exception>
    public static Database Create(DatabaseProvider provider, string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        try
        {
            if (provider == DatabaseProvider.Sqlite)
            {
                EnsureSqlite();

                // Parsed here so that a bad string stops the start, not the first request.
                var builder = new SqliteConnectionStringBuilder(connectionString) { ForeignKeys = true, DefaultTimeout = 5 };
                return new ServiceStore<SqliteDialect>(provider, new SqliteSource(builder.ToString()));
            }

            // design §14.2: the slim builder keeps NativeAOT free of the reflection-based features.
            return new ServiceStore<PostgreSqlDialect>(provider, new NpgsqlSlimDataSourceBuilder(connectionString).Build());
        }
        catch (ArgumentException e)
        {
            throw new ServiceStartException("The database connection string is not valid: " + e.Message);
        }
    }

    /// <summary>
    /// Opens a connection, for the migrations. SQLite connections wait up to 5 seconds for another writer and enforce foreign
    /// keys.
    /// </summary>
    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default) =>
        await Source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Makes a SQLite connection of <see cref="OpenAsync"/> wait up to <paramref name="wait"/> for another writer, for the
    /// migrations (design §14.2). Microsoft.Data.Sqlite retries a busy database until the timeout of the command, which a
    /// command and a write transaction take from the connection's default; SQLite's own busy wait stays at 5 seconds and is
    /// retried within it. Connections of other engines are left as they are.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="wait">How long to wait for another writer.</param>
    public static void WaitForWriters(DbConnection connection, TimeSpan wait)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection is SqliteConnection sqlite)
        {
            sqlite.DefaultTimeout = (int)wait.TotalSeconds;
        }
    }

    /// <summary>
    /// Starts a write transaction on a connection of <see cref="OpenAsync"/>. SQLite takes the write lock at once
    /// (<c>BEGIN IMMEDIATE</c>, design §14.4), so two writers do not both read and then fail to write.
    /// </summary>
    public static async Task<DbTransaction> BeginWriteAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection is SqliteConnection sqlite
            ? sqlite.BeginTransaction(deferred: false)
            : await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a command with named parameters (<c>@name</c> in both engines), for the migrations.
    /// </summary>
    public static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    /// <summary>Answers when the database answers a query.</summary>
    public abstract Task PingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Adds the bootstrap owner when it is new and stores a key for it, with its audit row, in one transaction (setup).
    /// </summary>
    /// <exception cref="ServiceStartException">The owner exists but was disabled or given another role.</exception>
    public abstract Task<ApiKeyRecord> SetupKeyAsync(
        AdminIdentity owner,
        NewApiKey key,
        Func<ApiKeyRecord, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Stores a key for an administrator with its audit row, in one transaction.
    /// </summary>
    public abstract Task<ApiKeyRecord> CreateKeyAsync(
        Guid adminId,
        NewApiKey key,
        Func<ApiKeyRecord, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );

    /// <summary>Finds a key by its hash, with its administrator's role and state.</summary>
    public abstract Task<KeyHolder?> FindKeyAsync(byte[] hash, CancellationToken cancellationToken);

    /// <summary>
    /// Returns keys after an ID, in ID order: UUID version 7 orders them by creation time to the millisecond.
    /// </summary>
    public abstract Task<IReadOnlyList<ApiKeyRecord>> ListKeysAsync(Guid? after, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Stops a key with its audit row, in one transaction. A stopped key stays stopped at its first time.
    /// </summary>
    /// <returns><see langword="false"/> when no key has the ID; nothing is written then.</returns>
    public abstract Task<bool> RevokeKeyAsync(Guid id, AuditEntry audit, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Appends an audit row in a transaction of its own.</summary>
    /// <returns>The row's number.</returns>
    public abstract Task<long> AppendAuditAsync(AuditEntry entry, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Returns audit rows oldest first after a row number, or newest first below it (design §14.5, M5d1); with no number,
    /// from the oldest or the newest row.
    /// </summary>
    public abstract Task<IReadOnlyList<AuditRow>> ListAuditAsync(long? after, bool descending, int limit, CancellationToken cancellationToken);

    /// <summary>Checks the audit chain from the anchor to the head, in one snapshot of the database (design §18.2).</summary>
    public abstract Task<AuditCheck> VerifyAuditAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes the oldest audit rows up to the first one written at or after a time and moves the anchor to the last row
    /// removed, so the rows left still check (design §18.2).
    /// </summary>
    /// <returns>The rows removed.</returns>
    public abstract Task<long> RemoveAuditOlderThanAsync(DateTimeOffset before, CancellationToken cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await Source.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    // SQLite is built from vendor/sqlite (design §14.2); the provider binds to it by name, once per process.
    private static void EnsureSqlite()
    {
        lock (SqliteInitLock)
        {
            if (!_sqliteReady)
            {
                SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
                _sqliteReady = true;
            }
        }
    }

    // Microsoft.Data.Sqlite has no data source; this one opens a new connection each time, which SQLite's own pool makes cheap.
    private sealed class SqliteSource(string connectionString) : DbDataSource
    {
        public override string ConnectionString => connectionString;

        protected override DbConnection CreateDbConnection() => new SqliteConnection(connectionString);

        protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = new SqliteConnection(connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using DbCommand pragma = Command(connection, null, "PRAGMA busy_timeout = 5000;");
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }
}
