using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ElasticLM.Licensing;
using ElasticLM.Licensing.Tests;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Native.Tests;

/// <summary>
/// Tests of the functions libelasticlm exports (design §17.3, §17.4), called through unmanaged function pointers as C calls
/// them. They share the verifiers' clock (<see cref="Exports.Time"/>), so they run one at a time in this class.
/// </summary>
public sealed unsafe class ExportsTests : IDisposable
{
    private readonly TestIssuer _issuer = new();
    private readonly NativeApi _api = new();
    private readonly FakeTimeProvider _time = new(TestIssuer.Now);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "elm-native-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));

    public ExportsTests() => Exports.Time = _time;

    private string[] Roots => [TestIssuer.PublicPem(_issuer.Root)];

    public void Dispose()
    {
        Exports.Time = TimeProvider.System;
        _api.Dispose();
        _issuer.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// The C# structs have the sizes of the header's structs on the 64-bit targets.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Take the size of each struct.
    ///
    /// Expected:
    /// - elm_installation_options 40, elm_verifier_options 64, elm_requirement 48, elm_feature 48 bytes (worked out by hand
    ///   from include/elasticlm.h with 8-byte pointers and natural alignment).
    /// </remarks>
    [Fact]
    public void Structs_HaveTheHeadersSizes()
    {
        Assert.Equal([40, 64, 48, 48], new[] { sizeof(InstallationOptions), sizeof(VerifierOptions), sizeof(Requirement), sizeof(Feature) });
    }

    /// <summary>
    /// An unbound license verifies, and every value function reads it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A standard license without a key binding: product acme-cad, issued an hour ago, expiring in 30 days, grace 7 days,
    ///   features pro (range and an expiry) and basic, meta {"customer": "Contoso"}.
    ///
    /// Steps:
    /// - Create a verifier with the root key; verify with feature pro 2.5; read every value of the license.
    ///
    /// Expected:
    /// - 0; the ID, product, kind 0, issue time, expiry, grace, not in grace; pro with its range, seats, and expiry, basic
    ///   without them, neither with seats (a standard license has none); the meta pair by index and by key; an empty error
    ///   message.
    /// </remarks>
    [Fact]
    public void Verify_UnboundLicense_ReadsEveryValue()
    {
        JsonObject payload = Unbound();
        payload["feat"] = new JsonArray(
            new JsonObject
            {
                ["id"] = "pro",
                ["ver"] = ">=2.0 <3.0",
                ["exp"] = TestIssuer.NowSeconds + 86400,
            },
            new JsonObject { ["id"] = "basic" }
        );
        payload["meta"] = new JsonObject { ["customer"] = "Contoso" };
        nint verifier = _api.CreateVerifier(_api.VerifierOptions(Roots));
        (int code, nint license) = _api.Verify(verifier, _issuer.File(payload), _api.Requirement(feature: "pro", version: "2.5"));

        Assert.Equal((0, string.Empty), (code, NativeApi.LastError));
        Assert.Equal("lic-1", NativeApi.Utf8(((delegate* unmanaged<nint, nint>)&Exports.LicenseId)(license)));
        Assert.Equal("acme-cad", NativeApi.Utf8(((delegate* unmanaged<nint, nint>)&Exports.LicenseProduct)(license)));
        Assert.Equal(0, ((delegate* unmanaged<nint, int>)&Exports.LicenseKindOf)(license));
        Assert.Equal(TestIssuer.NowSeconds - 3600, ((delegate* unmanaged<nint, long>)&Exports.LicenseIssuedAt)(license));
        int has;
        long expires;
        Assert.Equal(0, ((delegate* unmanaged<nint, int*, long*, int>)&Exports.LicenseExpiresAt)(license, &has, &expires));
        Assert.Equal((1, TestIssuer.NowSeconds + (86400 * 30)), (has, expires));
        Assert.Equal(86400 * 7, ((delegate* unmanaged<nint, long>)&Exports.LicenseGracePeriodSeconds)(license));
        Assert.Equal(0, ((delegate* unmanaged<nint, int>)&Exports.LicenseInGracePeriod)(license));
        Assert.Equal((nuint)2, ((delegate* unmanaged<nint, nuint>)&Exports.LicenseFeatureCount)(license));

        Feature pro = ReadFeature(license, 0);
        Feature basic = ReadFeature(license, 1);
        Assert.Equal(
            ("pro", ">=2.0 <3.0", 0, 0, 1, TestIssuer.NowSeconds + 86400),
            (NativeApi.Utf8((nint)pro.Id), NativeApi.Utf8((nint)pro.VersionRange), pro.HasSeats, pro.Seats, pro.HasExpiresAt, pro.ExpiresAt)
        );
        Assert.Equal(
            ("basic", null, 0, 0, 0, 0L),
            (
                NativeApi.Utf8((nint)basic.Id),
                NativeApi.Utf8((nint)basic.VersionRange),
                basic.HasSeats,
                basic.Seats,
                basic.HasExpiresAt,
                basic.ExpiresAt
            )
        );

        Assert.Equal((nuint)1, ((delegate* unmanaged<nint, nuint>)&Exports.LicenseMetaCount)(license));
        Assert.Equal(("customer", "Contoso"), MetaAt(license, 0));
        Assert.Equal("Contoso", MetaGet(license, "customer"));

        NativeApi.FreeLicense(license);
        NativeApi.DestroyVerifier(verifier);
    }

    /// <summary>
    /// Every failure of verification returns the verifier's number and message, and sets the output to null.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A verifier with the root key; a standard license.
    ///
    /// Steps:
    /// - Verify with the signing key revoked; the license padded with spaces to 65,536 bytes; the same with one more space;
    ///   a null file with length 0; product acme-other; a version that is not a version; a feature of 1,024 bytes.
    ///
    /// Expected:
    /// - ELM_KEY_REVOKED (17); 0 (the limit itself is accepted); ELM_MALFORMED (2); ELM_INVALID_ARGUMENT (1);
    ///   ELM_WRONG_PRODUCT (7); 1 (the verifier's ArgumentException, caught); ELM_FEATURE_NOT_LICENSED (11), so the string
    ///   bound accepts 1,024 bytes. Each failure leaves out null and a message.
    /// </remarks>
    [Fact]
    public void Verify_Failures_ReturnTheNumberAndNull()
    {
        nint revoked = _api.CreateVerifier(_api.VerifierOptions(Roots, [_issuer.SigningKid]));
        AssertFailure(_api.Verify(revoked, _issuer.File(Unbound()), _api.Requirement()), 17);

        nint verifier = _api.CreateVerifier(_api.VerifierOptions(Roots));
        byte[] file = _issuer.File(Unbound());
        byte[] padded = new byte[65_537];
        Array.Fill(padded, (byte)' ');
        file.CopyTo(padded, 0);
        (int code, nint license) = _api.Verify(verifier, padded[..65_536], _api.Requirement());
        Assert.Equal(0, code);
        NativeApi.FreeLicense(license);
        AssertFailure(_api.Verify(verifier, padded, _api.Requirement()), 2);
        Assert.Equal("The license file is larger than the limit.", NativeApi.LastError);

        nint result = -1;
        Assert.Equal(1, NativeApi.Verify(verifier, null, 0, _api.Struct(_api.Requirement()), &result));
        Assert.Equal(0, result);

        AssertFailure(_api.Verify(verifier, _issuer.File(Unbound()), _api.Requirement(product: "acme-other")), 7);
        AssertFailure(_api.Verify(verifier, _issuer.File(Unbound()), _api.Requirement(feature: "pro", version: "two")), 1);
        AssertFailure(_api.Verify(verifier, _issuer.File(Unbound()), _api.Requirement(feature: new string('f', 1024))), 11);
        NativeApi.DestroyVerifier(revoked);
        NativeApi.DestroyVerifier(verifier);
    }

    /// <summary>
    /// The arguments of a verification are checked before the verifier runs.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A verifier and a valid file.
    ///
    /// Steps:
    /// - Verify with: a null out; a null requirement; struct_size one less and one more; product null; kind 3 and -1; a
    ///   feature that is not UTF-8; a feature of 1,025 bytes; the verifier passed as the installation; a null verifier.
    ///
    /// Expected:
    /// - ELM_INVALID_ARGUMENT (1) each time, with out null.
    /// </remarks>
    [Fact]
    public void Verify_BadArguments_AreInvalid()
    {
        nint verifier = _api.CreateVerifier(_api.VerifierOptions(Roots));
        byte[] file = _issuer.File(Unbound());
        byte* bytes = _api.Bytes(file);
        Assert.Equal(1, NativeApi.Verify(verifier, bytes, (nuint)file.Length, _api.Struct(_api.Requirement()), null));
        nint result = -1;
        Assert.Equal(1, NativeApi.Verify(verifier, bytes, (nuint)file.Length, null, &result));
        Assert.Equal(0, result);

        Requirement good = _api.Requirement();
        Requirement smaller = good;
        smaller.StructSize--;
        Requirement larger = good;
        larger.StructSize++;
        Requirement noProduct = good;
        noProduct.ProductId = null;
        Requirement badUtf8 = good;
        badUtf8.FeatureId = _api.Bytes([0xC3, 0x28]);
        foreach (
            Requirement bad in new[]
            {
                smaller,
                larger,
                noProduct,
                _api.Requirement(kind: 3),
                _api.Requirement(kind: -1),
                badUtf8,
                _api.Requirement(feature: new string('f', 1025)),
                _api.Requirement(installation: verifier),
            }
        )
        {
            AssertFailure(_api.Verify(verifier, file, bad), 1);
        }

        AssertFailure(_api.Verify(0, file, good), 1);
        NativeApi.DestroyVerifier(verifier);
    }

    /// <summary>
    /// A standard requirement accepts a trial license by default, as .NET does, and refuses it with
    /// reject_trial_as_standard.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A trial license without a key binding.
    ///
    /// Steps:
    /// - Verify for a standard license with a zero-filled flag, then with reject_trial_as_standard = 1.
    ///
    /// Expected:
    /// - 0 with kind 1; ELM_WRONG_KIND (16).
    /// </remarks>
    [Fact]
    public void Verify_TrialAsStandard_FollowsTheFlag()
    {
        JsonObject payload = Unbound();
        payload["kind"] = "trial";
        payload["grace"] = 0;
        byte[] file = _issuer.File(payload);

        nint accepting = _api.CreateVerifier(_api.VerifierOptions(Roots));
        (int code, nint license) = _api.Verify(accepting, file, _api.Requirement());
        Assert.Equal((0, 1), (code, ((delegate* unmanaged<nint, int>)&Exports.LicenseKindOf)(license)));

        VerifierOptions rejecting = _api.VerifierOptions(Roots);
        rejecting.RejectTrialAsStandard = 1;
        AssertFailure(_api.Verify(_api.CreateVerifier(rejecting), file, _api.Requirement()), 16);
        NativeApi.FreeLicense(license);
    }

    /// <summary>
    /// The options of a verifier are checked: counts, booleans, the clock skew's range, and the PEM.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The root key's PEM.
    ///
    /// Steps:
    /// - Create with clock_skew_seconds 0, 1, and 3600; then with 3601 and -1; no root; 17 roots; 1,025 revoked IDs;
    ///   reject_trial_as_standard 2; a PEM that is not a key; struct_size one less; a null out.
    ///
    /// Expected:
    /// - 0 three times; ELM_INVALID_ARGUMENT (1) for the rest, with out null.
    /// </remarks>
    [Fact]
    public void Create_Options_AreChecked()
    {
        foreach (int skew in new[] { 0, 1, 3600 })
        {
            VerifierOptions options = _api.VerifierOptions(Roots);
            options.ClockSkewSeconds = skew;
            NativeApi.DestroyVerifier(_api.CreateVerifier(options));
        }

        var bad = new List<VerifierOptions>();
        foreach (int skew in new[] { 3601, -1 })
        {
            VerifierOptions options = _api.VerifierOptions(Roots);
            options.ClockSkewSeconds = skew;
            bad.Add(options);
        }

        bad.Add(_api.VerifierOptions([]));
        bad.Add(_api.VerifierOptions([.. Enumerable.Repeat(Roots[0], 17)]));
        bad.Add(_api.VerifierOptions(Roots, [.. Enumerable.Range(0, 1025).Select(i => $"k{i}")]));
        VerifierOptions flag = _api.VerifierOptions(Roots);
        flag.RejectTrialAsStandard = 2;
        bad.Add(flag);
        bad.Add(_api.VerifierOptions(["not a key"]));
        VerifierOptions smaller = _api.VerifierOptions(Roots);
        smaller.StructSize--;
        bad.Add(smaller);
        foreach (VerifierOptions options in bad)
        {
            nint result = -1;
            Assert.Equal(1, NativeApi.Create(_api.Struct(options), &result));
            Assert.Equal(0, result);
            Assert.NotEqual(string.Empty, NativeApi.LastError);
        }

        Assert.Equal(1, NativeApi.Create(_api.Struct(_api.VerifierOptions(Roots)), null));
    }

    /// <summary>
    /// A node-locked license verifies with its installation only, and the installation must be of the requirement's
    /// product.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An installation of acme-cad created in a temporary folder; a license bound to its thumbprint without fingerprints.
    ///
    /// Steps:
    /// - Verify with that installation; with none; with another installation of acme-cad; with an installation of
    ///   acme-other while the requirement names acme-cad.
    ///
    /// Expected:
    /// - 0; ELM_WRONG_INSTALLATION (8) twice; ELM_INVALID_ARGUMENT (1).
    /// </remarks>
    [Fact]
    public void Verify_NodeLocked_NeedsItsInstallation()
    {
        nint installation = _api.OpenInstallation("acme-cad", Path.Combine(_dir, "a"));
        string thumbprint = NativeApi.Utf8(((delegate* unmanaged<nint, nint>)&Exports.InstallationThumbprint)(installation))!;
        JsonObject payload = Unbound();
        payload["cnf"] = new JsonObject { ["jkt"] = thumbprint };
        byte[] file = _issuer.File(payload);
        nint verifier = _api.CreateVerifier(_api.VerifierOptions(Roots));

        (int code, nint license) = _api.Verify(verifier, file, _api.Requirement(installation: installation));
        Assert.Equal(0, code);
        AssertFailure(_api.Verify(verifier, file, _api.Requirement()), 8);
        nint other = _api.OpenInstallation("acme-cad", Path.Combine(_dir, "b"));
        AssertFailure(_api.Verify(verifier, file, _api.Requirement(installation: other)), 8);
        nint otherProduct = _api.OpenInstallation("acme-other", Path.Combine(_dir, "c"));
        AssertFailure(_api.Verify(verifier, file, _api.Requirement(installation: otherProduct)), 1);

        NativeApi.FreeLicense(license);
        NativeApi.DestroyVerifier(verifier);
        foreach (nint handle in new[] { installation, other, otherProduct })
        {
            NativeApi.FreeInstallation(handle);
        }
    }

    /// <summary>
    /// A server license gives the seats of its features through elm_feature.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An installation of acme-cad; a server license bound to its key with feature pro of 5 seats.
    ///
    /// Steps:
    /// - Verify for a server license with the installation; read feature 0.
    ///
    /// Expected:
    /// - 0; kind 2; pro with has_seats 1 and seats 5.
    /// </remarks>
    [Fact]
    public void Verify_ServerLicense_ReadsSeats()
    {
        nint installation = _api.OpenInstallation("acme-cad", Path.Combine(_dir, "a"));
        JsonObject payload = Unbound();
        payload["kind"] = "server";
        payload["cnf"] = new JsonObject
        {
            ["jkt"] = NativeApi.Utf8(((delegate* unmanaged<nint, nint>)&Exports.InstallationThumbprint)(installation)),
        };
        payload["feat"] = new JsonArray(new JsonObject { ["id"] = "pro", ["seats"] = 5 });
        payload["lease"] = 600;
        payload["borrow"] = 0;
        payload["overage"] = 0;
        payload["clone"] = "deny";
        payload["brl"] = 0;
        nint verifier = _api.CreateVerifier(_api.VerifierOptions(Roots));

        (int code, nint license) = _api.Verify(verifier, _issuer.File(payload), _api.Requirement(kind: 2, installation: installation));
        Assert.Equal((0, string.Empty), (code, NativeApi.LastError));
        Assert.Equal(2, ((delegate* unmanaged<nint, int>)&Exports.LicenseKindOf)(license));
        Feature pro = ReadFeature(license, 0);
        Assert.Equal(("pro", 1, 5), (NativeApi.Utf8((nint)pro.Id), pro.HasSeats, pro.Seats));
        NativeApi.FreeLicense(license);
        NativeApi.DestroyVerifier(verifier);
        NativeApi.FreeInstallation(installation);
    }

    /// <summary>
    /// A license bound to fingerprints of another machine fails on this machine's fingerprints.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An installation of acme-cad; a license bound to its key and to the fingerprint 0…0 with matching "any".
    ///
    /// Steps:
    /// - Verify with the installation.
    ///
    /// Expected:
    /// - ELM_FINGERPRINT_MISMATCH (9), or ELM_FINGERPRINT_UNAVAILABLE (14) on a machine without an OS ID.
    /// </remarks>
    [Fact]
    public void Verify_OtherFingerprints_Mismatch()
    {
        nint installation = _api.OpenInstallation("acme-cad", Path.Combine(_dir, "a"));
        JsonObject payload = Unbound();
        payload["cnf"] = new JsonObject
        {
            ["jkt"] = NativeApi.Utf8(((delegate* unmanaged<nint, nint>)&Exports.InstallationThumbprint)(installation)),
        };
        payload["fp"] = new JsonArray(new string('0', 32));
        payload["fpm"] = "any";
        nint verifier = _api.CreateVerifier(_api.VerifierOptions(Roots));

        int expected = MachineFingerprint.Compute("acme-cad").Count == 0 ? 14 : 9;
        AssertFailure(_api.Verify(verifier, _issuer.File(payload), _api.Requirement(installation: installation)), expected);
        NativeApi.DestroyVerifier(verifier);
        NativeApi.FreeInstallation(installation);
    }

    /// <summary>
    /// The installation is opened or created as asked, and its options are checked.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty temporary folder.
    ///
    /// Steps:
    /// - Open with create = 0; with create = 1 twice; with scope 2, create 2, a null product, a product that is not a
    ///   product ID, struct_size one more, and a null out.
    ///
    /// Expected:
    /// - ELM_INSTALLATION_KEY_UNREADABLE (15) with out null; 0 twice with the same 43-character thumbprint; then
    ///   ELM_INVALID_ARGUMENT (1) each time.
    /// </remarks>
    [Fact]
    public void Open_CreatesOrRefusesAsAsked()
    {
        string folder = Path.Combine(_dir, "key");
        nint result = -1;
        Assert.Equal(15, NativeApi.Open(_api.Struct(_api.InstallationOptions("acme-cad", folder, create: 0)), &result));
        Assert.Equal(0, result);

        nint first = _api.OpenInstallation("acme-cad", folder);
        nint second = _api.OpenInstallation("acme-cad", folder);
        string? thumbprint = NativeApi.Utf8(((delegate* unmanaged<nint, nint>)&Exports.InstallationThumbprint)(first));
        Assert.Equal(43, thumbprint?.Length);
        Assert.Equal(thumbprint, NativeApi.Utf8(((delegate* unmanaged<nint, nint>)&Exports.InstallationThumbprint)(second)));

        InstallationOptions larger = _api.InstallationOptions("acme-cad", folder);
        larger.StructSize++;
        foreach (
            InstallationOptions bad in new[]
            {
                _api.InstallationOptions("acme-cad", folder, scope: 2),
                _api.InstallationOptions("acme-cad", folder, create: 2),
                _api.InstallationOptions(null, folder),
                _api.InstallationOptions("Acme CAD", folder),
                larger,
            }
        )
        {
            result = -1;
            Assert.Equal(1, NativeApi.Open(_api.Struct(bad), &result));
            Assert.Equal(0, result);
        }

        Assert.Equal(1, NativeApi.Open(_api.Struct(_api.InstallationOptions("acme-cad", folder)), null));
        NativeApi.FreeInstallation(first);
        NativeApi.FreeInstallation(second);
    }

    /// <summary>
    /// The three clock guards: signed with the installation, unsigned at a path, and without state.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An unbound license; the clock at T0 + 20 days, then set back to T0 + 10 days.
    ///
    /// Steps:
    /// - For each guard: verify at T0 + 20 days with one verifier; set the clock back; verify with a new verifier of the same
    ///   options (the installation freed in between for the signed guard).
    ///
    /// Expected:
    /// - Signed and unsigned: ELM_CLOCK_TAMPERED (6) after the set-back. Without state: 0.
    /// </remarks>
    [Fact]
    public void Verify_ClockSetBack_FollowsTheGuard()
    {
        byte[] file = _issuer.File(Unbound());
        Directory.CreateDirectory(_dir);
        string unsignedPath = Path.Combine(_dir, "unsigned-clock");
        foreach (string guard in new[] { "signed", "unsigned", "none" })
        {
            // A fake clock cannot go back, so each verifier gets its own.
            Exports.Time = new FakeTimeProvider(TestIssuer.Now.AddDays(20));
            Assert.Equal(0, VerifyOnce(guard));
            Exports.Time = new FakeTimeProvider(TestIssuer.Now.AddDays(10));
            Assert.Equal(guard == "none" ? 0 : 6, VerifyOnce(guard));
        }

        int VerifyOnce(string guard)
        {
            VerifierOptions options = _api.VerifierOptions(Roots);
            nint installation = 0;
            if (guard == "signed")
            {
                installation = _api.OpenInstallation("acme-cad", Path.Combine(_dir, "signed"));
                options.Installation = installation;
            }
            else if (guard == "unsigned")
            {
                options.ClockGuardPath = _api.Text(unsignedPath);
            }

            nint verifier = _api.CreateVerifier(options);
            NativeApi.FreeInstallation(installation);
            (int code, nint license) = _api.Verify(verifier, file, _api.Requirement());
            NativeApi.FreeLicense(license);
            NativeApi.DestroyVerifier(verifier);
            return code;
        }
    }

    /// <summary>
    /// A zero clock_skew_seconds is the default of 300 seconds: a clock set back by 300 seconds passes the guard, by 301
    /// seconds it does not.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An unbound license; an unsigned guard at a path that has seen T0 + 20 days.
    ///
    /// Steps:
    /// - With zero-filled options and the same guard path, verify at T0 + 20 days - 300 s and at T0 + 20 days - 301 s.
    ///
    /// Expected:
    /// - 0; ELM_CLOCK_TAMPERED (6).
    /// </remarks>
    [Fact]
    public void Verify_ZeroClockSkew_IsTheDefault300Seconds()
    {
        byte[] file = _issuer.File(Unbound());
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "clock");
        DateTimeOffset seen = TestIssuer.Now.AddDays(20);
        Assert.Equal(0, VerifyAt(seen));
        Assert.Equal(0, VerifyAt(seen.AddSeconds(-300)));
        Assert.Equal(6, VerifyAt(seen.AddSeconds(-301)));

        int VerifyAt(DateTimeOffset now)
        {
            Exports.Time = new FakeTimeProvider(now);
            VerifierOptions options = _api.VerifierOptions(Roots);
            options.ClockGuardPath = _api.Text(path);
            nint verifier = _api.CreateVerifier(options);
            (int code, nint license) = _api.Verify(verifier, file, _api.Requirement());
            NativeApi.FreeLicense(license);
            NativeApi.DestroyVerifier(verifier);
            return code;
        }
    }

