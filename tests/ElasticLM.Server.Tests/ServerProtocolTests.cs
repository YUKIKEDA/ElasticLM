using System.Net;
using System.Text;
using System.Text.Json;
using ElasticLM.Client;
using ElasticLM.Issuing;
using ElasticLM.Licensing;

namespace ElasticLM.Server.Tests;

/// <summary>
/// The start of <c>elm-server</c> (design §13.1) and its HTTP protocol as seen without the client library: limits, proofs,
/// errors (design §12.1, §12.2, §12.7.1, §13.4, §13.8).
/// </summary>
public sealed class ServerProtocolTests : IAsyncDisposable
{
    private readonly Deployment _deployment = new();

    public ValueTask DisposeAsync() => _deployment.DisposeAsync();

    /// <summary>
    /// The server does not start without a valid server license for its own key, or without the vendor's root keys.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Data folders with: a standard license, a trial license, a server license bound to another key, no license, and a
    ///   valid license with no trust setting.
    ///
    /// Steps:
    /// - Start a server on each.
    ///
    /// Expected:
    /// - ServerStartException naming ELM_WRONG_KIND twice, ELM_WRONG_INSTALLATION, the server's own key thumbprint for the
    ///   missing license, and trust.rootKeys.
    /// </remarks>
    [Fact]
    public async Task Start_WithoutAValidServerLicenseOrTrust_Fails()
    {
        async Task<string> StartError(string data, Func<ServerSettings, ServerSettings>? change = null)
        {
            ServerSettings settings = _deployment.Settings(data);
            var e = await Assert.ThrowsAsync<ServerStartException>(() =>
                ElmServer.StartAsync(change?.Invoke(settings) ?? settings, _deployment.HostOptions(), TestContext.Current.CancellationToken)
            );
            return e.Message;
        }

        string standard = _deployment.NewFolder("standard");
        _deployment.WriteServerLicense(
            standard,
            s =>
                s with
                {
                    Kind = LicenseKind.Standard,
                    Features = [new FeatureSpec("pro")],
                    LeaseDuration = null,
                    BorrowLimit = null,
                    OveragePercent = null,
                    CloneStrategy = null,
                }
        );
        Assert.Contains("ELM_WRONG_KIND", await StartError(standard), StringComparison.Ordinal);

        string trial = _deployment.NewFolder("trial");
        _deployment.WriteServerLicense(
            trial,
            s =>
                s with
                {
                    Kind = LicenseKind.Trial,
                    Features = [new FeatureSpec("pro")],
                    GracePeriod = TimeSpan.Zero,
                    LeaseDuration = null,
                    BorrowLimit = null,
                    OveragePercent = null,
                    CloneStrategy = null,
                }
        );
        Assert.Contains("ELM_WRONG_KIND", await StartError(trial), StringComparison.Ordinal);

        string other = _deployment.NewFolder("other");
        string otherKey = Deployment.ServerKeyId(_deployment.NewFolder("elsewhere"));
        _deployment.WriteServerLicense(other, s => s with { InstallationKeyThumbprint = otherKey });
        Assert.Contains("ELM_WRONG_INSTALLATION", await StartError(other), StringComparison.Ordinal);

        string missing = _deployment.NewFolder("missing");
        Assert.Contains(Deployment.ServerKeyId(missing), await StartError(missing), StringComparison.Ordinal);

        string untrusted = _deployment.NewFolder("untrusted");
        _deployment.WriteServerLicense(untrusted);
        static ServerSettings WithoutTrust(ServerSettings s) =>
            new()
            {
                Product = s.Product,
                AdminListen = s.AdminListen,
                Listen = s.Listen,
                LicensePath = s.LicensePath,
                DataDirectory = s.DataDirectory,
            };
        Assert.Contains("trust.rootKeys", await StartError(untrusted, WithoutTrust), StringComparison.Ordinal);
    }

