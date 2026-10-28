using System.Net;
using System.Text.Json;
using ElasticLM.Cli;
using ElasticLM.Client;
using ElasticLM.Licensing;
using ElasticLM.Service;
using ElasticLM.Service.Tests;
using Microsoft.Extensions.Logging;

namespace ElasticLM.Server.Tests;

/// <summary>
/// The server license activated and refreshed against a real <c>elm-service</c> (design §13.7, M4c3): by <c>elm server</c>
/// in the server's data folder, and by the server's own refresh.
/// </summary>
public sealed class ServerLicenseTests : IAsyncDisposable
{
    private const string Product = "acme-cad";
    private readonly Deployment _deployment = new();
    private readonly List<IAsyncDisposable> _owned = [];

    public async ValueTask DisposeAsync()
    {
        foreach (IAsyncDisposable owned in Enumerable.Reverse(_owned))
        {
            await owned.DisposeAsync();
        }

        await _deployment.DisposeAsync();
    }

    /// <summary>
    /// A server without a license makes its key and points at elm server activate; the command activates it while the
    /// server is stopped; the started server refreshes on demand and on its own, writing the file and reloading it; it is
    /// deactivated only once stopped.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - elm-service with product acme-cad and a server policy (tokens of 2 hours, refresh window 1 hour); a license key of it.
    ///
    /// Steps:
    /// - Start a server with no license; activate with elm server; start the server with service.url; read the status; refresh
    ///   with elm server; move the clock an hour and a minute; read the usage log; deactivate; stop the server; deactivate
    ///   naming a file put there by hand; look for the activation in the service.
    ///
    /// Expected:
    /// - The start fails naming elm server activate; activate exits 0 saying the server is not running; the server starts with
    ///   sequence 1 and autoRefresh true; refresh exits 0, the server reloaded sequence 2; then the server's own refresh brings
    ///   sequence 3 and one license.refreshed line; deactivate exits 1 "Stop elm-server …" while it runs, then 0, the hand file
    ///   stays, and the service has no activation of the license.
    /// </remarks>
    [Fact]
    public async Task Online_ActivateRefreshDeactivate_AroundTheServer()
    {
        (TestService test, ElmService service, string key) = await ServiceAsync("\"tokenLifetime\":7200,\"refreshWindow\":3600");
        string folder = _deployment.NewFolder("server");
        string license = Path.Combine(folder, "server.elmlic");
        ServerSettings settings = Settings(test, folder) with { ServiceUrl = service.Url };

        ServerStartException none = await Assert.ThrowsAsync<ServerStartException>(() => StartAsync(test, settings));
        Assert.Contains("elm server activate", none.Message, StringComparison.Ordinal);
        (int code, string output, string error) = Run(
            test,
            ["server", "activate", key, .. Common(folder), "--service", service.Url.ToString(), "--license", license]
        );
        Assert.True(code == 0, error);
        Assert.Contains("The server", output, StringComparison.Ordinal);

        ElmServer server = await StartAsync(test, settings);
        Assert.Equal(1L, server.Engine.Server.License.Sequence);
        Assert.True((await StatusAsync(server)).GetProperty("autoRefresh").GetBoolean());

        (code, output, error) = Run(
            test,
            ["server", "refresh", .. Common(folder), "--service", service.Url.ToString(), "--license", license, "--url", server.AdminUrl.ToString()]
        );
        Assert.True(code == 0, error);
        Assert.Contains("The server reloaded the license.", output, StringComparison.Ordinal);
        Assert.Equal(2L, server.Engine.Server.License.Sequence);

        for (int minute = 0; minute < 61 && server.Engine.Server.License.Sequence != 3; minute++)
        {
            test.Time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await Deployment.WaitUntilAsync(() => server.Engine.Server.License.Sequence == 3);
        using (HttpClient seal = Deployment.AdminHttp(server))
        {
            // Lines reach the disk when they are sealed (design §18.1).
            (await seal.PostAsync("admin/v1/log/seal", null, TestContext.Current.CancellationToken)).Dispose();
        }

        // One line: the reload after elm server refresh found the kept file no newer than the one in use (design §13.7).
        Assert.Single(UsageLines(folder), l => l.Contains("\"license.refreshed\"", StringComparison.Ordinal));

        (code, _, error) = Run(
            test,
            [
                "server",
                "deactivate",
                .. Common(folder),
                "--service",
                service.Url.ToString(),
                "--license",
                license,
                "--url",
                server.AdminUrl.ToString(),
            ]
        );
        Assert.Equal((1, "error: Stop elm-server before deactivating its license."), (code, error.Trim()));
        await server.DisposeAsync();

        // A file put there by hand is not the activation's, so it stays (design §13.7).
        string hand = Path.Combine(folder, "hand.elmlic");
        File.WriteAllText(hand, "{}");
        (code, output, error) = Run(test, ["server", "deactivate", .. Common(folder), "--service", service.Url.ToString(), "--license", hand]);
        Assert.True(code == 0, error);
        Assert.True(File.Exists(hand));
        Assert.Equal(HttpStatusCode.NotFound, await ActivationStatusAsync(test, service));
    }

    /// <summary>
    /// A server without service.url is activated, refreshed, and deactivated by files, through elm server.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - elm-service with a server policy; a server data folder whose key the server made.
    ///
    /// Steps:
    /// - Make an activation request, process it, import the answer; start the server; make a refresh request, process it,
    ///   import it with the server running; make a deactivation proof while it runs; stop it; make the proof, process it,
    ///   forget it.
    ///
    /// Expected:
    /// - Each command exits 0; the server starts with sequence 1, autoRefresh false, and reloads sequence 2; the proof is
    ///   refused while the server runs; the service takes the proof with 204.
    /// </remarks>
    [Fact]
    public async Task Offline_ActivateRefreshDeactivate_ByFiles()
    {
        (TestService test, ElmService service, string key) = await ServiceAsync(string.Empty);
        string folder = _deployment.NewFolder("server");
        string license = Path.Combine(folder, "server.elmlic");
        ServerSettings settings = Settings(test, folder);
        await Assert.ThrowsAsync<ServerStartException>(() => StartAsync(test, settings));

        string request = Path.Combine(folder, "a.elmreq");
        Assert.Equal(0, Run(test, ["server", "activate-request", "--key", key, .. Common(folder), "--out", request]).Code);
        string answer = await ProcessAsync(service, request);
        Assert.Equal(0, Run(test, ["server", "import", answer, .. Common(folder), "--license", license]).Code);
        ElmServer server = await StartAsync(test, settings);
        Assert.Equal(1L, server.Engine.Server.License.Sequence);
        Assert.False((await StatusAsync(server)).GetProperty("autoRefresh").GetBoolean());

        string refresh = Path.Combine(folder, "r.elmreq");
        string admin = server.AdminUrl.ToString();
        Assert.Equal(0, Run(test, ["server", "refresh-request", .. Common(folder), "--out", refresh, "--url", admin]).Code);
        (int code, string output, string error) = Run(
            test,
            ["server", "import", await ProcessAsync(service, refresh), .. Common(folder), "--license", license, "--url", admin]
        );
        Assert.True(code == 0, error);
        Assert.Contains("The server reloaded the license.", output, StringComparison.Ordinal);
        Assert.Equal(2L, server.Engine.Server.License.Sequence);

        string proof = Path.Combine(folder, "p.elmdeact");
        Assert.Equal(1, Run(test, ["server", "deactivate-proof", .. Common(folder), "--out", proof, "--url", admin]).Code);
        await server.DisposeAsync();
        Assert.Equal(0, Run(test, ["server", "deactivate-proof", .. Common(folder), "--out", proof, "--license", license]).Code);
        Assert.False(File.Exists(license));
        using (var http = new HttpClient { BaseAddress = service.Url })
        {
            using HttpResponseMessage taken = await http.PostAsync(
                "v1/offline",
                new StringContent(File.ReadAllText(proof)),
                TestContext.Current.CancellationToken
            );
            Assert.Equal(HttpStatusCode.NoContent, taken.StatusCode);
        }

        Assert.Equal(0, Run(test, ["server", "forget-proof", .. Common(folder)]).Code);
    }

    /// <summary>
    /// The commands refuse what the design refuses, and the server catches up with a kept file at its start.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - elm-service with a server policy and a standard policy, a key of each; data folders whose key the server made, and one
    ///   without a key.
    ///
    /// Steps:
    /// - Activate in the folder without a key; activate with the standard key; activate with the server key; start the server
    ///   with service.url; refresh writing another path; make a refresh request; stop the server; start it again; start a
    ///   server with service.url and a license path in a missing folder; refresh in a folder whose kept activation is standard.
    ///
    /// Expected:
    /// - 2 "… start elm-server once …"; 1 ELM_WRONG_KIND; 0; 1 "… reads another license file …"; 1 "… refreshes its license
    ///   online …"; the restarted server uses sequence 2 from the kept file; the start fails "… cannot be written …"; 1 "… not a
    ///   server license …".
    /// </remarks>
    [Fact]
    public async Task Refusals_AndTheKeptFile_AtTheStart()
    {
        (TestService test, ElmService service, string key) = await ServiceAsync(string.Empty);
        string standard = await StandardKeyAsync(test, service);
        string url = service.Url.ToString();

        string bare = _deployment.NewFolder("bare");
        (int code, _, string error) = Run(
            test,
            ["server", "activate", key, .. Common(bare), "--service", url, "--license", Path.Combine(bare, "s.elmlic")]
        );
        Assert.Equal((2, "error: The server's installation key does not exist; start elm-server once to make it."), (code, error.Trim()));

        string folder = _deployment.NewFolder("server");
        string license = Path.Combine(folder, "server.elmlic");
        ServerSettings settings = Settings(test, folder) with { ServiceUrl = service.Url };
        await Assert.ThrowsAsync<ServerStartException>(() => StartAsync(test, settings));
        (code, _, error) = Run(test, ["server", "activate", standard, .. Common(folder), "--service", url, "--license", license]);
        Assert.Equal(1, code);
        Assert.StartsWith("error: ELM_WRONG_KIND", error, StringComparison.Ordinal);
        Assert.Equal(0, Run(test, ["server", "activate", key, .. Common(folder), "--service", url, "--license", license]).Code);

        // Without service.url the server does not read the kept activation, so the path it reads is all it has (R6).
        ElmServer server = await StartAsync(test, Settings(test, folder));
        string admin = server.AdminUrl.ToString();
        (code, _, error) = Run(
            test,
            ["server", "refresh", .. Common(folder), "--service", url, "--license", Path.Combine(folder, "other.elmlic"), "--url", admin]
        );
        Assert.Equal((1, "error: The server reads another license file than --license; pass its setting license."), (code, error.Trim()));
        await server.DisposeAsync();
        _owned.Remove(server);

        ElmServer restarted = await StartAsync(test, settings);
        Assert.Equal(2L, restarted.Engine.Server.License.Sequence);
        (code, _, error) = Run(
            test,
            ["server", "refresh-request", .. Common(folder), "--out", Path.Combine(folder, "r.elmreq"), "--url", restarted.AdminUrl.ToString()]
        );
        Assert.Equal((1, "error: ELM_INVALID_STATE: The server refreshes its license online; use elm server refresh."), (code, error.Trim()));
        await restarted.DisposeAsync();
        _owned.Remove(restarted);

        ServerSettings unwritable = settings with { LicensePath = Path.Combine(folder, "missing", "server.elmlic") };
        ServerStartException start = await Assert.ThrowsAsync<ServerStartException>(() => StartAsync(test, unwritable));
        Assert.Contains("cannot be written", start.Message, StringComparison.Ordinal);

        string shared = _deployment.NewFolder("shared");
        await Assert.ThrowsAsync<ServerStartException>(() => StartAsync(test, Settings(test, shared)));
        StoredActivation kept = new(Guid.CreateVersion7().ToString("D"), 1, "standard", "{}"u8.ToArray(), []);
        new ActivationStore(new InstallationKeyStore(Product, KeyScope.Machine, shared).Directory, test.Time).Write(kept);
        (code, _, error) = Run(test, ["server", "refresh", .. Common(shared), "--service", url, "--license", Path.Combine(shared, "s.elmlic")]);
        Assert.Equal(
            (1, "error: ELM_INVALID_STATE: The kept activation is not a server license; give the server its own dataDirectory."),
            (code, error.Trim())
        );
    }

    // The license's activations in the service, through its administration API: 404 when the one there was is gone.
    private async Task<HttpStatusCode> ActivationStatusAsync(TestService test, ElmService service)
    {
        _ = test;
        _ = service;
        JsonElement page = await TestService.GetAsync(_admin!, $"v1/admin/licenses/{_licenseId}/activations");
        return page.GetProperty("items").GetArrayLength() == 0 ? HttpStatusCode.NotFound : HttpStatusCode.OK;
    }

    /// <summary>
    /// A server started with a license issued by hand picks up an activation made while it runs, after the reload makes its
    /// client again; then a file put in place by hand is used until the activation's refresh brings its file back (design
    /// §13.7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - elm-service with a server policy (tokens of 2 hours, refresh window 1 hour); a server with service.url and a license
    ///   issued by hand for its key.
    ///
    /// Steps:
    /// - Read the status; activate with elm server while it runs; read the status; write the hand license again and reload;
    ///   move the clock an hour and a minute.
    ///
    /// Expected:
    /// - autoRefresh false; the activation's license in use and autoRefresh true; the hand license in use, autoRefresh still
    ///   true; then the activation's license again, with sequence 2.
    /// </remarks>
    [Fact]
    public async Task HandFile_AndAnActivationMadeWhileRunning()
    {
        (TestService test, ElmService service, string key) = await ServiceAsync("\"tokenLifetime\":7200,\"refreshWindow\":3600");
        string folder = _deployment.NewFolder("server");
        string license = Path.Combine(folder, "server.elmlic");
        ServerSettings settings = Settings(test, folder) with { ServiceUrl = service.Url };
        await Assert.ThrowsAsync<ServerStartException>(() => StartAsync(test, settings));
        byte[] hand = HandLicense(test, Deployment.ServerKeyId(folder));
        File.WriteAllBytes(license, hand);
        ElmServer server = await StartAsync(test, settings);
        Assert.False((await StatusAsync(server)).GetProperty("autoRefresh").GetBoolean());

        (int code, _, string error) = Run(
            test,
            [
                "server",
                "activate",
                key,
                .. Common(folder),
                "--service",
                service.Url.ToString(),
                "--license",
                license,
                "--url",
                server.AdminUrl.ToString(),
            ]
        );
        Assert.True(code == 0, error);
        string activated = server.Engine.Server.License.LicenseId;
        Assert.NotEqual("lic-hand", activated);
        Assert.True((await StatusAsync(server)).GetProperty("autoRefresh").GetBoolean());

        File.WriteAllBytes(license, hand);
        using (HttpClient admin = Deployment.AdminHttp(server))
        {
            using HttpResponseMessage reloaded = await admin.PostAsync("admin/v1/license/reload", null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, reloaded.StatusCode);
        }

        Assert.Equal("lic-hand", server.Engine.Server.License.LicenseId);
        Assert.True((await StatusAsync(server)).GetProperty("autoRefresh").GetBoolean());
        for (int minute = 0; minute < 61 && server.Engine.Server.License.LicenseId != activated; minute++)
        {
            test.Time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await Deployment.WaitUntilAsync(() => server.Engine.Server.License.LicenseId == activated);
        Assert.Equal(2L, server.Engine.Server.License.Sequence);
    }

    // A server license issued by hand (elm license issue's way) for a server key, signed by the service's signing key.
    private static byte[] HandLicense(TestService test, string keyId)
    {
        var spec = new Issuing.LicenseSpec
        {
            LicenseId = "lic-hand",
            ProductId = Product,
            Kind = LicenseKind.Server,
            Features = [new Issuing.FeatureSpec("pro", null, Seats: 2)],
            ExpiresAt = test.Time.GetUtcNow().AddDays(30),
            GracePeriod = TimeSpan.FromDays(7),
            InstallationKeyThumbprint = keyId,
            LeaseDuration = TimeSpan.FromMinutes(10),
            BorrowLimit = TimeSpan.Zero,
            OveragePercent = 0,
            CloneStrategy = CloneStrategy.Deny,
            BorrowReturnLimit = 2,
        };
        string certificate = File.ReadAllText(
            test.WriteCertificate("hand.cert", Issuing.KeyUses.License, test.Time.GetUtcNow().AddDays(-1), TimeSpan.FromDays(365))
        );
        return Issuing.LicenseIssuer.Issue(test.SigningKey, [certificate], spec, test.Time).File;
    }

    private static string[] Common(string folder) => ["--product", Product, "--data", folder];

    // The server keeps today's file open, so it is read with the sharing a second reader needs.
    private static IEnumerable<string> UsageLines(string folder) =>
        Directory
            .GetFiles(folder, "usage*.jsonl")
            .SelectMany(f =>
            {
                using var stream = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n');
            });

    private static async Task<JsonElement> StatusAsync(ElmServer server)
    {
        using HttpClient admin = Deployment.AdminHttp(server);
        using JsonDocument status = JsonDocument.Parse(await admin.GetStringAsync("admin/v1/status", TestContext.Current.CancellationToken));
        return status.RootElement.Clone();
    }

    private static async Task<string> ProcessAsync(ElmService service, string requestPath)
    {
        using var http = new HttpClient { BaseAddress = service.Url };
        using HttpResponseMessage response = await http.PostAsync(
            "v1/offline",
            new StringContent(File.ReadAllText(requestPath)),
            TestContext.Current.CancellationToken
        );
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        string answer = requestPath + ".elmresp";
        File.WriteAllText(answer, body);
        return answer;
    }

    // elm-service with product acme-cad (feature pro) and a server policy, and a license key of it.
    private async Task<(TestService Test, ElmService Service, string Key)> ServiceAsync(string members)
    {
        var test = new TestService();
        _owned.Add(test);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        string product = (await TestService.CreatedAsync(admin, "v1/admin/products", $$"""{"code":"{{Product}}","name":"cad"}"""))
            .GetProperty("id")
            .GetString()!;
        await TestService.CreatedAsync(admin, $"v1/admin/products/{product}/features", """{"code":"pro","name":"pro"}""");
        string policy = await TestService.PolicyAsync(
            admin,
            product,
            "\"licenseKind\":\"server\",\"leaseDuration\":600,\"maxActivations\":3" + (members.Length > 0 ? "," + members : string.Empty)
        );
        JsonElement created = await TestService.CreatedAsync(
            admin,
            "v1/admin/licenses",
            $$"""{"policyId":"{{policy}}","name":"n","features":[{"code":"pro","seats":2}]}"""
        );
        _admin = admin;
        _product = product;
        _licenseId = created.GetProperty("id").GetString();
        return (test, service, created.GetProperty("key").GetString()!);
    }

    private HttpClient? _admin;
    private string? _product;
    private string? _licenseId;

    private async Task<string> StandardKeyAsync(TestService test, ElmService service)
    {
        _ = test;
        _ = service;
        string policy = await TestService.PolicyAsync(_admin!, _product!, "\"maxActivations\":3");
        JsonElement created = await TestService.CreatedAsync(
            _admin!,
            "v1/admin/licenses",
            $$"""{"policyId":"{{policy}}","name":"s","features":[{"code":"pro"}]}"""
        );
        return created.GetProperty("key").GetString()!;
    }

    private ServerSettings Settings(TestService test, string folder) =>
        _deployment.Settings(folder) with
        {
            TrustRootKeyFiles = [test.FilePath("root.pub.pem")],
        };

    private async Task<ElmServer> StartAsync(TestService test, ServerSettings settings)
    {
        var host = new ServerHostOptions
        {
            Time = test.Time,
            Environment = _ => null,
            MinimumLogLevel = LogLevel.Warning,
        };
        ElmServer server = await ElmServer.StartAsync(settings, host);
        _owned.Add(server);
        return server;
    }

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