    /// <summary>
    /// A license past its expiry is in its grace period until the grace ends, and expires at the second after.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license expiring at T0 + 1 day with a grace period of 7 days.
    ///
    /// Steps:
    /// - Verify at the expiry minus one second, at the expiry plus 7 days, and at the expiry plus 7 days and one second.
    ///
    /// Expected:
    /// - 0 not in grace; 0 in grace; ELM_EXPIRED (10).
    /// </remarks>
    [Fact]
    public void Verify_GracePeriod_IsReported()
    {
        JsonObject payload = Unbound();
        long expiry = TestIssuer.NowSeconds + 86400;
        payload["exp"] = expiry;
        byte[] file = _issuer.File(payload);
        nint verifier = _api.CreateVerifier(_api.VerifierOptions(Roots));

        Assert.Equal((0, 0), InGraceAt(expiry - 1));
        Assert.Equal((0, 1), InGraceAt(expiry + (86400 * 7)));
        Assert.Equal(10, InGraceAt(expiry + (86400 * 7) + 1).Code);
        NativeApi.DestroyVerifier(verifier);

        (int Code, int InGrace) InGraceAt(long seconds)
        {
            _time.SetUtcNow(DateTimeOffset.FromUnixTimeSeconds(seconds));
            (int code, nint license) = _api.Verify(verifier, file, _api.Requirement());
            int inGrace = ((delegate* unmanaged<nint, int>)&Exports.LicenseInGracePeriod)(license);
            NativeApi.FreeLicense(license);
            return (code, inGrace);
        }
    }

