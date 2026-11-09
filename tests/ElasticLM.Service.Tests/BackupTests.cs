using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ElasticLM.Service.Tests;

/// <summary>
/// <c>elm-service backup</c> (design §14.2, V1c14).
/// </summary>
public sealed class BackupTests
{
    /// <summary>
    /// A backup taken while another connection holds an open write copies the rows committed before it, including those still
    /// in the WAL, and not the open write; it does not wait for that writer.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A migrated SQLite database with a table <c>probe</c>; a second connection with automatic checkpoints off.
    ///
    /// Steps:
    /// - On the second connection, commit the row 1, then begin an immediate transaction and insert the row 2 without
    ///   committing it; run the backup while that transaction is open.
    ///
    /// Expected:
    /// - Exit 0 within 5 seconds, the line <c>backup: &lt;path&gt;, schema version &lt;n&gt;</c> with the newest version
    ///   applied, nothing on standard error; the copy holds the row 1 and not the row 2, and its journal mode is not WAL.
    /// </remarks>
    [Fact]
    public async Task Run_WhileAWriteIsOpen_CopiesTheCommittedRows()
    {
        await using TestService test = TestService.Open("sqlite");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await test.SetupAsync();
        await test.ExecuteAsync("CREATE TABLE probe (n INTEGER NOT NULL)");
        string target = test.FilePath("backup.db");

        await using (var writer = new SqliteConnection(test.ConnectionString))
        {
            await writer.OpenAsync(cancel);
            await ExecuteAsync(writer, "PRAGMA wal_autocheckpoint = 0; INSERT INTO probe (n) VALUES (1);", cancel);
            Assert.True(new FileInfo(test.FilePath("elm.db-wal")).Length > 0);
            using SqliteTransaction open = writer.BeginTransaction(deferred: false);
            await ExecuteAsync(writer, "INSERT INTO probe (n) VALUES (2);", cancel, open);

            var watch = Stopwatch.StartNew();
            (int code, string output, string error) = await RunAsync(test.Settings(), target);
            watch.Stop();
            Assert.Equal(
                (0, $"backup: {target}, schema version {Migrator.Embedded(DatabaseProvider.Sqlite).Count}", string.Empty),
                (code, output, error)
            );
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        }

        Assert.Equal("1", await ScalarAsync(target, "SELECT group_concat(n) FROM probe"));
        Assert.NotEqual("wal", await ScalarAsync(target, "PRAGMA journal_mode"));
        Assert.Empty(Leftovers(test));
    }

