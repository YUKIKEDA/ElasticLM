using System.Security.Cryptography;
using ElasticLM.Licensing;
using ElasticLM.Licensing.Tests;

namespace ElasticLM.Native.Tests;

/// <summary>
/// The message of <c>ELM_INVALID_ARGUMENT</c> carries no .NET parameter name (design §17.3, V1c10): the refusals of the
/// .NET library keep their sentence without it, and the empty values whose sentence would not say which value it is are
/// refused first with the C argument's or member's name. The lease paths that need a server are in
/// <see cref="LeaseTests.Failures_RetryAfterArgumentsAndOwner"/>. A failure never returns 0 (design §17.3, the V1d1 Grill);
/// the functions that get a problem answer naming <c>ELM_OK</c> are in <see cref="LeaseTests"/> and
/// <see cref="ActivationTests"/>.
/// </summary>
[Collection(NativeClock.Name)]
public sealed unsafe class NativeErrorsTests : IDisposable
{
    private const string ServerUrl = "https://127.0.0.1:1/";

    private readonly TestIssuer _issuer = new();
    private readonly NativeApi _api = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "elm-native-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));

    private string Root => TestIssuer.PublicPem(_issuer.Root);

    public void Dispose()
    {
        _api.Dispose();
        _issuer.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// A refusal of the .NET library reaches <c>elm_last_error_message</c> with its own sentence and without
    /// " (Parameter '…')".
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The test issuer's root key; a license file of it for acme-cad; folders under the test's temporary folder.
    ///
    /// Steps:
    /// - elm_installation_open with product Acme-CAD.
    /// - elm_verifier_create with the license file as the root key; with the root key twice.
    /// - elm_verifier_verify with feature_version 2.
    /// - elm_activation_create with service_url http://example.com/; with an app_version of 65 bytes.
    /// - elm_client_create with server_url http://example.com/ and no license key; with an app_version of 65 bytes; with a
    ///   license key and an expected_server_license_id; with a license key and server_url http://example.com/.
    ///
    /// Expected:
    /// - ELM_INVALID_ARGUMENT (1) each time, with exactly the library's sentence: the product ID rule; "A root key is not a
    ///   P-256 public key in PEM."; "A root key is given more than once."; "The required feature version is not
    ///   major.minor[.patch]."; the service URL rule; "The app version is longer than 64 bytes." for the activation and for
    ///   the client; "The server URL must be an absolute https URL."; the license key rule; the service URL rule.
    /// </remarks>
    [Fact]
    public void Fail_LibraryRefusal_KeepsTheSentenceWithoutTheParameterName()
    {
        const string ServiceRule = "The service URL must be an absolute https URL, or http on a loopback address.";
        const string AppRule = "The app version is longer than 64 bytes.";
        string longApp = new('a', 65);
        nint result;

        AssertRefused(
            NativeApi.Open(_api.Struct(_api.InstallationOptions("Acme-CAD", Folder("installation"))), &result),
            "The product ID must be lowercase letters, digits, and '-', at most 64 characters."
        );

        string notPem = System.Text.Encoding.UTF8.GetString(_issuer.File());
        AssertRefused(NativeApi.Create(_api.Struct(_api.VerifierOptions([notPem])), &result), "A root key is not a P-256 public key in PEM.");
        AssertRefused(NativeApi.Create(_api.Struct(_api.VerifierOptions([Root, Root])), &result), "A root key is given more than once.");

        nint verifier = _api.CreateVerifier(_api.VerifierOptions([Root]));
        (int code, _) = _api.Verify(verifier, _issuer.File(), _api.Requirement(feature: "pro", version: "2"));
        AssertRefused(code, "The required feature version is not major.minor[.patch].");
        NativeApi.DestroyVerifier(verifier);

        AssertRefused(
            ActivationApi.Create(_api, ActivationApi.Options(_api, "acme-cad", "http://example.com/", Root, Folder("a1")), out _),
            ServiceRule
        );
        ActivationOptions longActivation = ActivationApi.WithAppVersion(
            _api,
            ActivationApi.Options(_api, "acme-cad", null, Root, Folder("a2")),
            longApp
        );
        AssertRefused(ActivationApi.Create(_api, longActivation, out _), AppRule);

        AssertRefused(
            LeaseApi.Create(_api, LeaseApi.Options(_api, Root, "http://example.com/", Folder("c1")), out _),
            "The server URL must be an absolute https URL."
        );
        AssertRefused(
            LeaseApi.Create(_api, LeaseApi.With(_api, LeaseApi.Options(_api, Root, ServerUrl, Folder("c2")), appVersion: longApp), out _),
            AppRule
        );
        AssertRefused(
            LeaseApi.Create(_api, LeaseApi.With(_api, LeaseApi.Options(_api, Root, ServerUrl, Folder("c3"), "ELM-KEY"), expected: "lic-srv"), out _),
            "A license key must not be empty, and has no expected server license ID."
        );
        AssertRefused(LeaseApi.Create(_api, LeaseApi.Options(_api, Root, "http://example.com/", Folder("c4"), "ELM-KEY"), out _), ServiceRule);
    }

    /// <summary>
    /// An empty value whose library sentence would not say which value it is gets a sentence that names the C argument or
    /// member (design §17.3, V1c10 Q3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The test issuer's root key and a license file of it for acme-cad; an activation without a service URL and a lease
    ///   client of https://127.0.0.1:1/, each in its own folder (neither contacts anything).
    ///
    /// Steps:
    /// - An empty revoked_key_ids[1] for elm_verifier_create, elm_activation_create, and elm_client_create.
    /// - An empty product_id for elm_installation_open, the requirement of elm_verifier_verify, elm_activation_create, and
    ///   elm_client_create.
    /// - An empty directory, and one of three spaces, for elm_installation_open, elm_activation_create, and
    ///   elm_client_create; the same for clock_guard_path of elm_verifier_create, with and without an installation.
    /// - An empty license_key for elm_activation_activate, elm_activation_create_activation_request, and the options of
    ///   elm_client_create.
    /// - An empty feature_id, and count 0, for elm_client_acquire_lease and elm_client_borrow_lease.
    ///
    /// Expected:
    /// - ELM_INVALID_ARGUMENT (1) each time, with exactly "revoked_key_ids[1] must not be empty.", "product_id must not be
    ///   empty.", "directory must not be empty or only white space.", "clock_guard_path must not be empty or only white
    ///   space.", "license_key must not be empty.", "feature_id must not be empty.", or "count must be at least 1.".
    /// </remarks>
    [Fact]
    public void Fail_EmptyValue_NamesTheCArgument()
    {
        const string Revoked = "revoked_key_ids[1] must not be empty.";
        const string Product = "product_id must not be empty.";
        const string DirectoryRule = "directory must not be empty or only white space.";
        const string GuardRule = "clock_guard_path must not be empty or only white space.";
        const string Key = "license_key must not be empty.";
        string[] revoked = [_issuer.SigningKid, string.Empty];
        nint result;

        AssertRefused(NativeApi.Create(_api.Struct(_api.VerifierOptions([Root], revoked)), &result), Revoked);
        ActivationOptions activationRevoked = ActivationApi.Options(_api, "acme-cad", null, Root, Folder("a1"));
        activationRevoked.RevokedKeyIds = _api.Texts(revoked);
        activationRevoked.RevokedKeyCount = 2;
        AssertRefused(ActivationApi.Create(_api, activationRevoked, out _), Revoked);
        ClientOptions clientRevoked = LeaseApi.Options(_api, Root, ServerUrl, Folder("c1"));
        clientRevoked.RevokedKeyIds = _api.Texts(revoked);
        clientRevoked.RevokedKeyCount = 2;
        AssertRefused(LeaseApi.Create(_api, clientRevoked, out _), Revoked);

        AssertRefused(NativeApi.Open(_api.Struct(_api.InstallationOptions(string.Empty, Folder("i1"))), &result), Product);
        nint verifier = _api.CreateVerifier(_api.VerifierOptions([Root]));
        AssertRefused(_api.Verify(verifier, _issuer.File(), _api.Requirement(product: string.Empty)).Code, Product);
        NativeApi.DestroyVerifier(verifier);
        AssertRefused(ActivationApi.Create(_api, ActivationApi.Options(_api, string.Empty, null, Root, Folder("a2")), out _), Product);
        ClientOptions clientProduct = LeaseApi.Options(_api, Root, ServerUrl, Folder("c2"));
        clientProduct.ProductId = _api.Text(string.Empty);
        AssertRefused(LeaseApi.Create(_api, clientProduct, out _), Product);

        nint installation = _api.OpenInstallation("acme-cad", Folder("i2"));
        foreach (string blank in new[] { string.Empty, "   " })
        {
            AssertRefused(NativeApi.Open(_api.Struct(_api.InstallationOptions("acme-cad", blank)), &result), DirectoryRule);
            AssertRefused(ActivationApi.Create(_api, ActivationApi.Options(_api, "acme-cad", null, Root, blank), out _), DirectoryRule);
            AssertRefused(LeaseApi.Create(_api, LeaseApi.Options(_api, Root, ServerUrl, blank), out _), DirectoryRule);

            VerifierOptions guarded = _api.VerifierOptions([Root]);
            guarded.ClockGuardPath = _api.Text(blank);
            AssertRefused(NativeApi.Create(_api.Struct(guarded), &result), GuardRule);
            guarded.Installation = installation;
            AssertRefused(NativeApi.Create(_api.Struct(guarded), &result), GuardRule);
        }

        NativeApi.FreeInstallation(installation);

        Assert.Equal(0, ActivationApi.Create(_api, ActivationApi.Options(_api, "acme-cad", null, Root, Folder("a3")), out nint activation));
        AssertRefused(ActivationApi.Activate(_api, activation, string.Empty, out _, out _), Key);
        AssertRefused(ActivationApi.Request(_api, activation, string.Empty, 0, out _, out _), Key);
        ActivationApi.Destroy(activation);
        AssertRefused(LeaseApi.Create(_api, LeaseApi.Options(_api, Root, ServerUrl, Folder("c3"), string.Empty), out _), Key);

        Assert.Equal(0, LeaseApi.Create(_api, LeaseApi.Options(_api, Root, ServerUrl, Folder("c4")), out nint client));
        AssertRefused(LeaseApi.Acquire(_api, client, string.Empty, 1, out _, out _), "feature_id must not be empty.");
        AssertRefused(LeaseApi.Borrow(_api, client, string.Empty, 60, out _, out _), "feature_id must not be empty.");
        AssertRefused(LeaseApi.Acquire(_api, client, "pro", 0, out _, out _), "count must be at least 1.");
        AssertRefused(LeaseApi.Borrow(_api, client, "pro", 60, out _, out _, count: 0), "count must be at least 1.");
        LeaseApi.Destroy(client);
    }

    /// <summary>
    /// A failure never returns 0: one whose code is ELM_OK returns ELM_INTERNAL_ERROR (99) and keeps the message it was
    /// given, so 0 stands for success only (design §17.3, the V1d1 Grill, Q4).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None; the client makes no failure with ELM_OK, so NativeErrors.Fail is called directly.
    ///
    /// Steps:
    /// - Fail with ELM_OK and "x"; fail with ELM_NO_SEATS and "y".
    ///
    /// Expected:
    /// - 99 with the message "x"; 40 with the message "y".
    /// </remarks>
    [Fact]
    public void Fail_CodeIsOk_Returns99AndKeepsTheMessage()
    {
        Assert.Equal((99, "x"), (NativeErrors.Fail(ErrorCode.Ok, "x"), NativeApi.LastError));
        Assert.Equal((40, "y"), (NativeErrors.Fail(ErrorCode.NoSeats, "y"), NativeApi.LastError));
    }

    private static void AssertRefused(int code, string message) => Assert.Equal((1, message), (code, NativeApi.LastError));

    private string Folder(string name) => Path.Combine(_dir, name);
}
