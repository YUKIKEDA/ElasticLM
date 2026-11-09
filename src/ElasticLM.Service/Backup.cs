using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using ElasticLM.Licensing;
using Microsoft.Data.Sqlite;

namespace ElasticLM.Service;

/// <summary>
/// <c>elm-service backup</c> (design §14.2): copies a SQLite database into one file with <c>VACUUM INTO</c>, also while the
/// service runs and writes. The copy is written under a temporary name beside the target and then moved to the target, which
/// must not exist. Nothing is migrated and nothing is recorded in the audit log.
/// </summary>
internal static class Backup
{
    private const string NotOurs = "is not an elm-service database";

    /// <summary>
    /// Runs the backup.
    /// </summary>
    /// <param name="settings">The settings, read without the signing settings.</param>
    /// <param name="target">The backup file, as the operator gave it; relative paths are read from the working folder.</param>
    /// <param name="output">Receives the one line that names the backup and its schema version.</param>
    /// <param name="error">Receives the reason of a failure.</param>
    /// <param name="cancellationToken">Stops the backup.</param>
    /// <returns>0 on success, 1 when the database or the backup file fails, 2 when the settings do not allow a backup.</returns>
    public static async Task<int> RunAsync(
        ServiceSettings settings,
        string target,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (settings.Database != DatabaseProvider.Sqlite)
        {
            await error.WriteLineAsync("Use pg_dump to back up a PostgreSQL database.").ConfigureAwait(false);
            return Setup.ExitSettings;
        }

        SqliteConnectionStringBuilder source;
        try
        {
            source = new SqliteConnectionStringBuilder(settings.ConnectionString);
        }
        catch (ArgumentException e)
        {
            await error
                .WriteLineAsync("The database connection string is not valid: " + ArgumentMessages.WithoutParameterName(e))
                .ConfigureAwait(false);
            return Setup.ExitSettings;
        }

        string? problem = SourceProblem(source);
        if (problem is null && Exists(target))
        {
            problem = $"The backup file '{target}' exists.";
        }

        if (problem is not null)
        {
            await error.WriteLineAsync(problem).ConfigureAwait(false);
            return Setup.ExitDatabase;
        }

        string temporary = TemporaryPath(target);
        try
        {
            long version = await CopyAsync(source, temporary, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, target, overwrite: false);
            await output
                .WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"backup: {target}, schema version {version}"))
                .ConfigureAwait(false);
            return Setup.ExitOk;
        }
        catch (ServiceStartException e)
        {
            await error.WriteLineAsync(e.Message).ConfigureAwait(false);
        }
        catch (DbException e)
        {
            await error.WriteLineAsync("The database cannot be used: " + e.Message).ConfigureAwait(false);
        }
        catch (IOException) when (Exists(target))
        {
            // Another process made the target after the check: the move does not overwrite it.
            await error.WriteLineAsync($"The backup file '{target}' exists.").ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"The backup file '{target}' cannot be written: {e.Message}").ConfigureAwait(false);
        }

        Remove(temporary);
        Remove(temporary + "-journal");
        return Setup.ExitDatabase;
    }

    // design §14.2: only a database file can be copied, and SQLite would create a missing one.
    private static string? SourceProblem(SqliteConnectionStringBuilder source)
    {
        string path = source.DataSource;
        if (path.Length == 0 || path == ":memory:" || source.Mode == SqliteOpenMode.Memory)
        {
            return $"The database {NotOurs}: the connection string names no database file.";
        }

        return File.Exists(path) ? null : $"The database file '{path}' does not exist.";
    }

    private static async Task<long> CopyAsync(SqliteConnectionStringBuilder source, string temporary, CancellationToken cancellationToken)
    {
        await using (SqliteConnection connection = await Database.OpenSqliteFileAsync(source, cancellationToken).ConfigureAwait(false))
        {
            // Checked before the copy, so that a file of another program is not copied; the version comes from the copy.
            await RequireSchemaAsync(connection, source.DataSource, cancellationToken).ConfigureAwait(false);
            CreateEmpty(temporary);

            // VACUUM INTO writes only into a missing or empty file, and reads the database in one read transaction.
            await using DbCommand vacuum = Database.Command(connection, null, "VACUUM INTO @path;", ("@path", temporary));
            await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var copy = new SqliteConnectionStringBuilder { DataSource = temporary };
        await using SqliteConnection read = await Database.OpenSqliteFileAsync(copy, cancellationToken).ConfigureAwait(false);
        await using DbCommand query = Database.Command(read, null, "SELECT max(version) FROM schema_migrations;");
        return Convert.ToInt64(await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task RequireSchemaAsync(SqliteConnection connection, string path, CancellationToken cancellationToken)
    {
        await using DbCommand table = Database.Command(
            connection,
            null,
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations';"
        );
        if (Convert.ToInt64(await table.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
        {
            throw new ServiceStartException($"The database '{path}' {NotOurs}: it has no schema_migrations table.");
        }

        // design §14.2: an empty table is a database whose first migration failed, which holds nothing to restore.
        await using DbCommand rows = Database.Command(connection, null, "SELECT count(*) FROM schema_migrations;");
        if (Convert.ToInt64(await rows.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
        {
            throw new ServiceStartException($"The database '{path}' {NotOurs}: no migration was applied to it.");
        }
    }

    // design §14.2: owner read and write only (0600) on Linux and macOS; on Windows the file takes the folder's access rules.
    // CreateNew refuses a file or a link that is already there.
    private static void CreateEmpty(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
    }

    private static string TemporaryPath(string target)
    {
        string full = Path.GetFullPath(target);
        string name = Path.GetFileName(full) + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) + ".tmp";
        return Path.Combine(Path.GetDirectoryName(full)!, name);
    }

    // A link counts even when it points nowhere: File.Exists follows it.
    private static bool Exists(string path) => Path.Exists(path) || new FileInfo(path).LinkTarget is not null;

    private static void Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left beside the target under the temporary name, which no service reads.
        }
    }
}
