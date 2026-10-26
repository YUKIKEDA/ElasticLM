using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ElasticLM.Licensing;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Issuing.Tests;

/// <summary>
/// Leases issued by <see cref="LeaseIssuer"/> and checked by <see cref="LeaseVerifier"/> (design §8.4.3, §12.7.5).
/// </summary>
public sealed class LeaseTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly ECDsa _root = KeyFiles.CreateKey();
    private readonly ECDsa _signing = KeyFiles.CreateKey();
    private readonly ECDsa _server = KeyFiles.CreateKey();
    private readonly ECDsa _client = KeyFiles.CreateKey();

    public void Dispose()
    {
        _root.Dispose();
        _signing.Dispose();
        _server.Dispose();
        _client.Dispose();
    }

    private string ClientKid => KeyFiles.KeyId(_client);

    /// <summary>
    /// A lease issued by the server under its server license passes the client's check.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server license (feature pro, 5 seats, lease 10 min, borrow 7 days, expires in 30 days, grace 7 days) bound to the
    ///   server key; the server identity built from it and the server's public JWK.
    ///
    /// Steps:
    /// - Issue a 10-minute lease for 2 seats and a 7-day borrow; verify both.
    ///
    /// Expected:
    /// - Valid, with the lease's values.
    /// </remarks>
    [Fact]
    public void VerifyOnPremises_IssuedLease_IsValid()
    {
        ServerIdentity server = Server();

        var valid = Assert.IsType<LeaseVerificationResult.Valid>(Verifier().VerifyOnPremises(Lease(server), server, Expect()));
        Assert.Equal("lease-1", valid.Lease.LeaseId);
        Assert.Equal(2, valid.Lease.Seats);
        Assert.False(valid.Lease.IsBorrow);
        Assert.Equal(server.License.TokenId, valid.Lease.ServerLicenseTokenId);

        string borrow = Lease(server, spec => spec with { IsBorrow = true, Duration = TimeSpan.FromDays(7) });
        Assert.True(Assert.IsType<LeaseVerificationResult.Valid>(Verifier().VerifyOnPremises(borrow, server, Expect())).Lease.IsBorrow);
    }

    /// <summary>
    /// Each step of design §12.7.5, broken alone, rejects the lease.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The server identity of the valid case.
    ///
    /// Steps:
    /// - Verify a lease that: is signed by another key; names another server license; is for another product; is bound to
    ///   another installation; names another license; another feature; more seats than the license; lasts longer than the
    ///   lease duration; borrows longer than the limit; outlives the server license and its grace; is issued in the future;
    ///   is expired; meets a clock guard that rejects.
    ///
    /// Expected:
    /// - The error code listed for each case.
    /// </remarks>
    [Theory]
    [InlineData("other-signer", ErrorCode.BadSignature)]
    [InlineData("other-server-license", ErrorCode.LeaseOutOfBounds)]
    [InlineData("product", ErrorCode.WrongProduct)]
    [InlineData("installation", ErrorCode.WrongInstallation)]
    [InlineData("license", ErrorCode.LeaseOutOfBounds)]
    [InlineData("feature", ErrorCode.LeaseOutOfBounds)]
    [InlineData("seats", ErrorCode.LeaseOutOfBounds)]
    [InlineData("duration", ErrorCode.LeaseOutOfBounds)]
    [InlineData("borrow", ErrorCode.LeaseOutOfBounds)]
    [InlineData("outlives", ErrorCode.LeaseOutOfBounds)]
    [InlineData("future", ErrorCode.ClockTampered)]
    [InlineData("expired", ErrorCode.Expired)]
    [InlineData("clock-guard", ErrorCode.ClockTampered)]
    public void VerifyOnPremises_StepBroken_IsRejected(string kind, ErrorCode expected)
    {
        ServerIdentity server = Server();
        ECDsa signer = _server;
        using ECDsa other = KeyFiles.CreateKey();
        IClockGuard guard = StatelessClockGuard.Instance;
        Func<LeaseSpec, LeaseSpec> change = kind switch
        {
            "other-server-license" => s => s with { ServerLicenseTokenId = "another" },
            "product" => s => s with { ProductId = "acme-cam" },
            "installation" => s => s with { InstallationKeyThumbprint = "another" },
            "license" => s => s with { LicenseId = "lic-other" },
            "feature" => s => s with { FeatureId = "basic" },
            "seats" => s => s with { Seats = 6 },
            "duration" => s => s with { Duration = TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1) },
            "borrow" => s => s with { IsBorrow = true, Duration = TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1) },
            "outlives" => s => s with { IsBorrow = true, Duration = TimeSpan.FromDays(7) },
            _ => s => s,
        };
        DateTimeOffset issueAt = Now;
        DateTimeOffset verifyAt = Now;
        switch (kind)
        {
            case "other-signer":
                signer = other;
                break;
            case "outlives":
                issueAt = Now.AddDays(31);
                verifyAt = issueAt;
                break;
            case "future":
                issueAt = Now.AddMinutes(6);
                break;
            case "expired":
                verifyAt = Now.AddMinutes(10).AddSeconds(1);
                break;
            case "clock-guard":
                guard = new RejectingGuard();
                break;
            default:
                break;
        }

        // The lease is issued on its own clock, because a fake clock cannot go back to verify before the issue time.
        string lease = LeaseIssuer.Issue(signer, change(Spec(server)), new FakeTimeProvider(issueAt));
        _time.SetUtcNow(verifyAt);

        var invalid = Assert.IsType<LeaseVerificationResult.Invalid>(Verifier(guard).VerifyOnPremises(lease, server, Expect()));
        Assert.Equal(expected, invalid.Error.Code);
    }

    /// <summary>
    /// A lease whose header names the server's key but whose signature comes from another key is refused.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A valid lease from the server; another key.
    ///
    /// Steps:
    /// - Replace the signature with the other key's signature over the same header and payload; verify.
    ///
    /// Expected:
    /// - ELM_BAD_SIGNATURE.
    /// </remarks>
    [Fact]
    public void VerifyOnPremises_ForgedSignatureUnderServerKid_IsRejected()
    {
        ServerIdentity server = Server();
        string lease = Lease(server);
        int last = lease.LastIndexOf('.');
        using ECDsa other = KeyFiles.CreateKey();
        byte[] forged = other.SignData(
            Encoding.ASCII.GetBytes(lease[..last]),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );

        string token = lease[..last] + "." + System.Buffers.Text.Base64Url.EncodeToString(forged);

        Assert.Equal(ErrorCode.BadSignature, Invalid(Verifier().VerifyOnPremises(token, server, Expect())).Code);
    }

    /// <summary>
    /// Lease issuance refuses specs the verifier would reject: zero seats, a negative sequence, a duration under one
    /// second, and an empty ID.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A valid lease spec.
    ///
    /// Steps:
    /// - Issue with each change.
    ///
    /// Expected:
    /// - ArgumentException each time.
    /// </remarks>
    [Fact]
    public void LeaseIssuer_InvalidSpec_Throws()
    {
        LeaseSpec spec = Spec(Server());

        Assert.ThrowsAny<ArgumentException>(() => LeaseIssuer.Issue(_server, spec with { Seats = 0 }, _time));
        Assert.ThrowsAny<ArgumentException>(() => LeaseIssuer.Issue(_server, spec with { Sequence = -1 }, _time));
        Assert.ThrowsAny<ArgumentException>(() => LeaseIssuer.Issue(_server, spec with { Duration = TimeSpan.FromMilliseconds(500) }, _time));
        Assert.ThrowsAny<ArgumentException>(() => LeaseIssuer.Issue(_server, spec with { LeaseId = string.Empty }, _time));
        Assert.NotEmpty(LeaseIssuer.Issue(_server, spec with { Seats = 1, Sequence = 0, Duration = TimeSpan.FromSeconds(1) }, _time));
    }

    /// <summary>
    /// A server identity needs a server license and the key named by its cnf.jkt: a trial license cannot act as a server
    /// (T18), another key is a pin mismatch, and a broken JWK is malformed.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A verified server license and a verified trial license bound to the server key.
    ///
    /// Steps:
    /// - Create identities from: the trial license; the server license with another key's JWK; with "{}"; with a 2 KiB text.
    ///
    /// Expected:
    /// - ELM_WRONG_KIND, ELM_SERVER_PIN_MISMATCH, ELM_MALFORMED, ELM_MALFORMED.
    /// </remarks>
    [Fact]
    public void ServerIdentity_WrongLicenseOrKey_IsRefused()
    {
        VerifiedLicense serverLicense = Server().License;
        VerifiedLicense trial = VerifiedLicenseOf(LicenseKind.Trial);
        using ECDsa other = KeyFiles.CreateKey();

        Assert.False(ServerIdentity.TryCreate(trial, Jwk(_server), out _, out LicenseError? kind));
        Assert.False(ServerIdentity.TryCreate(serverLicense, Jwk(other), out _, out LicenseError? pin));
        Assert.False(ServerIdentity.TryCreate(serverLicense, "{}"u8, out _, out LicenseError? empty));
        Assert.False(ServerIdentity.TryCreate(serverLicense, Encoding.UTF8.GetBytes(new string(' ', 2048)), out _, out LicenseError? large));

        Assert.Equal(ErrorCode.WrongKind, kind!.Code);
        Assert.Equal(ErrorCode.ServerPinMismatch, pin!.Code);
        Assert.Equal(ErrorCode.Malformed, empty!.Code);
        Assert.Equal(ErrorCode.Malformed, large!.Code);
    }

    /// <summary>
    /// A hosted lease is checked with a certificate for lease from a trusted root; a certificate for license only, another
    /// root, and another product are refused.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The signing key certified for lease by the root.
    ///
    /// Steps:
    /// - Verify a hosted lease with: the lease certificate; a license-only certificate; another root's trust store; a
    ///   lease for another product.
    ///
    /// Expected:
    /// - Valid; ELM_UNTRUSTED_KEY; ELM_UNTRUSTED_KEY; ELM_WRONG_PRODUCT.
    /// </remarks>
    [Fact]
    public void VerifyHosted_Certificates_AreChecked()
    {
        string leaseCert = CertificateIssuer.Issue(_root, _signing, KeyUses.Lease, Now.AddDays(-1), TimeSpan.FromDays(30), _time);
        string licenseCert = CertificateIssuer.Issue(_root, _signing, KeyUses.License, Now.AddDays(-1), TimeSpan.FromDays(30), _time);
        TrustStore trust = TrustStore.Create([KeyFiles.ExportPublic(_root)], []);
        using ECDsa otherRoot = KeyFiles.CreateKey();
        TrustStore otherTrust = TrustStore.Create([KeyFiles.ExportPublic(otherRoot)], []);
        LeaseSpec spec = new()
        {
            LeaseId = "lease-h",
            ProductId = "acme-cad",
            LicenseId = "lic-h",
            FeatureId = "pro",
            Seats = 1,
            InstallationKeyThumbprint = ClientKid,
            Duration = TimeSpan.FromMinutes(10),
        };
        string lease = LeaseIssuer.Issue(_signing, spec, _time);

        Assert.IsType<LeaseVerificationResult.Valid>(Verifier(trust: trust).VerifyHosted(lease, [leaseCert], Expect()));
        Assert.Equal(ErrorCode.UntrustedKey, Invalid(Verifier(trust: trust).VerifyHosted(lease, [licenseCert], Expect())).Code);
        Assert.Equal(ErrorCode.UntrustedKey, Invalid(Verifier(trust: otherTrust).VerifyHosted(lease, [leaseCert], Expect())).Code);
        string otherProduct = LeaseIssuer.Issue(_signing, spec with { ProductId = "acme-cam" }, _time);
        Assert.Equal(ErrorCode.WrongProduct, Invalid(Verifier(trust: trust).VerifyHosted(otherProduct, [leaseCert], Expect())).Code);
    }

    /// <summary>
    /// One verifier keeps a hosted lease's certificate after the first check, so later checks with the same certificate do
    /// not verify it or create its key again (review of PR #16).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A lease certificate from the trusted root; one lease verifier.
    ///
    /// Steps:
    /// - Verify three hosted leases that carry the same certificate.
    ///
    /// Expected:
    /// - All valid; the verifier keeps exactly one certificate.
    /// </remarks>
    [Fact]
    public void VerifyHosted_SameCertificate_IsKeptOnce()
    {
        string leaseCert = CertificateIssuer.Issue(_root, _signing, KeyUses.Lease, Now.AddDays(-1), TimeSpan.FromDays(30), _time);
        LeaseVerifier verifier = Verifier();
        LeaseSpec spec = Spec(Server()) with { ServerLicenseTokenId = null };

        for (int i = 0; i < 3; i++)
        {
            string lease = LeaseIssuer.Issue(_signing, spec with { LeaseId = "lease-" + i }, _time);
            Assert.IsType<LeaseVerificationResult.Valid>(verifier.VerifyHosted(lease, [leaseCert], Expect()));
        }

        Assert.Equal(1, verifier.CachedCertificateCount);
    }

    /// <summary>
    /// Malformed lease payloads, correctly signed, are refused without an exception: null members, zero seats, an expiry
    /// not after the issue time, a server ID other than the signer, and an empty token.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Lease payloads written by hand and signed with the server key.
    ///
    /// Steps:
    /// - Verify each.
    ///
    /// Expected:
    /// - ELM_MALFORMED each time.
    /// </remarks>
    [Theory]
    [InlineData("lse-null")]
    [InlineData("cnf-null")]
    [InlineData("n-zero")]
    [InlineData("exp-not-after-iat")]
    [InlineData("srv-other")]
    [InlineData("empty")]
    public void VerifyOnPremises_MalformedPayload_IsMalformed(string kind)
    {
        ServerIdentity server = Server();
        string kid = KeyFiles.KeyId(_server);
        long now = Now.ToUnixTimeSeconds();
        var payload = new JsonObject
        {
            ["ver"] = 1,
            ["iss"] = kid,
            ["iat"] = now,
            ["exp"] = now + 600,
            ["jti"] = "j",
            ["lse"] = "lease-1",
            ["prd"] = "acme-cad",
            ["lic"] = "lic-srv",
            ["feat"] = "pro",
            ["n"] = 1,
            ["cnf"] = new JsonObject { ["jkt"] = ClientKid },
            ["brw"] = false,
            ["srv"] = kid,
            ["slic"] = server.License.TokenId,
            ["seq"] = 0,
        };
        switch (kind)
        {
            case "lse-null":
                payload["lse"] = null;
                break;
            case "cnf-null":
                payload["cnf"] = null;
                break;
            case "n-zero":
                payload["n"] = 0;
                break;
            case "exp-not-after-iat":
                payload["exp"] = now;
                break;
            case "srv-other":
                payload["srv"] = "other";
                break;
            default:
                break;
        }

        string token = kind == "empty" ? string.Empty : SignJws(_server, kid, TokenTypes.Lease, payload.ToJsonString());

        Assert.Equal(ErrorCode.Malformed, Invalid(Verifier().VerifyOnPremises(token, server, Expect())).Code);
    }

    private static LicenseError Invalid(LeaseVerificationResult result) => Assert.IsType<LeaseVerificationResult.Invalid>(result).Error;

    private static byte[] Jwk(ECDsa key)
    {
        ECParameters p = key.ExportParameters(false);
        return Encoding.UTF8.GetBytes(
            $"{{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"{System.Buffers.Text.Base64Url.EncodeToString(p.Q.X)}\",\"y\":\"{System.Buffers.Text.Base64Url.EncodeToString(p.Q.Y)}\"}}"
        );
    }

    private static string SignJws(ECDsa key, string kid, string typ, string payload)
    {
        string input =
            System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{{\"alg\":\"ES256\",\"kid\":\"{kid}\",\"typ\":\"{typ}\"}}"))
            + "."
            + System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
        byte[] signature = key.SignData(
            Encoding.ASCII.GetBytes(input),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );
        return input + "." + System.Buffers.Text.Base64Url.EncodeToString(signature);
    }

    /// <summary>
    /// A client verifies a server's license with the key the server sent (design §13.5 step 2): the server machine's
    /// fingerprints are not compared, the key must be the license's cnf.jkt, and the key must be an EC P-256 JWK.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server license bound to the server key and to a fingerprint of the server machine (matching any).
    ///
    /// Steps:
    /// - TryVerify with the server key; with the client key; with a JWK that is not a key; with a product the license is not
    ///   for.
    ///
    /// Expected:
    /// - An identity whose key ID is the server key's; ELM_SERVER_PIN_MISMATCH; ELM_MALFORMED; ELM_WRONG_PRODUCT.
    /// </remarks>
    [Fact]
    public void TryVerify_ServerLicense_ChecksTheKeyButNotTheFingerprints()
    {
        var spec = new LicenseSpec
        {
            LicenseId = "lic-srv",
            ProductId = "acme-cad",
            Kind = LicenseKind.Server,
            Features = [new FeatureSpec("pro", Seats: 5)],
            ExpiresAt = Now.AddDays(30),
            GracePeriod = TimeSpan.FromDays(7),
            InstallationKeyThumbprint = KeyFiles.KeyId(_server),
            Fingerprints = ["2b9b86465b5d4fbda9c050845613fa89"],
            LeaseDuration = TimeSpan.FromMinutes(10),
            BorrowLimit = TimeSpan.Zero,
            OveragePercent = 0,
            CloneStrategy = CloneStrategy.Deny,
        };
        string cert = CertificateIssuer.Issue(_root, _signing, KeyUses.License, Now.AddDays(-1), TimeSpan.FromDays(365), _time);
        byte[] file = LicenseIssuer.Issue(_signing, [cert], spec, _time).File;
        var verifier = new LicenseVerifier(TrustStore.Create([KeyFiles.ExportPublic(_root)], []), _time, StatelessClockGuard.Instance);

        Assert.True(ServerIdentity.TryVerify(verifier, file, Jwk(_server), "acme-cad", out ServerIdentity? identity, out _));
        using (identity)
        {
            Assert.Equal(KeyFiles.KeyId(_server), identity!.KeyId);
        }

        Assert.False(ServerIdentity.TryVerify(verifier, file, Jwk(_client), "acme-cad", out _, out LicenseError? other));
        Assert.Equal(ErrorCode.ServerPinMismatch, other!.Code);
        Assert.False(ServerIdentity.TryVerify(verifier, file, "{\"kty\":\"EC\"}"u8, "acme-cad", out _, out LicenseError? malformed));
        Assert.Equal(ErrorCode.Malformed, malformed!.Code);
        Assert.False(ServerIdentity.TryVerify(verifier, file, Jwk(_server), "acme-cam", out _, out LicenseError? product));
        Assert.Equal(ErrorCode.WrongProduct, product!.Code);
    }

    private LeaseVerifier Verifier(IClockGuard? guard = null, TrustStore? trust = null) =>
        new(trust ?? TrustStore.Create([KeyFiles.ExportPublic(_root)], []), _time, guard ?? StatelessClockGuard.Instance);

    private LeaseExpectation Expect() => new("acme-cad", ClientKid);

    private ServerIdentity Server()
    {
        Assert.True(
            ServerIdentity.TryCreate(VerifiedLicenseOf(LicenseKind.Server), Jwk(_server), out ServerIdentity? server, out LicenseError? error),
            error?.Message
        );
        return server!;
    }

    private VerifiedLicense VerifiedLicenseOf(LicenseKind kind)
    {
        bool isServer = kind == LicenseKind.Server;
        var spec = new LicenseSpec
        {
            LicenseId = "lic-srv",
            ProductId = "acme-cad",
            Kind = kind,
            Features = isServer ? [new FeatureSpec("pro", Seats: 5), new FeatureSpec("basic", Seats: 5)] : [new FeatureSpec("pro")],
            ExpiresAt = Now.AddDays(30),
            GracePeriod = isServer ? TimeSpan.FromDays(7) : TimeSpan.Zero,
            InstallationKeyThumbprint = KeyFiles.KeyId(_server),
            LeaseDuration = isServer ? TimeSpan.FromMinutes(10) : null,
            BorrowLimit = isServer ? TimeSpan.FromDays(7) : null,
            OveragePercent = isServer ? 0 : null,
            CloneStrategy = isServer ? CloneStrategy.Flag : null,
        };
        spec = isServer ? spec with { Features = [new FeatureSpec("pro", Seats: 5)] } : spec;
        string cert = CertificateIssuer.Issue(_root, _signing, KeyUses.License, Now.AddDays(-1), TimeSpan.FromDays(365), _time);
        IssuedLicense issued = LicenseIssuer.Issue(_signing, [cert], spec, _time);
        var verifier = new LicenseVerifier(TrustStore.Create([KeyFiles.ExportPublic(_root)], []), _time, StatelessClockGuard.Instance);
        var requirement = new LicenseRequirement("acme-cad", kind, null, null, new InstallationIdentity(KeyFiles.KeyId(_server), []));
        return Assert.IsType<LicenseVerificationResult.Valid>(verifier.Verify(issued.File, requirement)).License;
    }

    private LeaseSpec Spec(ServerIdentity server) =>
        new()
        {
            LeaseId = "lease-1",
            ProductId = "acme-cad",
            LicenseId = "lic-srv",
            FeatureId = "pro",
            Seats = 2,
            InstallationKeyThumbprint = ClientKid,
            Duration = TimeSpan.FromMinutes(10),
            ServerLicenseTokenId = server.License.TokenId,
        };

    private string Lease(ServerIdentity server, Func<LeaseSpec, LeaseSpec>? change = null) =>
        LeaseIssuer.Issue(_server, (change ?? (s => s))(Spec(server)), _time);

    private sealed class RejectingGuard : IClockGuard
    {
        public bool Observe(DateTimeOffset now, DateTimeOffset tokenIssuedAt, TimeSpan allowedSkew) => false;
    }
}
