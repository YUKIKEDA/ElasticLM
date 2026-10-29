using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElasticLM.Client;
using ElasticLM.Licensing;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The answer to a device request: its status, body, the jti of the proof it carried, and the fresh nonce it returned.
/// </summary>
internal sealed record DeviceAnswer(HttpStatusCode Status, string Text, string Jti, string? Nonce)
{
    /// <summary>Gets the body as JSON.</summary>
    public JsonElement Json => JsonDocument.Parse(Text).RootElement.Clone();

    /// <summary>Gets the problem's code, or <see langword="null"/>.</summary>
    public string? Code => Text.Length > 0 && Json.TryGetProperty("code", out JsonElement code) ? code.GetString() : null;

    /// <summary>Gets the activation ID of an issued answer.</summary>
    public string ActivationId => Json.GetProperty("activationId").GetString()!;

    /// <summary>Gets the lease token of a lease answer.</summary>
    public string Lease => Json.GetProperty("lease").GetString()!;

    /// <summary>Gets the license file of an issued answer, as its bytes.</summary>
    public byte[] File => Encoding.UTF8.GetBytes(Json.GetProperty("license").GetRawText());
}

/// <summary>
/// A device: an installation key made in the test folder and the requests of design §12.4 to §12.6 and §12.9, each with a
/// proof (design §12.2) made by <see cref="ProofBuilder"/>.
/// </summary>
internal sealed class TestDevice : IDisposable
{
    private readonly TestService _test;
    private readonly HttpClient _http;

    private TestDevice(TestService test, ElmService service, InstallationKey key)
    {
        _test = test;
        Service = service;
        Key = key;
        _http = new HttpClient { BaseAddress = service.Url };
    }

    public ElmService Service { get; }

    public InstallationKey Key { get; }

    public string KeyId => Key.Thumbprint;

    /// <summary>Makes a device with a new installation key, or with the key kept in a folder (another copy of an installation).</summary>
    public static TestDevice Create(TestService test, ElmService service, string? directory = null)
    {
        var store = new InstallationKeyStore(
            "app",
            KeyScope.User,
            directory ?? Path.Combine(test.Directory, "device-" + Guid.NewGuid().ToString("N"))
        );
        Assert.True(store.TryLoadOrCreate(out InstallationKey? key, out LicenseError? error), error?.Message);
        return new TestDevice(test, service, key!);
    }

