using System.Net;
using System.Net.Sockets;
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

        // A file put there by hand is not the activation's, so it stays (design §13.7). The stopped server's own address is
        // named, where nothing listens now: at the default one, another server of this machine could answer.
        string hand = Path.Combine(folder, "hand.elmlic");
        File.WriteAllText(hand, "{}");
        (code, output, error) = Run(
            test,
            ["server", "deactivate", .. Common(folder), "--service", service.Url.ToString(), "--license", hand, "--url", server.AdminUrl.ToString()]
        );
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
        Assert.Equal(0, Run(test, ["server", "deactivate-proof", .. Common(folder), "--out", proof, "--license", license, "--url", admin]).Code);
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

    /// <summary>
    /// When the running server refuses the file that elm server activate wrote, the command prints one line with the code and
    /// the server's reason, and the server keeps its license.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - elm-service with a server policy; a server data folder whose key a first start made; a server running there with a
    ///   license issued by hand for that key, on a clock that the test can set.
    ///
    /// Steps:
    /// - Set the server's clock two days on and reload its license, so that its clock guard sees the later time; set the
    ///   clock back; activate with elm server, writing to the server's license path and naming its administration URL.
    ///
    /// Expected:
    /// - The reload answers 200. The activation exits 1, and standard error is the one line "error: the server kept its
    ///   license: ELM_CLOCK_TAMPERED: The server license does not verify: The clock has been set back. This server's
    ///   installation key is " followed by the server's thumbprint and a period. Standard output names the written file. The
    ///   server still uses the hand license, and the file at its license path is the new one.
    /// </remarks>
    [Fact]
    public async Task Activate_RunningServerRefusesTheFile_PrintsTheKeptLine()
    {
        (TestService test, ElmService service, string key) = await ServiceAsync(string.Empty);
        string folder = _deployment.NewFolder("server");
        string license = Path.Combine(folder, "server.elmlic");
        ServerSettings settings = Settings(test, folder);
        await Assert.ThrowsAsync<ServerStartException>(() => StartAsync(test, settings));
        byte[] hand = HandLicense(test, Deployment.ServerKeyId(folder));
        File.WriteAllBytes(license, hand);
        var clock = new SettableTime(test.Time.GetUtcNow());
        ElmServer server = await StartAsync(test, settings, clock);

        // elm passes the new file, since it keeps no clock state; the server's clock guard remembers the later time.
        clock.Now += TimeSpan.FromDays(2);
        using (HttpClient admin = Deployment.AdminHttp(server))
        {
            using HttpResponseMessage reloaded = await admin.PostAsync("admin/v1/license/reload", null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, reloaded.StatusCode);
        }

        clock.Now -= TimeSpan.FromDays(2);
        (int code, string output, string error) = Run(
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

        Assert.Equal(
            "error: the server kept its license: ELM_CLOCK_TAMPERED: The server license does not verify: The clock has been set "
                + $"back. This server's installation key is {Deployment.ServerKeyId(folder)}.",
            error.Trim()
        );
        Assert.Equal(1, code);
        Assert.Contains(license, output, StringComparison.Ordinal);
        Assert.Equal("lic-hand", server.Engine.Server.License.LicenseId);
        Assert.NotEqual(hand, File.ReadAllBytes(license));
    }

    /// <summary>
    /// An answer of the administration API that is not the server's status does not count as "no server runs": the
    /// commands that a running server restricts stop with that answer.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - elm-service with a server policy; a running server with a license issued by hand; a second data folder that holds
    ///   the administrator token of another server, which ran there and has stopped.
    ///
    /// Steps:
    /// - With the second folder and the running server's administration URL, run elm server deactivate, deactivate-proof,
    ///   activate-request, and refresh-request.
    ///
    /// Expected:
    /// - Each exits 1 with the one line "error: ELM_UNAUTHORIZED: The administrator token is missing or wrong." on standard
    ///   error. The two request commands and deactivate-proof write no file, and deactivate leaves the license file.
    /// </remarks>
    [Fact]
    public async Task ServerCommands_AnotherServersToken_StopWithTheApisAnswer()
    {
        (TestService test, ElmService service, string key) = await ServiceAsync(string.Empty);
        (ElmServer server, string other) = await RunningServerAndAnotherFolderAsync(test);
        string url = server.AdminUrl.ToString();
        string made = Path.Combine(other, "made.file");
        string[][] commands =
        [
            [
                "server",
                "deactivate",
                .. Common(other),
                "--service",
                service.Url.ToString(),
                "--license",
                Path.Combine(other, "server.elmlic"),
                "--url",
                url,
            ],
            ["server", "deactivate-proof", .. Common(other), "--out", made, "--url", url],
            ["server", "activate-request", "--key", key, .. Common(other), "--out", made, "--url", url],
            ["server", "refresh-request", .. Common(other), "--out", made, "--url", url],
        ];

        foreach (string[] command in commands)
        {
            (int code, _, string error) = Run(test, command);

            Assert.Equal((1, "error: ELM_UNAUTHORIZED: The administrator token is missing or wrong."), (code, error.Trim()));
            if (command[1] == "deactivate")
            {
                Assert.True(File.Exists(Path.Combine(other, "server.elmlic")));
            }
            else
            {
                Assert.False(File.Exists(made), command[1]);
            }
        }
    }

    /// <summary>
    /// When the administration API refuses the caller after elm server activate, refresh, or import wrote the file, the
    /// command prints that answer, not that the server kept its license: the server said nothing about the file.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - elm-service with a server policy; a running server with a license issued by hand; a second data folder with its
    ///   own installation key and the administrator token of another server, which ran there and has stopped.
    ///
    /// Steps:
    /// - For the second folder, writing to that folder's license path and naming the running server's administration URL:
    ///   activate with elm server; refresh; make a refresh request against a port where nothing listens, process it, and
    ///   import the answer.
    ///
    /// Expected:
    /// - Each of the three exits 1 with the one line "error: ELM_UNAUTHORIZED: The administrator token is missing or wrong.
    ///   The license file is written; the server did not confirm a reload." on standard error, names the written file on
    ///   standard output, and leaves a file that differs from the one before; the first replaces the hand
    ///   license.
    /// </remarks>
    [Fact]
    public async Task ActivateRefreshImport_AdministrationApiRefusesTheCaller_PrintItsAnswer()
    {
        const string Line =
            "error: ELM_UNAUTHORIZED: The administrator token is missing or wrong. The license file is written; the server did not "
            + "confirm a reload.";
        (TestService test, ElmService service, string key) = await ServiceAsync(string.Empty);
        (ElmServer server, string other) = await RunningServerAndAnotherFolderAsync(test);
        string license = Path.Combine(other, "server.elmlic");
        string request = Path.Combine(other, "refresh.elmreq");
        string[] tail = [.. Common(other), "--license", license, "--url", server.AdminUrl.ToString()];
        byte[] before = File.ReadAllBytes(license);

        (int Code, string Out, string Error) activated = Run(test, ["server", "activate", key, "--service", service.Url.ToString(), .. tail]);
        byte[] first = File.ReadAllBytes(license);
        (int Code, string Out, string Error) refreshed = Run(test, ["server", "refresh", "--service", service.Url.ToString(), .. tail]);
        byte[] second = File.ReadAllBytes(license);
        (int made, _, string makeError) = Run(
            test,
            ["server", "refresh-request", .. Common(other), "--out", request, "--url", $"http://127.0.0.1:{ClosedPort()}/"]
        );
        Assert.True(made == 0, makeError);
        (int Code, string Out, string Error) imported = Run(test, ["server", "import", await ProcessAsync(service, request), .. tail]);
        byte[] third = File.ReadAllBytes(license);

        foreach ((int code, string output, string error) in new[] { activated, refreshed, imported })
        {
            Assert.Equal((1, Line), (code, error.Trim()));
            Assert.Contains(license, output, StringComparison.Ordinal);
        }

        Assert.NotEqual(before, first);
        Assert.NotEqual(first, second);
        Assert.NotEqual(second, third);
    }

    /// <summary>
    /// An answer of the administration port that is not the server's, as another program on that port gives, stops the
    /// command with the status code; after a written file, the line also says that the file is written.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - elm-service with a server policy; a data folder that holds a stopped server's installation key and administrator
    ///   token (the helper also leaves a server running, which this test does not use); a listener on a loopback port that
    ///   answers one request with a given answer.
    ///
    /// Steps:
    /// - With that folder and the listener as the administration URL: run elm server deactivate-proof against 404 with no
    ///   body, against 502 with an HTML body, against 200 with an empty JSON object, against 302 to a port where nothing
    ///   listens, against 200 with a JSON array, and against 500 with a body that holds autoRefresh and licenseToken; then
    ///   activate with elm server against the 404, refresh against the 500, and run elm server status against the 302.
    ///
    /// Expected:
    /// - deactivate-proof exits 1 each time with the one line "error: The server's administration API answered 404." (502,
    ///   200, 302, 200, 500) and writes no file. The activation and the refresh exit 1 with "error: The server's administration API
    ///   answered 404. The license file is written; the server did not confirm a reload." (500) and name the written file
    ///   on standard output.
    ///   elm server status exits 1 with "error: ?:", the form of that command for an answer without a code.
    /// </remarks>
    [Fact]
    public async Task ServerCommands_AnswerThatIsNotTheServers_StopWithTheStatusCode()
    {
        (TestService test, ElmService service, string key) = await ServiceAsync(string.Empty);
        (_, string other) = await RunningServerAndAnotherFolderAsync(test);
        string made = Path.Combine(other, "made.file");
        string license = Path.Combine(other, "server.elmlic");

        // The last answer: followed, the redirect would end at a port where nothing listens, and the command would take the
        // server as not running.
        (string Answer, int Status)[] answers =
        [
            ("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", 404),
            ("HTTP/1.1 502 Bad Gateway\r\nContent-Type: text/html\r\nContent-Length: 13\r\nConnection: close\r\n\r\n<html></html>", 502),
            ("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}", 200),
            ($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{ClosedPort()}/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", 302),
            ("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n[]", 200),
            (
                "HTTP/1.1 500 Internal Server Error\r\nContent-Type: application/json\r\nContent-Length: 40\r\nConnection: close\r\n\r\n"
                    + "{\"autoRefresh\":false,\"licenseToken\":\"x\"}",
                500
            ),
        ];

        foreach ((string answer, int status) in answers)
        {
            (int code, _, string error) = await RunAgainstAsync(
                test,
                answer,
                url => ["server", "deactivate-proof", .. Common(other), "--out", made, "--url", url]
            );

            Assert.Equal((1, $"error: The server's administration API answered {status}."), (code, error.Trim()));
            Assert.False(File.Exists(made));
        }

        // The folder is activated once; the second file comes from a refresh, which asks for the reload the same way.
        string[] first = ["server", "activate", key];
        string[] second = ["server", "refresh"];
        foreach ((string[] head, (string answer, int status)) in new[] { (first, answers[0]), (second, answers[5]) })
        {
            (int activated, string output, string activateError) = await RunAgainstAsync(
                test,
                answer,
                url => [.. head, .. Common(other), "--service", service.Url.ToString(), "--license", license, "--url", url]
            );

            Assert.Equal(
                (
                    1,
                    $"error: The server's administration API answered {status}. The license file is written; the server did not confirm a "
                        + "reload."
                ),
                (activated, activateError.Trim())
            );
            Assert.Contains(license, output, StringComparison.Ordinal);
        }

        // elm server status and its sibling commands do not follow the redirect either: followed, it would end in "The
        // server cannot be reached" and exit code 3.
        (int status302, _, string statusError) = await RunAgainstAsync(
            test,
            answers[3].Answer,
            url => ["server", "status", .. Common(other), "--url", url]
        );

        Assert.Equal((1, "error: ?:"), (status302, statusError.Trim()));
    }

    // Runs elm against a listener on a loopback port that reads one request and answers it with the given bytes. The
    // listener stops after the run, so a command that never calls fails on its assertions instead of waiting.
    private static async Task<(int Code, string Out, string Error)> RunAgainstAsync(TestService test, string answer, Func<string, string[]> command)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<bool> answered = Task.Run(async () =>
        {
            try
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                NetworkStream stream = client.GetStream();
                var received = new List<byte>();
                var buffer = new byte[4096];
                while (!System.Text.Encoding.ASCII.GetString([.. received]).Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    int read = await stream.ReadAsync(buffer);
                    if (read == 0)
                    {
                        return false;
                    }

                    received.AddRange(buffer.AsSpan(0, read));
                }

                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(answer));
                return true;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException)
            {
                return false;
            }
        });

        (int Code, string Out, string Error) result = Run(test, command($"http://127.0.0.1:{port}/"));
        listener.Stop();
        Assert.True(await answered, "The command did not call the administration API.");
        return result;
    }

    // A loopback port where nothing listens: a listener takes it and stops.
    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // A running server, and the data folder of a second server that ran with a hand license and has stopped: the folder
    // holds that server's installation key, license, and administrator token.
    private async Task<(ElmServer Running, string OtherFolder)> RunningServerAndAnotherFolderAsync(TestService test)
    {
        var folders = new string[2];
        ElmServer? running = null;
        for (int i = 0; i < folders.Length; i++)
        {
            folders[i] = _deployment.NewFolder(i == 0 ? "server" : "other");
            ServerSettings settings = Settings(test, folders[i]);
            await Assert.ThrowsAsync<ServerStartException>(() => StartAsync(test, settings));
            File.WriteAllBytes(Path.Combine(folders[i], "server.elmlic"), HandLicense(test, Deployment.ServerKeyId(folders[i])));
            ElmServer server = await StartAsync(test, settings);
            if (i == 0)
            {
                running = server;
            }
            else
            {
                await server.DisposeAsync();
            }
        }

        return (running!, folders[1]);
    }

    // A clock that a test sets forward and back; FakeTimeProvider refuses to go back.
    private sealed class SettableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
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

    // The server reads the service's clock unless the test gives it one of its own.
    private async Task<ElmServer> StartAsync(TestService test, ServerSettings settings, TimeProvider? time = null)
    {
        var host = new ServerHostOptions
        {
            Time = time ?? test.Time,
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