    /// <summary>
    /// The restore of design §14.2 (stop the service, remove the database with its -wal and -shm files, put the copy in its
    /// place, start) brings back the database of the backup's time, and the service's start makes it WAL again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A migrated SQLite database with a table <c>probe</c> holding the row 1, and a running service.
    ///
    /// Steps:
    /// - Back up while the service runs; insert the row 2; stop the service; delete elm.db, elm.db-wal, and elm.db-shm; copy
    ///   the backup to elm.db; start the service; read the table.
    ///
    /// Expected:
    /// - The service starts and answers /health with 200, the table holds the row 1 only, and the journal mode is WAL.
    /// </remarks>
    [Fact]
    public async Task Run_RestoredAsDesigned_StartsWithTheBackupsData()
    {
        await using TestService test = TestService.Open("sqlite");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await test.SetupAsync();
        await test.ExecuteAsync("CREATE TABLE probe (n INTEGER NOT NULL); INSERT INTO probe (n) VALUES (1);");
        string target = test.FilePath("backup.db");

        await using (ElmService running = await ElmService.StartAsync(test.Settings(), test.HostOptions(), cancel))
        {
            Assert.Equal(0, (await RunAsync(test.Settings(), target)).Code);
            await test.ExecuteAsync("INSERT INTO probe (n) VALUES (2);");
        }

        SqliteConnection.ClearAllPools();
        foreach (string name in (string[])["elm.db", "elm.db-wal", "elm.db-shm"])
        {
            File.Delete(test.FilePath(name));
        }

        File.Copy(target, test.FilePath("elm.db"));
        ElmService restored = await test.StartAsync();
        using HttpResponseMessage health = await test.Http(restored).GetAsync(new Uri("/health", UriKind.Relative), cancel);
        Assert.Equal(System.Net.HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(1, await test.ScalarAsync("SELECT count(*) FROM probe"));
        Assert.Equal(1, await test.ScalarAsync("SELECT max(n) FROM probe"));
        Assert.Equal("wal", await ScalarAsync(test.FilePath("elm.db"), "PRAGMA journal_mode"));
    }

    /// <summary>
    /// The backup refuses a target that exists, a missing database, a database of another program, and PostgreSQL, and leaves
    /// no file behind.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A migrated SQLite database; a file at the target with known content.
    ///
    /// Steps:
    /// - Back up onto the existing file, onto the database itself, into a missing folder; back up a database path that does
    ///   not exist, an empty file, a SQLite database without schema_migrations, one with an empty schema_migrations, an
    ///   in-memory database; back up with the PostgreSQL provider.
    ///
    /// Expected:
    /// - 1 with "The backup file '…' exists." and the file unchanged (the database too); 1 with "cannot be written"; 1 with
    ///   "does not exist." and no file made at that path; 1 with "The database cannot be used:" for the empty file and with
    ///   "is not an elm-service database" for the other two and the in-memory one; 2 with "Use pg_dump to back up a PostgreSQL
    ///   database."; nothing on standard output in each case, and no temporary file left.
    /// </remarks>
    [Fact]
    public async Task Run_RefusedInputs_FailWithTheReason()
    {
        await using TestService test = TestService.Open("sqlite");
        await test.SetupAsync();
        string existing = test.FilePath("existing.db");
        await File.WriteAllTextAsync(existing, "kept", TestContext.Current.CancellationToken);
        long databaseLength = new FileInfo(test.FilePath("elm.db")).Length;

        Assert.Equal((1, string.Empty, $"The backup file '{existing}' exists."), await RunAsync(test.Settings(), existing));
        Assert.Equal("kept", await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken));
        Assert.Equal(
            (1, string.Empty, $"The backup file '{test.FilePath("elm.db")}' exists."),
            await RunAsync(test.Settings(), test.FilePath("elm.db"))
        );
        Assert.Equal(databaseLength, new FileInfo(test.FilePath("elm.db")).Length);

        string inMissing = Path.Combine(test.FilePath("missing"), "backup.db");
        (int code, string output, string error) = await RunAsync(test.Settings(), inMissing);
        Assert.Equal((1, string.Empty), (code, output));
        Assert.StartsWith($"The backup file '{inMissing}' cannot be written: ", error, StringComparison.Ordinal);

        string absent = test.FilePath("absent.db");
        Assert.Equal(
            (1, string.Empty, $"The database file '{absent}' does not exist."),
            await RunAsync(test.Settings(s => s with { ConnectionString = "Data Source=" + absent }), test.FilePath("b1.db"))
        );
        Assert.False(File.Exists(absent));

        string empty = test.FilePath("empty.db");
        await File.WriteAllBytesAsync(empty, new byte[4096], TestContext.Current.CancellationToken);
        (code, output, error) = await RunAsync(test.Settings(s => s with { ConnectionString = "Data Source=" + empty }), test.FilePath("b2.db"));
        Assert.Equal((1, string.Empty), (code, output));
        Assert.StartsWith("The database cannot be used: ", error, StringComparison.Ordinal);

        string other = test.FilePath("other.db");
        await ExecuteFileAsync(other, "CREATE TABLE notes (text TEXT);");
        Assert.Equal(
            (1, string.Empty, $"The database '{other}' is not an elm-service database: it has no schema_migrations table."),
            await RunAsync(test.Settings(s => s with { ConnectionString = "Data Source=" + other }), test.FilePath("b3.db"))
        );

        string failed = test.FilePath("failed.db");
        await ExecuteFileAsync(failed, "CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, name TEXT, sha256 BLOB, applied_at INTEGER);");
        Assert.Equal(
            (1, string.Empty, $"The database '{failed}' is not an elm-service database: no migration was applied to it."),
            await RunAsync(test.Settings(s => s with { ConnectionString = "Data Source=" + failed }), test.FilePath("b4.db"))
        );

        Assert.Equal(
            (1, string.Empty, "The database is not an elm-service database: the connection string names no database file."),
            await RunAsync(test.Settings(s => s with { ConnectionString = "Data Source=:memory:" }), test.FilePath("b5.db"))
        );

        Assert.Equal(
            (2, string.Empty, "Use pg_dump to back up a PostgreSQL database."),
            await RunAsync(test.Settings(s => s with { Database = DatabaseProvider.Postgres }), test.FilePath("b6.db"))
        );

        Assert.Empty(Leftovers(test));
    }

