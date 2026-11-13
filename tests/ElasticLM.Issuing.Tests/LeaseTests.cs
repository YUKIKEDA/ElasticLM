using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ElasticLM.Licensing;
using ElasticLM.Licensing.Tests;
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

    /// <summary>Gets each path that checks a lease or its certificate with each form of a token without a valid signature.</summary>
    public static TheoryData<string, string> PathsAndSignatureForms =>
        SignatureForms.Cross("on-premises", "reclaim", "borrow-record", "hosted", "hosted-certificate");

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
    /// - Valid, with the lease's values, among them the ID of the answered proof, proof-1 (design §8.4.3, V1d5).
    /// </remarks>
    [Fact]
    public void VerifyOnPremises_IssuedLease_IsValid()
    {
        ServerIdentity server = Server();

        var valid = Assert.IsType<LeaseVerificationResult.Valid>(Verifier().VerifyOnPremises(Lease(server), server, Expect()));
        Assert.Equal("lease-1-AAAAAAAAAAAAAA", valid.Lease.LeaseId);
        Assert.Equal("proof-1", valid.Lease.AnsweredProofId);
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
    /// A lease, or the certificate of a hosted lease, that carries no valid signature is refused on every path as an error,
    /// not an exception: with an empty signature part, with a signature that is not 64 bytes or not the key's, and with an
    /// alg other than ES256 (design §8.2 rules 3 and 5, §12.7.5 step 1, §13.4, §20.7 V1d14).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - One path per case, on which the token as issued is accepted: a client's check of an on-premises lease; the server's
    ///   check of its own lease at a reclaim; the server's reading of a borrow from its record; a client's check of a hosted
    ///   lease; the same check, for the hosted lease's certificate.
    /// - The token of the path, or the certificate on the last path, in a form of <see cref="SignatureForms"/> with the same
    ///   payload: an empty signature part; the first 63 bytes of its signature; 64 zero bytes; its signature and one more
    ///   zero byte; alg none and an empty signature part; alg HS256 and an ES256 signature of its own key over the changed
    ///   header.
    ///
    /// Steps:
    /// - Run the path's check on the changed token or certificate.
    ///
    /// Expected:
    /// - On-premises and reclaim: ELM_BAD_SIGNATURE, "The lease is not signed by the server's key." for the four signatures
    ///   and "The token algorithm is not ES256." for the two algs.
    /// - The borrow record: the token is not read.
    /// - Hosted: ELM_BAD_SIGNATURE, "The lease signature does not verify." for the four signatures and "The token algorithm
    ///   is not ES256." for the two algs.
    /// - The hosted lease's certificate: ELM_UNTRUSTED_KEY "No trusted certificate certifies the lease signing key.".
    /// </remarks>
    [Theory]
    [MemberData(nameof(PathsAndSignatureForms))]
    public void Verify_LeaseWithoutAValidSignature_IsRejected(string path, string form)
    {
        const string NotEs256 = "ELM_BAD_SIGNATURE: The token algorithm is not ES256.";
        bool algorithm = SignatureForms.ChangesAlgorithm(form);
        string expected = path switch
        {
            "on-premises" or "reclaim" => algorithm ? NotEs256 : "ELM_BAD_SIGNATURE: The lease is not signed by the server's key.",
            "borrow-record" => "not read",
            "hosted" => algorithm ? NotEs256 : "ELM_BAD_SIGNATURE: The lease signature does not verify.",
            _ => "ELM_UNTRUSTED_KEY: No trusted certificate certifies the lease signing key.",
        };
        Assert.Equal("valid", Outcome(path, (token, _) => token));

        Assert.Equal(expected, Outcome(path, (token, key) => SignatureForms.Change(token, form, SignatureForms.Es256(key))));
    }

    // What a path answers to its token, or to the certificate of a hosted lease, after a change by the function, which
    // also gets the key that signed the token: "valid", "not read", or the error's code and message.
    private string Outcome(string path, Func<string, ECDsa, string> change)
    {
        ServerIdentity server = Server();
        (string hosted, string certificate) = HostedLease();
        LeaseVerificationResult result;
        switch (path)
        {
            case "on-premises":
                result = Verifier().VerifyOnPremises(change(Lease(server), _server), server, Expect());
                break;
            case "reclaim":
                result = Verifier().VerifyOwnLease(change(Lease(server), _server), server, Expect());
                break;
            case "borrow-record":
                string borrow = Lease(server, spec => spec with { IsBorrow = true, Duration = TimeSpan.FromDays(7) });
                return LeaseVerifier.TryReadOwnLease(change(borrow, _server), server, out VerifiedLease? read) && read!.IsBorrow
                    ? "valid"
                    : "not read";
            case "hosted":
                result = Verifier().VerifyHosted(change(hosted, _signing), [certificate], Expect());
                break;
            default:
                result = Verifier().VerifyHosted(hosted, [change(certificate, _root)], Expect());
                break;
        }

        return result is LeaseVerificationResult.Invalid invalid ? $"{ErrorCodes.ToWireName(invalid.Error.Code)}: {invalid.Error.Message}" : "valid";
    }

    /// <summary>
    /// Lease issuance refuses specs the verifier would reject: zero seats, a negative sequence, a duration under one
    /// second, an empty ID, an ID that is not 22 characters of base64url, and an ID of the answered proof that is not 1 to
    /// 64 characters (design §8.4.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A valid lease spec.
    ///
    /// Steps:
    /// - Issue with each change, the IDs a path with "..", one with a '.', one of 21 and one of 23 characters; issue with
    ///   an ID of 22 characters using each kind of character, and with the smallest seats, sequence, and duration.
    /// - Issue with an ID of the answered proof that is empty and one of 65 characters; then with one of 1 and one of 64
    ///   characters, and verify those two.
    ///
    /// Expected:
    /// - ArgumentException each time, for the IDs "The lease ID must be 22 characters of base64url."; the
    ///   last two are issued.
    /// - "The ID of the answered proof must be 1 to 64 characters. (Parameter 'spec')" for the empty one and the 65
    ///   characters; the tokens with 1 and with 64 characters are issued and verify with that ID.
    /// </remarks>
    [Fact]
    public void LeaseIssuer_InvalidSpec_Throws()
    {
        ServerIdentity server = Server();
        LeaseSpec spec = Spec(server);

        Assert.ThrowsAny<ArgumentException>(() => LeaseIssuer.Issue(_server, spec with { Seats = 0 }, _time));
        Assert.ThrowsAny<ArgumentException>(() => LeaseIssuer.Issue(_server, spec with { Sequence = -1 }, _time));
        Assert.ThrowsAny<ArgumentException>(() => LeaseIssuer.Issue(_server, spec with { Duration = TimeSpan.FromMilliseconds(500) }, _time));
        Assert.ThrowsAny<ArgumentException>(() => LeaseIssuer.Issue(_server, spec with { LeaseId = string.Empty }, _time));
        foreach (string id in new[] { @"..\..\AAAAAAAAAAAAAAAA", "lease-1-AAAAAAAAAAAAA.", "lease-1-AAAAAAAAAAAAA", "lease-1-AAAAAAAAAAAAAAA" })
        {
            Assert.Equal(
                "The lease ID must be 22 characters of base64url. (Parameter 'spec')",
                Assert.Throws<ArgumentException>(() => LeaseIssuer.Issue(_server, spec with { LeaseId = id }, _time)).Message
            );
        }

        Assert.NotEmpty(LeaseIssuer.Issue(_server, spec with { LeaseId = "Az09-_Az09-_Az09-_Az09" }, _time));
        Assert.NotEmpty(LeaseIssuer.Issue(_server, spec with { Seats = 1, Sequence = 0, Duration = TimeSpan.FromSeconds(1) }, _time));

        foreach (string proofId in new[] { string.Empty, new string('p', 65) })
        {
            Assert.Equal(
                "The ID of the answered proof must be 1 to 64 characters. (Parameter 'spec')",
                Assert.Throws<ArgumentException>(() => LeaseIssuer.Issue(_server, spec with { AnsweredProofId = proofId }, _time)).Message
            );
        }

        foreach (string proofId in new[] { "p", new string('p', 64) })
        {
            string token = LeaseIssuer.Issue(_server, spec with { AnsweredProofId = proofId }, _time);
            LeaseVerificationResult result = Verifier().VerifyOnPremises(token, server, Expect());
            Assert.Equal(proofId, Assert.IsType<LeaseVerificationResult.Valid>(result).Lease.AnsweredProofId);
        }
    }

    /// <summary>
    /// A lease is issued at the longest duration and at the earliest and the latest end, and refused one second past each
    /// with a message that states the limit itself (design §8.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A valid lease spec.
    ///
    /// Steps:
    /// - Issue with a duration of 500 ms, of 3,162,240,000 s (100 years of 366 days), and of one second more; issue at
    ///   9999-12-31T23:59:58Z with a duration of 1 s and of 2 s; issue with a duration of 1 s at 1969-12-31T23:59:59Z and a
    ///   second earlier.
    ///
    /// Expected:
    /// - "The lease duration must be at least one second and at most 100 years. (Parameter 'spec')" for 500 ms and for one
    ///   second more, a lease for the limit; a lease that ends at 9999-12-31T23:59:59Z, and "The lease must end between 1970
    ///   and 9999. (Parameter 'spec')" for 2 s; a lease that ends at 1970-01-01T00:00:00Z, and the same message for the one
    ///   that would end a second earlier.
    /// </remarks>
    [Fact]
    public void LeaseIssuer_DurationAndEndAtTheirLimits_IssuesAndRefusesOnePast()
    {
        LeaseSpec spec = Spec(Server());
        const string Duration = "The lease duration must be at least one second and at most 100 years. (Parameter 'spec')";
        var longest = TimeSpan.FromSeconds(Limits.MaxDurationSeconds);
        string Refusal(TimeSpan duration, DateTimeOffset issuedAt) =>
            Assert.Throws<ArgumentException>(() => LeaseIssuer.Issue(_server, spec with { Duration = duration }, issuedAt)).Message;

        Assert.Equal(3_162_240_000L, Limits.MaxDurationSeconds);
        Assert.Equal(Duration, Refusal(TimeSpan.FromMilliseconds(500), Now));
        Assert.NotEmpty(LeaseIssuer.Issue(_server, spec with { Duration = longest }, Now));
        Assert.Equal(Duration, Refusal(longest + TimeSpan.FromSeconds(1), Now));

        var beforeTheEnd = new DateTimeOffset(9999, 12, 31, 23, 59, 58, TimeSpan.Zero);
        Assert.Equal(Limits.MaxUnixSeconds, beforeTheEnd.ToUnixTimeSeconds() + 1);
        Assert.NotEmpty(LeaseIssuer.Issue(_server, spec with { Duration = TimeSpan.FromSeconds(1) }, beforeTheEnd));
        const string End = "The lease must end between 1970 and 9999. (Parameter 'spec')";
        Assert.Equal(End, Refusal(TimeSpan.FromSeconds(2), beforeTheEnd));

        var beforeTheEpoch = new DateTimeOffset(1969, 12, 31, 23, 59, 59, TimeSpan.Zero);
        Assert.NotEmpty(LeaseIssuer.Issue(_server, spec with { Duration = TimeSpan.FromSeconds(1) }, beforeTheEpoch));
        Assert.Equal(End, Refusal(TimeSpan.FromSeconds(1), beforeTheEpoch.AddSeconds(-1)));
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
            LeaseId = "lease-h-AAAAAAAAAAAAAA",
            ProductId = "acme-cad",
            LicenseId = "lic-h",
            FeatureId = "pro",
            Seats = 1,
            InstallationKeyThumbprint = ClientKid,
            Duration = TimeSpan.FromMinutes(10),
            AnsweredProofId = "proof-1",
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
            string lease = LeaseIssuer.Issue(
                _signing,
                spec with
                {
                    LeaseId = "lease-" + i.ToString("D16", System.Globalization.CultureInfo.InvariantCulture),
                },
                _time
            );
            Assert.IsType<LeaseVerificationResult.Valid>(verifier.VerifyHosted(lease, [leaseCert], Expect()));
        }

        Assert.Equal(1, verifier.CachedCertificateCount);
    }

    /// <summary>
    /// Malformed lease payloads, correctly signed, are refused without an exception: null members, zero seats, an expiry
    /// not after the issue time, a server ID other than the signer, an empty token, a lease ID that is not 22
    /// characters of base64url (design §8.4.3), since a client names a borrow's file after it, and an ID of the answered
    /// proof (areq) that is missing, null, a number, an object, empty, or 65 characters long (design §8.4.3, V1d5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A lease payload written by hand, which verifies as it is (the "valid" case, and with an areq of 1 and of 64
    ///   characters), signed with the server key after one change.
    ///
    /// Steps:
    /// - Verify each.
    ///
    /// Expected:
    /// - Valid with the areq written for the three unchanged cases; ELM_MALFORMED with "The lease payload is not valid."
    ///   for every changed payload, and with "The lease token is empty." for the empty token.
    /// </remarks>
    [Theory]
    [InlineData("valid")]
    [InlineData("areq-one")]
    [InlineData("areq-longest")]
    [InlineData("areq-missing")]
    [InlineData("areq-null")]
    [InlineData("areq-number")]
    [InlineData("areq-object")]
    [InlineData("areq-empty")]
    [InlineData("areq-long")]
    [InlineData("lse-null")]
    [InlineData("cnf-null")]
    [InlineData("n-zero")]
    [InlineData("exp-not-after-iat")]
    [InlineData("srv-other")]
    [InlineData("empty")]
    [InlineData("lse-path")]
    [InlineData("lse-rooted")]
    [InlineData("lse-character")]
    [InlineData("lse-short")]
    [InlineData("lse-long")]
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
            ["lse"] = "lease-1-AAAAAAAAAAAAAA",
            ["prd"] = "acme-cad",
            ["lic"] = "lic-srv",
            ["feat"] = "pro",
            ["n"] = 1,
            ["cnf"] = new JsonObject { ["jkt"] = ClientKid },
            ["brw"] = false,
            ["srv"] = kid,
            ["slic"] = server.License.TokenId,
            ["seq"] = 0,
            ["areq"] = "proof-1",
        };
        switch (kind)
        {
            case "areq-one":
                payload["areq"] = "p";
                break;
            case "areq-longest":
                payload["areq"] = new string('p', 64);
                break;
            case "areq-missing":
                payload.Remove("areq");
                break;
            case "areq-null":
                payload["areq"] = null;
                break;
            case "areq-number":
                payload["areq"] = 7;
                break;
            case "areq-object":
                payload["areq"] = new JsonObject { ["jti"] = "proof-1" };
                break;
            case "areq-empty":
                payload["areq"] = string.Empty;
                break;
            case "areq-long":
                payload["areq"] = new string('p', 65);
                break;
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
            case "lse-path":
                payload["lse"] = @"..\..\AAAAAAAAAAAAAAAA";
                break;
            case "lse-rooted":
                payload["lse"] = @"C:\AAAAAAAAAAAAAAAAAAA";
                break;
            case "lse-character":
                payload["lse"] = "lease-1-AAAAAAAAAAAAA.";
                break;
            case "lse-short":
                payload["lse"] = "lease-1-AAAAAAAAAAAAA";
                break;
            case "lse-long":
                payload["lse"] = "lease-1-AAAAAAAAAAAAAAA";
                break;
            default:
                break;
        }

        string token = kind == "empty" ? string.Empty : SignJws(_server, kid, TokenTypes.Lease, payload.ToJsonString());

        LeaseVerificationResult result = Verifier().VerifyOnPremises(token, server, Expect());
        if (kind is "valid" or "areq-one" or "areq-longest")
        {
            Assert.Equal(payload["areq"]!.GetValue<string>(), Assert.IsType<LeaseVerificationResult.Valid>(result).Lease.AnsweredProofId);
            return;
        }

        Assert.Equal(
            new LicenseError(ErrorCode.Malformed, kind == "empty" ? "The lease token is empty." : "The lease payload is not valid."),
            Invalid(result)
        );
    }

    /// <summary>
    /// A lease of the hosted service whose payload does not name the proof it answers (areq), or names it in a wrong form,
    /// is refused as malformed, as on premises (design §8.4.3, §12.7.5, V1d5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The signing key certified for lease by the root; a hosted lease payload written by hand (no slic), which verifies
    ///   as it is, signed with the signing key after one change of areq.
    ///
    /// Steps:
    /// - Verify with VerifyHosted: unchanged; without areq; with null, a number, an empty text, and 65 characters.
    ///
    /// Expected:
    /// - Valid with the areq written; ELM_MALFORMED "The lease payload is not valid." for the five others.
    /// </remarks>
    [Theory]
    [InlineData("valid")]
    [InlineData("areq-missing")]
    [InlineData("areq-null")]
    [InlineData("areq-number")]
    [InlineData("areq-empty")]
    [InlineData("areq-long")]
    public void VerifyHosted_AnsweredProofIdMissingOrMalformed_IsMalformed(string kind)
    {
        string leaseCert = CertificateIssuer.Issue(_root, _signing, KeyUses.Lease, Now.AddDays(-1), TimeSpan.FromDays(30), _time);
        string kid = KeyFiles.KeyId(_signing);
        long now = Now.ToUnixTimeSeconds();
        var payload = new JsonObject
        {
            ["ver"] = 1,
            ["iss"] = kid,
            ["iat"] = now,
            ["exp"] = now + 600,
            ["jti"] = "j",
            ["lse"] = "lease-h-AAAAAAAAAAAAAA",
            ["prd"] = "acme-cad",
            ["lic"] = "lic-h",
            ["feat"] = "pro",
            ["n"] = 1,
            ["cnf"] = new JsonObject { ["jkt"] = ClientKid },
            ["brw"] = false,
            ["srv"] = kid,
            ["seq"] = 1,
            ["areq"] = "proof-1",
        };
        switch (kind)
        {
            case "areq-missing":
                payload.Remove("areq");
                break;
            case "areq-null":
                payload["areq"] = null;
                break;
            case "areq-number":
                payload["areq"] = 7;
                break;
            case "areq-empty":
                payload["areq"] = string.Empty;
                break;
            case "areq-long":
                payload["areq"] = new string('p', 65);
                break;
            default:
                break;
        }

        string token = SignJws(_signing, kid, TokenTypes.Lease, payload.ToJsonString());

        LeaseVerificationResult result = Verifier().VerifyHosted(token, [leaseCert], Expect());
        if (kind == "valid")
        {
            Assert.Equal("proof-1", Assert.IsType<LeaseVerificationResult.Valid>(result).Lease.AnsweredProofId);
            return;
        }

        Assert.Equal(new LicenseError(ErrorCode.Malformed, "The lease payload is not valid."), Invalid(result));
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
            BorrowReturnLimit = 2,
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
            BorrowReturnLimit = isServer ? 2 : null,
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
            LeaseId = "lease-1-AAAAAAAAAAAAAA",
            ProductId = "acme-cad",
            LicenseId = "lic-srv",
            FeatureId = "pro",
            Seats = 2,
            InstallationKeyThumbprint = ClientKid,
            Duration = TimeSpan.FromMinutes(10),
            ServerLicenseTokenId = server.License.TokenId,
            AnsweredProofId = "proof-1",
        };

    private string Lease(ServerIdentity server, Func<LeaseSpec, LeaseSpec>? change = null) =>
        LeaseIssuer.Issue(_server, (change ?? (s => s))(Spec(server)), _time);

    // A lease as the hosted service issues it, with the certificate of its signing key for lease.
    private (string Lease, string Certificate) HostedLease()
    {
        var spec = new LeaseSpec
        {
            LeaseId = "lease-h-AAAAAAAAAAAAAA",
            ProductId = "acme-cad",
            LicenseId = "lic-h",
            FeatureId = "pro",
            Seats = 1,
            InstallationKeyThumbprint = ClientKid,
            Duration = TimeSpan.FromMinutes(10),
            AnsweredProofId = "proof-1",
        };
        return (
            LeaseIssuer.Issue(_signing, spec, _time),
            CertificateIssuer.Issue(_root, _signing, KeyUses.Lease, Now.AddDays(-1), TimeSpan.FromDays(30), _time)
        );
    }

    private sealed class RejectingGuard : IClockGuard
    {
        public bool Observe(DateTimeOffset now, DateTimeOffset tokenIssuedAt, TimeSpan allowedSkew) => false;
    }
}
