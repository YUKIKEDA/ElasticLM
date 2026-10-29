using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ElasticLM.Client;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using static ElasticLM.Service.Tests.ElmServiceActivationsTests;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The SaaS mode of <see cref="LicenseClient"/> (design §12.7.6, M4d2) against a real <c>elm-service</c> in the same process:
/// leases and borrows with a license key, verified with the service's certificates, lost instead of reclaimed.
/// </summary>
public sealed class HostedLeaseClientTests
{
    private const string TwoSeats = """[{"code":"core","seats":2}]""";

    /// <summary>
    /// A client with a license key acquires a lease from the service, renews it every third of its duration, returns it, and
    /// returns the leases it holds when disposed; the key is never written next to the installation key.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy (lease 600 seconds) and a license with 2 seats of core; a client with its key; each engine.
    ///
    /// Steps:
    /// - Acquire; move the clock 200 seconds; release; acquire again and dispose the client; list the leases; read every file
    ///   in the client's folder.
    ///
    /// Expected:
    /// - The lease is held with sequence 1, for core, 1 seat; after 200 seconds its sequence is 2 and it is still held; the
    ///   release returns no error and the lease is released; after the dispose the service holds no lease; no file contains
    ///   the license key.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Hosted_AcquireRenewRelease_UsesTheService(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        (string licenseId, string key) = await LicenseAsync(admin, await CloudPolicyAsync(admin, string.Empty), TwoSeats);
        string folder = test.FilePath("client");
        LicenseClient client = Client(test, service, key, folder);
        CancellationToken cancel = TestContext.Current.CancellationToken;

        Lease lease = Acquired(await client.AcquireLeaseAsync("core", cancellationToken: cancel));
        Assert.Equal((LeaseState.Held, 1L, "core", 1), (lease.State, lease.Token.Sequence, lease.FeatureId, lease.Seats));
        test.Time.Advance(TimeSpan.FromSeconds(200));
        await TestService.WaitForAsync(() => Task.FromResult(lease.Token.Sequence == 2));
        Assert.Equal(LeaseState.Held, lease.State);
        Assert.Null(await client.ReleaseLeaseAsync(lease, cancel));
        Assert.Equal(LeaseState.Released, lease.State);

