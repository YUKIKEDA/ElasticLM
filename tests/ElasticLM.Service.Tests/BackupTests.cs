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
    ///   applied, nothing on standard error; the copy holds the row 1 and not the row 2, and its journal mode is not WAL; the
    ///   audit log of the database has as many rows as before; no temporary file is left.
    /// </remarks>
    [Fact]
    public async Task Run_WhileAWriteIsOpen_CopiesTheCommittedRows()
    {
        await using TestService test = TestService.Open("sqlite");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await test.SetupAsync();
        await test.ExecuteAsync("CREATE TABLE probe (n INTEGER NOT NULL)");
        string target = test.FilePath("backup.db");
        long auditRows = await test.ScalarAsync("SELECT count(*) FROM audit_log");

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
        Assert.Equal(auditRows, await test.ScalarAsync("SELECT count(*) FROM audit_log"));
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
    ///   in-memory database, an empty Data Source, the test's database with Mode=Memory; back up with a connection string
    ///   that is not valid, with one that sets Password, and with the PostgreSQL provider.
    ///
    /// Expected:
    /// - 1 with "The backup file '…' exists." and the file unchanged (the database too); 1 with "cannot be written"; 1 with
    ///   "does not exist." and no file made at that path; 1 with "The database cannot be used:" for the empty file and with
    ///   "is not an elm-service database" for the other two and the three that name no file; 2 with "The database connection
    ///   string is not valid:" for the bad string and the password; 2 with "Use pg_dump to back up a PostgreSQL database.";
    ///   nothing on standard output in each case, and no temporary file left.
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
            (1, string.Empty, "The database is not an elm-service database: the connection string names no database file."),
            await RunAsync(test.Settings(s => s with { ConnectionString = "Data Source=" }), test.FilePath("b7.db"))
        );
        Assert.Equal(
            (1, string.Empty, "The database is not an elm-service database: the connection string names no database file."),
            await RunAsync(test.Settings(s => s with { ConnectionString = test.ConnectionString + ";Mode=Memory" }), test.FilePath("b8.db"))
        );
        (code, output, error) = await RunAsync(test.Settings(s => s with { ConnectionString = "Data Source=x;Bogus=1" }), test.FilePath("b9.db"));
        Assert.Equal((2, string.Empty), (code, output));
        Assert.StartsWith("The database connection string is not valid: ", error, StringComparison.Ordinal);
        Assert.Equal(
            (2, string.Empty, "The database connection string is not valid: Password is set, and the SQLite of elm-service does not encrypt."),
            await RunAsync(test.Settings(s => s with { ConnectionString = test.ConnectionString + ";Password=x" }), test.FilePath("b10.db"))
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

        // CA1416 does not read SkipWhen as a platform check.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));
        }
    }

    /// <summary>
    /// A file that appears at the target after the copy and before the move is kept, the backup ends with 1, and the
    /// temporary file and its journal are removed.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A migrated SQLite database.
    ///
    /// Steps:
    /// - Back up with a hook that, before the move, writes "other" to the target and a journal beside the temporary file.
    ///
    /// Expected:
    /// - 1 with "The backup file '…' exists.", the target still holds "other", the temporary file was named
    ///   <c>backup.db.&lt;16 hex digits&gt;.tmp</c> in the target's folder, and neither it nor its journal is left.
    /// </remarks>
    [Fact]
    public async Task Run_TargetMadeBeforeTheMove_KeepsItAndRemovesTheTemporaryFiles()
    {
        await using TestService test = TestService.Open("sqlite");
        await test.SetupAsync();
        string target = test.FilePath("backup.db");
        string? temporary = null;
        void Race(string path)
        {
            temporary = path;
            File.WriteAllText(target, "other");
            File.WriteAllText(path + "-journal", "journal");
        }

        Assert.Equal((1, string.Empty, $"The backup file '{target}' exists."), await RunAsync(test.Settings(), target, Race));
        Assert.Equal("other", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        Assert.NotNull(temporary);
        Assert.Equal((test.Directory, false, false), (Path.GetDirectoryName(temporary), File.Exists(temporary), File.Exists(temporary + "-journal")));
        Assert.Matches(@"^backup\.db\.[0-9a-f]{16}\.tmp$", Path.GetFileName(temporary));
    }

    /// <summary>
    /// A link at the target counts as a file that exists, also when it points nowhere, and is left as it is.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A migrated SQLite database; a symbolic link at the target to a file that does not exist (skipped where this user
    ///   cannot make links, such as Windows without developer mode).
    ///
    /// Steps:
    /// - Back up.
    ///
    /// Expected:
    /// - 1 with "The backup file '…' exists.", and the link is still there and still points to the missing file.
    /// </remarks>
    [Fact]
    public async Task Run_LinkAtTheTarget_IsRefused()
    {
        await using TestService test = TestService.Open("sqlite");
        await test.SetupAsync();
        string target = test.FilePath("backup.db");
        string missing = test.FilePath("missing.db");
        try
        {
            File.CreateSymbolicLink(target, missing);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This user cannot make symbolic links here: " + e.Message);
        }

        Assert.Equal((1, string.Empty, $"The backup file '{target}' exists."), await RunAsync(test.Settings(), target));
        Assert.Equal((missing, false), (new FileInfo(target).LinkTarget, File.Exists(missing)));
    }

    /// <summary>
    /// A database that another connection holds locked is waited for about 5 seconds, and the backup then ends with 1.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A migrated SQLite database; a second connection in exclusive locking mode with an open write.
    ///
    /// Steps:
    /// - Back up while that write is open.
    ///
    /// Expected:
    /// - 1 with a line that starts "The database cannot be used: ", after at least 4 and at most 30 seconds, and no file at
    ///   the target.
    /// </remarks>
    [Fact]
    public async Task Run_DatabaseLocked_WaitsAndFails()
    {
        await using TestService test = TestService.Open("sqlite");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await test.SetupAsync();
        string target = test.FilePath("backup.db");

        // The idle connections of setup's pool would keep the exclusive lock from being taken.
        SqliteConnection.ClearAllPools();
        await using var writer = new SqliteConnection(new SqliteConnectionStringBuilder(test.ConnectionString) { Pooling = false }.ToString());
        await writer.OpenAsync(cancel);
        await ExecuteAsync(writer, "PRAGMA locking_mode = EXCLUSIVE;", cancel);
        using SqliteTransaction open = writer.BeginTransaction(deferred: false);
        await ExecuteAsync(writer, "CREATE TABLE probe (n INTEGER NOT NULL);", cancel, open);

        var watch = Stopwatch.StartNew();
        (int code, string output, string error) = await RunAsync(test.Settings(), target);
        watch.Stop();

        Assert.Equal((1, string.Empty), (code, output));
        Assert.StartsWith("The database cannot be used: ", error, StringComparison.Ordinal);
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(30));
        Assert.False(File.Exists(target));
    }

    /// <summary>
    /// The connection of the backup opens an existing file only, whatever mode the connection string gives.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A path where no file is.
    ///
    /// Steps:
    /// - Open it with <see cref="Database.OpenSqliteFileAsync"/> from a connection string with Mode=ReadWriteCreate.
    ///
    /// Expected:
    /// - A <see cref="SqliteException"/>, and no file at the path.
    /// </remarks>
    [Fact]
    public async Task OpenSqliteFile_ModeCreateInTheString_DoesNotCreateTheFile()
    {
        await using TestService test = TestService.Open("sqlite");
        string path = test.FilePath("absent.db");
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate };

        await Assert.ThrowsAsync<SqliteException>(() =>
            Database.OpenSqliteFileAsync(builder, readOnly: false, TestContext.Current.CancellationToken)
        );
        Assert.False(File.Exists(path));
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(
        ServiceSettings settings,
        string target,
        Action<string>? beforeMove = null
    )
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await Backup.RunAsync(settings, target, output, error, beforeMove, TestContext.Current.CancellationToken);
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