    /// <summary>
    /// A database of a newer version than this elm-service knows is copied, not refused, and the line names its version.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A migrated SQLite database to which a row for a later version was added in schema_migrations.
    ///
    /// Steps:
    /// - Back up.
    ///
    /// Expected:
    /// - Exit 0 and the line names the later version.
    /// </remarks>
    [Fact]
    public async Task Run_NewerDatabase_CopiesItWithItsVersion()
    {
        await using TestService test = TestService.Open("sqlite");
        await test.SetupAsync();
        int later = Migrator.Embedded(DatabaseProvider.Sqlite).Count + 1;
        await test.ExecuteAsync(
            string.Create(CultureInfo.InvariantCulture, $"INSERT INTO schema_migrations VALUES ({later}, '{later:D4}_later.sql', x'00', 0)")
        );
        string target = test.FilePath("backup.db");

        Assert.Equal((0, $"backup: {target}, schema version {later}", string.Empty), await RunAsync(test.Settings(), target));
    }

    /// <summary>
    /// On Linux and macOS the backup is readable and writable by its owner only, from its creation.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A migrated SQLite database; Linux or macOS (skipped on Windows, where the file takes the folder's access rules).
    ///
    /// Steps:
    /// - Back up.
    ///
    /// Expected:
    /// - The file's mode is 0600.
    /// </remarks>
    [Fact]
    public async Task Run_OnUnix_CreatesTheFileForItsOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no file modes; the backup takes the folder's access rules.");
        await using TestService test = TestService.Open("sqlite");
        await test.SetupAsync();
        string target = test.FilePath("backup.db");

        Assert.Equal(0, (await RunAsync(test.Settings(), target)).Code);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));
        }
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(ServiceSettings settings, string target)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await Backup.RunAsync(settings, target, output, error, TestContext.Current.CancellationToken);
        return (code, output.ToString().Trim(), error.ToString().Trim());
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancel, DbTransaction? transaction = null)
    {
        await using DbCommand command = Database.Command(connection, transaction, sql);
        await command.ExecuteNonQueryAsync(cancel);
    }

    private static async Task ExecuteFileAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync(connection, sql, TestContext.Current.CancellationToken);
    }

    private static async Task<string?> ScalarAsync(string path, string sql)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using DbCommand command = Database.Command(connection, null, sql);
        return Convert.ToString(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), CultureInfo.InvariantCulture);
    }

    // A temporary file of the backup, or the journal SQLite writes beside it, left in the test's folder.
    private static string[] Leftovers(TestService test) =>
        [
            .. Directory
                .EnumerateFiles(test.Directory)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => n.EndsWith(".tmp", StringComparison.Ordinal) || n.Contains(".tmp-", StringComparison.Ordinal)),
        ];
}
