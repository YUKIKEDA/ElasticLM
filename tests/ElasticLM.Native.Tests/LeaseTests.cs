using System.Text.Json;
using ElasticLM.Licensing;
using ElasticLM.Server;
using ElasticLM.Server.Tests;
using ElasticLM.Service;
using ElasticLM.Service.Tests;

namespace ElasticLM.Native.Tests;

/// <summary>
/// Tests of the lease functions of libelasticlm (design §17.6) against a real elm-server on loopback TLS and a real
/// elm-service, called through unmanaged function pointers as C calls them (<see cref="LeaseApi"/>). The servers and the
/// clients share one fake clock.
/// </summary>
[Collection(NativeClock.Name)]
public sealed class LeaseTests : IAsyncLifetime
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly NativeApi _api = new();
    private readonly Deployment _deployment = new();
    private string _root = null!;

    public ValueTask InitializeAsync()
    {
        _root = File.ReadAllText(_deployment.RootKeyFile);
        Exports.Time = _deployment.Time;
        LeaseCallbacks.Reset();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Exports.Time = TimeProvider.System;
        LeaseCallbacks.Reset();
        _api.Dispose();
        await _deployment.DisposeAsync();
    }

    /// <summary>
    /// An on-premises lease: acquired, read, renewed, released with its callback; the pin deleted; destroy returns the leases
    /// the client holds, and a lease can still be read afterwards.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server (2 seats of pro, 10-minute leases) past its reclaim window; a client with the lease callback (user 7).
    ///
    /// Steps:
    /// - Acquire pro; read the lease; move the clock 200 s and wait for the renewal; release it, and release it again; delete
    ///   the server's pin twice; acquire another lease; destroy the client; read that lease.
    ///
    /// Expected:
    /// - Acquire 0 with retry 0; ID set, feature pro, 1 seat, not a borrow, HELD, no error; a later expiry after the renewal;
    ///   release 0, RELEASED, one callback (the ID, HELD to RELEASED, no error, user 7); the second release 0 and no
    ///   callback; deleted 1, then 0; after destroy the server holds no seat and the second lease is RELEASED.
    /// </remarks>
    [Fact]
    public async Task OnPremises_AcquireReadRenewRelease_AndDestroyReturns()
    {
        ElmServer server = await StartOpenServerAsync();
        string url = server.PublicUrls[0].AbsoluteUri;
        nint client = Create(url);

        Assert.Equal((0, 0L), (LeaseApi.Acquire(_api, client, "pro", 1, out nint lease, out long retry), retry));
        string id = LeaseApi.Id(lease);
        Assert.NotEmpty(id);
        Assert.Equal(
            ("pro", 1, 0, 0, 0),
            (LeaseApi.Feature(lease), LeaseApi.Seats(lease), LeaseApi.IsBorrow(lease), LeaseApi.State(lease), LeaseApi.LastError(lease))
        );
        long first = LeaseApi.ExpiresAt(lease);
        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => LeaseApi.ExpiresAt(lease) > first);

        Assert.Equal(0, LeaseApi.Release(client, lease));
        Assert.Equal(4, LeaseApi.State(lease));
        Assert.Equal([(id, 0, 4, 0, string.Empty, (nint)7)], LeaseCallbacks.Changes());
        Assert.Equal(0, LeaseApi.Release(client, lease));
        Assert.Single(LeaseCallbacks.Changes());

        Assert.Equal((0, 1), (LeaseApi.ResetPin(_api, client, url, out int deleted), deleted));
        Assert.Equal((0, 0), (LeaseApi.ResetPin(_api, client, url, out deleted), deleted));

        Assert.Equal(0, LeaseApi.Acquire(_api, client, "pro", 1, out nint second, out _));
        LeaseApi.Destroy(client);
        Assert.Equal(0, server.Engine.SeatsInUse("pro"));
        Assert.Equal(4, LeaseApi.State(second));
        LeaseApi.Free(lease);
        LeaseApi.Free(second);
    }

    /// <summary>
    /// A server in its reclaim window gives ELM_SERVER_RECOVERING and its Retry-After; the arguments are checked; a lease of
    /// another client is refused; the outputs are emptied on a failure.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A freshly started server; clients A and B.
    ///
    /// Steps:
    /// - Acquire with A, with a retry pointer and with NULL. Move past the window. Acquire with count 0, a NULL feature, an
    ///   empty feature; borrow for 0, 3,162,240,001, and 3,162,240,000 seconds; acquire with A and release that lease with
    ///   B; delete the pin of ftp://x/ and of a text that is not a URL; call each function whose output is required with
    ///   NULL.
    ///
    /// Expected:
    /// - ELM_SERVER_RECOVERING (43) with out NULL and retry 600, twice; ELM_INVALID_ARGUMENT (1) for count 0, the NULL and the
    ///   empty feature, and both periods out of range; the longest period passes the check and the server refuses it with
    ///   another number; B's release 1 and the lease still HELD; 1 for both pins with deleted 0; 1 for each NULL output.
    /// </remarks>
    [Fact]
    public async Task Failures_RetryAfterArgumentsAndOwner()
    {
        ElmServer server = await _deployment.StartServerAsync();
        string url = server.PublicUrls[0].AbsoluteUri;
        nint a = Create(url);
        nint b = Create(url);

        Assert.Equal((43, (nint)0, 600L), (LeaseApi.Acquire(_api, a, "pro", 1, out nint none, out long retry), none, retry));
        Assert.Equal(43, LeaseApi.Acquire(_api, a, "pro", 1, out _, out _, nullRetry: true));
        _deployment.Time.Advance(Window);

        Assert.Equal(1, LeaseApi.Acquire(_api, a, "pro", 0, out _, out _));
        Assert.Equal(1, LeaseApi.Acquire(_api, a, null, 1, out _, out _));
        Assert.Equal(1, LeaseApi.Acquire(_api, a, string.Empty, 1, out _, out _));
        Assert.Equal(1, LeaseApi.Borrow(_api, a, "pro", 0, out _, out _));
        Assert.Equal(1, LeaseApi.Borrow(_api, a, "pro", 3_162_240_001, out _, out _));
        Assert.NotEqual(1, LeaseApi.Borrow(_api, a, "pro", 3_162_240_000, out nint longest, out _));
        Assert.Equal((nint)0, longest);

        Assert.Equal(0, LeaseApi.Acquire(_api, a, "pro", 1, out nint lease, out _));
        Assert.Equal(1, LeaseApi.Release(b, lease));
        Assert.Equal(0, LeaseApi.State(lease));

        Assert.Equal((1, 0), (LeaseApi.ResetPin(_api, a, "ftp://x/", out int deleted), deleted));
        Assert.Equal((1, 0), (LeaseApi.ResetPin(_api, a, "not a url", out deleted), deleted));
        Assert.Equal([1, 1, 1, 1, 1, 1, 1], LeaseApi.NullOutputs(_api, a, lease));

        LeaseApi.Free(lease);
        LeaseApi.Destroy(a);
        LeaseApi.Destroy(b);
    }

    /// <summary>
    /// A borrow: read, listed, and kept when the server refuses its early return.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server whose license allows no early return of a borrow (borrowReturns 0), past its reclaim window; a client.
    ///
    /// Steps:
    /// - Borrow pro for an hour; list the borrows; return the borrow; list the borrows again; destroy the client.
    ///
    /// Expected:
    /// - Borrow 0, a borrow of 1 seat, HELD, expiring an hour from now; the list holds its ID; the return is
    ///   ELM_RETURN_NOT_ALLOWED (44) and the borrow stays HELD and listed; after destroy it is still HELD.
    /// </remarks>
    [Fact]
    public async Task Borrow_ListedAndKeptWhenTheReturnIsRefused()
    {
        string data = _deployment.NewFolder("server");
        _deployment.WriteServerLicense(data, s => s with { BorrowReturnLimit = 0 });
        ElmServer server = await StartOpenServerAsync(data);
        nint client = Create(server.PublicUrls[0].AbsoluteUri);

        Assert.Equal(0, LeaseApi.Borrow(_api, client, "pro", 3600, out nint borrow, out _));
        Assert.Equal((1, 1, 0), (LeaseApi.IsBorrow(borrow), LeaseApi.Seats(borrow), LeaseApi.State(borrow)));
        Assert.Equal(_deployment.Time.GetUtcNow().AddHours(1).ToUnixTimeSeconds(), LeaseApi.ExpiresAt(borrow));
        Assert.Equal(0, LeaseApi.Borrowed(client, out string[] ids));
        Assert.Equal([LeaseApi.Id(borrow)], ids);

        Assert.Equal(44, LeaseApi.Release(client, borrow));
        Assert.Equal(0, LeaseApi.State(borrow));
        Assert.Equal(0, LeaseApi.Borrowed(client, out ids));
        Assert.Single(ids);
        LeaseApi.Destroy(client);
        Assert.Equal(0, LeaseApi.State(borrow));
        LeaseApi.Free(borrow);
        LeaseApi.FreeNullList();
    }

    /// <summary>
    /// A lease of the hosted service: acquired and released with a license key; such a client has no pin.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An elm-service with product acme-cad, a cloud-floating policy, and a license with 2 seats of pro; a client with its
    ///   key and the service's http loopback URL.
    ///
    /// Steps:
    /// - Acquire pro 2; read it; release it; delete the pin of the service URL and of ftp://x/.
    ///
    /// Expected:
    /// - 0, 2 seats, HELD; release 0 and RELEASED; the pin 0 with deleted 0, then 1.
    /// </remarks>
    [Fact]
    public async Task Hosted_AcquireRelease_AndNoPin()
    {
        await using var test = new TestService();
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        string product = (await TestService.CreatedAsync(admin, "v1/admin/products", """{"code":"acme-cad","name":"CAD"}"""))
            .GetProperty("id")
            .GetString()!;
        await TestService.CreatedAsync(admin, $"v1/admin/products/{product}/features", """{"code":"pro","name":"Pro"}""");
        string policy = await TestService.PolicyAsync(admin, product, "\"floating\":\"cloud\"");
        JsonElement license = await TestService.CreatedAsync(
            admin,
            "v1/admin/licenses",
            $$"""{"policyId":"{{policy}}","name":"n","features":[{"code":"pro","seats":2}]}"""
        );
        Exports.Time = test.Time;
        string root = File.ReadAllText(test.FilePath("root.pub.pem"));
        ClientOptions options = LeaseApi.Options(
            _api,
            root,
            service.Url.ToString(),
            _deployment.NewFolder("hosted"),
            license.GetProperty("key").GetString()
        );
        Assert.True(LeaseApi.Create(_api, options, out nint client) == 0, NativeApi.LastError);

        Assert.Equal(0, LeaseApi.Acquire(_api, client, "pro", 2, out nint lease, out _));
        Assert.Equal((2, 0), (LeaseApi.Seats(lease), LeaseApi.State(lease)));
        Assert.Equal(0, LeaseApi.Release(client, lease));
        Assert.Equal(4, LeaseApi.State(lease));
        Assert.Equal((0, 0), (LeaseApi.ResetPin(_api, client, service.Url.ToString(), out int deleted), deleted));
        Assert.Equal(1, LeaseApi.ResetPin(_api, client, "ftp://x/", out _));
        LeaseApi.Free(lease);
        LeaseApi.Destroy(client);
    }

    /// <summary>
    /// The states a lease goes through, each with its callback: retrying, retrying again, reclaiming, held, and lost.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A lease of pro from a server on port P.
    ///
    /// Steps:
    /// - Stop the server and move the clock 200 s; then 15 s; start the server again on P with a new license and move the
    ///   clock 30 s; stop it again and move the clock 11 minutes.
    ///
    /// Expected:
    /// - Callbacks HELD→RETRYING with an error, RETRYING→RETRYING with an error, RETRYING→RECLAIMING, RECLAIMING→HELD (the
    ///   last error still set), then a RETRYING and a LOST; the lease ends LOST.
    /// </remarks>
    [Fact]
    public async Task Transitions_RetryingReclaimingHeldLost()
    {
        string data = _deployment.NewFolder("server");
        ElmServer before = await StartOpenServerAsync(data);
        int port = before.PublicUrls[0].Port;
        nint client = Create(before.PublicUrls[0].AbsoluteUri);
        Assert.Equal(0, LeaseApi.Acquire(_api, client, "pro", 1, out nint lease, out _));

        await before.DisposeAsync();
        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => LeaseCallbacks.Seen.Count == 1);
        _deployment.Time.Advance(TimeSpan.FromSeconds(15));
        await Deployment.WaitUntilAsync(() => LeaseCallbacks.Seen.Count == 2);

        _deployment.WriteServerLicense(data);
        ElmServer after = await _deployment.StartServerAsync(data, s => OnPort(s, port));
        _deployment.Time.Advance(TimeSpan.FromSeconds(30));
        await Deployment.WaitUntilAsync(() => LeaseApi.State(lease) == 0 && LeaseCallbacks.Seen.Count >= 4);
        Assert.NotEqual(0, LeaseApi.LastError(lease));

        await after.DisposeAsync();
        _deployment.Time.Advance(TimeSpan.FromMinutes(11));
        await Deployment.WaitUntilAsync(() => LeaseApi.State(lease) == 3);

        var changes = LeaseCallbacks.Changes();
        Assert.Equal([(0, 1), (1, 1), (1, 2), (2, 0)], changes.Take(4).Select(c => (c.Previous, c.Current)));
        Assert.All(changes.Take(2), c => Assert.NotEqual(0, c.Error));
        Assert.Equal(3, changes[^1].Current);
        LeaseApi.Free(lease);
        LeaseApi.Destroy(client);
    }

    /// <summary>
    /// Inside its own callback, a client refuses its blocking calls and destroy, allows the lease functions and the pin, and
    /// the caller's message survives the callback's calls.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A client whose callback calls, on the same client: acquire, release, the borrow list, destroy, the lease's state,
    ///   delete a pin, and acquire again (so it ends with a failure's message).
    ///
    /// Steps:
    /// - Acquire two leases; release the first. Then fail a call on the test's thread, and move the clock 11 minutes, so the
    ///   second lease is lost on that thread inside the clock's move.
    ///
    /// Expected:
    /// - Inside, both times: acquire, release, and the list ELM_INVALID_STATE (19); destroy does nothing (the client works
    ///   afterwards); the state read; the pin 0; acquire 19. The release returns 0. The loss's callback runs on the test's
    ///   thread, and that thread's message is still the one of the failed call.
    /// </remarks>
    [Fact]
    public async Task InsideTheCallback_BlockingCallsAreRefused()
    {
        ElmServer server = await StartOpenServerAsync();
        nint client = Create(server.PublicUrls[0].AbsoluteUri);
        Assert.Equal(0, LeaseApi.Acquire(_api, client, "pro", 1, out nint first, out _));
        Assert.Equal(0, LeaseApi.Acquire(_api, client, "pro", 1, out nint second, out _));

        LeaseCallbacks.Reenter = (client, first);
        Assert.Equal(0, LeaseApi.Release(client, first));
        Assert.Equal(["acquire 19", "release 19", "borrowed 19", "state 4", "pin 0", "acquire 19"], LeaseCallbacks.Inside.ToArray());

        LeaseCallbacks.Reenter = (client, second);
        Assert.Equal(1, LeaseApi.Release(0, 0));
        string message = NativeApi.LastError;
        int thread = Environment.CurrentManagedThreadId;
        _deployment.Time.Advance(TimeSpan.FromMinutes(11));
        LeaseCallbacks.Reenter = default;
        Assert.Equal(3, LeaseApi.State(second));
        Assert.Equal((thread, message), (LeaseCallbacks.LastThread, NativeApi.LastError));
        Assert.Equal(12, LeaseCallbacks.Inside.Count);
        LeaseApi.Free(first);
        LeaseApi.Free(second);
        LeaseApi.Destroy(client);
    }

    /// <summary>
    /// destroy waits for a running callback, which can use the client and read the lease meanwhile; a change while destroy
    /// waits and any change after destroy call nothing.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Lease A of pro from a server, and lease B of pro 60 s later, so B's renewal comes 60 s after A's; the server
    ///   stopped.
    ///
    /// Steps:
    /// - Hold the callback; move the clock 200 s and wait until A's failed renewal enters it; destroy on another thread and
    ///   see it has not returned after 300 ms, nor after the clock moves 6 s (past the 5 s DisposeAsync gives the returns);
    ///   move the clock 60 s more, wait until B is RETRYING, and give its change 200 ms to reach the callback; let the
    ///   callback go, which then reads A and deletes a pin through the client while destroy waits; then move the clock a day.
    ///
    /// Expected:
    /// - One callback only, A's HELD→RETRYING, on a thread other than the test's (B's change, made while destroy waits, calls
    ///   nothing); destroy returns only after the release; inside, state 1, A's ID, and the pin call 0 (the client handle is
    ///   freed only after the callback); no callback after destroy.
    /// </remarks>
    [Fact]
    public async Task Destroy_WaitsForTheCallback_AndNoneComesAfter()
    {
        ElmServer server = await StartOpenServerAsync();
        nint client = Create(server.PublicUrls[0].AbsoluteUri);
        Assert.Equal(0, LeaseApi.Acquire(_api, client, "pro", 1, out nint lease, out _));
        string id = LeaseApi.Id(lease);
        _deployment.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(0, LeaseApi.Acquire(_api, client, "pro", 1, out nint later, out _));
        await server.DisposeAsync();

        using var hold = new ManualResetEventSlim(false);
        LeaseCallbacks.Hold = hold;
        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => LeaseCallbacks.Entered.IsSet);
        Task destroy = Task.Run(() => LeaseApi.Destroy(client), TestContext.Current.CancellationToken);
        Assert.NotSame(destroy, await Task.WhenAny(destroy, Task.Delay(300, TestContext.Current.CancellationToken)));
        _deployment.Time.Advance(TimeSpan.FromSeconds(6));
        Assert.NotSame(destroy, await Task.WhenAny(destroy, Task.Delay(300, TestContext.Current.CancellationToken)));
        _deployment.Time.Advance(TimeSpan.FromSeconds(60));
        await Deployment.WaitUntilAsync(() => LeaseApi.State(later) == 1);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        LeaseCallbacks.AfterHold = (client, lease);
        hold.Set();
        await destroy;
        LeaseCallbacks.Hold = null;

        (string seenId, int previous, int current, _, _, _) = Assert.Single(LeaseCallbacks.Changes());
        Assert.Equal((id, 0, 1), (seenId, previous, current));
        Assert.NotEqual(Environment.CurrentManagedThreadId, LeaseCallbacks.LastThread);
        Assert.Equal([$"after state 1 id {id} pin 0"], LeaseCallbacks.Inside.ToArray());
        _deployment.Time.Advance(TimeSpan.FromDays(1));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Single(LeaseCallbacks.Changes());
        LeaseApi.Free(lease);
        LeaseApi.Free(later);
    }

    /// <summary>
    /// The options are checked.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None beyond the deployment.
    ///
    /// Steps:
    /// - Create with: an https server, a license key with http on a loopback address, an app version of 64 bytes; then
    ///   http without a key, http on another host with a key, a text that is not a URL, no URL, a key with an expected server
    ///   license ID, an empty key, an app version of 65 bytes, struct_size one less; a null out.
    ///
    /// Expected:
    /// - The options are 128 bytes; 0 three times; ELM_INVALID_ARGUMENT (1) for the rest, with out NULL.
    /// </remarks>
    [Fact]
    public void Options_AreChecked()
    {
        Assert.Equal(128, LeaseApi.SizeOfOptions);
        foreach (
            ClientOptions good in new[]
            {
                Options("https://127.0.0.1:1/"),
                Options("http://127.0.0.1:1/", "ELM-AAAAA"),
                LeaseApi.With(_api, Options("https://127.0.0.1:1/"), appVersion: new string('v', 64)),
            }
        )
        {
            Assert.True(LeaseApi.Create(_api, good, out nint made) == 0, NativeApi.LastError);
            LeaseApi.Destroy(made);
        }

        ClientOptions smaller = Options("https://127.0.0.1:1/");
        smaller.StructSize--;
        foreach (
            ClientOptions bad in new[]
            {
                Options("http://127.0.0.1:1/"),
                Options("http://licenses.example.com/", "ELM-AAAAA"),
                Options("not a url"),
                Options(null),
                LeaseApi.With(_api, Options("https://127.0.0.1:1/", "ELM-AAAAA"), expected: "lic-srv"),
                Options("https://127.0.0.1:1/", string.Empty),
                LeaseApi.With(_api, Options("https://127.0.0.1:1/"), appVersion: new string('v', 65)),
                smaller,
            }
        )
        {
            Assert.Equal((1, (nint)0), (LeaseApi.Create(_api, bad, out nint made), made));
        }

        Assert.Equal(1, LeaseApi.Create(_api, Options("https://127.0.0.1:1/"), out _, nullOut: true));
    }

    private async Task<ElmServer> StartOpenServerAsync(string? data = null)
    {
        ElmServer server = await _deployment.StartServerAsync(data);
        _deployment.Time.Advance(Window);
        return server;
    }

    private static ServerSettings OnPort(ServerSettings settings, int port) =>
        new()
        {
            Product = settings.Product,
            AdminListen = settings.AdminListen,
            Listen = new Uri($"https://127.0.0.1:{port}"),
            LicensePath = settings.LicensePath,
            TrustRootKeyFiles = settings.TrustRootKeyFiles,
            DataDirectory = settings.DataDirectory,
            RateLimitPerIp = settings.RateLimitPerIp,
        };

    private ClientOptions Options(string? url, string? licenseKey = null) =>
        LeaseApi.Options(_api, _root, url, _deployment.NewFolder("client"), licenseKey);

    private nint Create(string url)
    {
        int code = LeaseApi.Create(_api, Options(url), out nint client);
        Assert.True(code == 0, NativeApi.LastError);
        return client;
    }
}