        Acquired(await client.AcquireLeaseAsync("core", cancellationToken: cancel));
        await client.DisposeAsync();
        Assert.Equal(0, (await TestService.GetAsync(admin, $"v1/admin/licenses/{licenseId}/leases")).GetProperty("items").GetArrayLength());
        string bare = key.Replace("-", string.Empty, StringComparison.Ordinal);
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            string text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Assert.False(text.Contains(key, StringComparison.OrdinalIgnoreCase) || text.Contains(bare, StringComparison.OrdinalIgnoreCase), file);
        }
    }

    /// <summary>
    /// A lease the service no longer knows is lost at once, not reclaimed (design §12.7.3); a clone stopped by the service is
    /// lost; a service that cannot be reached leaves the lease retrying.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy (lease 600 seconds, cloneStrategy deny) and a license with 2 seats of core; a client.
    ///
    /// Steps:
    /// - Acquire A; release it with the administration API; move the clock 200 seconds. Acquire B; renew it with sequence 1
    ///   from another copy of the installation (the same key folder); move the clock 200 seconds. Acquire C; stop the
    ///   service; move the clock 200 seconds.
    ///
    /// Expected:
    /// - A is lost with ELM_UNKNOWN_LEASE and an event from held to lost; B is lost with ELM_CLONE_DETECTED; C is retrying
    ///   with ELM_NETWORK_ERROR.
    /// </remarks>
    [Fact]
    public async Task Hosted_RenewalFails_LostOrRetrying()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        (_, string key) = await LicenseAsync(admin, await CloudPolicyAsync(admin, string.Empty), TwoSeats);
        string folder = test.FilePath("client");
        LicenseClient client = Client(test, service, key, folder);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        var changes = new List<LeaseStateChangedEventArgs>();
        client.LeaseStateChanged += (_, e) =>
        {
            lock (changes)
            {
                changes.Add(e);
            }
        };

        Lease a = Acquired(await client.AcquireLeaseAsync("core", cancellationToken: cancel));
        using (HttpResponseMessage released = await admin.DeleteAsync($"v1/admin/leases/{a.Id}", cancel))
        {
            Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        }

        test.Time.Advance(TimeSpan.FromSeconds(200));
        await TestService.WaitForAsync(() => Task.FromResult(a.State == LeaseState.Lost));
        Assert.Equal(ErrorCode.UnknownLease, a.LastError?.Code);
        lock (changes)
        {
            Assert.Contains(changes, e => e.Lease == a && e.Previous == LeaseState.Held && e.Current == LeaseState.Lost);
        }

        Lease b = Acquired(await client.AcquireLeaseAsync("core", cancellationToken: cancel));
        using (var copy = TestDevice.Create(test, service, folder))
        {
            Assert.Equal(client.InstallationKeyThumbprint, copy.KeyId);
            Assert.Equal(HttpStatusCode.OK, (await copy.RenewAsync(b.Id, 1)).Status);
        }

        test.Time.Advance(TimeSpan.FromSeconds(200));
        await TestService.WaitForAsync(() => Task.FromResult(b.State == LeaseState.Lost));
        Assert.Equal(ErrorCode.CloneDetected, b.LastError?.Code);

        Lease c = Acquired(await client.AcquireLeaseAsync("core", cancellationToken: cancel));
        await service.StopAsync();
        test.Time.Advance(TimeSpan.FromSeconds(200));
        await TestService.WaitForAsync(() => Task.FromResult(c.State == LeaseState.Retrying));
        Assert.Equal(ErrorCode.NetworkError, c.LastError?.Code);
        await client.DisposeAsync();
    }

    /// <summary>
    /// A borrow is saved with the service's certificates and no license key, found again by a new client, returned early,
    /// and deleted once expired; a changed file, or one of another service URL, is not used.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy with borrowLimit 86400 and a license with 2 seats of core; a client; each engine.
    ///
    /// Steps:
    /// - Borrow core for 3600 seconds twice; read the saved files; dispose the client. Write a copy of the first file with a
    ///   changed signature, and one naming another URL. With a new client on the same folder, read the borrowed leases;
    ///   return the first early; move the clock 3601 seconds and read the borrowed leases.
    ///
    /// Expected:
    /// - Each file has ver 1, the service URL, the token, and the service's certificate, and no license, key, or license key;
    ///   the new client returns the two borrows only; the return gives no error, deletes the file, and the service lists one
    ///   lease; after the expiry no borrow is returned and the second file is deleted, the two copies are left.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Hosted_Borrow_IsSavedWithTheCertificates(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        (string licenseId, string key) = await LicenseAsync(admin, await CloudPolicyAsync(admin, "\"borrowLimit\":86400"), TwoSeats);
        string folder = test.FilePath("client");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        LicenseClient first = Client(test, service, key, folder);
        Lease one = Acquired(await first.BorrowLeaseAsync("core", TimeSpan.FromHours(1), cancellationToken: cancel));
        Lease two = Acquired(await first.BorrowLeaseAsync("core", TimeSpan.FromHours(1), cancellationToken: cancel));
        string borrows = Directory.EnumerateDirectories(folder, "borrows", SearchOption.AllDirectories).Single();
        string onePath = Path.Combine(borrows, one.Id + ".json");
        string signing = File.ReadAllText(test.FilePath("signing.cert")).Trim();
        foreach (Lease lease in new[] { one, two })
        {
            string text = File.ReadAllText(Path.Combine(borrows, lease.Id + ".json"));
            using JsonDocument saved = JsonDocument.Parse(text);
            JsonElement root = saved.RootElement;
            Assert.Equal(
                (1, HttpTargetUri.Normalize(service.Url), lease.Token.Token, signing, false, false, false),
                (
                    root.GetProperty("ver").GetInt32(),
                    root.GetProperty("server").GetString(),
                    root.GetProperty("lease").GetString(),
                    root.GetProperty("certificates").EnumerateArray().Single().GetString()!.Trim(),
                    root.TryGetProperty("license", out _),
                    root.TryGetProperty("key", out _),
                    text.Contains(key, StringComparison.OrdinalIgnoreCase)
                )
            );
        }

        await first.DisposeAsync();
        string original = File.ReadAllText(onePath);
        string token = one.Token.Token;
        string changed = token[..^4] + (token[^4] == 'A' ? 'B' : 'A') + token[^3..];
        File.WriteAllText(Path.Combine(borrows, "changed.json"), original.Replace(token, changed, StringComparison.Ordinal));
        File.WriteAllText(
            Path.Combine(borrows, "elsewhere.json"),
            original.Replace(HttpTargetUri.Normalize(service.Url), "https://elsewhere.example/", StringComparison.Ordinal)
        );

        LicenseClient second = Client(test, service, key, folder);
        IReadOnlyList<Lease> found = second.GetBorrowedLeases();
        Assert.Equal(new[] { one.Id, two.Id }.Order(), found.Select(l => l.Id).Order());
        Assert.Null(await second.ReleaseLeaseAsync(found.Single(l => l.Id == one.Id), cancel));
        Assert.False(File.Exists(onePath));
        Assert.Equal(1, (await TestService.GetAsync(admin, $"v1/admin/licenses/{licenseId}/leases")).GetProperty("items").GetArrayLength());

        test.Time.Advance(TimeSpan.FromSeconds(3601));
        Assert.Empty(second.GetBorrowedLeases());
        Assert.Equal(["changed.json", "elsewhere.json"], [.. Directory.EnumerateFiles(borrows).Select(Path.GetFileName).Order()]);
        await second.DisposeAsync();
    }

    /// <summary>
    /// A lease is used only when the client's trust store certifies the service's signing key (design §12.7.5 step 1, T18);
    /// a service whose TLS certificate the OS does not trust is not reached, since a client with a license key keeps no pin.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating license with 2 seats of core. A second service on https with a self-signed certificate.
    ///
    /// Steps:
    /// - Acquire with a client whose trust store has another root key. Acquire from the https service with a normal client.
    ///
    /// Expected:
    /// - ELM_UNTRUSTED_KEY "No trusted certificate certifies the lease signing key."; ELM_NETWORK_ERROR.
    /// </remarks>
    [Fact]
    public async Task Hosted_UntrustedSigner_OrTls_IsRefused()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        (_, string key) = await LicenseAsync(admin, await CloudPolicyAsync(admin, string.Empty), TwoSeats);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using ECDsa otherRoot = KeyFiles.CreateKey();
        LicenseClient stranger = Client(test, service, key, test.FilePath("stranger"), TrustStore.Create([KeyFiles.ExportPublic(otherRoot)], []));
        LeaseResult refused = await stranger.AcquireLeaseAsync("core", cancellationToken: cancel);
        Assert.Equal(
            (ErrorCode.UntrustedKey, "No trusted certificate certifies the lease signing key."),
            refused is LeaseResult.Failed failed ? (failed.Error.Code, failed.Error.Message) : default
        );
        await stranger.DisposeAsync();

        await using TestService tls = TestService.Open("sqlite");
        using ECDsa tlsKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=elm-service-test", tlsKey, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using X509Certificate2 certificate = request.CreateSelfSigned(TestService.Start.AddDays(-1), TestService.Start.AddDays(30));
        File.WriteAllText(tls.FilePath("tls.crt"), certificate.ExportCertificatePem());
        File.WriteAllText(tls.FilePath("tls.key"), tlsKey.ExportPkcs8PrivateKeyPem());
        ElmService https = await tls.StartAsync(s =>
            s with
            {
                Listen = new Uri("https://127.0.0.1:0"),
                TlsCertificate = tls.FilePath("tls.crt"),
                TlsKey = tls.FilePath("tls.key"),
            }
        );
        LicenseClient client = Client(tls, https, key, tls.FilePath("client"));
        LeaseResult unreached = await client.AcquireLeaseAsync("core", cancellationToken: cancel);
        Assert.Equal(ErrorCode.NetworkError, unreached is LeaseResult.Failed f ? f.Error.Code : default);
        await client.DisposeAsync();
    }

    /// <summary>
    /// The service's rate limit gives a failed acquisition with its Retry-After, as an on-premises server's reclaim window
    /// does.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - RateLimitClientPerKey 1; a cloud-floating license with 2 seats of core.
    ///
    /// Steps:
    /// - Acquire twice at the same instant.
    ///
    /// Expected:
    /// - The first is acquired; the second fails with ELM_RATE_LIMITED and a retry after 1 second.
    /// </remarks>
    [Fact]
    public async Task Hosted_RateLimited_GivesRetryAfter()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync(s => s with { RateLimitClientPerKey = 1 });
        HttpClient admin = test.Http(service, owner);
        (_, string key) = await LicenseAsync(admin, await CloudPolicyAsync(admin, string.Empty), TwoSeats);
        LicenseClient client = Client(test, service, key, test.FilePath("client"));
        CancellationToken cancel = TestContext.Current.CancellationToken;

        Acquired(await client.AcquireLeaseAsync("core", cancellationToken: cancel));
        LeaseResult second = await client.AcquireLeaseAsync("core", cancellationToken: cancel);
        Assert.Equal(
            (ErrorCode.RateLimited, TimeSpan.FromSeconds(1)),
            second is LeaseResult.Failed failed ? (failed.Error.Code, failed.RetryAfter) : default
        );
        await client.DisposeAsync();
    }

    /// <summary>
    /// The options of the SaaS mode are checked when the client is made, and it has no server pin to reset.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A trust store; a folder for the key.
    ///
    /// Steps:
    /// - Make clients with a license key and: http on another host; an empty key; an expected server license ID. Make one
    ///   with http on 127.0.0.1 and reset its pin.
    ///
    /// Expected:
    /// - ArgumentException with "The service URL must be an absolute https URL, or http on a loopback address." and twice
    ///   "A license key must not be empty, and has no expected server license ID."; the last is made, and the reset returns
    ///   false.
    /// </remarks>
    [Fact]
    public async Task Hosted_Options_AreChecked()
    {
        await using TestService test = TestService.Open("sqlite");
        TrustStore trust = TrustStore.Create([File.ReadAllText(test.FilePath("root.pub.pem"))], []);
        LicenseClientOptions Options(string url, string licenseKey, string? expected = null) =>
            new()
            {
                ProductId = "app",
                ServerUrl = new Uri(url),
                LicenseKey = licenseKey,
                ExpectedServerLicenseId = expected,
                Trust = trust,
                DataDirectory = test.FilePath("client"),
                TimeProvider = test.Time,
            };

        Assert.Equal(
            "The service URL must be an absolute https URL, or http on a loopback address. (Parameter 'options')",
            Assert.Throws<ArgumentException>(() => LicenseClient.TryCreate(Options("http://elm.example/", "K"), out _, out _)).Message
        );
        const string KeyRule = "A license key must not be empty, and has no expected server license ID. (Parameter 'options')";
        Assert.Equal(
            KeyRule,
            Assert.Throws<ArgumentException>(() => LicenseClient.TryCreate(Options("https://elm.example/", string.Empty), out _, out _)).Message
        );
        Assert.Equal(
            KeyRule,
            Assert.Throws<ArgumentException>(() => LicenseClient.TryCreate(Options("https://elm.example/", "K", "lic"), out _, out _)).Message
        );
        Assert.True(LicenseClient.TryCreate(Options("http://127.0.0.1:9/", "K"), out LicenseClient? client, out LicenseError? error), error?.Message);
        Assert.False(client!.ResetServerPin(new Uri("http://127.0.0.1:9/")));
        await client.DisposeAsync();
    }

    private static LicenseClient Client(TestService test, ElmService service, string key, string folder, TrustStore? trust = null)
    {
        var options = new LicenseClientOptions
        {
            ProductId = "app",
            ServerUrl = service.Url,
            LicenseKey = key,
            Trust = trust ?? TrustStore.Create([File.ReadAllText(test.FilePath("root.pub.pem"))], []),
            DataDirectory = folder,
            TimeProvider = test.Time,
        };
        Assert.True(LicenseClient.TryCreate(options, out LicenseClient? client, out LicenseError? error), error?.Message);
        return client!;
    }

    private static Lease Acquired(LeaseResult result) =>
        result is LeaseResult.Acquired acquired ? acquired.Lease : throw new InvalidOperationException(result.ToString());

    // Product app with feature core, and a cloud-floating policy of it with the members given.
    private static async Task<string> CloudPolicyAsync(HttpClient admin, string members)
    {
        string product = await ProductAsync(admin, "app", "core");
        return await TestService.PolicyAsync(admin, product, members.Length > 0 ? "\"floating\":\"cloud\"," + members : "\"floating\":\"cloud\"");
    }
}
