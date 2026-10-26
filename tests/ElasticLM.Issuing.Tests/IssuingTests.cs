using System.Security.Cryptography;
using ElasticLM.Licensing;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Issuing.Tests;

/// <summary>
/// Tests of <see cref="KeyFiles"/>, <see cref="CertificateIssuer"/>, and <see cref="LicenseIssuer"/>, checked against the
/// M1 verifier.
/// </summary>
public sealed class IssuingTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly ECDsa _root = KeyFiles.CreateKey();
    private readonly ECDsa _signing = KeyFiles.CreateKey();
    private readonly ECDsa _installation = KeyFiles.CreateKey();

    public void Dispose()
    {
        _root.Dispose();
        _signing.Dispose();
        _installation.Dispose();
    }

    /// <summary>
    /// An issued license of each kind, bound or unbound, with or without expiry, passes the verifier.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A root key, a signing key certified for license by the root, the root in the verifier's trust store.
    ///
    /// Steps:
    /// - Issue the license described by the case and verify it with the matching requirement.
    ///
    /// Expected:
    /// - Valid, with the license ID and kind of the spec.
    /// </remarks>
    [Theory]
    [InlineData(LicenseKind.Standard, true, true)]
    [InlineData(LicenseKind.Standard, false, true)]
    [InlineData(LicenseKind.Standard, false, false)]
    [InlineData(LicenseKind.Trial, true, true)]
    [InlineData(LicenseKind.Server, true, true)]
    public void Issue_EachKind_PassesVerifier(LicenseKind kind, bool bound, bool expires)
    {
        LicenseSpec spec = Spec(kind, bound, expires);

        IssuedLicense issued = LicenseIssuer.Issue(_signing, [Certificate()], spec, _time);
        LicenseVerificationResult result = Verifier().Verify(issued.File, Requirement(kind, bound));

        var valid = Assert.IsType<LicenseVerificationResult.Valid>(result);
        Assert.Equal("lic-1", valid.License.LicenseId);
        Assert.Equal(kind, valid.License.Kind);
        Assert.Equal(issued.TokenId, valid.License.TokenId);
        Assert.Equal(spec.CloneStrategy, valid.License.CloneStrategy);
        Assert.Equal(expires ? 0 : 1, issued.Warnings.Count(w => w.Contains("no expiry", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A server license with the flag clone strategy reads back as flag; deny is covered by the test of each kind.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server license spec with CloneStrategy.Flag.
    ///
    /// Steps:
    /// - Issue and verify it.
    ///
    /// Expected:
    /// - The verified license has CloneStrategy.Flag.
    /// </remarks>
    [Fact]
    public void Issue_ServerWithFlag_ReadsBackFlag()
    {
        LicenseSpec spec = Spec(LicenseKind.Server, true, true) with { CloneStrategy = CloneStrategy.Flag };

        IssuedLicense issued = LicenseIssuer.Issue(_signing, [Certificate()], spec, _time);

        var valid = Assert.IsType<LicenseVerificationResult.Valid>(Verifier().Verify(issued.File, Requirement(LicenseKind.Server, true)));
        Assert.Equal(CloneStrategy.Flag, valid.License.CloneStrategy);
    }

    /// <summary>
    /// The issuer refuses specs the verifier would reject, before signing.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A valid spec, changed in one way: a server license without lease or without clone strategy, seats on a standard license, a trial with grace,
    ///   257 features, 9 fingerprints, a bad version range, an empty product, a negative grace period.
    ///
    /// Steps:
    /// - Issue it.
    ///
    /// Expected:
    /// - ArgumentException each time.
    /// </remarks>
    [Theory]
    [InlineData("server-without-lease")]
    [InlineData("server-without-clone")]
    [InlineData("seats-on-standard")]
    [InlineData("trial-with-grace")]
    [InlineData("too-many-features")]
    [InlineData("too-many-fingerprints")]
    [InlineData("bad-range")]
    [InlineData("empty-product")]
    [InlineData("negative-grace")]
    public void Issue_SpecTheVerifierRejects_Throws(string kind)
    {
        LicenseSpec spec = Spec(LicenseKind.Standard, bound: true, expires: true);
        spec = kind switch
        {
            "server-without-lease" => Spec(LicenseKind.Server, true, true) with { LeaseDuration = null },
            "server-without-clone" => Spec(LicenseKind.Server, true, true) with { CloneStrategy = null },
            "seats-on-standard" => spec with { Features = [new FeatureSpec("pro", Seats: 5)] },
            "trial-with-grace" => Spec(LicenseKind.Trial, true, true) with { GracePeriod = TimeSpan.FromDays(1) },
            "too-many-features" => spec with { Features = [.. Enumerable.Range(0, 257).Select(i => new FeatureSpec("f" + i))] },
            "too-many-fingerprints" => spec with
            {
                Fingerprints = [.. Enumerable.Range(0, 9).Select(i => i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture))],
            },
            "bad-range" => spec with { Features = [new FeatureSpec("pro", VersionRange: "~2")] },
            "empty-product" => spec with { ProductId = string.Empty },
            _ => spec with { GracePeriod = TimeSpan.FromSeconds(-1) },
        };

        Assert.Throws<ArgumentException>(() => LicenseIssuer.Issue(_signing, [Certificate()], spec, _time));
    }

    /// <summary>
    /// The issuer refuses certificates that do not let the signing key sign licenses at the issue time.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Certificates for another key, for lease only, and for a period that ended before the issue time; no certificate; five.
    ///
    /// Steps:
    /// - Issue a valid spec with each.
    ///
    /// Expected:
    /// - ArgumentException each time.
    /// </remarks>
    [Fact]
    public void Issue_CertificateDoesNotCover_Throws()
    {
        using ECDsa other = KeyFiles.CreateKey();
        LicenseSpec spec = Spec(LicenseKind.Standard, bound: true, expires: true);
        string forOther = CertificateIssuer.Issue(_root, other, KeyUses.License, Now.AddDays(-1), TimeSpan.FromDays(30), _time);
        string leaseOnly = CertificateIssuer.Issue(_root, _signing, KeyUses.Lease, Now.AddDays(-1), TimeSpan.FromDays(30), _time);
        string ended = CertificateIssuer.Issue(_root, _signing, KeyUses.License, Now.AddDays(-60), TimeSpan.FromDays(30), _time);

        AssertThrows("is for the signing key", () => LicenseIssuer.Issue(_signing, [forOther], spec, _time));
        AssertThrows("use license", () => LicenseIssuer.Issue(_signing, [leaseOnly], spec, _time));
        AssertThrows("issue time", () => LicenseIssuer.Issue(_signing, [ended], spec, _time));
        AssertThrows("can be read", () => LicenseIssuer.Issue(_signing, [KeyFiles.ExportPublic(_root)], spec, _time));
        AssertThrows("issue time", () => LicenseIssuer.Issue(_signing, [forOther, ended], spec, _time));
        AssertThrows("Between one and four", () => LicenseIssuer.Issue(_signing, [], spec, _time));
        string good = Certificate();
        AssertThrows("Between one and four", () => LicenseIssuer.Issue(_signing, [good, good, good, good, good], spec, _time));
        Assert.NotNull(LicenseIssuer.Issue(_signing, ["  " + good + Environment.NewLine], spec, _time));
    }

    /// <summary>
    /// With a trust store, the issued file is run through the verifier, so certificates the root did not sign, or whose key
    /// does not match their subject, are refused at issuance; a correct file passes.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A certificate from the trusted root; one from another root; one whose jwk is another key under the right subject.
    ///
    /// Steps:
    /// - Issue with each and the trust store of the right root; issue with the right certificate and another root's trust
    ///   store; issue an already-expired license.
    ///
    /// Expected:
    /// - The right certificate passes; the others throw ArgumentException naming the verifier's error.
    /// </remarks>
    [Fact]
    public void Issue_WithTrustStore_VerifiesWithTheVerifier()
    {
        using ECDsa otherRoot = KeyFiles.CreateKey();
        using ECDsa otherKey = KeyFiles.CreateKey();
        LicenseSpec spec = Spec(LicenseKind.Standard, bound: true, expires: true);
        TrustStore trust = TrustStore.Create([KeyFiles.ExportPublic(_root)], []);
        string fromOtherRoot = CertificateIssuer.Issue(otherRoot, _signing, KeyUses.License, Now.AddDays(-1), TimeSpan.FromDays(30), _time);
        string[] parts = CertificateIssuer.Issue(_root, otherKey, KeyUses.License, Now.AddDays(-1), TimeSpan.FromDays(30), _time).Split('.');
        string payload = System.Text.Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(parts[1]));
        string swapped = payload.Replace(KeyFiles.KeyId(otherKey), KeyFiles.KeyId(_signing), StringComparison.Ordinal);
        string jwkMismatch = SignAsRoot(parts[0], swapped);

        Assert.NotNull(LicenseIssuer.Issue(_signing, [Certificate()], spec, _time, trust));
        AssertThrows("ELM_UNTRUSTED_KEY", () => LicenseIssuer.Issue(_signing, [fromOtherRoot], spec, _time, trust));
        AssertThrows("ELM_UNTRUSTED_KEY", () => LicenseIssuer.Issue(_signing, [jwkMismatch], spec, _time, trust));
        AssertThrows(
            "ELM_UNTRUSTED_KEY",
            () => LicenseIssuer.Issue(_signing, [Certificate()], spec, _time, TrustStore.Create([KeyFiles.ExportPublic(otherRoot)], []))
        );
        AssertThrows("ELM_EXPIRED", () => LicenseIssuer.Issue(_signing, [Certificate()], spec with { ExpiresAt = Now.AddDays(-30) }, _time, trust));
    }

    /// <summary>
    /// A certificate period outside 1970..9999 is refused (design §8.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A root and a signing key.
    ///
    /// Steps:
    /// - Certify with not-before in 1969, and with not-before 9999-12-01 and 60 days of validity.
    ///
    /// Expected:
    /// - ArgumentException both times.
    /// </remarks>
    [Fact]
    public void IssueCertificate_PeriodOutsideTimes_Throws()
    {
        AssertThrows(
            "1970 and 9999",
            () =>
                CertificateIssuer.Issue(
                    _root,
                    _signing,
                    KeyUses.License,
                    new DateTimeOffset(1969, 12, 31, 0, 0, 0, TimeSpan.Zero),
                    TimeSpan.FromDays(1),
                    _time
                )
        );
        AssertThrows(
            "1970 and 9999",
            () =>
                CertificateIssuer.Issue(
                    _root,
                    _signing,
                    KeyUses.License,
                    new DateTimeOffset(9999, 12, 1, 0, 0, 0, TimeSpan.Zero),
                    TimeSpan.FromDays(60),
                    _time
                )
        );
    }

    /// <summary>
    /// A spec whose token would exceed 16 KiB is refused, though each metadata entry is inside its own limit.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 32 metadata entries of 1,000 bytes each (the entry limits are 32 and 1,024 bytes).
    ///
    /// Steps:
    /// - Issue.
    ///
    /// Expected:
    /// - ArgumentException, so no file larger than the verifier accepts is written.
    /// </remarks>
    [Fact]
    public void Issue_TokenOverLimit_Throws()
    {
        LicenseSpec spec = Spec(LicenseKind.Standard, bound: true, expires: true) with
        {
            Metadata = Enumerable.Range(0, 32).ToDictionary(i => "k" + i, _ => new string('v', 1000)),
        };

        Assert.Throws<ArgumentException>(() => LicenseIssuer.Issue(_signing, [Certificate()], spec, _time));
    }

    /// <summary>
    /// Keys that are not P-256 are refused when exported, imported, and certified.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A P-384 key.
    ///
    /// Steps:
    /// - Export it encrypted; import a P-384 key encrypted by .NET directly; certify it.
    ///
    /// Expected:
    /// - ArgumentException on export and certify; the import returns false.
    /// </remarks>
    [Fact]
    public void Keys_NotP256_AreRefused()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string encrypted = p384.ExportEncryptedPkcs8PrivateKeyPem(
            Passphrase,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000)
        );

        Assert.Throws<ArgumentException>(() => KeyFiles.ExportEncrypted(p384, Passphrase));
        Assert.False(KeyFiles.TryImportEncrypted(encrypted, Passphrase, out _));
        Assert.Throws<ArgumentException>(() => CertificateIssuer.Issue(_root, p384, KeyUses.License, Now, TimeSpan.FromDays(1), _time));
    }

    /// <summary>
    /// Certificates accept a validity up to two years and refuse longer, zero, and no use.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A root and a signing key.
    ///
    /// Steps:
    /// - Issue certificates with validity 731 days, 732 days, zero, and with KeyUses.None.
    ///
    /// Expected:
    /// - 731 days succeeds; the others throw ArgumentException.
    /// </remarks>
    [Fact]
    public void IssueCertificate_ValidityAndUses_AreBounded()
    {
        Assert.NotEmpty(CertificateIssuer.Issue(_root, _signing, KeyUses.License, Now, TimeSpan.FromDays(731), _time));
        Assert.Throws<ArgumentException>(() => CertificateIssuer.Issue(_root, _signing, KeyUses.License, Now, TimeSpan.FromDays(732), _time));
        Assert.Throws<ArgumentException>(() => CertificateIssuer.Issue(_root, _signing, KeyUses.License, Now, TimeSpan.Zero, _time));
        Assert.Throws<ArgumentException>(() => CertificateIssuer.Issue(_root, _signing, KeyUses.None, Now, TimeSpan.FromDays(1), _time));
    }

    /// <summary>
    /// An encrypted key file reads back with the right passphrase and not with a wrong one; plain and public PEMs are
    /// not accepted as encrypted keys; short passphrases are refused.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A key exported encrypted with a 28-character passphrase.
    ///
    /// Steps:
    /// - Import with the passphrase, with another passphrase, import a PKCS#8 plain PEM and a public PEM, export with an
    ///   11-character passphrase.
    ///
    /// Expected:
    /// - Only the right passphrase reads the key, and the read key has the same key ID; the 11-character export throws.
    /// </remarks>
    [Fact]
    public void KeyFiles_EncryptedRoundTrip_NeedsThePassphrase()
    {
        string pem = KeyFiles.ExportEncrypted(_signing, Passphrase);

        Assert.StartsWith("-----BEGIN ENCRYPTED PRIVATE KEY-----", pem, StringComparison.Ordinal);
        Assert.True(KeyFiles.TryImportEncrypted(pem, Passphrase, out ECDsa? read));
        using (read)
        {
            Assert.Equal(KeyFiles.KeyId(_signing), KeyFiles.KeyId(read!));
        }

        Assert.False(KeyFiles.TryImportEncrypted(pem, "wrong horse battery staple", out _));
        Assert.False(KeyFiles.TryImportEncrypted(_signing.ExportPkcs8PrivateKeyPem(), Passphrase, out _));
        Assert.False(KeyFiles.TryImportEncrypted(KeyFiles.ExportPublic(_signing), Passphrase, out _));
        Assert.Throws<ArgumentException>(() => KeyFiles.ExportEncrypted(_signing, "short-11chr"));
    }

    private static void AssertThrows(string expected, Action action)
    {
        ArgumentException e = Assert.ThrowsAny<ArgumentException>(action);
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    private string SignAsRoot(string encodedHeader, string payloadJson)
    {
        string input = encodedHeader + "." + System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(payloadJson));
        byte[] signature = _root.SignData(
            System.Text.Encoding.ASCII.GetBytes(input),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );
        return input + "." + System.Buffers.Text.Base64Url.EncodeToString(signature);
    }

    private string Certificate() => CertificateIssuer.Issue(_root, _signing, KeyUses.License, Now.AddDays(-1), TimeSpan.FromDays(365), _time);

    private LicenseVerifier Verifier() => new(TrustStore.Create([KeyFiles.ExportPublic(_root)], []), _time, StatelessClockGuard.Instance);

    private LicenseSpec Spec(LicenseKind kind, bool bound, bool expires) =>
        new()
        {
            LicenseId = "lic-1",
            ProductId = "acme-cad",
            Kind = kind,
            Features = kind == LicenseKind.Server ? [new FeatureSpec("pro", Seats: 5)] : [new FeatureSpec("pro", ">=2.0 <3.0")],
            ExpiresAt = expires ? Now.AddDays(30) : null,
            GracePeriod = kind == LicenseKind.Trial ? TimeSpan.Zero : TimeSpan.FromDays(7),
            InstallationKeyThumbprint = bound ? KeyFiles.KeyId(_installation) : null,
            Fingerprints = bound ? ["2b9b86465b5d4fbda9c050845613fa89"] : [],
            LeaseDuration = kind == LicenseKind.Server ? TimeSpan.FromMinutes(10) : null,
            BorrowLimit = kind == LicenseKind.Server ? TimeSpan.Zero : null,
            OveragePercent = kind == LicenseKind.Server ? 0 : null,
            CloneStrategy = kind == LicenseKind.Server ? CloneStrategy.Deny : null,
        };

    private LicenseRequirement Requirement(LicenseKind kind, bool bound) =>
        new(
            "acme-cad",
            kind,
            "pro",
            kind == LicenseKind.Server ? null : "2.5",
            bound ? new InstallationIdentity(KeyFiles.KeyId(_installation), ["2b9b86465b5d4fbda9c050845613fa89"]) : null
        );
}
