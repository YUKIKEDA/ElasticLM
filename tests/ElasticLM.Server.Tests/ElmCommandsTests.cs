using System.Text.Json;
using ElasticLM.Cli;
using ElasticLM.Client;
using ElasticLM.Issuing;

namespace ElasticLM.Server.Tests;

/// <summary>
/// <c>elm server</c> and <c>elm log verify</c> against a server in the same process (design §13.7, §16, §18.1).
/// </summary>
public sealed class ElmCommandsTests : IAsyncDisposable
{
    private readonly Deployment _deployment = new();

    public ValueTask DisposeAsync() => _deployment.DisposeAsync();

    /// <summary>
    /// elm server reads the administrator token from the data folder and calls each operation; the answers print as text or
    /// JSON; a refusal exits with 1, input errors with 2, and an unreachable server with 3.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server past its window with one lease.
    ///
    /// Steps:
    /// - status, status --json, leases, seal, reload, release of an unknown ID, release of the lease; status without --data
    ///   and --product; status with a data folder that holds no token; status at a port where nothing listens.
    ///
    /// Expected:
    /// - Exit 0 with the license, the lease ID, "reloaded"; exit 1 with ELM_UNKNOWN_LEASE; exit 0 "released" and no seat in
    ///   use; exit 2 twice; exit 3.
    /// </remarks>
    [Fact]
    public async Task ServerCommands_CallTheAdministrationApi()
    {
        ElmServer server = await _deployment.StartServerAsync();
        _deployment.Time.Advance(TimeSpan.FromMinutes(10));
        Lease lease = Assert
            .IsType<LeaseResult.Acquired>(
                await _deployment.Client(server.PublicUrls[0]).AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken)
            )
            .Lease;
        string[] target = ["--url", server.AdminUrl.ToString(), "--data", server.DataDirectory];

        (int code, string output, _) = Run(["server", "status", .. target]);
        Assert.Equal(0, code);
        Assert.Contains("license: lic-srv", output, StringComparison.Ordinal);
        Assert.Contains("pro: 1 of 2 seats in use", output, StringComparison.Ordinal);
        using (JsonDocument json = JsonDocument.Parse(Run(["server", "status", "--json", .. target]).Out))
        {
            Assert.Equal("lic-srv", json.RootElement.GetProperty("license").GetString());
        }

        Assert.Contains(lease.Id, Run(["server", "leases", .. target]).Out, StringComparison.Ordinal);
        Assert.Equal(0, Run(["server", "seal", .. target]).Code);
        Assert.Contains("reloaded: license lic-srv", Run(["server", "reload", .. target]).Out, StringComparison.Ordinal);

        (int unknown, _, string unknownError) = Run(["server", "release", "nope", .. target]);
        Assert.Equal(1, unknown);
        Assert.Contains("ELM_UNKNOWN_LEASE", unknownError, StringComparison.Ordinal);
        (int released, string releasedOutput, _) = Run(["server", "release", lease.Id, .. target]);
        Assert.Equal(0, released);
        Assert.Contains("released", releasedOutput, StringComparison.Ordinal);
        Assert.Equal(0, server.Engine.SeatsInUse("pro"));

        Assert.Equal(2, Run(["server", "status", "--url", server.AdminUrl.ToString()]).Code);
        Assert.Equal(2, Run(["server", "status", "--data", _deployment.NewFolder("empty")]).Code);
        Assert.Equal(3, Run(["server", "status", "--url", "http://127.0.0.1:9/", "--data", server.DataDirectory]).Code);
    }

    /// <summary>
    /// elm log verify checks a folder of usage logs and of the borrow record, and fails on a changed line.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server that lent a lease and a borrow and stopped.
    ///
    /// Steps:
    /// - Verify the data folder as usage and as borrows; verify with --json; change a line of the usage log and verify;
    ///   verify with --stream other.
    ///
    /// Expected:
    /// - Exit 0 "intact" twice and an intact JSON report; exit 1 "NOT INTACT"; exit 2.
    /// </remarks>
    [Fact]
    public async Task LogVerify_ChecksTheServerLogs()
    {
        ElmServer server = await _deployment.StartServerAsync();
        _deployment.Time.Advance(TimeSpan.FromMinutes(10));
        LicenseClient client = _deployment.Client(server.PublicUrls[0]);
        Assert.IsType<LeaseResult.Acquired>(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
        Assert.IsType<LeaseResult.Acquired>(
            await client.BorrowLeaseAsync("pro", TimeSpan.FromDays(1), cancellationToken: TestContext.Current.CancellationToken)
        );
        await server.DisposeAsync();
        string data = server.DataDirectory;

        (int code, string output, _) = Run(["log", "verify", data, "--product", "acme-cad"]);
        Assert.Equal(0, code);
        Assert.StartsWith("intact", output.Trim(), StringComparison.Ordinal);
        Assert.Equal(0, Run(["log", "verify", data, "--product", "acme-cad", "--stream", "borrows"]).Code);
        using (JsonDocument json = JsonDocument.Parse(Run(["log", "verify", data, "--product", "acme-cad", "--json"]).Out))
        {
            Assert.True(json.RootElement.GetProperty("intact").GetBoolean());
        }

        string usage = SignedLog.FindFiles(data, "usage")[0];
        string[] lines = File.ReadAllLines(usage);
        int acquired = Array.FindIndex(lines, l => l.Contains("lease.acquired", StringComparison.Ordinal));
        lines[acquired] = lines[acquired].Replace("\"n\":1", "\"n\":2", StringComparison.Ordinal);
        File.WriteAllText(usage, string.Join('\n', lines) + "\n");
        (int broken, string brokenOutput, _) = Run(["log", "verify", data, "--product", "acme-cad"]);
        Assert.Equal(1, broken);
        Assert.Contains("NOT INTACT", brokenOutput, StringComparison.Ordinal);

        Assert.Equal(2, Run(["log", "verify", data, "--product", "acme-cad", "--stream", "other"]).Code);
    }

    private (int Code, string Out, string Error) Run(string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        string pem = File.ReadAllText(_deployment.RootKeyFile);
        var host = new CliHost
        {
            Out = output,
            Error = error,
            Time = _deployment.Time,
            GetEnvironmentVariable = name => name == "ELM_TRUST_ROOT_KEYS" ? pem : null,
        };
        int code = ElmApp.Run(args, host);
        return (code, output.ToString(), error.ToString());
    }
}
