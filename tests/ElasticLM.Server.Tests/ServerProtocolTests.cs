using System.Net;
using System.Text;
using System.Text.Json;
using ElasticLM.Client;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using ElasticLM.Licensing.Tests;
using ElasticLM.Service.Tests;

namespace ElasticLM.Server.Tests;

/// <summary>
/// The start of <c>elm-server</c> (design §13.1) and its HTTP protocol as seen without the client library: limits, proofs,
/// errors (design §12.1, §12.2, §12.7.1, §13.4, §13.8).
/// </summary>
public sealed class ServerProtocolTests : IAsyncDisposable
{
    private readonly Deployment _deployment = new();

    /// <summary>Gets each way the server gets a proof's key with each form of a token without a valid signature.</summary>
    public static TheoryData<string, string> KeySourcesAndSignatureForms => SignatureForms.Cross("embedded", "cached");

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

        string standard = _deployment.NewFolder();
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

        string trial = _deployment.NewFolder();
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

        string other = _deployment.NewFolder();
        string otherKey = Deployment.ServerKeyId(_deployment.NewFolder());
        _deployment.WriteServerLicense(other, s => s with { InstallationKeyThumbprint = otherKey });
        Assert.Contains("ELM_WRONG_INSTALLATION", await StartError(other), StringComparison.Ordinal);

        string missing = _deployment.NewFolder();
        Assert.Contains(Deployment.ServerKeyId(missing), await StartError(missing), StringComparison.Ordinal);

