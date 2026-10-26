using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ElasticLM.Client;
using ElasticLM.Issuing;
using ElasticLM.Licensing;

namespace ElasticLM.Server.Tests;

/// <summary>
/// <see cref="LicenseClient"/> against an in-process <c>elm-server</c> over loopback TLS (design §12.7, §13.4, §13.5).
/// </summary>
public sealed class LeaseClientTests : IAsyncDisposable
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly Deployment _deployment = new();

    public ValueTask DisposeAsync() => _deployment.DisposeAsync();

    /// <summary>
    /// A client acquires a lease over the self-signed TLS, renews it every third of its duration, and returns it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server (2 seats of pro, 10-minute leases) past its reclaim window; a client.
    ///
    /// Steps:
    /// - Acquire pro 2.5; advance 200 s; advance 200 s more; release.
    ///
    /// Expected:
    /// - Held with sequence 1, then 2, then 3, each token checked by the client; the server counts 1 seat, then none after
    ///   the release, and the lease is Released.
    /// </remarks>
    [Fact]
    public async Task Lease_AcquireRenewRelease_Works()
    {
        ElmServer server = await StartOpenServerAsync();
        LicenseClient client = _deployment.Client(server.PublicUrls[0]);

        Lease lease = Acquired(await client.AcquireLeaseAsync("pro", "2.5", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(LeaseState.Held, lease.State);
        Assert.Equal(1, lease.Token.Sequence);
        Assert.Equal(1, server.Engine.SeatsInUse("pro"));

        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => lease.Token.Sequence == 2);
        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => lease.Token.Sequence == 3);
        Assert.Equal(LeaseState.Held, lease.State);

        Assert.Null(await client.ReleaseLeaseAsync(lease, TestContext.Current.CancellationToken));
        Assert.Equal(LeaseState.Released, lease.State);
        Assert.Equal(0, server.Engine.SeatsInUse("pro"));
    }

    /// <summary>
    /// The server answers acquires during its reclaim window with ELM_SERVER_RECOVERING and Retry-After; the client returns
    /// that to the app without waiting. Past the seats it answers ELM_NO_SEATS.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A freshly started server with 2 seats; three clients.
    ///
    /// Steps:
    /// - Acquire at once; advance past the window; acquire with two clients; acquire with the third.
    ///
    /// Expected:
    /// - Failed with ELM_SERVER_RECOVERING and Retry-After 600 s; then two leases; then ELM_NO_SEATS.
    /// </remarks>
    [Fact]
    public async Task Acquire_RecoveringAndFull_ReturnsTheServerError()
    {
        ElmServer server = await _deployment.StartServerAsync();
        LicenseClient first = _deployment.Client(server.PublicUrls[0]);

        var recovering = Assert.IsType<LeaseResult.Failed>(
            await first.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken)
        );
        Assert.Equal(ErrorCode.ServerRecovering, recovering.Error.Code);
        Assert.Equal(Window, recovering.RetryAfter);

        _deployment.Time.Advance(Window);
        Acquired(await first.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
        Acquired(await _deployment.Client(server.PublicUrls[0]).AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
        var full = Assert.IsType<LeaseResult.Failed>(
            await _deployment.Client(server.PublicUrls[0]).AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken)
        );
        Assert.Equal(ErrorCode.NoSeats, full.Error.Code);
    }

    /// <summary>
    /// After a restart with a replaced server license, the client's renewal gets ELM_UNKNOWN_LEASE, the client reclaims the
    /// lease, renews it, and fetches and checks the new server license that the answer names.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A lease from a server on port P; the server stopped; a new server license for the same server (new jti); the server
    ///   started again on P (new nonce secret, empty state).
    ///
    /// Steps:
    /// - Advance 200 s so the client renews.
    ///
    /// Expected:
    /// - The lease is Held with sequence 2, its token names the new server license, and the new server counts 1 seat; the
    ///   client went through Reclaiming.
    /// </remarks>
    [Fact]
    public async Task Restart_ClientReclaimsAndRenews()
    {
        string data = _deployment.NewFolder("server");
        ElmServer before = await StartOpenServerAsync(data);
        int port = before.PublicUrls[0].Port;
        LicenseClient client = _deployment.Client(before.PublicUrls[0]);
        var states = new List<LeaseState>();
        client.LeaseStateChanged += (_, e) =>
        {
            lock (states)
            {
                states.Add(e.Current);
            }
        };
        Lease lease = Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));

        await before.DisposeAsync();
        _deployment.WriteServerLicense(data);
        ElmServer after = await _deployment.StartServerAsync(data, s => With(s, port));
        _deployment.Time.Advance(TimeSpan.FromSeconds(200));

        await Deployment.WaitUntilAsync(() => lease.Token.Sequence == 2);
        Assert.Equal(LeaseState.Held, lease.State);
        Assert.Contains(LeaseState.Reclaiming, states);
        Assert.Equal(1, after.Engine.SeatsInUse("pro"));
    }

    /// <summary>
    /// A client that holds more than 64 leases on a restarted server reclaims them in requests of 64 tokens.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server with 100 seats; one client holding 70 leases; the server restarted on the same port.
    ///
    /// Steps:
    /// - Advance 200 s so every lease renews.
    ///
    /// Expected:
    /// - All 70 leases are Held with sequence 2 and the new server counts 70 seats.
    /// </remarks>
    [Fact]
    public async Task Restart_MoreThan64Leases_AreReclaimedInBatches()
    {
        string data = _deployment.NewFolder("server");
        _deployment.WriteServerLicense(data, s => s with { Features = [new FeatureSpec("pro", Seats: 100)] });
        ElmServer before = await StartOpenServerAsync(data);
        int port = before.PublicUrls[0].Port;
        LicenseClient client = _deployment.Client(before.PublicUrls[0]);
        var leases = new List<Lease>();
        for (int i = 0; i < 70; i++)
        {
            leases.Add(Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken)));
        }

        await before.DisposeAsync();
        ElmServer after = await _deployment.StartServerAsync(data, s => With(s, port));
        _deployment.Time.Advance(TimeSpan.FromSeconds(200));

        await Deployment.WaitUntilAsync(() => leases.TrueForAll(l => l.Token.Sequence == 2));
        Assert.True(leases.TrueForAll(l => l.State == LeaseState.Held));
        Assert.Equal(70, after.Engine.SeatsInUse("pro"));
    }

    /// <summary>
    /// A server moved to another machine (another key, the same license ID) is refused by the pin until the pin is reset; a
    /// client configured with the license ID accepts it (design §13.5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Client A (first-connection pin) and client B (ExpectedServerLicenseId lic-srv) connected to a server on port P; that
    ///   server stopped and a server with another installation key and lic-srv started on P, past its window.
    ///
    /// Steps:
    /// - A acquires twice; resets the pin; acquires. B acquires twice.
    ///
    /// Expected:
    /// - A: ELM_SERVER_PIN_MISMATCH from the TLS check, then from the stored pin; after the reset a lease. B: the TLS check
    ///   fails once, then the new server is accepted.
    /// </remarks>
    [Fact]
    public async Task MovedServer_IsRefusedByThePinUntilReset()
    {
        ElmServer before = await StartOpenServerAsync();
        int port = before.PublicUrls[0].Port;
        LicenseClient a = _deployment.Client(before.PublicUrls[0]);
        LicenseClient b = _deployment.Client(before.PublicUrls[0], change: o => With(o, "lic-srv"));

        // Released before the move, so no renewal timer reconnects in the background while the clock advances.
        Assert.Null(
            await a.ReleaseLeaseAsync(
                Acquired(await a.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken)),
                TestContext.Current.CancellationToken
            )
        );
        Assert.Null(
            await b.ReleaseLeaseAsync(
                Acquired(await b.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken)),
                TestContext.Current.CancellationToken
            )
        );

        await before.DisposeAsync();
        await _deployment.StartServerAsync(change: s => With(s, port));
        _deployment.Time.Advance(Window);

        Assert.Equal(ErrorCode.ServerPinMismatch, Failed(await a.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.Equal(ErrorCode.ServerPinMismatch, Failed(await a.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken)));
        Assert.True(a.ResetServerPin(before.PublicUrls[0]));
        Acquired(await a.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCode.ServerPinMismatch, Failed(await b.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken)));
        Acquired(await b.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// On the first connection, a TLS key that is not the server license's key makes the client discard what it received
    /// (design §13.5), even though the license itself verifies.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server with a valid license that serves TLS with a self-signed certificate of another key (as a TLS proxy would).
    ///
    /// Steps:
    /// - Acquire.
    ///
    /// Expected:
    /// - ELM_SERVER_PIN_MISMATCH, and no pin is stored: after the server uses its own key, the client connects.
    /// </remarks>
    [Fact]
    public async Task FirstConnection_TlsKeyIsNotTheLicenseKey_IsRefused()
    {
        string pfx = _deployment.FilePath("other.pfx");
        using (ECDsa other = KeyFiles.CreateKey())
        {
            var request = new CertificateRequest("CN=proxy", other, HashAlgorithmName.SHA256);
            using X509Certificate2 certificate = request.CreateSelfSigned(Deployment.Start.AddDays(-1), Deployment.Start.AddDays(30));
            File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pkcs12));
        }

        string data = _deployment.NewFolder("server");
        ElmServer proxied = await StartOpenServerAsync(data, s => With(s, tlsCertificate: pfx));
        int port = proxied.PublicUrls[0].Port;
        string clientData = _deployment.NewFolder("client");
        LicenseClient client = _deployment.Client(proxied.PublicUrls[0], clientData);

        Assert.Equal(
            ErrorCode.ServerPinMismatch,
            Failed(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken))
        );
        Assert.False(Directory.Exists(System.IO.Path.Combine(clientData, "servers")));

        await proxied.DisposeAsync();
        await _deployment.StartServerAsync(data, s => With(s, port));
        _deployment.Time.Advance(Window);
        Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A damaged pin file cannot vouch for a server, so the client refuses the server until the pin is reset.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A client that connected once (its pin is stored) and was disposed; the pin file overwritten with text that is not
    ///   JSON; a new client on the same data folder.
    ///
    /// Steps:
    /// - Acquire; reset the pin; acquire.
    ///
    /// Expected:
    /// - ELM_SERVER_PIN_MISMATCH, then a lease.
    /// </remarks>
    [Fact]
    public async Task DamagedPin_RefusesTheServerUntilReset()
    {
        ElmServer server = await StartOpenServerAsync();
        string data = _deployment.NewFolder("client");
        LicenseClient first = _deployment.Client(server.PublicUrls[0], data);
        Assert.Null(
            await first.ReleaseLeaseAsync(
                Acquired(await first.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken)),
                TestContext.Current.CancellationToken
            )
        );
        await first.DisposeAsync();
        File.WriteAllText(Assert.Single(Directory.GetFiles(System.IO.Path.Combine(data, "servers"))), "{damaged");

        LicenseClient second = _deployment.Client(server.PublicUrls[0], data);
        Assert.Equal(
            ErrorCode.ServerPinMismatch,
            Failed(await second.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken))
        );
        Assert.True(second.ResetServerPin(server.PublicUrls[0]));
        Acquired(await second.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A server license signed under another vendor's root is refused when the client connects.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server; a client that trusts only another root key.
    ///
    /// Steps:
    /// - Acquire.
    ///
    /// Expected:
    /// - ELM_UNTRUSTED_KEY.
    /// </remarks>
    [Fact]
    public async Task ServerLicense_FromAnotherVendor_IsRefused()
    {
        ElmServer server = await StartOpenServerAsync();
        using ECDsa otherRoot = KeyFiles.CreateKey();
        TrustStore other = TrustStore.Create([KeyFiles.ExportPublic(otherRoot)], []);
        LicenseClient client = _deployment.Client(
            server.PublicUrls[0],
            change: o => new LicenseClientOptions
            {
                ProductId = o.ProductId,
                ServerUrl = o.ServerUrl,
                Trust = other,
                DataDirectory = o.DataDirectory,
                ClockGuard = o.ClockGuard,
                TimeProvider = o.TimeProvider,
            }
        );

        Assert.Equal(ErrorCode.UntrustedKey, Failed(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken)));
    }

    /// <summary>
    /// When a renewal's answer is lost after the server renewed, the client's retry names the unanswered request, and the
    /// server gives the same token again instead of taking the retry for a clone (design §10.4).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A lease (clone strategy deny); the client's HTTP handler loses the answer of the next renewal after the server
    ///   handled it.
    ///
    /// Steps:
    /// - Advance 200 s (the renewal, answer lost); advance 15 s (the retry); advance 200 s (the next renewal).
    ///
    /// Expected:
    /// - Retrying with ELM_NETWORK_ERROR; then Held with sequence 2; then sequence 3. The server never stopped the lease.
    /// </remarks>
    [Fact]
    public async Task LostRenewalAnswer_RetryIsNotAClone()
    {
        ElmServer server = await StartOpenServerAsync();
        var losing = new LosingHandler();
        LicenseClient client = _deployment.Client(
            server.PublicUrls[0],
            change: o => new LicenseClientOptions
            {
                ProductId = o.ProductId,
                ServerUrl = o.ServerUrl,
                Trust = o.Trust,
                DataDirectory = o.DataDirectory,
                ClockGuard = o.ClockGuard,
                TimeProvider = o.TimeProvider,
                WrapHandler = inner =>
                {
                    losing.InnerHandler = inner;
                    return losing;
                },
            }
        );
        Lease lease = Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
        losing.LoseNextRenewal = true;

        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => lease.State == LeaseState.Retrying);
        Assert.Equal(ErrorCode.NetworkError, lease.LastError?.Code);
        Assert.Equal(1, lease.Token.Sequence);

        _deployment.Time.Advance(TimeSpan.FromSeconds(15));
        await Deployment.WaitUntilAsync(() => lease.State == LeaseState.Held);
        Assert.Equal(2, lease.Token.Sequence);

        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => lease.Token.Sequence == 3);
        Assert.Equal(LeaseState.Held, lease.State);
    }

    /// <summary>
    /// A lease the server stopped as a clone is lost at the client's next renewal (design §10.4).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A lease; the server sees a renewal with a rolled-back sequence number (another copy of the installation).
    ///
    /// Steps:
    /// - Advance 200 s so the client renews.
    ///
    /// Expected:
    /// - The lease is Lost with ELM_CLONE_DETECTED and an event says so.
    /// </remarks>
    [Fact]
    public async Task Clone_StoppedLease_IsLost()
    {
        ElmServer server = await StartOpenServerAsync();
        LicenseClient client = _deployment.Client(server.PublicUrls[0]);
        LeaseStateChangedEventArgs? lost = null;
        client.LeaseStateChanged += (_, e) => lost = e.Current == LeaseState.Lost ? e : lost;
        Lease lease = Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
        Assert.IsType<Leasing.LeaseOutcome.Refused>(server.Engine.Renew(lease.Id, 5, client.InstallationKeyThumbprint, "another-copy", []));

        _deployment.Time.Advance(TimeSpan.FromSeconds(200));

        await Deployment.WaitUntilAsync(() => lost is not null);
        Assert.Equal(ErrorCode.CloneDetected, lost!.Error?.Code);
        Assert.Equal(LeaseState.Lost, lease.State);
    }

    /// <summary>
    /// When the server is down, the client retries from 15 seconds and loses the lease at its expiry (design §12.7.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A lease (10 minutes); the server stopped.
    ///
    /// Steps:
    /// - Advance 200 s (renewal); advance 15 s (first retry); advance past the expiry.
    ///
    /// Expected:
    /// - Retrying with ELM_NETWORK_ERROR after the renewal and still after the first retry; Lost after the expiry.
    /// </remarks>
    [Fact]
    public async Task ServerDown_RetriesThenLosesTheLease()
    {
        ElmServer server = await StartOpenServerAsync();
        LicenseClient client = _deployment.Client(server.PublicUrls[0]);
        int retries = 0;
        client.LeaseStateChanged += (_, e) => retries += e.Current == LeaseState.Retrying ? 1 : 0;
        Lease lease = Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
        await server.DisposeAsync();

        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => retries == 1);
        Assert.Equal(LeaseState.Retrying, lease.State);
        Assert.Equal(ErrorCode.NetworkError, lease.LastError?.Code);

        _deployment.Time.Advance(TimeSpan.FromSeconds(15));
        await Deployment.WaitUntilAsync(() => retries == 2);

        _deployment.Time.Advance(TimeSpan.FromMinutes(10));
        await Deployment.WaitUntilAsync(() => lease.State == LeaseState.Lost);
    }

    /// <summary>
    /// Disposing the client returns its leases (design §17.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two leases of one client.
    ///
    /// Steps:
    /// - Dispose the client.
    ///
    /// Expected:
    /// - The server counts no seat; both leases are Released.
    /// </remarks>
    [Fact]
    public async Task Dispose_ReturnsTheLeases()
    {
        ElmServer server = await StartOpenServerAsync();
        LicenseClient client = _deployment.Client(server.PublicUrls[0]);
        Lease first = Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
        Lease second = Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));

        await client.DisposeAsync();

        Assert.Equal(0, server.Engine.SeatsInUse("pro"));
        Assert.Equal(LeaseState.Released, first.State);
        Assert.Equal(LeaseState.Released, second.State);
    }

    /// <summary>
    /// Passes requests on and, when asked, throws away the answer of the next renewal after the server handled it.
    /// </summary>
    private sealed class LosingHandler : DelegatingHandler
    {
        public bool LoseNextRenewal { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (LoseNextRenewal && request.RequestUri!.AbsolutePath.EndsWith("/renew", StringComparison.Ordinal))
            {
                LoseNextRenewal = false;
                response.Dispose();
                throw new HttpRequestException("The answer was lost on the way.");
            }

            return response;
        }
    }

    private static Lease Acquired(LeaseResult result) => Assert.IsType<LeaseResult.Acquired>(result).Lease;

    private static ErrorCode Failed(LeaseResult result) => Assert.IsType<LeaseResult.Failed>(result).Error.Code;

    private static ServerSettings With(ServerSettings settings, int? port = null, string? tlsCertificate = null) =>
        new()
        {
            Product = settings.Product,
            Listen = port is int p ? new Uri($"https://127.0.0.1:{p}") : settings.Listen,
            LicensePath = settings.LicensePath,
            TrustRootKeyFiles = settings.TrustRootKeyFiles,
            DataDirectory = settings.DataDirectory,
            RateLimitPerIp = settings.RateLimitPerIp,
            TlsCertificate = tlsCertificate ?? settings.TlsCertificate,
        };

    private static LicenseClientOptions With(LicenseClientOptions options, string expectedLicenseId) =>
        new()
        {
            ProductId = options.ProductId,
            ServerUrl = options.ServerUrl,
            Trust = options.Trust,
            DataDirectory = options.DataDirectory,
            ClockGuard = options.ClockGuard,
            TimeProvider = options.TimeProvider,
            ExpectedServerLicenseId = expectedLicenseId,
        };

    private async Task<ElmServer> StartOpenServerAsync(string? data = null, Func<ServerSettings, ServerSettings>? change = null)
    {
        ElmServer server = await _deployment.StartServerAsync(data, change);
        _deployment.Time.Advance(Window);
        return server;
    }
}
