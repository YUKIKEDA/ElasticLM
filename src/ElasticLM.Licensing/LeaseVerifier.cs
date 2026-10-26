using System.Text.Json;

namespace ElasticLM.Licensing;

/// <summary>
/// Checks lease tokens on the client (design §12.7.5): when a lease arrives, and before a borrowed lease is used.
/// </summary>
public sealed class LeaseVerifier
{
    private readonly TrustStore _trust;
    private readonly CertificateSet _leaseCertificates;
    private readonly TimeProvider _time;
    private readonly IClockGuard _clockGuard;
    private readonly LicenseVerifierOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="LeaseVerifier"/> class.
    /// </summary>
    /// <param name="trust">The trust store, for the certificates of hosted leases. Certificates that verified are kept for the
    /// life of the verifier, as in <see cref="LicenseVerifier"/>.</param>
    /// <param name="time">The time provider.</param>
    /// <param name="clockGuard">The clock guard (design §11.4 item 5: leases are guarded like licenses).</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults. Only <see cref="LicenseVerifierOptions.ClockSkew"/> is used.</param>
    /// <exception cref="ArgumentOutOfRangeException">The clock skew is negative or more than <see cref="LicenseVerifierOptions.MaxClockSkew"/>.</exception>
    public LeaseVerifier(TrustStore trust, TimeProvider time, IClockGuard clockGuard, LicenseVerifierOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(clockGuard);
        options ??= new LicenseVerifierOptions();
        if (options.ClockSkew < TimeSpan.Zero || options.ClockSkew > LicenseVerifierOptions.MaxClockSkew)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ClockSkew, "The clock skew must be between zero and one hour.");
        }

        _trust = trust;
        _leaseCertificates = new CertificateSet(trust, CertificateSet.UseLease);
        _time = time;
        _clockGuard = clockGuard;
        _options = new LicenseVerifierOptions { ClockSkew = options.ClockSkew };
    }

    /// <summary>
    /// Checks a lease issued by an on-premises server: the signature with the server's key, and that the lease stays inside
    /// the server license (design §12.7.5 steps 1 to 6).
    /// </summary>
    /// <param name="leaseToken">The lease token.</param>
    /// <param name="server">The server, built from its verified server license.</param>
    /// <param name="expect">The product and installation the lease must be for.</param>
    /// <returns>The result.</returns>
    public LeaseVerificationResult VerifyOnPremises(string leaseToken, ServerIdentity server, LeaseExpectation expect) =>
        VerifyOnPremises(leaseToken, server, expect, requireCurrentServerLicense: true);

    /// <summary>
    /// Checks a lease token that a server issued itself, for a reclaim after a restart (design §13.4): the same checks as
    /// <see cref="VerifyOnPremises(string, ServerIdentity, LeaseExpectation)"/>, except that the token may name an earlier
    /// server license of the same server. Only that server's key could have signed it, and the token must still fit the
    /// current server license, so a server license replaced while the server was down does not cost the clients their seats.
    /// </summary>
    internal LeaseVerificationResult VerifyOwnLease(string leaseToken, ServerIdentity server, LeaseExpectation expect) =>
        VerifyOnPremises(leaseToken, server, expect, requireCurrentServerLicense: false);

    private LeaseVerificationResult VerifyOnPremises(
        string leaseToken,
        ServerIdentity server,
        LeaseExpectation expect,
        bool requireCurrentServerLicense
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(expect);
        if (!TryReadSigned(leaseToken, out CompactJws? jws, out LicenseError? error))
        {
            return new LeaseVerificationResult.Invalid(error!);
        }

        // Step 1: signed by the server's key.
        if (jws!.Header.Kid != server.KeyId || !server.Key.Verify(jws))
        {
            return Fail(ErrorCode.BadSignature, "The lease is not signed by the server's key.");
        }

        if (!TryReadPayload(jws, out LeasePayload? read))
        {
            return Fail(ErrorCode.Malformed, "The lease payload is not valid.");
        }

        LeasePayload payload = read!;
        VerifiedLicense license = server.License;
        if (requireCurrentServerLicense && payload.Slic != license.TokenId)
        {
            return Fail(ErrorCode.LeaseOutOfBounds, "The lease was issued under another server license; fetch the server license again.");
        }

        LicenseError? common = CheckForClient(payload, expect);
        if (common is not null)
        {
            return new LeaseVerificationResult.Invalid(common);
        }

        // Step 3: inside the server license's license, feature, and seats.
        LicensedFeature? feature = license.Features.FirstOrDefault(f => f.Id == payload.Feat);
        if (payload.Lic != license.LicenseId || feature is null || payload.N > feature.Seats)
        {
            return Fail(ErrorCode.LeaseOutOfBounds, "The lease is outside the features or seats of the server license.");
        }

        // Step 4: no longer than the lease duration, or the borrow limit for a borrow.
        TimeSpan limit = payload.Brw ? license.BorrowLimit ?? TimeSpan.Zero : license.LeaseDuration ?? TimeSpan.Zero;
        if (payload.Exp - payload.Iat > (long)limit.TotalSeconds)
        {
            return Fail(ErrorCode.LeaseOutOfBounds, "The lease is longer than the server license allows.");
        }

        // Step 5: not past the server license's expiry and grace.
        if (license.ExpiresAt is DateTimeOffset expires && payload.Exp > (expires + license.GracePeriod).ToUnixTimeSeconds())
        {
            return Fail(ErrorCode.LeaseOutOfBounds, "The lease outlives the server license.");
        }

        return CheckTime(payload, leaseToken);
    }

    /// <summary>
    /// Checks a lease issued by the hosted service: the signature with a signing key certified for <c>lease</c> by a trusted
    /// root (design §12.7.5 steps 1, 2, and 6; steps 3 to 5 are for on-premises servers only).
    /// </summary>
    /// <param name="leaseToken">The lease token.</param>
    /// <param name="certificates">The certificates sent with the lease.</param>
    /// <param name="expect">The product and installation the lease must be for.</param>
    /// <returns>The result.</returns>
    public LeaseVerificationResult VerifyHosted(string leaseToken, IReadOnlyList<string> certificates, LeaseExpectation expect)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(expect);
        if (!TryReadSigned(leaseToken, out CompactJws? jws, out LicenseError? error))
        {
            return new LeaseVerificationResult.Invalid(error!);
        }

        if (certificates.Count is 0 or > Limits.MaxCertificates || certificates.Contains(null!))
        {
            return Fail(ErrorCode.Malformed, "Between one and four certificates are required.");
        }

        if (_trust.IsRevoked(jws!.Header.Kid))
        {
            return Fail(ErrorCode.KeyRevoked, "The lease was signed by a revoked key.");
        }

        List<CertifiedKey> candidates = _leaseCertificates.CollectCandidates([.. certificates], jws.Header.Kid, out List<CertifiedKey>? uncached);
        try
        {
            if (candidates.Count == 0)
            {
                return Fail(ErrorCode.UntrustedKey, "No trusted certificate certifies the lease signing key.");
            }

            if (!candidates[0].Key.Verify(jws))
            {
                return Fail(ErrorCode.BadSignature, "The lease signature does not verify.");
            }

            if (!TryReadPayload(jws, out LeasePayload? payload))
            {
                return Fail(ErrorCode.Malformed, "The lease payload is not valid.");
            }

            long skew = (long)_options.ClockSkew.TotalSeconds;
            if (!candidates.Exists(c => payload!.Iat >= c.Nbf - skew && payload.Iat <= c.Exp + skew))
            {
                return Fail(ErrorCode.CertificateExpired, "The lease was issued outside the validity period of its key certificates.");
            }

            LicenseError? common = CheckForClient(payload!, expect);
            return common is not null ? new LeaseVerificationResult.Invalid(common) : CheckTime(payload!, leaseToken);
        }
        finally
        {
            foreach (CertifiedKey entry in uncached ?? [])
            {
                entry.Key.Dispose();
            }
        }
    }

    /// <summary>Gets the number of hosted-lease certificates kept, for tests of the cache.</summary>
    internal int CachedCertificateCount => _leaseCertificates.CachedCount;

    private static LeaseVerificationResult.Invalid Fail(ErrorCode code, string message) => new(new LicenseError(code, message));

    private static bool TryReadSigned(string? token, out CompactJws? jws, out LicenseError? error)
    {
        if (string.IsNullOrEmpty(token))
        {
            jws = null;
            error = new LicenseError(ErrorCode.Malformed, "The lease token is empty.");
            return false;
        }

        return CompactJws.TryParse(token, TokenTypes.Lease, out jws, out error);
    }

    private static bool TryReadPayload(CompactJws jws, out LeasePayload? payload)
    {
        payload = null;
        LeasePayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(jws.Payload, LicensingJsonContext.Default.LeasePayload);
        }
        catch (JsonException)
        {
            return false;
        }

        bool valid =
            parsed is not null
            && parsed.Ver == 1
            && parsed.Iss == jws.Header.Kid
            && parsed.Srv == jws.Header.Kid
            && parsed.Lse.Length > 0
            && parsed.Prd.Length > 0
            && parsed.Lic.Length > 0
            && parsed.Feat.Length > 0
            && parsed.Jti.Length > 0
            && parsed.Cnf.Jkt.Length > 0
            && parsed.N >= 1
            && parsed.Seq >= 0
            && Limits.IsTime(parsed.Iat)
            && Limits.IsTime(parsed.Exp)
            && parsed.Exp > parsed.Iat;
        payload = valid ? parsed : null;
        return valid;
    }

    // Step 2: the client's own product and installation.
    private static LicenseError? CheckForClient(LeasePayload payload, LeaseExpectation expect)
    {
        if (payload.Prd != expect.ProductId)
        {
            return new LicenseError(ErrorCode.WrongProduct, "The lease is for another product.");
        }

        return payload.Cnf.Jkt != expect.InstallationKeyThumbprint
            ? new LicenseError(ErrorCode.WrongInstallation, "The lease is bound to another installation key.")
            : null;
    }

    // Step 6 and the expiry: not issued in the future, the clock guard, and not expired.
    private LeaseVerificationResult CheckTime(LeasePayload payload, string token)
    {
        DateTimeOffset now = _time.GetUtcNow();
        long nowSeconds = now.ToUnixTimeSeconds();
        long skew = (long)_options.ClockSkew.TotalSeconds;
        if (payload.Iat > nowSeconds + skew)
        {
            return Fail(ErrorCode.ClockTampered, "The lease is issued in the future.");
        }

        if (!_clockGuard.Observe(now, DateTimeOffset.FromUnixTimeSeconds(payload.Iat), _options.ClockSkew))
        {
            return Fail(ErrorCode.ClockTampered, "The clock has been set back.");
        }

        return nowSeconds > payload.Exp
            ? Fail(ErrorCode.Expired, "The lease has expired.")
            : new LeaseVerificationResult.Valid(new VerifiedLease(payload, token));
    }
}
