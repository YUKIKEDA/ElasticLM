using System.Text.Json;
using ElasticLM.Cli;
using ElasticLM.Client;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using ElasticLM.Service;
using ElasticLM.Service.Tests;

namespace ElasticLM.Server.Tests;

/// <summary>
/// <c>elm log verify</c> on the usage log of <c>elm-service</c> (design §16, §18.1, decision 0.95): no product, and a node
/// chosen in a folder with <c>--node</c>.
/// </summary>
public sealed class ServiceLogCommandTests
{
    /// <summary>
    /// The usage log a node of the service wrote verifies with <c>--node</c> and no product; a folder without <c>--node</c>
    /// has no file of the server's form; a bad node name or another stream is refused; a changed line is found.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - elm-service node n1 writing its usage log; a cloud-floating license with 2 seats of core.
    ///
    /// Steps:
    /// - Acquire and release a lease with a LicenseClient that has the key; stop the service. Verify the folder with --node
    ///   n1; without --node; with --node a_b; with --node n1 --stream borrows. Change "lease.acquired" in the file and verify
    ///   again.
    ///
    /// Expected:
    /// - 0 and "intact: 1 file(s), 4 event line(s)"; 2 "No usage log file was found."; 2 for the name and for the stream,
    ///   "--node must be …"; 1 and "NOT INTACT".
    /// </remarks>
    [Fact]
    public async Task Verify_ServiceLog_WithNode()
    {
        await using var test = new TestService();
        string owner = await test.SetupAsync();
        string certificate = test.WriteCertificate(
            "log.cert",
            KeyUses.License | KeyUses.Lease | KeyUses.Log,
            TestService.Start.AddDays(-1),
            TimeSpan.FromDays(365)
        );
        string folder = test.FilePath("usage");
        ElmService service = await test.StartAsync(s =>
            s with
            {
                UsageLogDirectory = folder,
                NodeName = "n1",
                SigningCertificateFiles = [certificate],
            }
        );
        HttpClient admin = test.Http(service, owner);
        string product = (await TestService.CreatedAsync(admin, "v1/admin/products", """{"code":"app","name":"app"}"""))
            .GetProperty("id")
            .GetString()!;
        await TestService.CreatedAsync(admin, $"v1/admin/products/{product}/features", """{"code":"core","name":"core"}""");
        string policy = await TestService.PolicyAsync(admin, product, "\"floating\":\"cloud\"");
        string key = (
            await TestService.CreatedAsync(
                admin,
                "v1/admin/licenses",
                $$"""{"policyId":"{{policy}}","name":"n","features":[{"code":"core","seats":2}]}"""
            )
        )
            .GetProperty("key")
            .GetString()!;
        var options = new LicenseClientOptions
        {
            ProductId = "app",
            ServerUrl = service.Url,
            LicenseKey = key,
            Trust = TrustStore.Create([File.ReadAllText(test.FilePath("root.pub.pem"))], []),
            DataDirectory = test.FilePath("client"),
            TimeProvider = test.Time,
        };
        Assert.True(LicenseClient.TryCreate(options, out LicenseClient? client, out LicenseError? error), error?.Message);
        LeaseResult acquired = await client!.AcquireLeaseAsync("core", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await client.ReleaseLeaseAsync(((LeaseResult.Acquired)acquired).Lease, TestContext.Current.CancellationToken));
        await client.DisposeAsync();
        await service.StopAsync();

        (int code, string output, string fault) = Run(test, ["log", "verify", folder, "--node", "n1"]);
        Assert.True(code == 0, output + fault);
        Assert.Equal("intact: 1 file(s), 4 event line(s)", output.Trim());
        (code, _, fault) = Run(test, ["log", "verify", folder]);
        Assert.Equal((2, "error: No usage log file was found."), (code, fault.Trim()));
        const string NodeRule = "error: --node must be 1 to 64 characters of A-Z, a-z, 0-9, and '-', for the usage stream.";
        Assert.Equal((2, NodeRule), Trimmed(Run(test, ["log", "verify", folder, "--node", "a_b"])));
        Assert.Equal((2, NodeRule), Trimmed(Run(test, ["log", "verify", folder, "--node", "n1", "--stream", "borrows"])));

        string file = SignedLog.FindFiles(folder, "usage-n1").Single();
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"lease.acquired\"", "\"lease.acquireX\"", StringComparison.Ordinal));
        (code, output, _) = Run(test, ["log", "verify", folder, "--node", "n1"]);
        Assert.Equal(1, code);
        Assert.Contains("NOT INTACT", output, StringComparison.Ordinal);
    }

    private static (int Code, string Text) Trimmed((int Code, string Out, string Error) result) => (result.Code, result.Error.Trim());

    private static (int Code, string Out, string Error) Run(TestService test, string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        string pem = File.ReadAllText(test.FilePath("root.pub.pem"));
        var host = new CliHost
        {
            Out = output,
            Error = error,
            Time = test.Time,
            GetEnvironmentVariable = name => name == "ELM_TRUST_ROOT_KEYS" ? pem : null,
        };
        int code = ElmApp.Run(args, host);
        return (code, output.ToString(), error.ToString());
    }
}
