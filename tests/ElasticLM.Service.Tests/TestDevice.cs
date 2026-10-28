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

    /// <summary>Makes a device with a new installation key.</summary>
    public static TestDevice Create(TestService test, ElmService service)
    {
        var store = new InstallationKeyStore("app", KeyScope.User, Path.Combine(test.Directory, "device-" + Guid.NewGuid().ToString("N")));
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
    public async Task<DeviceAnswer> PostAsync(
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
            "POST",
            new Uri(Service.Url, signedPath ?? path),
            nonce,
            signedBody is null ? bytes : Encoding.UTF8.GetBytes(signedBody),
            includeKey,
            _test.Time
        );
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(bytes) };
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

    private static string Array(string[] items) => "[" + string.Join(',', items.Select(i => "\"" + i + "\"")) + "]";

    private static string Seq(long? seq) => seq is long s ? $",\"seq\":{s}" : string.Empty;

    private static string Retry(string[]? retry) => retry is null ? string.Empty : ",\"retry\":" + Array(retry);
}