        string untrusted = _deployment.NewFolder();
        _deployment.WriteServerLicense(untrusted);
        static ServerSettings WithoutTrust(ServerSettings s) =>
            new()
            {
                Product = s.Product,
                Listen = s.Listen,
                LicensePath = s.LicensePath,
                DataDirectory = s.DataDirectory,
            };
        Assert.Contains("trust.rootKeys", await StartError(untrusted, WithoutTrust), StringComparison.Ordinal);
    }

    /// <summary>
    /// A start that the license verifier refused says so in a message that begins with the verifier's error code and holds
    /// it once; a license file that is missing, too large, or unreadable stops the start with a message without a code
    /// (design §13.1, V1c6 Q5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Data folders with: a server license bound to another installation key; a license file of 65,536 zero bytes; one of
    ///   65,537 zero bytes; no license; a valid license that another handle holds open without sharing.
    ///
    /// Steps:
    /// - Start a server on each.
    ///
    /// Expected:
    /// - ServerStartException with: "ELM_WRONG_INSTALLATION: The server license does not verify: The license is bound to
    ///   another installation key. This server's installation key is {thumbprint}."; a message that begins "ELM_MALFORMED:
    ///   The server license does not verify: ", ends with the thumbprint sentence, and holds "ELM_" once; "The server
    ///   license '{path}' is larger than 64 KiB."; a message that begins "The server license '{path}' does not exist. ",
    ///   names the thumbprint, and holds no "ELM_"; a message that begins "The server license '{path}' cannot be read: "
    ///   and holds no "ELM_". "ELM_" is counted in the message without the thumbprint.
    /// </remarks>
    [Fact]
    public async Task Start_LicenseRefused_OnlyTheVerifiersRefusalBeginsWithItsCode()
    {
        async Task<string> StartError(string data)
        {
            var e = await Assert.ThrowsAsync<ServerStartException>(() =>
                ElmServer.StartAsync(_deployment.Settings(data), _deployment.HostOptions(), TestContext.Current.CancellationToken)
            );
            return e.Message;
        }

        // The thumbprint is random base64url and may itself hold "ELM_", so it is taken out before the count.
        static int Codes(string message, string thumbprint) =>
            message.Replace(thumbprint, "{thumbprint}", StringComparison.Ordinal).Split("ELM_").Length - 1;

        string other = _deployment.NewFolder();
        string otherKey = Deployment.ServerKeyId(_deployment.NewFolder());
        _deployment.WriteServerLicense(other, s => s with { InstallationKeyThumbprint = otherKey });
        Assert.Equal(
            "ELM_WRONG_INSTALLATION: The server license does not verify: The license is bound to another installation key. "
                + $"This server's installation key is {Deployment.ServerKeyId(other)}.",
            await StartError(other)
        );

        string atLimit = _deployment.NewFolder();
        string atLimitKey = Deployment.ServerKeyId(atLimit);
        File.WriteAllBytes(Path.Combine(atLimit, "server.elmlic"), new byte[64 * 1024]);
        string refused = await StartError(atLimit);
        Assert.StartsWith("ELM_MALFORMED: The server license does not verify: ", refused, StringComparison.Ordinal);
        Assert.EndsWith($" This server's installation key is {atLimitKey}.", refused, StringComparison.Ordinal);
        Assert.Equal(1, Codes(refused, atLimitKey));

        string large = _deployment.NewFolder();
        string largePath = Path.Combine(large, "server.elmlic");
        File.WriteAllBytes(largePath, new byte[(64 * 1024) + 1]);
        Assert.Equal($"The server license '{largePath}' is larger than 64 KiB.", await StartError(large));

        string missing = _deployment.NewFolder();
        string missingKey = Deployment.ServerKeyId(missing);
        string none = await StartError(missing);
        Assert.StartsWith($"The server license '{Path.Combine(missing, "server.elmlic")}' does not exist. ", none, StringComparison.Ordinal);
        Assert.Contains($"installation key {missingKey}, ", none, StringComparison.Ordinal);
        Assert.Equal(0, Codes(none, missingKey));

        string held = _deployment.NewFolder();
        string heldPath = _deployment.WriteServerLicense(held);
        using (new FileStream(heldPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            string unreadable = await StartError(held);
            Assert.StartsWith($"The server license '{heldPath}' cannot be read: ", unreadable, StringComparison.Ordinal);
            Assert.Equal(0, Codes(unreadable, Deployment.ServerKeyId(held)));
        }
    }

    /// <summary>
    /// The server does not start on a server license whose token carries no valid signature: with an empty signature part,
    /// with a signature that is not 64 bytes or not the signing key's, and with an alg other than ES256 (design §8.2 rules
    /// 3 and 5, §13.1, §20.7 V1d14).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a server license on which a server starts and stops.
    /// - The token in that file in a form of <see cref="SignatureForms"/> with the same payload and the same certificate: an
    ///   empty signature part; the first 63 bytes of its signature; 64 zero bytes; its signature and one more zero byte; alg
    ///   none and an empty signature part; alg HS256 and an ES256 signature of the signing key over the changed header.
    ///
    /// Steps:
    /// - Start a server on the folder.
    ///
    /// Expected:
    /// - ServerStartException "ELM_BAD_SIGNATURE: The server license does not verify: {reason} This server's installation
    ///   key is {thumbprint}.", where the reason is "The license signature does not verify." for the four signatures and
    ///   "The token algorithm is not ES256." for the two algs.
    /// </remarks>
    [Theory]
    [MemberData(nameof(SignatureForms.All), MemberType = typeof(SignatureForms))]
    public async Task Start_ServerLicenseWithoutAValidSignature_Fails(string form)
    {
        string data = _deployment.NewFolder();
        await (await _deployment.StartServerAsync(data)).DisposeAsync();
        _deployment.ChangeServerLicenseToken(data, (token, sign) => SignatureForms.Change(token, form, sign));

        var refused = await Assert.ThrowsAsync<ServerStartException>(() =>
            ElmServer.StartAsync(_deployment.Settings(data), _deployment.HostOptions(), TestContext.Current.CancellationToken)
        );

        string reason = SignatureForms.ChangesAlgorithm(form) ? "The token algorithm is not ES256." : "The license signature does not verify.";
        Assert.Equal(
            $"ELM_BAD_SIGNATURE: The server license does not verify: {reason} This server's installation key is {Deployment.ServerKeyId(data)}.",
            refused.Message
        );
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
        string data = _deployment.NewFolder();
        _deployment.WriteServerLicense(data);
        ServerSettings settings = _deployment.Settings(data);
        var withoutFiles = new ServerSettings
        {
            Product = settings.Product,
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
    ///   (the license allows 7 days); borrow 60 s without fingerprints; borrow -1 and 100 years + 1 s; borrow of exactly 100 years; count 0 and -1;
    ///   count 2147483648, -2147483649, and 4294967297 (outside 32 bits).
    ///   Acquire with a version of exactly 32 characters; with every other field exactly at its limit (names with a two-byte
    ///   character); with an app one byte over. Renew
    ///   with 16 retry IDs of 64 characters, 17 IDs, an ID of 65 characters, and a null ID. Reclaim 65 tokens.
    ///
    /// Expected:
    /// - 64 KiB is read (a JSON error, ELM_MALFORMED) and 64 KiB + 1 is 413 ELM_PAYLOAD_TOO_LARGE; ELM_MALFORMED for the
    ///   shapes; ELM_INVALID_ARGUMENT for borrow 0 and counts 0 and -1; ELM_MALFORMED for the counts outside 32 bits;
    ///   ELM_BORROW_NOT_ALLOWED for the borrow over 7 days;
    ///   ELM_FINGERPRINT_UNAVAILABLE (400) for the borrow without fingerprints; ELM_MALFORMED for borrows out of range and ELM_BORROW_NOT_ALLOWED (past the size check) for 100 years; the reclaim is
    ///   ELM_MALFORMED. The 32-character version is ELM_INVALID_ARGUMENT (past the size check); every field at its limit is 201 and one byte
    ///   over is ELM_MALFORMED; the renewal at the limits is 200
    ///   and the three over it are ELM_MALFORMED. At the end the server holds 2 leases, the two that were granted.
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
        await raw.AssertAcquireAsync(Raw.Json("""{"feature":"pro","count":-1}"""), "ELM_INVALID_ARGUMENT");

        // The body's count is a 32-bit integer, so a value outside it is not read (design §12.7.1); cut to 32 bits,
        // 4294967297 would be 1.
        await raw.AssertAcquireAsync(Raw.Json("""{"feature":"pro","count":2147483648}"""), "ELM_MALFORMED");
        await raw.AssertAcquireAsync(Raw.Json("""{"feature":"pro","count":-2147483649}"""), "ELM_MALFORMED");
        await raw.AssertAcquireAsync(Raw.Json("""{"feature":"pro","count":4294967297}"""), "ELM_MALFORMED");

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
    /// A lease request names a feature of 1 to 64 bytes in UTF-8, as a license does: an empty or longer name is 400
    /// ELM_MALFORMED before anything is counted, and a name the server license does not grant is counted without the name,
    /// so the usage log never holds a name a client made up (design §12.7.1, §18.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server license with pro and a feature whose ID is 21 times あ (3 bytes each) and one a: 64 bytes in 22
    ///   characters. The server is past its window; an installation key.
    ///
    /// Steps:
    /// - Acquire the 64-byte feature; 64 letters a, which the license does not grant; 65 letters a; 21 times あ and ab (65
    ///   bytes in 23 characters); an empty name. Stop and verify the usage log.
    ///
    /// Expected:
    /// - 201; 403 ELM_FEATURE_NOT_LICENSED; then three times 400 ELM_MALFORMED "The request body is not a valid lease
    ///   request.".
    /// - The usage log is intact. Its lease.acquired line has the 64-byte feature. It has one lease.denied line, without
    ///   feat, with ELM_FEATURE_NOT_LICENSED and count 1: the three malformed requests are in no line and no count. No file
    ///   holds 64 letters a.
    /// </remarks>
    [Fact]
    public async Task Acquire_FeatureName_IsLimitedAndUnlistedNamesAreNotLogged()
    {
        string wide = string.Concat(Enumerable.Repeat("あ", 21));
        string data = _deployment.NewFolder();
        _deployment.WriteServerLicense(data, s => s with { Features = [.. s.Features, new FeatureSpec(wide + "a", Seats: 2)] });
        ElmServer server = await _deployment.StartServerAsync(data);
        _deployment.Time.Advance(TimeSpan.FromMinutes(10));
        await using Raw raw = await Raw.StartAsync(_deployment, server);
        static byte[] Body(string feature) => Raw.Json("{\"feature\":\"" + feature + "\",\"count\":1}");

        using (HttpResponseMessage granted = await raw.SignedAsync(HttpMethod.Post, "v1/leases", Body(wide + "a"), includeKey: true))
        {
            Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        }

        await raw.AssertAcquireAsync(Body(new string('a', 64)), "ELM_FEATURE_NOT_LICENSED", HttpStatusCode.Forbidden);
        foreach (string name in new[] { new string('a', 65), wide + "ab", string.Empty })
        {
            using HttpResponseMessage refused = await raw.SignedAsync(HttpMethod.Post, "v1/leases", Body(name), includeKey: true);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            using JsonDocument problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("ELM_MALFORMED", problem.RootElement.GetProperty("code").GetString());
            Assert.Equal("The request body is not a valid lease request.", problem.RootElement.GetProperty("detail").GetString());
        }

        await server.DisposeAsync();
        IReadOnlyList<string> files = SignedLog.FindFiles(data, "usage");
        LogReport usage = LogVerifier.Verify(files, _deployment.Trust, "acme-cad", "usage");
        Assert.True(usage.Intact);
        Assert.Equal(wide + "a", Assert.Single(usage.Entries, e => e.Type == "lease.acquired").Value.GetProperty("feat").GetString());
        JsonElement denied = Assert.Single(usage.Entries, e => e.Type == "lease.denied").Value;
        Assert.False(denied.TryGetProperty("feat", out _));
        Assert.Equal("ELM_FEATURE_NOT_LICENSED", denied.GetProperty("code").GetString());
        Assert.Equal(1, denied.GetProperty("count").GetInt64());
        Assert.All(files, file => Assert.DoesNotContain(new string('a', 64), File.ReadAllText(file), StringComparison.Ordinal));
    }

    /// <summary>
    /// A request refused because the server license has expired is counted with its feature only when the license grants a
    /// feature of that name (design §18.1): the refusal comes before the feature is looked up.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server license of pro that expires one hour after the start, without grace; the clock is 70 minutes past the
    ///   start; an installation key.
    ///
    /// Steps:
    /// - Acquire pro; acquire made-up. Stop and verify the usage log.
    ///
    /// Expected:
    /// - 403 ELM_EXPIRED twice. Two lease.denied lines with ELM_EXPIRED and count 1: one with feat pro and one without
    ///   feat. No file holds "made-up".
    /// </remarks>
    [Fact]
    public async Task Acquire_ServerLicenseExpired_CountsTheFeatureOnlyWhenListed()
    {
        string data = _deployment.NewFolder();
        _deployment.WriteServerLicense(data, s => s with { ExpiresAt = Deployment.Start.AddHours(1), GracePeriod = TimeSpan.Zero });
        ElmServer server = await _deployment.StartServerAsync(data);
        _deployment.Time.Advance(TimeSpan.FromMinutes(70));
        await using Raw raw = await Raw.StartAsync(_deployment, server);

        await raw.AssertAcquireAsync(Raw.Json("""{"feature":"pro","count":1}"""), "ELM_EXPIRED", HttpStatusCode.Forbidden);
        await raw.AssertAcquireAsync(Raw.Json("""{"feature":"made-up","count":1}"""), "ELM_EXPIRED", HttpStatusCode.Forbidden);

        await server.DisposeAsync();
        IReadOnlyList<string> files = SignedLog.FindFiles(data, "usage");
        LogReport usage = LogVerifier.Verify(files, _deployment.Trust, "acme-cad", "usage");
        JsonElement[] denied = [.. usage.Entries.Where(e => e.Type == "lease.denied").Select(e => e.Value)];
        Assert.Equal(2, denied.Length);
        Assert.All(denied, d => Assert.Equal(("ELM_EXPIRED", 1L), (d.GetProperty("code").GetString(), d.GetProperty("count").GetInt64())));
        Assert.Equal(["pro"], denied.Where(d => d.TryGetProperty("feat", out _)).Select(d => d.GetProperty("feat").GetString()));
        Assert.All(files, file => Assert.DoesNotContain("made-up", File.ReadAllText(file), StringComparison.Ordinal));
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
    /// A request whose proof carries no valid signature is refused and changes nothing, both when the proof brings its key
    /// and when the server already holds the key: with an empty signature part, with a signature that is not 64 bytes or
    /// not the key's, and with an alg other than ES256 (design §8.2 rules 3 and 5, §12.2, §20.7 V1d14).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server past its window; an installation key. Either the server has not seen the key and the request is an
    ///   acquire whose proof embeds it, or the key holds a lease and the request is that lease's renewal with sequence
    ///   number 1, whose proof carries no key.
    /// - A valid proof of the request on a fresh nonce, in a form of <see cref="SignatureForms"/> with the same payload: an
    ///   empty signature part; the first 63 bytes of its signature; 64 zero bytes; its signature and one more zero byte; alg
    ///   none and an empty signature part; alg HS256 and an ES256 signature of the installation key over the changed header.
    ///
    /// Steps:
    /// - Send the request with the changed proof, then with a good proof.
    ///
    /// Expected:
    /// - 401 ELM_PROOF_INVALID: "The request proof signature does not verify." for the four signatures, "The request proof
    ///   is missing or not a valid elm-proof+jws." for the two algs.
    /// - The good request succeeds (201 for the acquire, 200 for the renewal) and the server holds one lease, so the refused
    ///   request took no seat and moved no sequence number.
    /// </remarks>
    [Theory]
    [MemberData(nameof(KeySourcesAndSignatureForms))]
    public async Task Proofs_WithoutAValidSignature_AreRefused(string keyFrom, string form)
    {
        await using Raw raw = await Raw.StartAsync(_deployment);
        bool embedded = keyFrom == "embedded";
        string path = "v1/leases";
        byte[] body = Raw.Json("""{"feature":"pro","count":1}""");
        if (!embedded)
        {
            path = $"v1/leases/{await raw.AcquireAsync()}/renew";
            body = Raw.Json("""{"seq":1}""");
        }

        string proof = raw.Proof(HttpMethod.Post, path, body, await raw.NonceAsync(), includeKey: embedded);
        using HttpResponseMessage refused = await raw.SendAsync(HttpMethod.Post, path, body, SignatureForms.Change(proof, form, raw.Sign));

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        string detail = SignatureForms.ChangesAlgorithm(form)
            ? "The request proof is missing or not a valid elm-proof+jws."
            : "The request proof signature does not verify.";
        await Raw.AssertCodeAsync(refused, "ELM_PROOF_INVALID", detail);
        using HttpResponseMessage accepted = await raw.SignedAsync(HttpMethod.Post, path, body, includeKey: embedded);
        Assert.Equal(embedded ? HttpStatusCode.Created : HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, raw.Server.Engine.LeaseCount);
    }

    /// <summary>
    /// After a restart the server takes back only a lease that carries its own signature: a token with an empty signature
    /// part, with a signature that is not 64 bytes or not the server key's, or with an alg other than ES256 is refused and
    /// holds no seat (design §8.2 rules 3 and 5, §13.4, §20.7 V1d14).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A lease of one seat that the server issued to an installation key; the server restarted on the same data folder,
    ///   inside the lease's time.
    /// - One copy of the lease token in each form of <see cref="SignatureForms"/>, with the same payload: an empty signature
    ///   part; the first 63 bytes of its signature; 64 zero bytes; its signature and one more zero byte; alg none and an
    ///   empty signature part; alg HS256 and an ES256 signature of the server's installation key over the changed header.
    ///
    /// Steps:
    /// - Reclaim the six copies in one request with the installation key's proof; reclaim the token itself.
    ///
    /// Expected:
    /// - 200 with ELM_BAD_SIGNATURE for each copy, in order, and no lease on the server; then ELM_OK and one lease.
    /// </remarks>
    [Fact]
    public async Task Reclaim_LeaseWithoutAValidSignature_IsRefused()
    {
        string data = _deployment.NewFolder();
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.Machine, data).TryLoadOrCreate(out InstallationKey? serverKey, out _));
        using InstallationKey signer = serverKey!;
        string keyFolder = _deployment.NewFolder();
        ElmServer before = await _deployment.StartServerAsync(data);
        _deployment.Time.Advance(TimeSpan.FromMinutes(10));
        string lease;
        await using (Raw first = await Raw.StartAsync(_deployment, before, keyFolder))
        {
            lease = await first.AcquireTokenAsync();
        }

        await before.DisposeAsync();
        ElmServer after = await _deployment.StartServerAsync(data);
        await using Raw raw = await Raw.StartAsync(_deployment, after, keyFolder);
        string[] unsigned = [.. SignatureForms.Names.Select(form => SignatureForms.Change(lease, form, signer.Sign))];

        Assert.Equal(Enumerable.Repeat("ELM_BAD_SIGNATURE", unsigned.Length), await raw.ReclaimAsync(unsigned));
        Assert.Equal(0, after.Engine.LeaseCount);
        Assert.Equal(["ELM_OK"], await raw.ReclaimAsync([lease]));
        Assert.Equal(1, after.Engine.LeaseCount);
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

    /// <summary>
    /// The release of the lease an acquisition issued, named by the jti of the acquisition's proof, follows the order of the
    /// release checks: the form of the jti before anything, then the proof, which carries the key, then the lease of that key
    /// and request; a stopped clone is refused (design §12.7.7, V1d5b).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server past its reclaim window; key 1 holds a lease of pro from an acquisition whose proof's jti is J (the token's
    ///   areq); key 2 is another installation.
    ///
    /// Steps:
    /// - DELETE /v1/leases/requests/ with 65 characters and no proof; with J and no proof; with J and a proof of key 2 that
    ///   carries its key; with J and a proof of key 1 that carries its key.
    /// - Acquire again with key 1 (jti K); stop that lease as a clone at the server; DELETE /v1/leases/requests/K with a proof
    ///   of key 1; DELETE /v1/leases/ with that lease's ID. Stop the server and verify its usage log.
    ///
    /// Expected:
    /// - 204 with an ELM-Nonce header, and the lease still held; 401 ELM_PROOF_INVALID; 204 with the lease still held; 204
    ///   with no lease left.
    /// - 409 ELM_CLONE_DETECTED twice, and the stopped lease still held.
    /// - The usage log has one lease.released line, and one lease.denied line with ELM_CLONE_DETECTED, feat pro, and count 2:
    ///   both refusals of the stopped lease are counted (the owner's answer K4 to the review of PR #440).
    /// </remarks>
    [Fact]
    public async Task ReleaseRequest_OverHttp_FollowsTheReleaseChecks()
    {
        string data = _deployment.NewFolder();
        _deployment.WriteServerLicense(data);
        ElmServer server = await _deployment.StartServerAsync(data);
        _deployment.Time.Advance(TimeSpan.FromMinutes(10));
        await using Raw raw = await Raw.StartAsync(_deployment, server);
        await using Raw other = await Raw.StartAsync(_deployment, raw.Server);
        string jti = AnsweredProofId(await raw.AcquireTokenAsync());

        using (HttpResponseMessage malformed = await raw.SendAsync(HttpMethod.Delete, "v1/leases/requests/" + new string('a', 65), [], null))
        {
            Assert.Equal(HttpStatusCode.NoContent, malformed.StatusCode);
            Assert.True(malformed.Headers.Contains("ELM-Nonce"));
        }

        string path = "v1/leases/requests/" + jti;
        using (HttpResponseMessage unsigned = await raw.SendAsync(HttpMethod.Delete, path, [], null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
            await Raw.AssertCodeAsync(unsigned, "ELM_PROOF_INVALID");
        }

        using (HttpResponseMessage otherKey = await other.SignedAsync(HttpMethod.Delete, path, [], includeKey: true))
        {
            Assert.Equal(HttpStatusCode.NoContent, otherKey.StatusCode);
        }

        Assert.Equal(1, raw.Server.Engine.LeaseCount);
        using (HttpResponseMessage released = await raw.SignedAsync(HttpMethod.Delete, path, [], includeKey: true))
        {
            Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        }

        Assert.Equal(0, raw.Server.Engine.LeaseCount);

        string token = await raw.AcquireTokenAsync();
        string leaseId;
        using (JsonDocument payload = JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(token.Split('.')[1])))
        {
            leaseId = payload.RootElement.GetProperty("lse").GetString()!;
            Assert.IsType<Leasing.LeaseOutcome.Refused>(raw.Server.Engine.Renew(leaseId, 5, raw.KeyId, "another-copy", []));
        }

        using HttpResponseMessage stopped = await raw.SignedAsync(
            HttpMethod.Delete,
            "v1/leases/requests/" + AnsweredProofId(token),
            [],
            includeKey: true
        );
        Assert.Equal(HttpStatusCode.Conflict, stopped.StatusCode);
        await Raw.AssertCodeAsync(stopped, "ELM_CLONE_DETECTED");
        using (HttpResponseMessage byId = await raw.SignedAsync(HttpMethod.Delete, "v1/leases/" + leaseId, []))
        {
            Assert.Equal(HttpStatusCode.Conflict, byId.StatusCode);
            await Raw.AssertCodeAsync(byId, "ELM_CLONE_DETECTED");
        }

        Assert.Equal(1, raw.Server.Engine.LeaseCount);

        await server.DisposeAsync();
        LogReport usage = LogVerifier.Verify(SignedLog.FindFiles(data, "usage"), _deployment.Trust, "acme-cad", "usage");
        Assert.Single(usage.Entries, e => e.Type == "lease.released");
        JsonElement denied = Assert.Single(usage.Entries, e => e.Type == "lease.denied").Value;
        Assert.Equal(
            ("ELM_CLONE_DETECTED", "pro", 2L),
            (denied.GetProperty("code").GetString(), denied.GetProperty("feat").GetString(), denied.GetProperty("count").GetInt64())
        );
    }

    // The areq of a lease token: the jti of the proof of the request it answers (design §8.4.3).
    private static string AnsweredProofId(string token)
    {
        using JsonDocument payload = JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(token.Split('.')[1]));
        return payload.RootElement.GetProperty("areq").GetString()!;
    }

    private static ServerSettings Change(ServerSettings settings, int? perIp = null, AccessRules? access = null) =>
        new()
        {
            Product = settings.Product,
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

        public string KeyId => _key.Thumbprint;

        public static async Task<Raw> StartAsync(Deployment deployment, ElmServer? server = null, string? keyFolder = null)
        {
            if (server is null)
            {
                server = await deployment.StartServerAsync();
                deployment.Time.Advance(TimeSpan.FromMinutes(10));
            }

            var store = new InstallationKeyStore("acme-cad", KeyScope.User, keyFolder ?? deployment.NewFolder());
            Assert.True(store.TryLoadOrCreate(out InstallationKey? key, out _));
            return new Raw(deployment, server, key!);
        }

        public static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

        public static async Task AssertCodeAsync(HttpResponseMessage response, string code, string? detail = null)
        {
            using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
            Assert.Equal((int)response.StatusCode, problem.RootElement.GetProperty("status").GetInt32());
            if (detail is not null)
            {
                Assert.Equal(detail, problem.RootElement.GetProperty("detail").GetString());
            }
        }

        public async Task<string> NonceAsync()
        {
            using JsonDocument body = JsonDocument.Parse(await _http.GetStringAsync("v1/nonce", TestContext.Current.CancellationToken));
            return body.RootElement.GetProperty("nonce").GetString()!;
        }

        public string Proof(HttpMethod method, string path, byte[] body, string nonce, bool includeKey = true) =>
            ProofBuilder.Create(_key, method.Method, new Uri(Server.PublicUrls[0], path), nonce, body, includeKey, _deployment.Time);

        /// <summary>Signs as the installation key signs a proof.</summary>
        public byte[] Sign(byte[] input) => _key.Sign(input);

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
            string token = await AcquireTokenAsync();
            using JsonDocument payload = JsonDocument.Parse(Convert.FromBase64String(Pad(token.Split('.')[1])));
            return payload.RootElement.GetProperty("lse").GetString()!;
        }

        /// <summary>Acquires one seat of pro and returns the lease token.</summary>
        public async Task<string> AcquireTokenAsync()
        {
            using HttpResponseMessage response = await SignedAsync(
                HttpMethod.Post,
                "v1/leases",
                Json("""{"feature":"pro","count":1}"""),
                includeKey: true
            );
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using JsonDocument answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            return answer.RootElement.GetProperty("lease").GetString()!;
        }

        /// <summary>Reclaims lease tokens and returns the code of each result, in order.</summary>
        public async Task<string?[]> ReclaimAsync(IEnumerable<string> tokens)
        {
            byte[] body = Json($$"""{"leases":[{{string.Join(',', tokens.Select(token => $"\"{token}\""))}}]}""");
            using HttpResponseMessage response = await SignedAsync(HttpMethod.Post, "v1/leases/reclaim", body, includeKey: true);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using JsonDocument answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            return [.. answer.RootElement.GetProperty("results").EnumerateArray().Select(result => result.GetProperty("code").GetString())];
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

    /// <summary>
    /// Every route of the server appears in the elm-server section of its reference page, in both languages (design §20.7,
    /// V1c2b).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server started with a server license.
    /// - docs/guide/reference and docs/guide.ja/reference under the repository root.
    ///
    /// Steps:
    /// - Read every route endpoint of the running host as its method and path, such as POST /v1/leases.
    /// - Look for the routes under /admin/v1/ on administration-api.md, and every other route on device-protocol.md, each in the
    ///   section whose level-2 heading holds `elm-server`.
    ///
    /// Expected:
    /// - Both groups have routes.
    /// - On both pages of each group, a code span in the elm-server section is each route exactly; the message lists every
    ///   route that is not.
    /// </remarks>
    [Fact]
    public async Task Routes_AllEndpoints_AppearOnReferencePages()
    {
        ElmServer server = await _deployment.StartServerAsync();
        ILookup<bool, string> routes = EndpointRoutes.Of(server.Services).ToLookup(route => route.Contains(" /admin/v1/", StringComparison.Ordinal));
        Assert.NotEmpty(routes[true]);
        Assert.NotEmpty(routes[false]);

        IReadOnlyList<string> missing =
        [
            .. ReferencePages.FindMissingInSections(
                "administration-api.md",
                2,
                new Dictionary<string, IReadOnlyList<string>> { ["elm-server"] = [.. routes[true]] }
            ),
            .. ReferencePages.FindMissingInSections(
                "device-protocol.md",
                2,
                new Dictionary<string, IReadOnlyList<string>> { ["elm-server"] = [.. routes[false]] }
            ),
        ];

        Assert.True(missing.Count == 0, ReferencePages.Message(missing));
    }

    /// <summary>
    /// The error code reference gives, for each error code, the HTTP status the server answers it with (design §19, §20.7,
    /// V1c2b).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - docs/guide/reference/error-codes.md and docs/guide.ja/reference/error-codes.md under the repository root.
    ///
    /// Steps:
    /// - For every pair of <see cref="ElmServer.ErrorStatuses"/>, find the table row whose cell is the code's wire name in a code
    ///   span, and read its cell in the column headed "HTTP status" or "HTTP の状態".
    ///
    /// Expected:
    /// - On both pages, that cell starts with the status as its first whole number, such as 503 for ELM_SERVER_RECOVERING; the
    ///   message lists every pair that it does not.
    /// </remarks>
    [Fact]
    public void ErrorStatuses_AllPairs_AppearOnReferencePage()
    {
        IReadOnlyList<string> missing = ReferencePages.FindMissingStatuses(
            "error-codes.md",
            ElmServer.ErrorStatuses.Select(pair => (ErrorCodes.ToWireName(pair.Key), pair.Value))
        );

        Assert.True(missing.Count == 0, ReferencePages.Message(missing));
    }
}