    /// <summary>
    /// Meta keys and values may hold NUL; lookups, indexes, and the outputs are checked.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license with meta {"a\0b": "x\0y", "c": "d"} and one feature, and no expiry.
    ///
    /// Steps:
    /// - Read both pairs by index; look up "c" and "zz"; look up a null key; read index 2; read feature 1 into the struct
    ///   that holds feature 0; read feature 0 with struct_size one more; read the expiry with a null output.
    ///
    /// Expected:
    /// - "a\0b" = "x\0y" with lengths 3 and 3, then "c" = "d"; "d"; null with 0; then ELM_INVALID_ARGUMENT (1) for the
    ///   rest with the outputs null and 0 (the feature struct's members too, once its struct_size is right); the expiry
    ///   absent.
    /// </remarks>
    [Fact]
    public void Meta_WithNul_AndBadIndexes()
    {
        JsonObject payload = Unbound();
        payload.Remove("exp");
        payload["feat"] = new JsonArray(new JsonObject { ["id"] = "pro" });
        payload["meta"] = new JsonObject { ["a\0b"] = "x\0y", ["c"] = "d" };
        nint verifier = _api.CreateVerifier(_api.VerifierOptions(Roots));
        (int code, nint license) = _api.Verify(verifier, _issuer.File(payload), _api.Requirement());
        Assert.Equal(0, code);

        Assert.Equal(("a\0b", "x\0y"), MetaAt(license, 0));
        Assert.Equal(("c", "d"), MetaAt(license, 1));
        Assert.Equal("d", MetaGet(license, "c"));
        Assert.Null(MetaGet(license, "zz"));

        nint value = -1;
        nuint length = 7;
        Assert.Equal(1, ((delegate* unmanaged<nint, byte*, nint*, nuint*, int>)&Exports.LicenseMetaGet)(license, null, &value, &length));
        Assert.Equal((0, (nuint)0), (value, length));
        nint key = -1;
        nuint keyLength = 7;
        Assert.Equal(
            1,
            ((delegate* unmanaged<nint, nuint, nint*, nuint*, nint*, nuint*, int>)&Exports.LicenseMetaAt)(
                license,
                2,
                &key,
                &keyLength,
                &value,
                &length
            )
        );
        Assert.Equal((0, (nuint)0, "index must be less than 2."), (key, keyLength, NativeApi.LastError));

        Feature feature = ReadFeature(license, 0);
        Assert.Equal(1, ((delegate* unmanaged<nint, nuint, nint, int>)&Exports.LicenseFeature)(license, 1, (nint)(&feature)));
        Assert.Equal("index must be less than 1.", NativeApi.LastError);
        Assert.Equal(
            ((nint)0, (nint)0, 0, 0, 0, 0L),
            ((nint)feature.Id, (nint)feature.VersionRange, feature.HasSeats, feature.Seats, feature.HasExpiresAt, feature.ExpiresAt)
        );
        feature.StructSize++;
        Assert.Equal(1, ((delegate* unmanaged<nint, nuint, nint, int>)&Exports.LicenseFeature)(license, 0, (nint)(&feature)));

        int has = 7;
        long expires = 7;
        Assert.Equal(1, ((delegate* unmanaged<nint, int*, long*, int>)&Exports.LicenseExpiresAt)(license, &has, null));
        Assert.Equal(0, has);
        Assert.Equal(0, ((delegate* unmanaged<nint, int*, long*, int>)&Exports.LicenseExpiresAt)(license, &has, &expires));
        Assert.Equal((0, 0L), (has, expires));

        NativeApi.FreeLicense(license);
        NativeApi.DestroyVerifier(verifier);
    }