    /// <summary>Returns fingerprints of the §10.3 form, made at random.</summary>
    public static string[] Fingerprints(int count = 1) =>
        [.. Enumerable.Range(0, count).Select(_ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)))];

    public async Task<string> NonceAsync()
    {
        using HttpResponseMessage response = await _http.GetAsync("v1/nonce", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return answer.RootElement.GetProperty("nonce").GetString()!;
    }

    /// <summary>
    /// Posts a body with a proof for the path. <paramref name="proof"/> may change the proof's header value; <paramref
    /// name="signedPath"/> and <paramref name="signedBody"/> sign another path or body than the one sent.
    /// </summary>
    public Task<DeviceAnswer> PostAsync(
        string path,
        string body,
        bool includeKey = true,
        string? nonce = null,
        Func<string, string?>? proof = null,
        string? signedPath = null,
        string? signedBody = null,
        InstallationKey? signer = null
    ) => SendAsync(HttpMethod.Post, path, body, includeKey, nonce, proof, signedPath, signedBody, signer);

    /// <summary>Sends a request of a method with a body and a proof for the path, as <see cref="PostAsync"/> does.</summary>
    public async Task<DeviceAnswer> SendAsync(
        HttpMethod method,
        string path,
        string body,
        bool includeKey = true,
        string? nonce = null,
        Func<string, string?>? proof = null,
        string? signedPath = null,
        string? signedBody = null,
        InstallationKey? signer = null
    )
    {
        nonce ??= await NonceAsync();
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string made = ProofBuilder.Create(
            signer ?? Key,
            method.Method,
            new Uri(Service.Url, signedPath ?? path),
            nonce,
            signedBody is null ? bytes : Encoding.UTF8.GetBytes(signedBody),
            includeKey,
            _test.Time
        );
        using var request = new HttpRequestMessage(method, path) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        if ((proof is null ? made : proof(made)) is string header)
        {
            request.Headers.Add("ELM-Proof", header);
        }

        using HttpResponseMessage response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        string text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        string? fresh = response.Headers.TryGetValues("ELM-Nonce", out IEnumerable<string>? values) ? values.Single() : null;
        return new DeviceAnswer(response.StatusCode, text, JtiOf(made), fresh);
    }

    public Task<DeviceAnswer> ActivateAsync(
        string licenseKey,
        string[] fingerprints,
        long? seq = null,
        string[]? retry = null,
        string more = "",
        string product = "app"
    ) =>
        PostAsync(
            "v1/activations",
            $$"""{"licenseKey":"{{licenseKey}}","product":"{{product}}","fingerprints":{{Array(fingerprints)}}{{Seq(seq)}}{{Retry(retry)}}{{more}}}"""
        );

    public Task<DeviceAnswer> RefreshAsync(string activationId, long seq, string[]? retry = null) =>
        PostAsync($"v1/activations/{activationId}/refresh", $$"""{"seq":{{seq}}{{Retry(retry)}}}""", includeKey: false);

    public Task<DeviceAnswer> DeactivateAsync(string activationId, long seq, string[]? retry = null) =>
        PostAsync($"v1/activations/{activationId}/deactivate", $$"""{"seq":{{seq}}{{Retry(retry)}}}""", includeKey: false);

    public Task<DeviceAnswer> TrialAsync(string product, string[] fingerprints, string more = "", string[]? retry = null) =>
        PostAsync("v1/trials", $$"""{"product":"{{product}}","fingerprints":{{Array(fingerprints)}}{{Retry(retry)}}{{more}}}""");

    /// <summary>Acquires a cloud lease (design §12.7.6) of a license key's feature; <paramref name="more"/> adds members.</summary>
    public Task<DeviceAnswer> AcquireAsync(string licenseKey, int count = 1, string feature = "core", string more = "", string product = "app") =>
        PostAsync("v1/leases", $$"""{"licenseKey":"{{licenseKey}}","product":"{{product}}","feature":"{{feature}}","count":{{count}}{{more}}}""");

    /// <summary>Acquires a borrow of a seat for a number of seconds, with fingerprints.</summary>
    public Task<DeviceAnswer> BorrowAsync(string licenseKey, long seconds, string[] fingerprints) =>
        AcquireAsync(licenseKey, more: $",\"borrow\":{seconds},\"fingerprints\":{Array(fingerprints)}");

    public Task<DeviceAnswer> RenewAsync(string leaseId, long seq, string[]? retry = null) =>
        PostAsync($"v1/leases/{leaseId}/renew", $$"""{"seq":{{seq}}{{Retry(retry)}}}""", includeKey: false);

    /// <summary>Releases a lease with a proof and an empty body.</summary>
    public Task<DeviceAnswer> ReleaseAsync(string leaseId, InstallationKey? signer = null) =>
        SendAsync(HttpMethod.Delete, $"v1/leases/{leaseId}", string.Empty, includeKey: false, signer: signer);

    /// <summary>Verifies a lease answer as a SaaS client would (design §12.7.5), for this device and product app.</summary>
    public VerifiedLease VerifyLease(DeviceAnswer answer)
    {
        TrustStore trust = TrustStore.Create([System.IO.File.ReadAllText(_test.FilePath("root.pub.pem"))], []);
        var verifier = new LeaseVerifier(trust, _test.Time, StatelessClockGuard.Instance);
        string[] certificates = [.. answer.Json.GetProperty("certificates").EnumerateArray().Select(c => c.GetString()!)];
        LeaseVerificationResult result = verifier.VerifyHosted(answer.Lease, certificates, new LeaseExpectation("app", KeyId));
        return result is LeaseVerificationResult.Valid valid ? valid.Lease : throw new InvalidOperationException(result.ToString());
    }

    /// <summary>Returns a 128-bit nonce in base64url, as an offline request carries (design §8.4.4).</summary>
    public static string NewNonce() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Makes an offline activation request (design §8.4.4) signed by this device's key; <paramref name="more"/> adds claims,
    /// <paramref name="change"/> edits the payload's text before it is signed, and <paramref name="iat"/> and <paramref
    /// name="exp"/> are seconds from the test's start.
    /// </summary>
    public string ActivationFile(
        string licenseKey,
        string[] fingerprints,
        string? nonce = null,
        long iat = 0,
        long? exp = null,
        string product = "app",
        string more = "",
        Func<string, string>? change = null
    ) =>
        Sign(
            TokenTypes.ActivationRequest,
            (change ?? (c => c))(
                $$"""{"ver":1,"op":"activate","key":"{{licenseKey}}","prd":"{{product}}","fp":{{Array(fingerprints)}},"jwk":{{Jwk()}},"nonce":"{{nonce ?? NewNonce()}}","iat":{{At(iat)}},"exp":{{At(exp ?? iat + 604_800)}}{{more}}}"""
            )
        );

    /// <summary>Makes an offline refresh request (design §8.4.4, §12.8.3) signed by this device's key.</summary>
    public string RefreshFile(string activationId, long seq, string[]? retry = null, string? nonce = null, long iat = 0, string more = "") =>
        Sign(
            TokenTypes.ActivationRequest,
            $$"""{"ver":1,"op":"refresh","act":"{{activationId}}","seq":{{seq}}{{Retry(retry)}},"fp":[],"jwk":{{Jwk()}},"nonce":"{{nonce ?? NewNonce()}}","iat":{{At(iat)}},"exp":{{At(iat + 604_800)}}{{more}}}"""
        );

    /// <summary>Makes an offline deactivation proof (design §8.4.5), signed by this device's key or another.</summary>
    public string DeactivationFile(string activationId, long seq, string[]? retry = null, long iat = 0, InstallationKey? signer = null) =>
        Sign(TokenTypes.DeactivationProof, $$"""{"ver":1,"act":"{{activationId}}","seq":{{seq}}{{Retry(retry)}},"iat":{{At(iat)}}}""", signer);

    /// <summary>Signs a payload as a compact JWS of a type with this device's key, or another.</summary>
    public string Sign(string typ, string payload, InstallationKey? signer = null)
    {
        InstallationKey key = signer ?? Key;
        string input =
            Base64Url.EncodeToString(Encoding.UTF8.GetBytes($$"""{"alg":"ES256","kid":"{{key.Thumbprint}}","typ":"{{typ}}"}"""))
            + "."
            + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
        return input + "." + Base64Url.EncodeToString(key.Sign(Encoding.ASCII.GetBytes(input)));
    }

    /// <summary>Posts an offline file, with a line end after it, to <c>/v1/offline</c>, or to <c>/v1/admin/offline</c> with an
    /// API key.</summary>
    public async Task<DeviceAnswer> OfflineAsync(string file, string? apiKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, apiKey is null ? "v1/offline" : "v1/admin/offline")
        {
            Content = new StringContent(file + "\n"),
        };
        if (apiKey is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }

        using HttpResponseMessage response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        string text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        string? fresh = response.Headers.TryGetValues("ELM-Nonce", out IEnumerable<string>? values) ? values.Single() : null;
        return new DeviceAnswer(response.StatusCode, text, string.Empty, fresh);
    }

    /// <summary>Verifies a license file as the app would, bound to this device and its fingerprints.</summary>
    public VerifiedLicense Verify(byte[] file, string[] fingerprints, LicenseKind kind = LicenseKind.Standard)
    {
        TrustStore trust = TrustStore.Create([System.IO.File.ReadAllText(_test.FilePath("root.pub.pem"))], []);
        var verifier = new LicenseVerifier(trust, _test.Time, StatelessClockGuard.Instance);
        LicenseVerificationResult result = verifier.Verify(file, new LicenseRequirement("app", kind, null, null, Key.Identity(fingerprints)));
        return result is LicenseVerificationResult.Valid valid ? valid.License : throw new InvalidOperationException(result.ToString());
    }

    public void Dispose()
    {
        _http.Dispose();
        Key.Dispose();
    }

    private static string JtiOf(string proof)
    {
        using JsonDocument payload = JsonDocument.Parse(Base64Url.DecodeFromChars(proof.Split('.')[1]));
        return payload.RootElement.GetProperty("jti").GetString()!;
    }

    private static long At(long seconds) => TestService.Start.ToUnixTimeSeconds() + seconds;

    private string Jwk()
    {
        ECParameters key = Key.PublicParameters();
        return $$"""{"kty":"EC","crv":"P-256","x":"{{Base64Url.EncodeToString(key.Q.X)}}","y":"{{Base64Url.EncodeToString(key.Q.Y)}}"}""";
    }

    private static string Array(string[] items) => "[" + string.Join(',', items.Select(i => "\"" + i + "\"")) + "]";

    private static string Seq(long? seq) => seq is long s ? $",\"seq\":{s}" : string.Empty;

    private static string Retry(string[]? retry) => retry is null ? string.Empty : ",\"retry\":" + Array(retry);
}
