using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace ElasticLM.Service;

/// <summary>
/// The thin layer over <see cref="DbConnection"/> that hides the two engines (design §14.2): SQL is written by hand, and
/// only what differs between SQLite and PostgreSQL is here (how a write transaction starts, how IDs and bytes are stored).
/// </summary>
internal sealed class Database : IAsyncDisposable
{
    private static readonly Lock SqliteInitLock = new();
    private static bool _sqliteReady;

    private readonly NpgsqlDataSource? _postgres;
    private readonly string _connectionString;

    private Database(DatabaseProvider provider, string connectionString, NpgsqlDataSource? postgres)
    {
        Provider = provider;
        _connectionString = connectionString;
        _postgres = postgres;
    }

    public DatabaseProvider Provider { get; }

    /// <summary>
    /// Creates the layer for a connection string. Nothing is opened yet.
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
                _ = new SqliteConnectionStringBuilder(connectionString);
                return new Database(provider, connectionString, null);
            }

            // design §14.2: the slim builder keeps NativeAOT free of the reflection-based features.
            return new Database(provider, connectionString, new NpgsqlSlimDataSourceBuilder(connectionString).Build());
        }
        catch (ArgumentException e)
        {
            throw new ServiceStartException("The database connection string is not valid: " + e.Message);
        }
    }

    /// <summary>
    /// Opens a connection. SQLite connections wait up to 5 seconds for another writer and enforce foreign keys.
    /// </summary>
    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_postgres is not null)
        {
            return await _postgres.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        var connection = new SqliteConnection(_connectionString) { DefaultTimeout = 5 };
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using DbCommand pragma = Command(connection, null, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Starts a write transaction. SQLite takes the write lock at once (<c>BEGIN IMMEDIATE</c>, design §14.4), so two
    /// writers do not both read and then fail to write; PostgreSQL uses read committed with explicit row locks.
    /// </summary>
    public static async Task<DbTransaction> BeginWriteAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection is SqliteConnection sqlite
            ? sqlite.BeginTransaction(deferred: false)
            : await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a command with named parameters (<c>@name</c> in both engines).
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

    /// <summary>Returns an ID as the engine stores it: text in SQLite, <c>uuid</c> in PostgreSQL (decision 0.67).</summary>
    public object Id(Guid id) => Provider == DatabaseProvider.Sqlite ? id.ToString("D") : id;

    /// <summary>Reads an ID column.</summary>
    public Guid ReadId(DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return Provider == DatabaseProvider.Sqlite ? Guid.ParseExact(reader.GetString(ordinal), "D") : reader.GetGuid(ordinal);
    }

    /// <summary>Reads a byte column (<c>BLOB</c> or <c>bytea</c>).</summary>
    public static byte[] ReadBytes(DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return (byte[])reader.GetValue(ordinal);
    }

    /// <summary>Reads a text column that may be <c>NULL</c>.</summary>
    public static string? ReadText(DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync().ConfigureAwait(false);
        }
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
}