    /// <summary>
    /// Value functions take null and wrong handles without failing and without changing the message; releases ignore null.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A message left by a failed call; a verifier handle.
    ///
    /// Steps:
    /// - Call each value function with null; read the expiry of the verifier handle as a license; free null handles.
    ///
    /// Expected:
    /// - null, 0, or -1 as the header says, and the message unchanged; the wrong handle: ELM_INVALID_ARGUMENT (1).
    /// </remarks>
    [Fact]
    public void NullAndWrongHandles_AreHandled()
    {
        nint verifier = _api.CreateVerifier(_api.VerifierOptions(Roots));
        AssertFailure(_api.Verify(verifier, [], _api.Requirement()), 2);
        string message = NativeApi.LastError;

        Assert.Equal(0, ((delegate* unmanaged<nint, nint>)&Exports.InstallationThumbprint)(0));
        Assert.Equal(0, ((delegate* unmanaged<nint, nint>)&Exports.LicenseId)(0));
        Assert.Equal(0, ((delegate* unmanaged<nint, nint>)&Exports.LicenseProduct)(0));
        Assert.Equal(-1, ((delegate* unmanaged<nint, int>)&Exports.LicenseKindOf)(0));
        Assert.Equal(0, ((delegate* unmanaged<nint, long>)&Exports.LicenseIssuedAt)(0));
        Assert.Equal(0, ((delegate* unmanaged<nint, long>)&Exports.LicenseGracePeriodSeconds)(0));
        Assert.Equal(-1, ((delegate* unmanaged<nint, int>)&Exports.LicenseInGracePeriod)(0));
        Assert.Equal((nuint)0, ((delegate* unmanaged<nint, nuint>)&Exports.LicenseFeatureCount)(0));
        Assert.Equal((nuint)0, ((delegate* unmanaged<nint, nuint>)&Exports.LicenseMetaCount)(0));
        Assert.Equal(-1, ((delegate* unmanaged<nint, int>)&Exports.LicenseKindOf)(verifier));
        Assert.Equal(message, NativeApi.LastError);

        int has;
        long expires;
        Assert.Equal(1, ((delegate* unmanaged<nint, int*, long*, int>)&Exports.LicenseExpiresAt)(verifier, &has, &expires));
        NativeApi.FreeInstallation(0);
        NativeApi.FreeLicense(0);
        NativeApi.DestroyVerifier(0);
        NativeApi.DestroyVerifier(verifier);
        Assert.NotEqual(string.Empty, NativeApi.Utf8(((delegate* unmanaged<nint>)&Exports.VersionText)()));
    }

