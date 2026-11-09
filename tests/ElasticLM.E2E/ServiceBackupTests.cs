namespace ElasticLM.E2E;

/// <summary>
/// The published elm-service's <c>backup</c> command beside the running service (design §14.2, V1c14).
/// </summary>
public sealed class ServiceBackupTests
{
    private const string Usage = "Usage: elm-service [setup | backup --out <file>] [--config <file>]";

    /// <summary>
    /// The published elm-service backs up the SQLite database of the running service, refuses a target that exists, and
    /// refuses arguments that the design does not allow with the usage and exit code 2; on PostgreSQL it points to pg_dump.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The published elm-service, set up and running for a vendor, with one license; its settings in the environment.
    ///
    /// Steps:
    /// - Run <c>backup --out backup.db</c> while the service runs, then the same again; run <c>backup</c> alone,
    ///   <c>backup --out</c>, <c>backup --out ""</c>, <c>backup --out a.db --out b.db</c>, <c>setup --out a.db</c>,
    ///   <c>--out a.db backup</c>, and <c>backup --config x.json --config y.json --out a.db</c>.
    ///
    /// Expected:
    /// - On SQLite: 0 with the one line <c>backup: backup.db, schema version &lt;n&gt;</c> and a file beside the database;
    ///   then 1 with "The backup file 'backup.db' exists.". On PostgreSQL: 2 with "Use pg_dump to back up a PostgreSQL
    ///   database." and no file. Then 2 with the usage line for each refused argument list, and 2 with "The backup file path
    ///   is empty." for the empty path; none of them makes a.db or b.db.
    /// </remarks>
    /// <param name="database"><c>sqlite</c> or <c>postgres</c>.</param>
    [Theory]
    [MemberData(nameof(ServiceProcess.Databases), MemberType = typeof(ServiceProcess))]
    public async Task Backup_BesideTheRunningService_CopiesOrRefuses(string database)
    {
        await using Vendor vendor = await Vendor.CreateAsync("license");
        await using ServiceProcess service = await ServiceProcess.StartAsync(vendor, database);
        string product = await service.ProductAsync("acme-cad");
        await service.LicenseAsync(await service.PolicyAsync(product));

        (int code, string output, string error) = await service.RunAsync("backup", "--out", "backup.db");
        if (database == "sqlite")
        {
            Assert.Equal(0, code);
            Assert.Matches(@"^backup: backup\.db, schema version [1-9][0-9]*$", output.TrimEnd('\r', '\n'));
            Assert.Equal(string.Empty, error);
            Assert.True(new FileInfo(vendor.File("backup.db")).Length > 0);
            Assert.Equal((1, string.Empty, "The backup file 'backup.db' exists."), Trimmed(await service.RunAsync("backup", "--out", "backup.db")));
        }
        else
        {
            Assert.Equal((2, string.Empty, "Use pg_dump to back up a PostgreSQL database."), Trimmed((code, output, error)));
            Assert.False(File.Exists(vendor.File("backup.db")));
        }

        string[][] refused =
        [
            ["backup"],
            ["backup", "--out"],
            ["backup", "--out", "a.db", "--out", "b.db"],
            ["setup", "--out", "a.db"],
            ["--out", "a.db", "backup"],
            ["backup", "--config", "x.json", "--config", "y.json", "--out", "a.db"],
        ];
        foreach (string[] arguments in refused)
        {
            Assert.Equal((2, string.Empty, Usage), Trimmed(await service.RunAsync(arguments)));
        }

        Assert.Equal((2, string.Empty, "The backup file path is empty."), Trimmed(await service.RunAsync("backup", "--out", string.Empty)));
        Assert.False(File.Exists(vendor.File("a.db")) || File.Exists(vendor.File("b.db")));
    }

    private static (int Code, string Out, string Error) Trimmed((int Code, string Out, string Error) run) =>
        (run.Code, run.Out.Trim(), run.Error.Trim());
}
