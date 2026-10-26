using System.Text;
using System.Text.Json.Nodes;

namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Tests of <see cref="LicenseVerifier"/> against design §8.2, §8.6, and the steps of §11.2.
/// </summary>
public sealed class LicenseVerifierTests : IDisposable
{
    private readonly TestIssuer _issuer = new();

    public void Dispose() => _issuer.Dispose();

    /// <summary>
    /// A license that meets every requirement verifies.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license file with a certificate from the trusted root, bound to the installation, with feature pro >=2.0 &lt;3.0.
    ///
    /// Steps:
    /// - Verify with feature pro at version 2.5 at a time inside the token's validity.
    ///
    /// Expected:
    /// - Valid, not in the grace period, with the claims of the token.
    /// </remarks>
    [Fact]
    public void Verify_ValidLicense_ReturnsValidWithClaims()
    {
        LicenseVerificationResult result = _issuer.Verifier().Verify(_issuer.File(), _issuer.Requirement());

        var valid = Assert.IsType<LicenseVerificationResult.Valid>(result);
        Assert.False(valid.InGracePeriod);
        Assert.Equal("lic-1", valid.License.LicenseId);
        Assert.Equal("acme-cad", valid.License.ProductId);
        Assert.Equal(LicenseKind.Standard, valid.License.Kind);
        Assert.Equal(_issuer.InstallationKid, valid.License.InstallationKeyThumbprint);
        Assert.Equal(_issuer.SigningKid, valid.License.SigningKeyId);
        Assert.Equal(["pro", "basic"], valid.License.Features.Select(f => f.Id));
        Assert.Equal(1, valid.License.Sequence);
    }