    /// <summary>
    /// The error message belongs to its thread, and the next call on the thread empties it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A verifier.
    ///
    /// Steps:
    /// - Fail a verification on this thread; read the message on another thread; read it here; verify a good file here.
    ///
    /// Expected:
    /// - The other thread's message is empty; this thread's names the failure; it is empty after the success.
    /// </remarks>
    [Fact]
    public void ErrorMessage_IsPerThread()
    {
        nint verifier = _api.CreateVerifier(_api.VerifierOptions(Roots));
        AssertFailure(_api.Verify(verifier, [], _api.Requirement()), 2);

        string? other = null;
        var thread = new Thread(() => other = NativeApi.LastError);
        thread.Start();
        thread.Join();
        Assert.Equal(string.Empty, other);
        Assert.NotEqual(string.Empty, NativeApi.LastError);

        (int code, nint license) = _api.Verify(verifier, _issuer.File(Unbound()), _api.Requirement());
        Assert.Equal((0, string.Empty), (code, NativeApi.LastError));
        NativeApi.FreeLicense(license);
        NativeApi.DestroyVerifier(verifier);
    }

    /// <summary>
    /// A license is read from several threads at once with the same results.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A verified license with meta {"customer": "Contoso"}.
    ///
    /// Steps:
    /// - On 8 threads, read the ID and the meta value 1,000 times each.
    ///
    /// Expected:
    /// - Every read gives "lic-1" and "Contoso".
    /// </remarks>
    [Fact]
    public void License_IsReadFromSeveralThreads()
    {
        JsonObject payload = Unbound();
        payload["meta"] = new JsonObject { ["customer"] = "Contoso" };
        nint verifier = _api.CreateVerifier(_api.VerifierOptions(Roots));
        (int code, nint license) = _api.Verify(verifier, _issuer.File(payload), _api.Requirement());
        Assert.Equal(0, code);

        int wrong = 0;
        nint customer = (nint)_api.Text("customer");
        Parallel.For(
            0,
            8,
            _ =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    nint value;
                    nuint length;
                    int code = ((delegate* unmanaged<nint, byte*, nint*, nuint*, int>)&Exports.LicenseMetaGet)(
                        license,
                        (byte*)customer,
                        &value,
                        &length
                    );
                    if (
                        NativeApi.Utf8(((delegate* unmanaged<nint, nint>)&Exports.LicenseId)(license)) != "lic-1"
                        || code != 0
                        || NativeApi.Utf8(value, length) != "Contoso"
                    )
                    {
                        Interlocked.Increment(ref wrong);
                    }
                }
            }
        );
        Assert.Equal(0, wrong);
        NativeApi.FreeLicense(license);
        NativeApi.DestroyVerifier(verifier);
    }

    private static void AssertFailure((int Code, nint License) result, int expected)
    {
        Assert.Equal((expected, (nint)0), result);
        Assert.NotEqual(string.Empty, NativeApi.LastError);
    }

    private static Feature ReadFeature(nint license, nuint index)
    {
        Feature feature = new() { StructSize = (uint)sizeof(Feature) };
        Assert.Equal(0, ((delegate* unmanaged<nint, nuint, nint, int>)&Exports.LicenseFeature)(license, index, (nint)(&feature)));
        return feature;
    }

    private static (string Key, string Value) MetaAt(nint license, nuint index)
    {
        nint key;
        nuint keyLength;
        nint value;
        nuint valueLength;
        Assert.Equal(
            0,
            ((delegate* unmanaged<nint, nuint, nint*, nuint*, nint*, nuint*, int>)&Exports.LicenseMetaAt)(
                license,
                index,
                &key,
                &keyLength,
                &value,
                &valueLength
            )
        );
        return (NativeApi.Utf8(key, keyLength), NativeApi.Utf8(value, valueLength));
    }

    private string? MetaGet(nint license, string key)
    {
        nint value;
        nuint length;
        byte* text = _api.Text(key);
        Assert.Equal(0, ((delegate* unmanaged<nint, byte*, nint*, nuint*, int>)&Exports.LicenseMetaGet)(license, text, &value, &length));
        return value == 0 ? null : NativeApi.Utf8(value, length);
    }

    // A standard license of acme-cad without a key or fingerprint binding.
    private JsonObject Unbound()
    {
        JsonObject payload = _issuer.LicensePayload();
        payload.Remove("cnf");
        payload.Remove("fp");
        payload.Remove("fpm");
        return payload;
    }
}
