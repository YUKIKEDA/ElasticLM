using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ElasticLM.Cli;
using ElasticLM.Client;
using ElasticLM.Licensing;
using ElasticLM.Native.Tests;
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
    private const string NotFound = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
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
    ///   with elm server; move the clock an hour and wait for the server's refresh; read the usage log; deactivate; stop the server; deactivate
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
        string folder = _deployment.NewFolder();
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

        (code, output, error) = Run(test, ["server", "refresh", .. Common(folder), "--service", service.Url.ToString(), "--license", license]);
        Assert.True(code == 0, error);
        Assert.Contains("The server reloaded the license.", output, StringComparison.Ordinal);
        Assert.Equal(2L, server.Engine.Server.License.Sequence);

        // The server's refresh is due an hour after the token of sequence 2 was issued, and the clock has not moved since.
        // The clock then stands still: it also counts the 30 seconds the server's client gives its request (design §12.6.2),
        // so a move while the refresh is on its way would end that request.
        test.Time.Advance(TimeSpan.FromHours(1));
        await Deployment.WaitUntilAsync(() => server.Engine.Server.License.Sequence == 3);
        using (HttpClient seal = Deployment.AdminHttp(server))
        {
            // Lines reach the disk when they are sealed (design §18.1).
            (await seal.PostAsync("admin/v1/log/seal", null, TestContext.Current.CancellationToken)).Dispose();
        }

        // One line: the reload after elm server refresh found the kept file no newer than the one in use (design §13.7).
        Assert.Single(UsageLines(folder), l => l.Contains("\"license.refreshed\"", StringComparison.Ordinal));

        (code, _, error) = Run(test, ["server", "deactivate", .. Common(folder), "--service", service.Url.ToString(), "--license", license]);
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
    /// The server's own refresh, answered by a relay on the path with the service's genuine answer to an earlier refresh,
    /// keeps the license in use and logs ELM_REQUEST_MISMATCH; the next refresh through the service goes on from it (design
    /// §12.6.2, §13.7, V1d5a).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - elm-service with a server policy (tokens of 2 hours, refresh window 1 hour) whose public URL is a relay that keeps
    ///   the answer of the last refresh it passed on.
    ///
    /// Steps:
    /// - Start a server with no license, which makes its key; activate and refresh with elm server through the relay (it
    ///   keeps the answer of sequence 2); start a server with service.url set to the relay; have the relay answer the next
    ///   refresh with the kept answer; move the clock an hour, to the refresh; seal and read the usage log until the line is
    ///   there; move the clock an hour, to the retry. The clock stands still after each move, since a move of more than 30
    ///   seconds while the refresh is on its way would end its request (design §12.6.2).
    ///
    /// Expected:
    /// - The server starts with sequence 2; after the replay the server still uses sequence 2, the kept activation is at
    ///   sequence 2 with one request in its retry list, and the usage log has license.refresh_failed with stage service and
    ///   ELM_REQUEST_MISMATCH; the next refresh brings sequence 3.
    /// </remarks>
    [Fact]
    public async Task OwnRefresh_ReplayedAnswerOfAnotherRequest_KeepsTheLicenseAndLogsTheMismatch()
    {
        await using ReplayingRelay relay = await ReplayingRelay.StartAsync(path => path.EndsWith("/refresh", StringComparison.Ordinal));
        (TestService test, ElmService service, string key) = await ServiceAsync(
            "\"tokenLifetime\":7200,\"refreshWindow\":3600",
            s => s with { PublicUrls = [relay.Url] }
        );
        relay.Target = service.Url;
        string folder = _deployment.NewFolder();
        string license = Path.Combine(folder, "server.elmlic");
        string[] through = [.. Common(folder), "--service", relay.Url.ToString(), "--license", license];
        ServerSettings settings = Settings(test, folder) with { ServiceUrl = relay.Url };
        await Assert.ThrowsAsync<ServerStartException>(() => StartAsync(test, settings));
        (int code, _, string error) = Run(test, ["server", "activate", key, .. through]);
        Assert.True(code == 0, error);
        (code, _, error) = Run(test, ["server", "refresh", .. through]);
        Assert.True(code == 0, error);

        ElmServer server = await StartAsync(test, settings);
        Assert.Equal(2L, server.Engine.Server.License.Sequence);
        relay.ReplayToNext();

        // The refresh is due an hour after the token of sequence 2 was issued, and the clock has not moved since. The clock
        // then stands still: it also counts the 30 seconds the server's client gives its request (design §12.6.2), so a move
        // while the refresh is on its way would end that request.
        test.Time.Advance(TimeSpan.FromHours(1));

        // Lines reach the disk when they are sealed (design §18.1), so the log is sealed until the line is there.
        bool logged = false;
        using (HttpClient seal = Deployment.AdminHttp(server))
        {
            for (int i = 0; i < 100 && !logged; i++)
            {
                (await seal.PostAsync("admin/v1/log/seal", null, TestContext.Current.CancellationToken)).Dispose();
                logged = UsageLines(folder)
                    .Any(l =>
                        l.Contains("\"license.refresh_failed\"", StringComparison.Ordinal)
                        && l.Contains("\"stage\":\"service\"", StringComparison.Ordinal)
                        && l.Contains("\"code\":\"ELM_REQUEST_MISMATCH\"", StringComparison.Ordinal)
                    );
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        Assert.True(logged, "No license.refresh_failed line with ELM_REQUEST_MISMATCH.");
        StoredActivation kept = new ActivationStore(folder, test.Time).Read(out _)!;
        Assert.Equal((2L, 2L, 1), (server.Engine.Server.License.Sequence, kept.Seq, kept.Retry.Count));

        // A failed refresh tries again an hour later (design §12.6.2). The client sets that timer on the thread that wrote the
        // line, right after it, while the test sees the line only after a seal and a read of the log. The clock again stands
        // still once moved.
        test.Time.Advance(TimeSpan.FromHours(1));
        await Deployment.WaitUntilAsync(() => server.Engine.Server.License.Sequence == 3);
        Assert.Equal(1, relay.Replayed);
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
        string folder = _deployment.NewFolder();
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
        Assert.Equal(0, Run(test, ["server", "refresh-request", .. Common(folder), "--out", refresh]).Code);
        (int code, string output, string error) = Run(
            test,
            ["server", "import", await ProcessAsync(service, refresh), .. Common(folder), "--license", license]
        );
        Assert.True(code == 0, error);
        Assert.Contains("The server reloaded the license.", output, StringComparison.Ordinal);
        Assert.Equal(2L, server.Engine.Server.License.Sequence);

        string proof = Path.Combine(folder, "p.elmdeact");
        Assert.Equal(1, Run(test, ["server", "deactivate-proof", .. Common(folder), "--out", proof]).Code);
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

        string bare = _deployment.NewFolder();
        (int code, _, string error) = Run(
            test,
            ["server", "activate", key, .. Common(bare), "--service", url, "--license", Path.Combine(bare, "s.elmlic")]
        );
        Assert.Equal((2, "error: The server's installation key does not exist; start elm-server once to make it."), (code, error.Trim()));

        string folder = _deployment.NewFolder();
        string license = Path.Combine(folder, "server.elmlic");
        ServerSettings settings = Settings(test, folder) with { ServiceUrl = service.Url };
        await Assert.ThrowsAsync<ServerStartException>(() => StartAsync(test, settings));
        (code, _, error) = Run(test, ["server", "activate", standard, .. Common(folder), "--service", url, "--license", license]);
        Assert.Equal(1, code);
        Assert.StartsWith("error: ELM_WRONG_KIND", error, StringComparison.Ordinal);
        Assert.Equal(0, Run(test, ["server", "activate", key, .. Common(folder), "--service", url, "--license", license]).Code);

        // Without service.url the server does not read the kept activation, so the path it reads is all it has (R6).
        ElmServer server = await StartAsync(test, Settings(test, folder));
        (code, _, error) = Run(test, ["server", "refresh", .. Common(folder), "--service", url, "--license", Path.Combine(folder, "other.elmlic")]);
        Assert.Equal((1, "error: The server reads another license file than --license; pass its setting license."), (code, error.Trim()));
        await server.DisposeAsync();
        _owned.Remove(server);

        ElmServer restarted = await StartAsync(test, settings);
        Assert.Equal(2L, restarted.Engine.Server.License.Sequence);
        (code, _, error) = Run(test, ["server", "refresh-request", .. Common(folder), "--out", Path.Combine(folder, "r.elmreq")]);
        Assert.Equal((1, "error: ELM_INVALID_STATE: The server refreshes its license online; use elm server refresh."), (code, error.Trim()));
        await restarted.DisposeAsync();
        _owned.Remove(restarted);

        ServerSettings unwritable = settings with { LicensePath = Path.Combine(folder, "missing", "server.elmlic") };
        ServerStartException start = await Assert.ThrowsAsync<ServerStartException>(() => StartAsync(test, unwritable));
        Assert.Contains("cannot be written", start.Message, StringComparison.Ordinal);

        string shared = _deployment.NewFolder();
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
    ///   move the clock an hour and wait for the server's refresh.
    ///
    /// Expected:
    /// - autoRefresh false; the activation's license in use and autoRefresh true; the hand license in use, autoRefresh still
    ///   true; then the activation's license again, with sequence 2.
    /// </remarks>
    [Fact]
    public async Task HandFile_AndAnActivationMadeWhileRunning()
    {
        (TestService test, ElmService service, string key) = await ServiceAsync("\"tokenLifetime\":7200,\"refreshWindow\":3600");
        string folder = _deployment.NewFolder();
        string license = Path.Combine(folder, "server.elmlic");
        ServerSettings settings = Settings(test, folder) with { ServiceUrl = service.Url };
        await Assert.ThrowsAsync<ServerStartException>(() => StartAsync(test, settings));
        byte[] hand = HandLicense(test, Deployment.ServerKeyId(folder));
        File.WriteAllBytes(license, hand);
        ElmServer server = await StartAsync(test, settings);
        Assert.False((await StatusAsync(server)).GetProperty("autoRefresh").GetBoolean());

        (int code, _, string error) = Run(
            test,
            ["server", "activate", key, .. Common(folder), "--service", service.Url.ToString(), "--license", license]
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

        // The activation's refresh is due an hour after its token was issued, and the clock has not moved since. The clock
        // then stands still, so that it does not end the refresh's request (30 seconds of this clock, design §12.6.2).
        test.Time.Advance(TimeSpan.FromHours(1));
        await Deployment.WaitUntilAsync(() => server.Engine.Server.License.LicenseId == activated);
        Assert.Equal(2L, server.Engine.Server.License.Sequence);
    }

    /// <summary>
    /// A server whose data folder is reached through two links in a row goes on in the folder where they ended at the start:
    /// its socket is there, and when an activation made while it runs makes its client again, the client keeps its files
    /// there too, though the second link now leads elsewhere (design §10.2, V1d24, review K1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - elm-service with a server policy; end and other, two data folders that pass the check; in a folder of the test,
    ///   two/second, a link to end, and one/first, a link to two/second. Links are symbolic links on Linux and macOS and
    ///   junctions on Windows, where the first junction gets an access list of its own that passes. A server with
    ///   dataDirectory one/first, service.url, and a license issued by hand at end/server.elmlic.
    ///
    /// Steps:
    /// - Start the server; point two/second at other; activate with elm server on end while the server runs.
    ///
    /// Expected:
    /// - The server's data folder is end and its socket end/admin.sock; after the activation autoRefresh is true, and other
    ///   holds neither a key nor an activation.
    /// </remarks>
    [Fact]
    public async Task DataFolderThroughTwoLinks_LinkChangedWhileRunning_KeepsTheFilesWhereTheLinksEnded()
    {
        (TestService test, ElmService service, string key) = await ServiceAsync("\"tokenLifetime\":7200,\"refreshWindow\":3600");
        string end = _deployment.NewFolder();
        string other = _deployment.NewFolder();
        string two = _deployment.NextFolderPath();
        string one = _deployment.NextFolderPath();
        Directory.CreateDirectory(two);
        Directory.CreateDirectory(one);
        string second = Path.Combine(two, "2");
        string first = Path.Combine(one, "1");
        MakeLink(second, end);
        MakeLink(first, second);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string me = "*" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
                Run("icacls", first, "/inheritance:r", "/grant:r", me + ":(F)", "*S-1-5-18:(F)", "*S-1-5-32-544:(F)", "/L");
            }

            string license = Path.Combine(end, "server.elmlic");
            ServerSettings settings = Settings(test, first) with { ServiceUrl = service.Url, LicensePath = license };
            File.WriteAllBytes(license, HandLicense(test, Deployment.ServerKeyId(end)));
            ElmServer server = await StartAsync(test, settings);
            Assert.Equal(end, server.DataDirectory);
            Assert.Equal(Path.Combine(end, "admin.sock"), server.AdminSocketPath);

            Directory.Delete(second);
            MakeLink(second, other);
            (int code, _, string error) = Run(
                test,
                ["server", "activate", key, .. Common(end), "--service", service.Url.ToString(), "--license", license]
            );
            Assert.True(code == 0, error);
            Assert.True((await StatusAsync(server)).GetProperty("autoRefresh").GetBoolean());
            Assert.False(File.Exists(Path.Combine(other, "installation.key")));
            Assert.False(File.Exists(Path.Combine(other, "activation.json")));
        }
        finally
        {
            foreach (IAsyncDisposable owned in Enumerable.Reverse(_owned))
            {
                await owned.DisposeAsync();
            }

            _owned.Clear();
            Directory.Delete(first);
            Directory.Delete(second);
        }
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
    ///   clock back; activate with elm server, writing to the server's license path.
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
        string folder = _deployment.NewFolder();
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
            ["server", "activate", key, .. Common(folder), "--service", service.Url.ToString(), "--license", license]
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
    /// An answer of the administration API that is not the server's status does not count as "no server runs": with an
    /// administrator token that is not the running server's, the commands that a running server restricts stop with that
    /// answer, and they send their request once (design §13.7, V1d2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - elm-service with a server policy; a running server with a license issued by hand; after its start, admin.token in its
    ///   data folder replaced by another token, as a hand would replace it (the server's own writer makes the file).
    ///
    /// Steps:
    /// - In the server's folder, run elm server deactivate, deactivate-proof, activate-request, and refresh-request; stop the
    ///   server and read the usage log.
    ///
    /// Expected:
    /// - Each exits 1 with the one line "error: ELM_UNAUTHORIZED: The administrator token is missing or wrong." on standard
    ///   error. The two request commands and deactivate-proof write no file, and deactivate leaves the license file.
    /// - The usage log counts 4 refused requests (admin.denied): one for each command, so no command read the token again
    ///   and sent its request a second time.
    /// </remarks>
    [Fact]
    public async Task ServerCommands_ReplacedToken_StopWithTheApisAnswer()
    {
        (TestService test, ElmService service, string key) = await ServiceAsync(string.Empty);
        (ElmServer server, string folder) = await RunningServerAsync(test);
        Assert.NotNull(await AdminToken.CreateAsync(folder, test.Time, TestContext.Current.CancellationToken));
        string made = Path.Combine(folder, "made.file");
        string license = Path.Combine(folder, "server.elmlic");
        string[][] commands =
        [
            ["server", "deactivate", .. Common(folder), "--service", service.Url.ToString(), "--license", license],
            ["server", "deactivate-proof", .. Common(folder), "--out", made],
            ["server", "activate-request", "--key", key, .. Common(folder), "--out", made],
            ["server", "refresh-request", .. Common(folder), "--out", made],
        ];

        foreach (string[] command in commands)
        {
            (int code, _, string error) = Run(test, command);

            Assert.Equal((1, "error: ELM_UNAUTHORIZED: The administrator token is missing or wrong."), (code, error.Trim()));
            if (command[1] == "deactivate")
            {
                Assert.True(File.Exists(license));
            }
            else
            {
                Assert.False(File.Exists(made), command[1]);
            }
        }

        await server.DisposeAsync();
        Assert.Equal(commands.Length, DeniedCount(folder));
    }

    /// <summary>
    /// When the administration API refuses the caller after elm server activate, refresh, or import wrote the file, the
    /// command prints that answer, not that the server kept its license: the server said nothing about the file.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - elm-service with a server policy; a running server with a license issued by hand; a copy of the bytes of its
    ///   admin.token.
    ///
    /// Steps:
    /// - Replace admin.token by another token. In the server's folder, writing to the server's license path: activate with
    ///   elm server; refresh. Put the server's own token back, make a refresh request, process it in the service, and
    ///   replace the token again; import the answer. Stop the server and read the usage log.
    ///
    /// Expected:
    /// - Each of the three exits 1 with the one line "error: ELM_UNAUTHORIZED: The administrator token is missing or wrong.
    ///   The license file is written; the server did not confirm a reload." on standard error, names the written file on
    ///   standard output, and leaves a file that differs from the one before; the first replaces the hand
    ///   license.
    /// - The usage log counts 3 refused requests: one for each of the three commands.
    /// </remarks>
    [Fact]
    public async Task ActivateRefreshImport_AdministrationApiRefusesTheCaller_PrintItsAnswer()
    {
        const string Line =
            "error: ELM_UNAUTHORIZED: The administrator token is missing or wrong. The license file is written; the server did not "
            + "confirm a reload.";
        (TestService test, ElmService service, string key) = await ServiceAsync(string.Empty);
        (ElmServer server, string folder) = await RunningServerAsync(test);
        string license = Path.Combine(folder, "server.elmlic");
        string request = Path.Combine(folder, "refresh.elmreq");
        string tokenFile = Path.Combine(folder, AdminToken.FileName);
        byte[] serversToken = File.ReadAllBytes(tokenFile);
        string[] tail = [.. Common(folder), "--license", license];
        byte[] before = File.ReadAllBytes(license);

        Assert.NotNull(await AdminToken.CreateAsync(folder, test.Time, TestContext.Current.CancellationToken));
        (int Code, string Out, string Error) activated = Run(test, ["server", "activate", key, "--service", service.Url.ToString(), .. tail]);
        byte[] first = File.ReadAllBytes(license);
        (int Code, string Out, string Error) refreshed = Run(test, ["server", "refresh", "--service", service.Url.ToString(), .. tail]);
        byte[] second = File.ReadAllBytes(license);
        File.WriteAllBytes(tokenFile, serversToken);
        (int made, _, string makeError) = Run(test, ["server", "refresh-request", .. Common(folder), "--out", request]);
        Assert.True(made == 0, makeError);
        Assert.NotNull(await AdminToken.CreateAsync(folder, test.Time, TestContext.Current.CancellationToken));
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
        await server.DisposeAsync();
        Assert.Equal(3, DeniedCount(folder));
    }

    /// <summary>
    /// An answer on the administration socket that is not the server's, as another program on that socket gives, stops the
    /// command with the status code; after a written file, the line also says that the file is written.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - elm-service with a server policy; a data folder that holds a stopped server's installation key and administrator
    ///   token; a listener of the test on admin.sock in that folder that answers the first request with a given answer, and
    ///   any later one with 404.
    /// - The 302 points at a closed port: a listener of the test takes a port the system chooses and stops. The command must
    ///   not follow the redirect; only if it wrongly did, another program that took the port in between would get the
    ///   request (the V1d3b Grill, 3b-5).
    ///
    /// Steps:
    /// - In that folder: run elm server deactivate-proof against 404 with no body, against 502 with an HTML body, against 200
    ///   with an empty JSON object, against 302 to a loopback port where nothing listens, against 200 with a JSON array, and
    ///   against 500 with a body that holds autoRefresh and licenseToken; then activate with elm server against the 404,
    ///   refresh against the 500, and run elm server status against the 302.
    ///
    /// Expected:
    /// - deactivate-proof exits 1 each time with the one line "error: The server's administration API answered 404." (502,
    ///   200, 302, 200, 500) and writes no file. The activation and the refresh exit 1 with "error: The server's administration API
    ///   answered 404. The license file is written; the server did not confirm a reload." (500) and name the written file
    ///   on standard output.
    ///   elm server status exits 1 with "error: The server's administration API answered 302.", the same line.
    /// </remarks>
    [Fact]
    public async Task ServerCommands_AnswerThatIsNotTheServers_StopWithTheStatusCode()
    {
        (TestService test, ElmService service, string key) = await ServiceAsync(string.Empty);
        string other = await StoppedServerFolderAsync(test);
        string made = Path.Combine(other, "made.file");
        string license = Path.Combine(other, "server.elmlic");

        // The fourth answer: a client that followed the redirect would send a second request, which the listener answers
        // with 404, and the line would name that status.
        (string Answer, int Status)[] answers =
        [
            (NotFound, 404),
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
            (int code, _, string error) = RunAgainst(test, other, answer, ["server", "deactivate-proof", .. Common(other), "--out", made]);

            Assert.Equal((1, $"error: The server's administration API answered {status}."), (code, error.Trim()));
            Assert.False(File.Exists(made));
        }

        // The folder is activated once; the second file comes from a refresh, which asks for the reload the same way.
        string[] first = ["server", "activate", key];
        string[] second = ["server", "refresh"];
        foreach ((string[] head, (string answer, int status)) in new[] { (first, answers[0]), (second, answers[5]) })
        {
            (int activated, string output, string activateError) = RunAgainst(
                test,
                other,
                answer,
                [.. head, .. Common(other), "--service", service.Url.ToString(), "--license", license]
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

        // elm server status and its sibling commands do not follow the redirect either.
        (int status302, _, string statusError) = RunAgainst(test, other, answers[3].Answer, ["server", "status", .. Common(other)]);

        Assert.Equal((1, "error: The server's administration API answered 302."), (status302, statusError.Trim()));
    }

    /// <summary>
    /// Something that listens on the administration socket and gives no whole answer in time does not count as "no server
    /// runs": the commands stop with a network error.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - elm-service with a server policy; a data folder that holds a stopped server's installation key and administrator
    ///   token; a listener of the test on admin.sock in that folder that reads one request and then either answers with a
    ///   body cut short and closes, or keeps the connection open without answering.
    ///
    /// Steps:
    /// - In that folder: run elm server deactivate-proof against the listener that cuts its answer short; run it against the
    ///   listener that keeps the connection open and moves the clock 30 seconds on once it has read the request; activate
    ///   with elm server against such a listener; run deactivate-proof, and then refresh, against a listener that answers
    ///   200 with an HTML body.
    ///
    /// Expected:
    /// - deactivate-proof exits 3 with the one line "error: The server's administration API gave no whole answer.", then
    ///   with the one line "error: The server's administration API did not answer in time."; it writes no file. The activation exits 3 with "error: The server's administration API did not
    ///   answer in time. The license file is written; the server did not confirm a reload." and names the written file on
    ///   standard output. Against the HTML body, deactivate-proof exits 3 with "error: The server's administration API
    ///   answered with a body that is not JSON." and writes no file, and the refresh exits 3 with that line followed by "The
    ///   license file is written; the server did not confirm a reload.".
    /// </remarks>
    [Fact]
    public async Task ServerCommands_ListenerWithoutAWholeAnswer_StopWithANetworkError()
    {
        (TestService test, ElmService service, string key) = await ServiceAsync(string.Empty);
        string other = await StoppedServerFolderAsync(test);
        string made = Path.Combine(other, "made.file");
        string license = Path.Combine(other, "server.elmlic");

        // The wait of the commands, to the second: a longer wait would not end at this move of the clock.
        TimeSpan wait = TimeSpan.FromSeconds(30);

        // The answer ends inside its body. A connection closed before any byte of the answer is tried again by the HTTP
        // client on a new connection, so that case would end as the listener's answer to a second request does.
        (int code, _, string error) = RunAgainst(
            test,
            other,
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 40\r\n\r\n{",
            ["server", "deactivate-proof", .. Common(other), "--out", made]
        );

        Assert.Equal((3, "error: The server's administration API gave no whole answer."), (code, error.Trim()));
        Assert.False(File.Exists(made));

        (code, _, error) = RunAgainst(test, other, null, ["server", "deactivate-proof", .. Common(other), "--out", made], wait);

        Assert.Equal((3, "error: The server's administration API did not answer in time."), (code, error.Trim()));
        Assert.False(File.Exists(made));

        (code, string output, error) = RunAgainst(
            test,
            other,
            null,
            ["server", "activate", key, .. Common(other), "--service", service.Url.ToString(), "--license", license],
            wait
        );

        Assert.Equal(
            (
                3,
                "error: The server's administration API did not answer in time. The license file is written; the server did not confirm a "
                    + "reload."
            ),
            (code, error.Trim())
        );
        Assert.Contains(license, output, StringComparison.Ordinal);

        // A success with a body that is not JSON: the answer cannot be read.
        const string Page = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 13\r\nConnection: close\r\n\r\n<html></html>";
        (code, _, error) = RunAgainst(test, other, Page, ["server", "deactivate-proof", .. Common(other), "--out", made]);

        Assert.Equal((3, "error: The server's administration API answered with a body that is not JSON."), (code, error.Trim()));
        Assert.False(File.Exists(made));

        (code, output, error) = RunAgainst(
            test,
            other,
            Page,
            ["server", "refresh", .. Common(other), "--service", service.Url.ToString(), "--license", license]
        );

        Assert.Equal(
            (
                3,
                "error: The server's administration API answered with a body that is not JSON. The license file is written; the server did "
                    + "not confirm a reload."
            ),
            (code, error.Trim())
        );
        Assert.Contains(license, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The commands take the server as not running in three cases only, and go on: no administrator token that can be read,
    /// no socket file, and a socket file that refuses the connection. Whether the server's lock file is there, and whether
    /// something holds it, changes none of them, nor an answer that stops the command (design §13.7, V1d2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - elm-service with a server policy and a license key. For each case a data folder of its own:
    ///   no token: a server started there without a license, so the folder has the key and no token, and a listener of the
    ///   test on admin.sock that would answer a status with autoRefresh true;
    ///   no socket file: a server ran there with a license issued by hand and stopped;
    ///   refused: the same, and a socket file on which nobody listens at admin.sock;
    ///   answering: the same, and a listener of the test on admin.sock that answers 404.
    /// - Each case runs three times: with server.lock deleted, with server.lock as the server left it, and with server.lock
    ///   held open by the test, shared with nobody.
    ///
    /// Steps:
    /// - elm server activate-request with the key, writing a request file.
    ///
    /// Expected:
    /// - No token, no socket file, refused: exit 0 and the request file is written, for every state of the lock file; the
    ///   listener of the first case gets no connection.
    /// - Answering: exit 1 with "error: The server's administration API answered 404." and no file, for every state of the
    ///   lock file.
    /// </remarks>
    [Fact]
    public async Task ActivateRequest_NotRunningCases_GoOnWhateverTheLockFile()
    {
        const string AutoRefresh =
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 20\r\nConnection: close\r\n\r\n{\"autoRefresh\":true}";
        (TestService test, _, string key) = await ServiceAsync(string.Empty);

        foreach (string state in new[] { "no token", "no socket file", "refused", "answering" })
        {
            foreach (string lockState in new[] { "deleted", "left", "held" })
            {
                string folder;
                if (state == "no token")
                {
                    folder = _deployment.NewFolder();
                    await Assert.ThrowsAsync<ServerStartException>(() => StartAsync(test, Settings(test, folder)));
                    Assert.Null(AdminToken.Read(folder));
                }
                else
                {
                    folder = await StoppedServerFolderAsync(test);
                }

                string lockFile = Path.Combine(folder, "server.lock");
                Assert.True(File.Exists(lockFile));
                if (lockState == "deleted")
                {
                    File.Delete(lockFile);
                }

                using FileStream? held = lockState == "held" ? new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
                if (state == "refused")
                {
                    FakeAdminSocket.LeaveFile(folder);
                }

                using FakeAdminSocket? fake = state switch
                {
                    "no token" => new FakeAdminSocket(folder, _ => AutoRefresh),
                    "answering" => new FakeAdminSocket(folder, _ => NotFound),
                    _ => null,
                };
                string made = Path.Combine(folder, "made.elmreq");

                (int code, _, string error) = Run(test, ["server", "activate-request", "--key", key, .. Common(folder), "--out", made]);

                string what = $"{state}, server.lock {lockState}";
                if (state == "answering")
                {
                    Assert.True((1, "error: The server's administration API answered 404.") == (code, error.Trim()), $"{what}: {code} {error}");
                    Assert.False(File.Exists(made), what);
                    continue;
                }

                Assert.True(code == 0, $"{what}: {code} {error}");
                Assert.True(File.Exists(made), what);
                Assert.True(fake is null || await fake.ConnectionsBeforeNowAsync() == 0, what);
            }
        }
    }

    /// <summary>
    /// A connection to the administration socket that fails otherwise than by a refusal, as it does for a user who may not
    /// write the socket file, does not count as "no server runs": the commands stop with a network error (design §13.7,
    /// V1d2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - elm-service with a server policy; a data folder that holds a stopped server's installation key and administrator
    ///   token, and a socket file at admin.sock; the commands' connection to the socket replaced by one that throws
    ///   SocketException with SocketError.AccessDenied, which the OS gives only to another user.
    ///
    /// Steps:
    /// - In that folder: elm server deactivate-proof, refresh-request, and activate with the key.
    ///
    /// Expected:
    /// - deactivate-proof and refresh-request exit 3 with the one line "error: The server's administration API gave no whole
    ///   answer." and write no file. The activation exits 3 with that line followed by "The license file is written; the
    ///   server did not confirm a reload." and names the written file on standard output.
    /// </remarks>
    [Fact]
    public async Task ServerCommands_ConnectionErrorOtherThanRefused_StopWithANetworkError()
    {
        (TestService test, ElmService service, string key) = await ServiceAsync(string.Empty);
        string other = await StoppedServerFolderAsync(test);
        FakeAdminSocket.LeaveFile(other);
        string made = Path.Combine(other, "made.file");
        string license = Path.Combine(other, "server.elmlic");
        static ValueTask<Stream> Denied(UnixDomainSocketEndPoint endPoint, CancellationToken cancellationToken) =>
            throw new SocketException((int)SocketError.AccessDenied);

        foreach (
            string[] command in new string[][]
            {
                ["server", "deactivate-proof", .. Common(other), "--out", made],
                ["server", "refresh-request", .. Common(other), "--out", made],
            }
        )
        {
            (int code, _, string error) = Run(test, command, Denied);

            Assert.Equal((3, "error: The server's administration API gave no whole answer."), (code, error.Trim()));
            Assert.False(File.Exists(made), command[1]);
        }

        (int activated, string output, string activateError) = Run(
            test,
            ["server", "activate", key, .. Common(other), "--service", service.Url.ToString(), "--license", license],
            Denied
        );

        Assert.Equal(
            (3, "error: The server's administration API gave no whole answer. The license file is written; the server did not confirm a reload."),
            (activated, activateError.Trim())
        );
        Assert.Contains(license, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// No elm server command sends anything to the loopback TCP port 7071, where the administration API listened before
    /// V1d2: with the server stopped and another process listening there, the administrator token of the data folder stays
    /// on this side (design §5.3 T20, §13.7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - elm-service with a server policy and a license key; a data folder in which a server ran with a license issued by
    ///   hand and stopped, so it holds a token that can be read and no socket file; a TcpListener of the test on
    ///   127.0.0.1:7071 that takes each connection, counts it, and closes it. A machine on which another program holds
    ///   that port skips the test and says so.
    ///
    /// Steps:
    /// - In two such folders, the first without a socket file and the second with a socket file at admin.sock that refuses
    ///   connections, so that each command there tries to connect: status, leases, release, reload, seal; activate, refresh,
    ///   refresh-request, import of the processed answer, activate-request, deactivate-proof, forget-proof, and deactivate.
    ///
    /// Expected:
    /// - The five exit 3. activate, refresh, and import print "The server is not running; it reads the license when it
    ///   starts." and exit 0. refresh-request and deactivate-proof exit 0, so they took the server as not running.
    /// - The listener on port 7071 took no connection: after the five commands in each folder, and at the end.
    /// </remarks>
    [Fact]
    public async Task ServerCommands_ServerStoppedAndPort7071Taken_SendNothingThere()
    {
        using var legacy = new TcpListener(IPAddress.Loopback, 7071);
        try
        {
            legacy.Start();
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            Assert.Skip("Another program listens on 127.0.0.1:7071 on this machine, so the test cannot watch that port.");
        }

        // A connection that is taken and closed ends the command that made it at once; one left waiting would keep a
        // command that sends there until its own time limit.
        int taken = 0;
        CancellationToken cancel = TestContext.Current.CancellationToken;
        Task accepting = Task.Run(
            async () =>
            {
                try
                {
                    while (true)
                    {
                        using TcpClient client = await legacy.AcceptTcpClientAsync(cancel);
                        Interlocked.Increment(ref taken);
                    }
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
                {
                    // The listener stopped.
                }
            },
            cancel
        );

        const string NotRunning = "The server is not running; it reads the license when it starts.";
        (TestService test, ElmService service, string key) = await ServiceAsync(string.Empty);
        string url = service.Url.ToString();

        foreach (bool refused in new[] { false, true })
        {
            string folder = await StoppedServerFolderAsync(test);
            Assert.NotNull(AdminToken.Read(folder));
            if (refused)
            {
                FakeAdminSocket.LeaveFile(folder);
            }

            string license = Path.Combine(folder, "server.elmlic");
            string request = Path.Combine(folder, "r.elmreq");
            string[] tail = [.. Common(folder), "--service", url, "--license", license];
            foreach (string[] command in new string[][] { ["status"], ["leases"], ["release", "some-lease"], ["reload"], ["seal"] })
            {
                Assert.Equal(3, Run(test, ["server", .. command, "--data", folder]).Code);
            }

            Assert.True(Volatile.Read(ref taken) == 0, "A connection came to 127.0.0.1:7071.");
            void Reloads(string name, string[] command)
            {
                (int code, string output, string error) = Run(test, command);
                Assert.True(code == 0 && output.Contains(NotRunning, StringComparison.Ordinal), $"{name}: {code} {error}");
            }

            void Passes(string name, string[] command)
            {
                (int code, _, string error) = Run(test, command);
                Assert.True(code == 0, $"{name}: {code} {error}");
            }

            Reloads("activate", ["server", "activate", key, .. tail]);
            Reloads("refresh", ["server", "refresh", .. tail]);
            Passes("refresh-request", ["server", "refresh-request", .. Common(folder), "--out", request]);
            Reloads("import", ["server", "import", await ProcessAsync(service, request), .. Common(folder), "--license", license]);

            // These two end as the activation's state has it; what counts is the status they ask the server for first.
            Run(test, ["server", "activate-request", "--key", key, .. Common(folder), "--out", Path.Combine(folder, "a.elmreq")]);
            Passes("deactivate-proof", ["server", "deactivate-proof", .. Common(folder), "--out", Path.Combine(folder, "p.elmdeact")]);
            Run(test, ["server", "forget-proof", .. Common(folder)]);
            Run(test, ["server", "deactivate", .. tail]);
        }

        legacy.Stop();
        await accepting;
        Assert.True(Volatile.Read(ref taken) == 0, "A connection came to 127.0.0.1:7071.");
    }

    // Runs elm against a listener of the test on admin.sock of the folder, which answers the first request with the given
    // bytes and any later one with 404, after which the connection closes. With no answer (null), the listener moves the
    // test's clock on by the given time once it has read the request, and keeps the connection open until the command has
    // ended. The listener stops after the run, and .NET then removes its socket file.
    private static (int Code, string Out, string Error) RunAgainst(
        TestService test,
        string folder,
        string? answer,
        string[] command,
        TimeSpan? clockAdvance = null
    )
    {
        using var listener = new FakeAdminSocket(
            folder,
            connection => connection == 0 ? answer : NotFound,
            clockAdvance is TimeSpan advance ? () => test.Time.Advance(advance) : null
        );
        (int Code, string Out, string Error) result = Run(test, command);
        Assert.True(listener.Requests > 0, "The command did not call the administration API.");
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

    // The refused requests that the usage log of a stopped server counts (admin.denied, design §13.7).
    private static int DeniedCount(string folder) =>
        UsageLines(folder)
            .Where(l => l.Contains("\"admin.denied\"", StringComparison.Ordinal))
            .Sum(l =>
            {
                using JsonDocument line = JsonDocument.Parse(l);
                return line.RootElement.GetProperty("count").GetInt32();
            });

    // A running server with a license issued by hand, and its data folder.
    private async Task<(ElmServer Server, string Folder)> RunningServerAsync(TestService test)
    {
        string folder = _deployment.NewFolder();
        ServerSettings settings = Settings(test, folder);
        await Assert.ThrowsAsync<ServerStartException>(() => StartAsync(test, settings));
        File.WriteAllBytes(Path.Combine(folder, "server.elmlic"), HandLicense(test, Deployment.ServerKeyId(folder)));
        return (await StartAsync(test, settings), folder);
    }

    // The data folder of a server that ran with a hand license and has stopped: it holds that server's installation key,
    // license, and administrator token, and no socket file.
    private async Task<string> StoppedServerFolderAsync(TestService test)
    {
        (ElmServer server, string folder) = await RunningServerAsync(test);
        await server.DisposeAsync();
        _owned.Remove(server);
        Assert.False(Path.Exists(AdminSocket.PathIn(folder)));
        return folder;
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
    private async Task<(TestService Test, ElmService Service, string Key)> ServiceAsync(
        string members,
        Func<ServiceSettings, ServiceSettings>? change = null
    )
    {
        var test = new TestService();
        _owned.Add(test);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync(change);
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

    // A symbolic link on Linux and macOS; a junction on Windows, which needs no right of its own.
    private static void MakeLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            Run("cmd.exe", "/c", "mklink", "/J", link, target);
        }
        else
        {
            File.CreateSymbolicLink(link, target);
        }
    }

    // Runs a program and fails the test when it does not succeed.
    private static void Run(string program, params string[] arguments)
    {
        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(program, arguments) { RedirectStandardError = true, RedirectStandardOutput = true }
        )!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{program} {string.Join(' ', arguments.Take(3))} exited with {process.ExitCode}: {output.Trim()}");
    }

    private static (int Code, string Out, string Error) Run(
        TestService test,
        string[] args,
        Func<UnixDomainSocketEndPoint, CancellationToken, ValueTask<Stream>>? connect = null
    )
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
            ConnectAdminSocket = connect,
        };
        int code = ElmApp.Run(args, host);
        return (code, output.ToString(), error.ToString());
    }
}