    /// <summary>
    /// Step 1 rejects a file larger than 64 KiB, malformed JSON, the wrong format or version, and a certificate count outside 1..4.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A valid license token and certificate.
    ///
    /// Steps:
    /// - Verify each malformed file: 64 KiB + 1 bytes, not JSON, wrong format, version 2, zero certificates, five certificates.
    ///
    /// Expected:
    /// - ELM_MALFORMED for each.
    /// </remarks>
    [Theory]
    [InlineData("oversize")]
    [InlineData("not-json")]
    [InlineData("wrong-format")]
    [InlineData("wrong-version")]
    [InlineData("no-certificates")]
    [InlineData("five-certificates")]
    public void Verify_MalformedFile_ReturnsMalformed(string kind)
    {
        string license = _issuer.License();
        string cert = _issuer.Certificate();
        byte[] file = kind switch
        {
            "oversize" => new byte[(64 * 1024) + 1],
            "not-json" => Encoding.UTF8.GetBytes("{not json"),
            "wrong-format" => Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(TestIssuer.File(license, cert)).Replace("elasticlm-license", "other", StringComparison.Ordinal)
            ),
            "wrong-version" => Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(TestIssuer.File(license, cert)).Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal)
            ),
            "no-certificates" => TestIssuer.File(license),
            _ => TestIssuer.File(license, cert, cert, cert, cert, cert),
        };

        AssertError(ErrorCode.Malformed, _issuer.Verifier().Verify(file, _issuer.Requirement()));
    }

    /// <summary>
    /// Four certificates, the limit, are accepted.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A valid license and its certificate.
    ///
    /// Steps:
    /// - Verify a file with the certificate four times.
    ///
    /// Expected:
    /// - Valid.
    /// </remarks>
    [Fact]
    public void Verify_FourCertificates_ReturnsValid()
    {
        string cert = _issuer.Certificate();
        byte[] file = TestIssuer.File(_issuer.License(), cert, cert, cert, cert);

        Assert.IsType<LicenseVerificationResult.Valid>(_issuer.Verifier().Verify(file, _issuer.Requirement()));
    }

    /// <summary>
    /// A token longer than 16 KiB is rejected; one at the limit is not rejected for its size.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - License payloads padded with metadata so the signed token is just under and just over 16 KiB.
    ///
    /// Steps:
    /// - Verify both, with a requirement that ignores metadata limits only by size (the padding stays inside the meta limits).
    ///
    /// Expected:
    /// - Over the limit: ELM_MALFORMED. Under: Valid.
    /// </remarks>
    [Fact]
    public void Verify_TokenLength_RejectsOverLimitOnly()
    {
        // 16 meta values of up to 1024 bytes (the meta limit) make a token longer than 16 KiB; shrink until it fits.
        string Build(int valueLength)
        {
            JsonObject payload = _issuer.LicensePayload();
            var meta = new JsonObject();
            for (int i = 0; i < 16; i++)
            {
                meta["k" + i] = new string('a', valueLength);
            }

            payload["meta"] = meta;
            return _issuer.License(payload);
        }

        int length = 1024;
        while (Build(length).Length > 16 * 1024)
        {
            length--;
        }

        string under = Build(length);
        string over = Build(length + 1);
        Assert.True(under.Length <= 16 * 1024);
        Assert.True(over.Length > 16 * 1024);

        Assert.IsType<LicenseVerificationResult.Valid>(
            _issuer.Verifier().Verify(TestIssuer.File(under, _issuer.Certificate()), _issuer.Requirement())
        );
        AssertError(ErrorCode.Malformed, _issuer.Verifier().Verify(TestIssuer.File(over, _issuer.Certificate()), _issuer.Requirement()));
    }

    /// <summary>
    /// The JWS rules of design §8.2 reject other serializations, header members, algorithms, types, and signature forms.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license token re-signed with the trusted signing key, so only the named property differs from a valid token.
    ///
    /// Steps:
    /// - Verify a token with: four segments; padded base64url; an extra crit, jwk, or x5c header member; a duplicated alg;
    ///   alg none or HS256; typ elm-lease+jws; a DER signature.
    ///
    /// Expected:
    /// - ELM_MALFORMED for the shapes and types; ELM_BAD_SIGNATURE for the algorithms and the DER signature.
    /// </remarks>
    [Theory]
    [InlineData("four-segments", ErrorCode.Malformed)]
    [InlineData("padded", ErrorCode.Malformed)]
    [InlineData("crit", ErrorCode.Malformed)]
    [InlineData("jwk", ErrorCode.Malformed)]
    [InlineData("x5c", ErrorCode.Malformed)]
    [InlineData("duplicate-alg", ErrorCode.Malformed)]
    [InlineData("alg-none", ErrorCode.BadSignature)]
    [InlineData("alg-hs256", ErrorCode.BadSignature)]
    [InlineData("typ-lease", ErrorCode.Malformed)]
    [InlineData("der-signature", ErrorCode.BadSignature)]
    public void Verify_JwsRuleBroken_IsRejected(string kind, ErrorCode expected)
    {
        string payload = _issuer.LicensePayload().ToJsonString();
        string kid = _issuer.SigningKid;
        string token = kind switch
        {
            "four-segments" => _issuer.License() + ".AAAA",
            "padded" => PadFirstSegment(_issuer.License()),
            "crit" => TestIssuer.Sign(
                _issuer.Signing,
                $"{{\"alg\":\"ES256\",\"kid\":\"{kid}\",\"typ\":\"elm-license+jws\",\"crit\":[\"x\"]}}",
                payload
            ),
            "jwk" => TestIssuer.Sign(
                _issuer.Signing,
                $"{{\"alg\":\"ES256\",\"kid\":\"{kid}\",\"typ\":\"elm-license+jws\",\"jwk\":{TestIssuer.Jwk(_issuer.Signing).ToJsonString()}}}",
                payload
            ),
            "x5c" => TestIssuer.Sign(
                _issuer.Signing,
                $"{{\"alg\":\"ES256\",\"kid\":\"{kid}\",\"typ\":\"elm-license+jws\",\"x5c\":[\"AA\"]}}",
                payload
            ),
            "duplicate-alg" => TestIssuer.Sign(
                _issuer.Signing,
                $"{{\"alg\":\"ES256\",\"alg\":\"none\",\"kid\":\"{kid}\",\"typ\":\"elm-license+jws\"}}",
                payload
            ),
            "alg-none" => TestIssuer.Sign(_issuer.Signing, $"{{\"alg\":\"none\",\"kid\":\"{kid}\",\"typ\":\"elm-license+jws\"}}", payload),
            "alg-hs256" => TestIssuer.Sign(_issuer.Signing, $"{{\"alg\":\"HS256\",\"kid\":\"{kid}\",\"typ\":\"elm-license+jws\"}}", payload),
            "typ-lease" => TestIssuer.Sign(_issuer.Signing, kid, TokenTypes.Lease, _issuer.LicensePayload()),
            _ => WithDerSignature(_issuer.License()),
        };

        AssertError(expected, _issuer.Verifier().Verify(TestIssuer.File(token, _issuer.Certificate()), _issuer.Requirement()));
    }

    /// <summary>
    /// Step 2 rejects a token whose signing key ID is revoked, even with a valid certificate.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A valid license file; a trust store that revokes the signing key ID.
    ///
    /// Steps:
    /// - Verify.
    ///
    /// Expected:
    /// - ELM_KEY_REVOKED.
    /// </remarks>
    [Fact]
    public void Verify_SigningKeyRevoked_ReturnsKeyRevoked()
    {
        LicenseVerifier verifier = _issuer.Verifier(trust: _issuer.TrustStore(_issuer.SigningKid));

        AssertError(ErrorCode.KeyRevoked, verifier.Verify(_issuer.File(), _issuer.Requirement()));
    }

    /// <summary>
    /// Step 3 finds no usable certificate when the root is untrusted or revoked, the subject differs, the use lacks license,
    /// or the certified key does not match the subject.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A valid license token; one certificate broken in the named way.
    ///
    /// Steps:
    /// - Verify the token with only that certificate.
    ///
    /// Expected:
    /// - ELM_UNTRUSTED_KEY for each.
    /// </remarks>
    [Theory]
    [InlineData("untrusted-root")]
    [InlineData("revoked-root")]
    [InlineData("wrong-sub")]
    [InlineData("use-lease-only")]
    [InlineData("jwk-not-sub")]
    [InlineData("bad-root-signature")]
    public void Verify_NoUsableCertificate_ReturnsUntrustedKey(string kind)
    {
        using var other = new TestIssuer();
        JsonObject certPayload = _issuer.CertificatePayload();
        string cert;
        TrustStore trust = _issuer.TrustStore();
        switch (kind)
        {
            case "untrusted-root":
                cert = TestIssuer.Sign(other.Root, other.RootKid, TokenTypes.KeyCertificate, certPayload);
                break;
            case "revoked-root":
                cert = _issuer.Certificate();
                trust = _issuer.TrustStore(_issuer.RootKid);
                break;
            case "wrong-sub":
                certPayload["sub"] = other.SigningKid;
                cert = _issuer.Certificate(certPayload);
                break;
            case "use-lease-only":
                certPayload["use"] = new JsonArray("lease");
                cert = _issuer.Certificate(certPayload);
                break;
            case "jwk-not-sub":
                certPayload["jwk"] = TestIssuer.Jwk(other.Signing);
                cert = _issuer.Certificate(certPayload);
                break;
            default:
                cert = TestIssuer.Sign(other.Root, _issuer.RootKid, TokenTypes.KeyCertificate, certPayload);
                break;
        }

        LicenseVerifier verifier = _issuer.Verifier(trust: trust);

        AssertError(ErrorCode.UntrustedKey, verifier.Verify(TestIssuer.File(_issuer.License(), cert), _issuer.Requirement()));
    }

    /// <summary>
    /// A valid certificate for another signing key does not certify the token's key: step 3 finds no certificate, before
    /// the signature is tried.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A token signed by the test signing key; a valid certificate from the trusted root for a different key.
    ///
    /// Steps:
    /// - Verify the token with only that certificate.
    ///
    /// Expected:
    /// - ELM_UNTRUSTED_KEY, not ELM_BAD_SIGNATURE.
    /// </remarks>
    [Fact]
    public void Verify_CertificateForAnotherKey_ReturnsUntrustedKey()
    {
        using var other = new TestIssuer();
        JsonObject certPayload = _issuer.CertificatePayload();
        certPayload["sub"] = other.SigningKid;
        certPayload["jwk"] = TestIssuer.Jwk(other.Signing);

        AssertError(
            ErrorCode.UntrustedKey,
            _issuer.Verifier().Verify(TestIssuer.File(_issuer.License(), _issuer.Certificate(certPayload)), _issuer.Requirement())
        );
    }

    /// <summary>
    /// A file of exactly 64 KiB passes the size check; one byte more is rejected for its size (design §8.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A valid license file padded with trailing spaces (still valid JSON) to 65,536 and 65,537 bytes.
    ///
    /// Steps:
    /// - Verify both.
    ///
    /// Expected:
    /// - 65,536 bytes: Valid. 65,537 bytes: ELM_MALFORMED.
    /// </remarks>
    [Fact]
    public void Verify_FileSizeBoundary_RejectsOverLimitOnly()
    {
        byte[] file = _issuer.File();
        byte[] Padded(int length) => [.. file, .. Enumerable.Repeat((byte)' ', length - file.Length)];

        Assert.IsType<LicenseVerificationResult.Valid>(_issuer.Verifier().Verify(Padded(64 * 1024), _issuer.Requirement()));
        AssertError(ErrorCode.Malformed, _issuer.Verifier().Verify(Padded((64 * 1024) + 1), _issuer.Requirement()));
    }

    /// <summary>
    /// A broken certificate kept beside a good one does not fail the file (design §11.2 step 3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A certificate signed by an untrusted root, placed before the good certificate.
    ///
    /// Steps:
    /// - Verify.
    ///
    /// Expected:
    /// - Valid.
    /// </remarks>
    [Fact]
    public void Verify_BrokenCertificateBesideGoodOne_ReturnsValid()
    {
        using var other = new TestIssuer();
        string broken = TestIssuer.Sign(other.Root, other.RootKid, TokenTypes.KeyCertificate, _issuer.CertificatePayload());
        byte[] file = TestIssuer.File(_issuer.License(), broken, _issuer.Certificate());

        Assert.IsType<LicenseVerificationResult.Valid>(_issuer.Verifier().Verify(file, _issuer.Requirement()));
    }

    /// <summary>
    /// Step 4: changing any single payload claim while keeping the signature fails the signature check.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A valid license token.
    ///
    /// Steps:
    /// - For each claim of the default payload, change only that claim, re-encode the payload, keep header and signature.
    ///
    /// Expected:
    /// - ELM_BAD_SIGNATURE for every claim.
    /// </remarks>
    [Theory]
    [InlineData("ver")]
    [InlineData("iss")]
    [InlineData("iat")]
    [InlineData("exp")]
    [InlineData("jti")]
    [InlineData("lic")]
    [InlineData("prd")]
    [InlineData("kind")]
    [InlineData("feat")]
    [InlineData("cnf")]
    [InlineData("fp")]
    [InlineData("fpm")]
    [InlineData("grace")]
    [InlineData("refresh")]
    [InlineData("seq")]
    public void Verify_OneClaimChanged_ReturnsBadSignature(string claim)
    {
        string token = _issuer.License();
        string[] parts = token.Split('.');
        JsonObject payload = _issuer.LicensePayload();
        payload[claim] = claim switch
        {
            "ver" or "iat" or "exp" or "grace" or "refresh" or "seq" => (JsonNode)(
                long.Parse(payload[claim]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) + 1
            ),
            "feat" => new JsonArray(new JsonObject { ["id"] = "pro" }),
            "cnf" => new JsonObject { ["jkt"] = "other" },
            "fp" => new JsonArray("00000000000000000000000000000000"),
            "kind" => "trial",
            "fpm" => "ignore",
            _ => payload[claim]!.GetValue<string>() + "x",
        };
        string tampered = parts[0] + "." + TestIssuer.B64(payload.ToJsonString()) + "." + parts[2];

        AssertError(ErrorCode.BadSignature, _issuer.Verifier().Verify(TestIssuer.File(tampered, _issuer.Certificate()), _issuer.Requirement()));
    }

    /// <summary>
    /// Step 4: the signature of another valid token does not verify this token.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two valid license tokens with different license IDs.
    ///
    /// Steps:
    /// - Replace the first token's signature with the second's.
    ///
    /// Expected:
    /// - ELM_BAD_SIGNATURE.
    /// </remarks>
    [Fact]
    public void Verify_SignatureFromAnotherToken_ReturnsBadSignature()
    {
        JsonObject second = _issuer.LicensePayload();
        second["lic"] = "lic-2";
        string[] a = _issuer.License().Split('.');
        string[] b = _issuer.License(second).Split('.');

        AssertError(
            ErrorCode.BadSignature,
            _issuer.Verifier().Verify(TestIssuer.File($"{a[0]}.{a[1]}.{b[2]}", _issuer.Certificate()), _issuer.Requirement())
        );
    }

    /// <summary>
    /// Step 5: the token's iat must lie inside the certificate's nbf..exp, with the clock skew, even after the certificate expired.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Certificates whose validity ends or starts around the token's iat; the skew is 300 s.
    ///
    /// Steps:
    /// - Verify with iat = exp + 300 (inside), exp + 301 (outside), nbf - 300 (inside), nbf - 301 (outside); and a token
    ///   issued inside a certificate that has expired by the verification time.
    ///
    /// Expected:
    /// - Inside: Valid. Outside: ELM_CERTIFICATE_EXPIRED. Expired certificate with iat inside: Valid.
    /// </remarks>
    [Theory]
    [InlineData("exp", 300, true)]
    [InlineData("exp", 301, false)]
    [InlineData("nbf", -300, true)]
    [InlineData("nbf", -301, false)]
    public void Verify_IssuedAtAgainstCertificatePeriod_HonorsBoundsWithSkew(string bound, long offset, bool valid)
    {
        long iat = TestIssuer.NowSeconds - 86400;
        JsonObject cert = _issuer.CertificatePayload();
        cert[bound] = iat - offset;
        if (bound == "nbf")
        {
            cert["exp"] = TestIssuer.NowSeconds + 86400;
        }
        else
        {
            cert["nbf"] = iat - 86400;
        }

        JsonObject license = _issuer.LicensePayload();
        license["iat"] = iat;
        LicenseVerificationResult result = _issuer
            .Verifier()
            .Verify(TestIssuer.File(_issuer.License(license), _issuer.Certificate(cert)), _issuer.Requirement());

        if (valid)
        {
            Assert.IsType<LicenseVerificationResult.Valid>(result);
        }
        else
        {
            AssertError(ErrorCode.CertificateExpired, result);
        }
    }

    /// <summary>
    /// Step 6: a token issued more than the skew in the future is rejected; one exactly at the skew is accepted.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The skew is 300 s.
    ///
    /// Steps:
    /// - Verify tokens with iat = now + 300 and now + 301.
    ///
    /// Expected:
    /// - now + 300: Valid. now + 301: ELM_CLOCK_TAMPERED.
    /// </remarks>
    [Theory]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public void Verify_IssuedInFuture_RejectsBeyondSkew(long offset, bool valid)
    {
        JsonObject license = _issuer.LicensePayload();
        license["iat"] = TestIssuer.NowSeconds + offset;
        LicenseVerificationResult result = _issuer.Verifier().Verify(_issuer.File(license), _issuer.Requirement());

        if (valid)
        {
            Assert.IsType<LicenseVerificationResult.Valid>(result);
        }
        else
        {
            AssertError(ErrorCode.ClockTampered, result);
        }
    }

    /// <summary>
    /// Step 7: a clock guard that reports a set-back clock fails verification, and it receives the token's iat.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A clock guard that records its arguments and returns false.
    ///
    /// Steps:
    /// - Verify a valid file.
    ///
    /// Expected:
    /// - ELM_CLOCK_TAMPERED; the guard saw the token's iat and the verifier's time.
    /// </remarks>
    [Fact]
    public void Verify_ClockGuardRejects_ReturnsClockTampered()
    {
        var guard = new RecordingGuard(accept: false);

        AssertError(ErrorCode.ClockTampered, _issuer.Verifier(guard).Verify(_issuer.File(), _issuer.Requirement()));
        Assert.Equal(TestIssuer.NowSeconds - 3600, guard.IssuedAt?.ToUnixTimeSeconds());
        Assert.Equal(TestIssuer.Now, guard.Now);
    }

    /// <summary>
    /// Step 8: the kind must match; a trial passes a standard requirement unless the option is off; a server license passes
    /// only a server requirement.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Licenses of each kind (server licenses carry seats, lease, borrow, overage, cnf).
    ///
    /// Steps:
    /// - Verify each kind against each required kind, with AcceptTrialAsStandard on and off.
    ///
    /// Expected:
    /// - Valid or ELM_WRONG_KIND as listed in the cases.
    /// </remarks>
    [Theory]
    [InlineData("trial", LicenseKind.Standard, true, true)]
    [InlineData("trial", LicenseKind.Standard, false, false)]
    [InlineData("standard", LicenseKind.Trial, true, false)]
    [InlineData("server", LicenseKind.Standard, true, false)]
    [InlineData("standard", LicenseKind.Server, true, false)]
    [InlineData("server", LicenseKind.Server, true, true)]
    public void Verify_Kind_MatchesRequirement(string kind, LicenseKind required, bool acceptTrial, bool valid)
    {
        JsonObject license = _issuer.LicensePayload();
        license["kind"] = kind;
        if (kind == "trial")
        {
            license["grace"] = 0;
        }

        if (kind == "server")
        {
            license["feat"] = new JsonArray(new JsonObject { ["id"] = "pro", ["seats"] = 5 });
            license["lease"] = 600;
            license["borrow"] = 0;
            license["overage"] = 0;
        }

        LicenseRequirement requirement = _issuer.Requirement(version: null) with { Kind = required };
        LicenseVerifier verifier = _issuer.Verifier(options: new LicenseVerifierOptions { AcceptTrialAsStandard = acceptTrial });
        LicenseVerificationResult result = verifier.Verify(_issuer.File(license), requirement);

        if (valid)
        {
            Assert.IsType<LicenseVerificationResult.Valid>(result);
        }
        else
        {
            AssertError(ErrorCode.WrongKind, result);
        }
    }

    /// <summary>
    /// Step 9 rejects a license for another product.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A valid license for acme-cad.
    ///
    /// Steps:
    /// - Verify with product acme-cam.
    ///
    /// Expected:
    /// - ELM_WRONG_PRODUCT.
    /// </remarks>
    [Fact]
    public void Verify_OtherProduct_ReturnsWrongProduct()
    {
        AssertError(ErrorCode.WrongProduct, _issuer.Verifier().Verify(_issuer.File(), _issuer.Requirement() with { ProductId = "acme-cam" }));
    }

    /// <summary>
    /// Step 10 checks the installation key thumbprint and the fingerprints with the any, all, and ignore strategies.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license bound to the test installation key with fingerprints [A] or [A, B] and the named strategy.
    ///
    /// Steps:
    /// - Verify with no installation, another key, no fingerprints, and fingerprint sets that match some or none.
    ///
    /// Expected:
    /// - The error or Valid listed in each case.
    /// </remarks>
    [Theory]
    [InlineData("no-installation", "any", ErrorCode.WrongInstallation)]
    [InlineData("other-key", "any", ErrorCode.WrongInstallation)]
    [InlineData("no-fingerprints", "any", ErrorCode.FingerprintUnavailable)]
    [InlineData("no-fingerprints", "ignore", ErrorCode.Ok)]
    [InlineData("match-a", "any", ErrorCode.Ok)]
    [InlineData("match-a", "all", ErrorCode.FingerprintMismatch)]
    [InlineData("match-ab", "all", ErrorCode.Ok)]
    [InlineData("match-none", "any", ErrorCode.FingerprintMismatch)]
    [InlineData("match-none", "ignore", ErrorCode.Ok)]
    public void Verify_Binding_ChecksKeyAndFingerprints(string installation, string strategy, ErrorCode expected)
    {
        const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        JsonObject license = _issuer.LicensePayload();
        license["fp"] = new JsonArray(A, B);
        license["fpm"] = strategy;
        InstallationIdentity? identity = installation switch
        {
            "no-installation" => null,
            "other-key" => new InstallationIdentity("other", [A, B]),
            "no-fingerprints" => new InstallationIdentity(_issuer.InstallationKid, []),
            "match-a" => new InstallationIdentity(_issuer.InstallationKid, [A]),
            "match-ab" => new InstallationIdentity(_issuer.InstallationKid, [B, A]),
            _ => new InstallationIdentity(_issuer.InstallationKid, ["cccccccccccccccccccccccccccccccc"]),
        };

        LicenseVerificationResult result = _issuer.Verifier().Verify(_issuer.File(license), _issuer.Requirement() with { Installation = identity });

        if (expected == ErrorCode.Ok)
        {
            Assert.IsType<LicenseVerificationResult.Valid>(result);
        }
        else
        {
            AssertError(expected, result);
        }
    }

    /// <summary>
    /// An unbound license (no cnf, no fp) verifies without an installation.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license without cnf, fp, and fpm.
    ///
    /// Steps:
    /// - Verify with Installation = null.
    ///
    /// Expected:
    /// - Valid, with no installation key thumbprint.
    /// </remarks>
    [Fact]
    public void Verify_UnboundLicense_ReturnsValidWithoutInstallation()
    {
        JsonObject license = _issuer.LicensePayload();
        license.Remove("cnf");
        license.Remove("fp");
        license.Remove("fpm");

        var valid = Assert.IsType<LicenseVerificationResult.Valid>(
            _issuer.Verifier().Verify(_issuer.File(license), _issuer.Requirement() with { Installation = null })
        );
        Assert.Null(valid.License.InstallationKeyThumbprint);
    }

    /// <summary>
    /// Step 11 applies the expiry and the grace period at their boundaries, and a token without exp never expires.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A token with exp = now + offset and grace 7 days, or without exp.
    ///
    /// Steps:
    /// - Verify at exp, exp + 1, exp + grace, exp + grace + 1, and without exp.
    ///
    /// Expected:
    /// - At exp: Valid, not in grace. exp + 1 and exp + grace: Valid in grace. exp + grace + 1: ELM_EXPIRED. No exp: Valid.
    /// </remarks>
    [Theory]
    [InlineData(0L, "valid")]
    [InlineData(-1L, "grace")]
    [InlineData(-(86400L * 7), "grace")]
    [InlineData(-(86400L * 7) - 1, "expired")]
    [InlineData(null, "valid")]
    public void Verify_Expiry_HonorsGracePeriod(long? expOffset, string expected)
    {
        JsonObject license = _issuer.LicensePayload();
        if (expOffset is long offset)
        {
            license["exp"] = TestIssuer.NowSeconds + offset;
        }
        else
        {
            license.Remove("exp");
        }

        LicenseVerificationResult result = _issuer.Verifier().Verify(_issuer.File(license), _issuer.Requirement());

        if (expected == "expired")
        {
            AssertError(ErrorCode.Expired, result);
        }
        else
        {
            Assert.Equal(expected == "grace", Assert.IsType<LicenseVerificationResult.Valid>(result).InGracePeriod);
        }
    }

    /// <summary>
    /// Step 12 checks that the feature exists, its version range, and its own expiry.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Feature pro with range >=2.0 &lt;3.0; feature basic without range.
    ///
    /// Steps:
    /// - Verify for a missing feature, versions 1.9, 2.0, 2.9.9, 3.0, no version, basic at 9.0, and pro with exp in the past.
    ///
    /// Expected:
    /// - The error or Valid listed in each case.
    /// </remarks>
    [Theory]
    [InlineData("missing", "2.5", ErrorCode.FeatureNotLicensed)]
    [InlineData("pro", "1.9", ErrorCode.FeatureVersionNotLicensed)]
    [InlineData("pro", "2.0", ErrorCode.Ok)]
    [InlineData("pro", "2.9.9", ErrorCode.Ok)]
    [InlineData("pro", "3.0", ErrorCode.FeatureVersionNotLicensed)]
    [InlineData("pro", null, ErrorCode.Ok)]
    [InlineData("basic", "9.0", ErrorCode.Ok)]
    [InlineData("expired-pro", "2.5", ErrorCode.FeatureExpired)]
    public void Verify_Feature_ChecksPresenceVersionAndExpiry(string feature, string? version, ErrorCode expected)
    {
        JsonObject license = _issuer.LicensePayload();
        if (feature == "expired-pro")
        {
            license["feat"] = new JsonArray(new JsonObject { ["id"] = "pro", ["exp"] = TestIssuer.NowSeconds - 1 });
            feature = "pro";
        }

        LicenseVerificationResult result = _issuer.Verifier().Verify(_issuer.File(license), _issuer.Requirement(feature, version));

        if (expected == ErrorCode.Ok)
        {
            Assert.IsType<LicenseVerificationResult.Valid>(result);
        }
        else
        {
            AssertError(expected, result);
        }
    }

    /// <summary>
    /// When two adjacent steps fail, the first one is reported (design §11.2 order).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Tokens that fail two adjacent steps at once.
    ///
    /// Steps:
    /// - Verify: wrong kind and wrong product; wrong product and wrong installation; wrong installation and expired; expired
    ///   and missing feature.
    ///
    /// Expected:
    /// - The earlier step's error each time.
    /// </remarks>
    [Theory]
    [InlineData("kind+product", ErrorCode.WrongKind)]
    [InlineData("product+installation", ErrorCode.WrongProduct)]
    [InlineData("installation+expiry", ErrorCode.WrongInstallation)]
    [InlineData("expiry+feature", ErrorCode.Expired)]
    public void Verify_TwoStepsFail_ReportsFirst(string kind, ErrorCode expected)
    {
        JsonObject license = _issuer.LicensePayload();
        LicenseRequirement requirement = _issuer.Requirement();
        switch (kind)
        {
            case "kind+product":
                requirement = requirement with { Kind = LicenseKind.Trial, ProductId = "other" };
                break;
            case "product+installation":
                requirement = requirement with { ProductId = "other", Installation = null };
                break;
            case "installation+expiry":
                license["exp"] = TestIssuer.NowSeconds - (86400 * 30);
                requirement = requirement with { Installation = null };
                break;
            default:
                license["exp"] = TestIssuer.NowSeconds - (86400 * 30);
                requirement = requirement with { FeatureId = "missing" };
                break;
        }

        AssertError(expected, _issuer.Verifier().Verify(_issuer.File(license), requirement));
    }

    /// <summary>
    /// The payload limits of design §8.6 accept the limit and reject one more: features, fingerprints, metadata entries and sizes.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Payloads with 256 or 257 features, 8 or 9 fingerprints, 32 or 33 metadata entries, a 64- or 65-byte key, a 1024- or
    ///   1025-byte value.
    ///
    /// Steps:
    /// - Verify each.
    ///
    /// Expected:
    /// - At the limit: Valid. Over: ELM_MALFORMED.
    /// </remarks>
    [Theory]
    [InlineData("feat", 256, true)]
    [InlineData("feat", 257, false)]
    [InlineData("fp", 8, true)]
    [InlineData("fp", 9, false)]
    [InlineData("meta-count", 32, true)]
    [InlineData("meta-count", 33, false)]
    [InlineData("meta-key", 64, true)]
    [InlineData("meta-key", 65, false)]
    [InlineData("meta-value", 1024, true)]
    [InlineData("meta-value", 1025, false)]
    public void Verify_PayloadLimits_AcceptLimitRejectOver(string kind, int count, bool valid)
    {
        JsonObject license = _issuer.LicensePayload();
        switch (kind)
        {
            case "feat":
                var features = new JsonArray(new JsonObject { ["id"] = "pro" });
                for (int i = 1; i < count; i++)
                {
                    features.Add(new JsonObject { ["id"] = "f" + i });
                }

                license["feat"] = features;
                break;
            case "fp":
                var fps = new JsonArray("2b9b86465b5d4fbda9c050845613fa89");
                for (int i = 1; i < count; i++)
                {
                    fps.Add(i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture));
                }

                license["fp"] = fps;
                break;
            case "meta-count":
                var meta = new JsonObject();
                for (int i = 0; i < count; i++)
                {
                    meta["k" + i] = "v";
                }

                license["meta"] = meta;
                break;
            case "meta-key":
                license["meta"] = new JsonObject { [new string('k', count)] = "v" };
                break;
            default:
                license["meta"] = new JsonObject { ["k"] = new string('v', count) };
                break;
        }

        LicenseVerificationResult result = _issuer.Verifier().Verify(_issuer.File(license), _issuer.Requirement(version: null));

        if (valid)
        {
            Assert.IsType<LicenseVerificationResult.Valid>(result);
        }
        else
        {
            AssertError(ErrorCode.Malformed, result);
        }
    }

    /// <summary>
    /// Payload shapes the design forbids are malformed: a server license without its server claims, seats on a standard
    /// license, a trial with grace, an unknown kind, ver 2, fp without fpm, an issuer that differs from the key ID, a bad
    /// version range, and times or durations outside the limits of design §8.6.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Correctly signed payloads with the named defect.
    ///
    /// Steps:
    /// - Verify each.
    ///
    /// Expected:
    /// - ELM_MALFORMED for each.
    /// </remarks>
    [Theory]
    [InlineData("server-without-lease")]
    [InlineData("seats-on-standard")]
    [InlineData("trial-with-grace")]
    [InlineData("unknown-kind")]
    [InlineData("ver-2")]
    [InlineData("fp-without-fpm")]
    [InlineData("iss-mismatch")]
    [InlineData("bad-range")]
    [InlineData("iat-beyond-year-9999")]
    [InlineData("exp-negative")]
    [InlineData("grace-huge")]
    [InlineData("feature-exp-huge")]
    public void Verify_ForbiddenPayloadShape_ReturnsMalformed(string kind)
    {
        JsonObject license = _issuer.LicensePayload();
        switch (kind)
        {
            case "server-without-lease":
                license["kind"] = "server";
                license["feat"] = new JsonArray(new JsonObject { ["id"] = "pro", ["seats"] = 5 });
                break;
            case "seats-on-standard":
                license["feat"] = new JsonArray(new JsonObject { ["id"] = "pro", ["seats"] = 5 });
                break;
            case "trial-with-grace":
                license["kind"] = "trial";
                break;
            case "unknown-kind":
                license["kind"] = "enterprise";
                break;
            case "ver-2":
                license["ver"] = 2;
                break;
            case "fp-without-fpm":
                license.Remove("fpm");
                break;
            case "iss-mismatch":
                license["iss"] = "someone-else";
                break;
            case "iat-beyond-year-9999":
                license["iat"] = 253_402_300_800L;
                break;
            case "exp-negative":
                license["exp"] = -1;
                break;
            case "grace-huge":
                license["grace"] = long.MaxValue;
                break;
            case "feature-exp-huge":
                license["feat"] = new JsonArray(new JsonObject { ["id"] = "pro", ["exp"] = long.MaxValue });
                break;
            default:
                license["feat"] = new JsonArray(new JsonObject { ["id"] = "pro", ["ver"] = "~2" });
                break;
        }

        AssertError(ErrorCode.Malformed, _issuer.Verifier().Verify(_issuer.File(license), _issuer.Requirement(version: null)));
    }

    /// <summary>
    /// Reusing one verifier, with its certificate cache, gives the same results as a fresh verifier, also after the cache
    /// is cleared by more distinct certificates than it holds.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - One verifier; a valid file; a tampered token with the same certificate; 20 distinct valid certificates for the same key.
    ///
    /// Steps:
    /// - Verify valid, tampered, valid; then a file with each of the 20 certificates; then the first file again.
    ///
    /// Expected:
    /// - Valid, ELM_BAD_SIGNATURE, Valid; Valid for each of the 20; Valid at the end.
    /// </remarks>
    [Fact]
    public void Verify_ReusedVerifier_GivesSameResults()
    {
        LicenseVerifier verifier = _issuer.Verifier();
        string cert = _issuer.Certificate();
        string license = _issuer.License();
        string[] parts = license.Split('.');
        JsonObject changed = _issuer.LicensePayload();
        changed["lic"] = "lic-other";
        string tampered = parts[0] + "." + TestIssuer.B64(changed.ToJsonString()) + "." + parts[2];

        Assert.IsType<LicenseVerificationResult.Valid>(verifier.Verify(TestIssuer.File(license, cert), _issuer.Requirement()));
        AssertError(ErrorCode.BadSignature, verifier.Verify(TestIssuer.File(tampered, cert), _issuer.Requirement()));
        Assert.IsType<LicenseVerificationResult.Valid>(verifier.Verify(TestIssuer.File(license, cert), _issuer.Requirement()));

        for (int i = 0; i < 20; i++)
        {
            JsonObject certPayload = _issuer.CertificatePayload();
            certPayload["jti"] = "cert-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.IsType<LicenseVerificationResult.Valid>(
                verifier.Verify(TestIssuer.File(license, _issuer.Certificate(certPayload)), _issuer.Requirement())
            );
        }

        Assert.IsType<LicenseVerificationResult.Valid>(verifier.Verify(TestIssuer.File(license, cert), _issuer.Requirement()));
    }

    /// <summary>
    /// The verifier refuses a clock skew outside zero to one hour.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Options with skew -1 s or 1 h + 1 s.
    ///
    /// Steps:
    /// - Create a verifier.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException.
    /// </remarks>
    [Theory]
    [InlineData(-1)]
    [InlineData(3601)]
    public void Constructor_ClockSkewOutOfRange_Throws(int seconds)
    {
        var options = new LicenseVerifierOptions { ClockSkew = TimeSpan.FromSeconds(seconds) };

        Assert.Throws<ArgumentOutOfRangeException>(() => _issuer.Verifier(options: options));
    }

    private static void AssertError(ErrorCode expected, LicenseVerificationResult result)
    {
        var invalid = Assert.IsType<LicenseVerificationResult.Invalid>(result);
        Assert.Equal(expected, invalid.Error.Code);
    }

    private static string PadFirstSegment(string token)
    {
        int dot = token.IndexOf('.', StringComparison.Ordinal);
        return token[..dot] + "=" + token[dot..];
    }

    private string WithDerSignature(string token)
    {
        int last = token.LastIndexOf('.');
        byte[] der = _issuer.Signing.SignData(
            Encoding.ASCII.GetBytes(token[..last]),
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence
        );
        return token[..last] + "." + TestIssuer.B64(der);
    }

    private sealed class RecordingGuard(bool accept) : IClockGuard
    {
        public DateTimeOffset? Now { get; private set; }

        public DateTimeOffset? IssuedAt { get; private set; }

        public bool Observe(DateTimeOffset now, DateTimeOffset tokenIssuedAt, TimeSpan allowedSkew)
        {
            Now = now;
            IssuedAt = tokenIssuedAt;
            return accept;
        }
    }
}