    /// <summary>
    /// The root keys may come from ELM_TRUST_ROOT_KEYS instead of files (design §13.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a valid license; settings without trust.rootKeys.
    ///
    /// Steps:
    /// - Start with the variable holding the root key PEM.
    ///
    /// Expected:
    /// - The server starts and its key is the license's cnf.jkt.
    /// </remarks>
    [Fact]
    public async Task Start_TrustFromEnvironment_Works()
    {
        string data = _deployment.NewFolder("server");
        _deployment.WriteServerLicense(data);
        ServerSettings settings = _deployment.Settings(data);
        var withoutFiles = new ServerSettings
        {
            Product = settings.Product,
            AdminListen = settings.AdminListen,
            Listen = settings.Listen,
            LicensePath = settings.LicensePath,
            DataDirectory = settings.DataDirectory,
        };
        string pem = File.ReadAllText(_deployment.RootKeyFile);

        await using ElmServer server = await ElmServer.StartAsync(
            withoutFiles,
            _deployment.HostOptions(name => name == "ELM_TRUST_ROOT_KEYS" ? pem : null),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(Deployment.ServerKeyId(data), server.KeyId);
    }

    /// <summary>
    /// GET /v1/server/license returns the license file and the server's JWK, from which a client builds the server identity;
    /// GET /v1/nonce returns a nonce in the body and the ELM-Nonce header.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server.
    ///
    /// Steps:
    /// - Get both endpoints over a raw HTTP client.
    ///
    /// Expected:
    /// - The license and key verify as a server identity whose key is the server's; the nonce is 75 characters, the same in
    ///   body and header.
    /// </remarks>
    [Fact]
    public async Task ServerLicenseAndNonce_HaveTheirShapes()
    {
        ElmServer server = await _deployment.StartServerAsync();
        using HttpClient http = Deployment.RawHttp(server.PublicUrls[0]);

        using JsonDocument answer = JsonDocument.Parse(await http.GetStringAsync("v1/server/license", TestContext.Current.CancellationToken));
        byte[] license = Encoding.UTF8.GetBytes(answer.RootElement.GetProperty("license").GetRawText());
        byte[] key = Encoding.UTF8.GetBytes(answer.RootElement.GetProperty("key").GetRawText());
        var verifier = new LicenseVerifier(_deployment.Trust, _deployment.Time, StatelessClockGuard.Instance);
        Assert.True(ServerIdentity.TryVerify(verifier, license, key, "acme-cad", out ServerIdentity? identity, out _));
        using (identity)
        {
            Assert.Equal(server.KeyId, identity!.KeyId);
        }

        using HttpResponseMessage nonce = await http.GetAsync("v1/nonce", TestContext.Current.CancellationToken);
        using JsonDocument body = JsonDocument.Parse(await nonce.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string value = body.RootElement.GetProperty("nonce").GetString()!;
        Assert.Equal(75, value.Length);
        Assert.Equal(value, Assert.Single(nonce.Headers.GetValues("ELM-Nonce")));
    }

    /// <summary>
    /// Proofs are required and checked (design §12.2): no proof, a stale nonce, and a replayed proof are refused with RFC 9457
    /// problems that carry the §19 code, and every lease answer carries a fresh nonce.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server past its window; an installation key.
    ///
    /// Steps:
    /// - Acquire without a proof; with a proof on a nonce older than 300 s; with a good proof twice.
    ///
    /// Expected:
    /// - 401 ELM_PROOF_INVALID as application/problem+json; 401 ELM_NONCE_REQUIRED with an ELM-Nonce header; 201 with
    ///   ELM-Server-License and ELM-Nonce, then 401 ELM_REPLAYED.
    /// </remarks>
    [Fact]
    public async Task Proofs_AreRequiredFreshAndSingleUse()
    {
        await using Raw raw = await Raw.StartAsync(_deployment);
        byte[] body = Raw.Json("""{"feature":"pro","count":1}""");

        using HttpResponseMessage unsigned = await raw.SendAsync(HttpMethod.Post, "v1/leases", body, proof: null);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        Assert.Equal("application/problem+json", unsigned.Content.Headers.ContentType?.MediaType);
        await Raw.AssertCodeAsync(unsigned, "ELM_PROOF_INVALID");

        string nonce = await raw.NonceAsync();
        _deployment.Time.Advance(TimeSpan.FromSeconds(301));
        using HttpResponseMessage stale = await raw.SendAsync(
            HttpMethod.Post,
            "v1/leases",
            body,
            raw.Proof(HttpMethod.Post, "v1/leases", body, nonce)
        );
        await Raw.AssertCodeAsync(stale, "ELM_NONCE_REQUIRED");
        Assert.True(stale.Headers.Contains("ELM-Nonce"));

        string proof = raw.Proof(HttpMethod.Post, "v1/leases", body, await raw.NonceAsync());
        using HttpResponseMessage first = await raw.SendAsync(HttpMethod.Post, "v1/leases", body, proof);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(1, raw.Server.Engine.LeaseCount);
        Assert.True(first.Headers.Contains("ELM-Server-License"));
        Assert.True(first.Headers.Contains("ELM-Nonce"));
        using HttpResponseMessage replayed = await raw.SendAsync(HttpMethod.Post, "v1/leases", body, proof);
        await Raw.AssertCodeAsync(replayed, "ELM_REPLAYED");
    }

    /// <summary>
    /// Request bodies are limited and checked before the engine sees them (design §8.6, §12.1, §12.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server past its window; an installation key.
    ///
    /// Steps:
    /// - Acquire with: a body of exactly 64 KiB of padding and one byte more, with Content-Length and chunked; not JSON; 9 fingerprints; an upper-case
    ///   fingerprint; a host of 256 bytes; an app of 65 bytes; a version of 33 characters; borrow 0; borrow 7 days + 1 s
    ///   (the license allows 7 days); borrow 60 s without fingerprints; borrow -1 and 100 years + 1 s; borrow of exactly 100 years; count 0.
    ///   Acquire with a version of exactly 32 characters; with every other field exactly at its limit (names with a two-byte
    ///   character); with an app one byte over. Renew
    ///   with 16 retry IDs of 64 characters, 17 IDs, an ID of 65 characters, and a null ID. Reclaim 65 tokens.
    ///
    /// Expected:
    /// - 64 KiB is read (a JSON error, ELM_MALFORMED) and 64 KiB + 1 is 413 ELM_PAYLOAD_TOO_LARGE; ELM_MALFORMED for the
    ///   shapes; ELM_INVALID_ARGUMENT for borrow 0 and count 0; ELM_BORROW_NOT_ALLOWED for the borrow over 7 days;
    ///   ELM_FINGERPRINT_UNAVAILABLE (400) for the borrow without fingerprints; ELM_MALFORMED for borrows out of range and ELM_BORROW_NOT_ALLOWED (past the size check) for 100 years; the reclaim is
    ///   ELM_MALFORMED. The 32-character version is ELM_INVALID_ARGUMENT (past the size check); every field at its limit is 201 and one byte
    ///   over is ELM_MALFORMED; the renewal at the limits is 200
    ///   and the three over it are ELM_MALFORMED.
    /// </remarks>
    [Fact]
    public async Task Bodies_AreLimitedAndChecked()
    {
        await using Raw raw = await Raw.StartAsync(_deployment);
        string fingerprint = new('a', 32);

        await raw.AssertAcquireAsync(new byte[64 * 1024], "ELM_MALFORMED");
        await raw.AssertAcquireAsync(new byte[(64 * 1024) + 1], "ELM_PAYLOAD_TOO_LARGE", HttpStatusCode.RequestEntityTooLarge);
        await raw.AssertAcquireAsync(new byte[64 * 1024], "ELM_MALFORMED", chunked: true);
        await raw.AssertAcquireAsync(new byte[(64 * 1024) + 1], "ELM_PAYLOAD_TOO_LARGE", HttpStatusCode.RequestEntityTooLarge, chunked: true);
        await raw.AssertAcquireAsync(Raw.Json("not json"), "ELM_MALFORMED");
        await raw.AssertAcquireAsync(
            Raw.Json($$"""{"feature":"pro","count":1,"fingerprints":[{{string.Join(',', Enumerable.Repeat($"\"{fingerprint}\"", 9))}}]}"""),
            "ELM_MALFORMED"
        );
        await raw.AssertAcquireAsync(Raw.Json($$"""{"feature":"pro","count":1,"fingerprints":["{{new string('A', 32)}}"]}"""), "ELM_MALFORMED");
        await raw.AssertAcquireAsync(
            Raw.Json("{\"feature\":\"pro\",\"count\":1,\"client\":{\"host\":\"" + new string('h', 256) + "\",\"user\":\"u\"}}"),
            "ELM_MALFORMED"
        );
        await raw.AssertAcquireAsync(Raw.Json($$"""{"feature":"pro","count":1,"app":"{{new string('a', 65)}}"}"""), "ELM_MALFORMED");
        await raw.AssertAcquireAsync(Raw.Json($$"""{"feature":"pro","count":1,"version":"{{new string('1', 33)}}"}"""), "ELM_MALFORMED");
        await raw.AssertAcquireAsync(Raw.Json("""{"feature":"pro","count":1,"borrow":0}"""), "ELM_INVALID_ARGUMENT");
        await raw.AssertAcquireAsync(Raw.Json("""{"feature":"pro","count":1,"borrow":604801}"""), "ELM_BORROW_NOT_ALLOWED", HttpStatusCode.Forbidden);
        await raw.AssertAcquireAsync(Raw.Json("""{"feature":"pro","count":1,"borrow":60}"""), "ELM_FINGERPRINT_UNAVAILABLE");
        await raw.AssertAcquireAsync(Raw.Json("""{"feature":"pro","count":1,"borrow":-1}"""), "ELM_MALFORMED");
        await raw.AssertAcquireAsync(Raw.Json("""{"feature":"pro","count":1,"borrow":3162240001}"""), "ELM_MALFORMED");
        await raw.AssertAcquireAsync(
            Raw.Json("""{"feature":"pro","count":1,"borrow":3162240000}"""),
            "ELM_BORROW_NOT_ALLOWED",
            HttpStatusCode.Forbidden
        );
        await raw.AssertAcquireAsync(Raw.Json("""{"feature":"pro","count":0}"""), "ELM_INVALID_ARGUMENT");

        // A version of 32 characters passes the size check and is refused only as a version (each part has at most 9 digits,
        // so a valid version has at most 29 characters).
        await raw.AssertAcquireAsync(
            Raw.Json("{\"feature\":\"pro\",\"count\":1,\"version\":\"" + new string('1', 32) + "\"}"),
            "ELM_INVALID_ARGUMENT"
        );

        // Exactly at each limit passes: 8 fingerprints, host and user 255 bytes, app 64 bytes, with the longest valid version.
        // A two-byte character in the names fixes that they are counted in UTF-8 bytes.
        string version = "2.000000000.000000000";
        string host = "é" + new string('h', 253);
        string app = "é" + new string('a', 62);
        byte[] atLimits = Raw.Json(
            "{\"feature\":\"pro\",\"count\":1,\"version\":\""
                + version
                + "\",\"fingerprints\":["
                + string.Join(',', Enumerable.Repeat($"\"{fingerprint}\"", 8))
                + "],\"client\":{\"host\":\""
                + host
                + "\",\"user\":\""
                + host
                + "\"},\"app\":\""
                + app
                + "\"}"
        );
        using (HttpResponseMessage accepted = await raw.SignedAsync(HttpMethod.Post, "v1/leases", atLimits, includeKey: true))
        {
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }

        await raw.AssertAcquireAsync(Raw.Json("{\"feature\":\"pro\",\"count\":1,\"app\":\"é" + new string('a', 63) + "\"}"), "ELM_MALFORMED");

        // A renewal names at most 16 unanswered proofs of at most 64 characters each.
        string id = await raw.AcquireAsync();
        string RenewBody(int count, int length) =>
            "{\"seq\":1,\"retry\":[" + string.Join(',', Enumerable.Repeat("\"" + new string('j', length) + "\"", count)) + "]}";
        using (HttpResponseMessage renewed = await raw.SignedAsync(HttpMethod.Post, $"v1/leases/{id}/renew", Raw.Json(RenewBody(16, 64))))
        {
            Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        }

        foreach (string tooMuch in new[] { RenewBody(17, 64), RenewBody(1, 65), "{\"seq\":2,\"retry\":[null]}" })
        {
            using HttpResponseMessage refused = await raw.SignedAsync(HttpMethod.Post, $"v1/leases/{id}/renew", Raw.Json(tooMuch));
            await Raw.AssertCodeAsync(refused, "ELM_MALFORMED");
        }

        byte[] reclaim = Raw.Json($$"""{"leases":[{{string.Join(',', Enumerable.Repeat("\"x\"", 65))}}]}""");
        using HttpResponseMessage tooMany = await raw.SendAsync(
            HttpMethod.Post,
            "v1/leases/reclaim",
            reclaim,
            raw.Proof(HttpMethod.Post, "v1/leases/reclaim", reclaim, await raw.NonceAsync(), includeKey: true)
        );
        await Raw.AssertCodeAsync(tooMany, "ELM_MALFORMED");
        Assert.Equal(2, raw.Server.Engine.LeaseCount);
    }

    /// <summary>
    /// Renewal and release name a lease: an unknown lease is 404 for renewal and 204 for release, and another key can neither
    /// renew nor release a lease (design §12.7.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A lease of key A; key B.
    ///
    /// Steps:
    /// - Renew and release an unknown ID; B renews and releases A's lease; A releases it.
    ///
    /// Expected:
    /// - 404 ELM_UNKNOWN_LEASE and 204; 401 ELM_PROOF_INVALID twice with the seat kept; 204 and no seat.
    /// </remarks>
    [Fact]
    public async Task Renewal_AndRelease_BelongToTheLeaseKey()
    {
        await using Raw a = await Raw.StartAsync(_deployment);
        await using Raw b = await Raw.StartAsync(_deployment, a.Server);
        string id = await a.AcquireAsync();

        using HttpResponseMessage unknownRenew = await a.SignedAsync(HttpMethod.Post, "v1/leases/nope/renew", Raw.Json("""{"seq":1}"""));
        Assert.Equal(HttpStatusCode.NotFound, unknownRenew.StatusCode);
        await Raw.AssertCodeAsync(unknownRenew, "ELM_UNKNOWN_LEASE");
        using HttpResponseMessage unknownRelease = await a.SignedAsync(HttpMethod.Delete, "v1/leases/nope", []);
        Assert.Equal(HttpStatusCode.NoContent, unknownRelease.StatusCode);

        using HttpResponseMessage otherRenew = await b.SignedAsync(
            HttpMethod.Post,
            $"v1/leases/{id}/renew",
            Raw.Json("""{"seq":1}"""),
            includeKey: true
        );
        await Raw.AssertCodeAsync(otherRenew, "ELM_PROOF_INVALID");
        using HttpResponseMessage otherRelease = await b.SignedAsync(HttpMethod.Delete, $"v1/leases/{id}", [], includeKey: true);
        await Raw.AssertCodeAsync(otherRelease, "ELM_PROOF_INVALID");
        Assert.Equal(1, a.Server.Engine.SeatsInUse("pro"));

        using HttpResponseMessage released = await a.SignedAsync(HttpMethod.Delete, $"v1/leases/{id}", []);
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        Assert.Equal(0, a.Server.Engine.SeatsInUse("pro"));
    }

    /// <summary>
    /// Each source address may send rateLimit.perIp requests per second (design §13.8, T16).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server with rateLimit.perIp 3.
    ///
    /// Steps:
    /// - Four nonce requests at one instant; advance one second; one more.
    ///
    /// Expected:
    /// - Three 200, then 429 ELM_RATE_LIMITED with Retry-After 1; then 200.
    /// </remarks>
    [Fact]
    public async Task RateLimit_PerAddress_IsApplied()
    {
        ElmServer server = await _deployment.StartServerAsync(change: s => Change(s, perIp: 3));
        using HttpClient http = Deployment.RawHttp(server.PublicUrls[0]);

        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage ok = await http.GetAsync("v1/nonce", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using HttpResponseMessage limited = await http.GetAsync("v1/nonce", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), limited.Headers.RetryAfter?.Delta);
        await Raw.AssertCodeAsync(limited, "ELM_RATE_LIMITED");

        _deployment.Time.Advance(TimeSpan.FromSeconds(1));
        using HttpResponseMessage again = await http.GetAsync("v1/nonce", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    /// <summary>
    /// IP entries refuse every request from an address; host and user entries refuse new leases whose client info does not
    /// match (design §13.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server with deny 127.0.0.0/8; a server with allow host:THIS-MACHINE.
    ///
    /// Steps:
    /// - Get a nonce from the first; acquire from the second with a client that sends no client info and with one that does.
    ///
    /// Expected:
    /// - 403 ELM_FORBIDDEN; ELM_FORBIDDEN for the silent client; a lease for the client that sends its host.
    /// </remarks>
    [Fact]
    public async Task AccessRules_AreApplied()
    {
        ElmServer denied = await _deployment.StartServerAsync(change: s => Change(s, access: AccessRules.Parse([], ["127.0.0.0/8"])));
        using HttpClient http = Deployment.RawHttp(denied.PublicUrls[0]);
        using HttpResponseMessage forbidden = await http.GetAsync("v1/nonce", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await Raw.AssertCodeAsync(forbidden, "ELM_FORBIDDEN");

        ElmServer hosts = await _deployment.StartServerAsync(change: s =>
            Change(s, access: AccessRules.Parse(["host:" + Environment.MachineName], []))
        );
        _deployment.Time.Advance(TimeSpan.FromMinutes(10));
        LicenseClient silent = _deployment.Client(hosts.PublicUrls[0]);
        LicenseClient telling = _deployment.Client(
            hosts.PublicUrls[0],
            change: o => new LicenseClientOptions
            {
                ProductId = o.ProductId,
                ServerUrl = o.ServerUrl,
                Trust = o.Trust,
                DataDirectory = o.DataDirectory,
                ClockGuard = o.ClockGuard,
                TimeProvider = o.TimeProvider,
                SendClientInfo = true,
            }
        );

        var refused = Assert.IsType<LeaseResult.Failed>(
            await silent.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken)
        );
        Assert.Equal(ErrorCode.Forbidden, refused.Error.Code);
        Assert.IsType<LeaseResult.Acquired>(await telling.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
    }

    private static ServerSettings Change(ServerSettings settings, int? perIp = null, AccessRules? access = null) =>
        new()
        {
            Product = settings.Product,
            AdminListen = settings.AdminListen,
            Listen = settings.Listen,
            LicensePath = settings.LicensePath,
            TrustRootKeyFiles = settings.TrustRootKeyFiles,
            DataDirectory = settings.DataDirectory,
            RateLimitPerIp = perIp ?? settings.RateLimitPerIp,
            Access = access ?? settings.Access,
        };

    /// <summary>
    /// A raw protocol client: an installation key, a server past its window, and an HTTP client that skips the TLS check.
    /// </summary>
    private sealed class Raw : IAsyncDisposable
    {
        private readonly HttpClient _http;
        private readonly InstallationKey _key;
        private readonly Deployment _deployment;

        private Raw(Deployment deployment, ElmServer server, InstallationKey key)
        {
            _deployment = deployment;
            Server = server;
            _key = key;
            _http = Deployment.RawHttp(server.PublicUrls[0]);
        }

        public ElmServer Server { get; }

        public static async Task<Raw> StartAsync(Deployment deployment, ElmServer? server = null)
        {
            if (server is null)
            {
                server = await deployment.StartServerAsync();
                deployment.Time.Advance(TimeSpan.FromMinutes(10));
            }

            var store = new InstallationKeyStore("acme-cad", KeyScope.User, deployment.NewFolder("raw"));
            Assert.True(store.TryLoadOrCreate(out InstallationKey? key, out _));
            return new Raw(deployment, server, key!);
        }

        public static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

        public static async Task AssertCodeAsync(HttpResponseMessage response, string code)
        {
            using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
            Assert.Equal((int)response.StatusCode, problem.RootElement.GetProperty("status").GetInt32());
        }

        public async Task<string> NonceAsync()
        {
            using JsonDocument body = JsonDocument.Parse(await _http.GetStringAsync("v1/nonce", TestContext.Current.CancellationToken));
            return body.RootElement.GetProperty("nonce").GetString()!;
        }

        public string Proof(HttpMethod method, string path, byte[] body, string nonce, bool includeKey = true) =>
            ProofBuilder.Create(_key, method.Method, new Uri(Server.PublicUrls[0], path), nonce, body, includeKey, _deployment.Time);

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, byte[] body, string? proof, bool chunked = false)
        {
            using var request = new HttpRequestMessage(method, path) { Content = new ByteArrayContent(body) };
            request.Headers.TransferEncodingChunked = chunked;
            if (proof is not null)
            {
                request.Headers.Add("ELM-Proof", proof);
            }

            return await _http.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async Task<HttpResponseMessage> SignedAsync(
            HttpMethod method,
            string path,
            byte[] body,
            bool includeKey = false,
            bool chunked = false
        ) => await SendAsync(method, path, body, Proof(method, path, body, await NonceAsync(), includeKey), chunked);

        public async Task<string> AcquireAsync()
        {
            using HttpResponseMessage response = await SignedAsync(
                HttpMethod.Post,
                "v1/leases",
                Json("""{"feature":"pro","count":1}"""),
                includeKey: true
            );
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using JsonDocument answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            string token = answer.RootElement.GetProperty("lease").GetString()!;
            using JsonDocument payload = JsonDocument.Parse(Convert.FromBase64String(Pad(token.Split('.')[1])));
            return payload.RootElement.GetProperty("lse").GetString()!;
        }

        public async Task AssertAcquireAsync(byte[] body, string code, HttpStatusCode status = HttpStatusCode.BadRequest, bool chunked = false)
        {
            using HttpResponseMessage response = await SignedAsync(HttpMethod.Post, "v1/leases", body, includeKey: true, chunked);
            Assert.Equal(status, response.StatusCode);
            await AssertCodeAsync(response, code);
        }

        public ValueTask DisposeAsync()
        {
            _http.Dispose();
            _key.Dispose();
            return ValueTask.CompletedTask;
        }

        private static string Pad(string base64Url)
        {
            string text = base64Url.Replace('-', '+').Replace('_', '/');
            return text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=');
        }
    }
}
