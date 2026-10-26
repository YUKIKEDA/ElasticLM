using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ElasticLM.Licensing;

/// <summary>
/// Verifies license files offline (design §11). The steps and their order are those of design §11.2; the result names the
/// first step that failed.
/// </summary>
public sealed class LicenseVerifier
{
    private const string LicenseFileFormat = "elasticlm-license";

    private readonly TrustStore _trust;
    private readonly TimeProvider _time;
    private readonly IClockGuard _clockGuard;
    private readonly LicenseVerifierOptions _options;

    private readonly CertificateSet _certificates;

    /// <summary>
    /// Initializes a new instance of the <see cref="LicenseVerifier"/> class.
    /// </summary>
    /// <param name="trust">The trust store.</param>
    /// <param name="time">The time provider.</param>
    /// <param name="clockGuard">The clock guard, such as <see cref="FileClockGuard"/> or <see cref="StatelessClockGuard.Instance"/>.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults. They are copied.</param>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="LicenseVerifierOptions.ClockSkew"/> is negative or more than <see cref="LicenseVerifierOptions.MaxClockSkew"/>.</exception>
    public LicenseVerifier(TrustStore trust, TimeProvider time, IClockGuard clockGuard, LicenseVerifierOptions? options = null)
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
        _certificates = new CertificateSet(trust, CertificateSet.UseLicense);
        _time = time;
        _clockGuard = clockGuard;
        _options = new LicenseVerifierOptions { ClockSkew = options.ClockSkew, AcceptTrialAsStandard = options.AcceptTrialAsStandard };
    }

    /// <summary>
    /// Verifies a license file against a requirement.
    /// </summary>
    /// <param name="licenseFile">The license file bytes (UTF-8 JSON, design §8.5).</param>
    /// <param name="requirement">What the app requires.</param>
    /// <returns><see cref="LicenseVerificationResult.Valid"/>, or <see cref="LicenseVerificationResult.Invalid"/> with the first failed check.</returns>
    /// <exception cref="ArgumentException">The requirement has an empty product ID or a feature version that is not <c>major.minor[.patch]</c>.</exception>
    public LicenseVerificationResult Verify(ReadOnlySpan<byte> licenseFile, LicenseRequirement requirement)
    {
        ArgumentException.ThrowIfNullOrEmpty(requirement.ProductId, nameof(requirement));
        FeatureVersion? requiredVersion = null;
        if (requirement.FeatureVersion is not null)
        {
            if (!FeatureVersion.TryParse(requirement.FeatureVersion, out FeatureVersion parsed))
            {
                throw new ArgumentException("The required feature version is not major.minor[.patch].", nameof(requirement));
            }

            requiredVersion = parsed;
        }

        // Step 1: size and format.
        if (licenseFile.Length > Limits.MaxLicenseFileLength)
        {
            return Fail(ErrorCode.Malformed, "The license file is larger than the limit.");
        }

        if (!TryReadEnvelope(licenseFile, out LicenseFileEnvelope? envelope))
        {
            return Fail(ErrorCode.Malformed, "The license file is not a valid ElasticLM license file.");
        }

        if (!CompactJws.TryParse(envelope!.License, TokenTypes.License, out CompactJws? license, out LicenseError? parseError))
        {
            return new LicenseVerificationResult.Invalid(parseError!);
        }

        // Step 2: the signer of the token must not be revoked.
        if (_trust.IsRevoked(license!.Header.Kid))
        {
            return Fail(ErrorCode.KeyRevoked, "The license was signed by a revoked key.");
        }

        // Step 3: the certificates that verify under a trusted root and certify the token's key for licenses. Every candidate
        // certifies the same key, so the signature is checked once with it before the payload is read (design §8.2 rule 6).
        List<CertifiedKey> candidates = _certificates.CollectCandidates(envelope.Certificates, license.Header.Kid, out List<CertifiedKey>? uncached);
        try
        {
            return VerifyToken(license, candidates, requirement, requiredVersion);
        }
        finally
        {
            foreach (CertifiedKey entry in uncached ?? [])
            {
                entry.Key.Dispose();
            }
        }
    }

    private LicenseVerificationResult VerifyToken(
        CompactJws license,
        List<CertifiedKey> candidates,
        LicenseRequirement requirement,
        FeatureVersion? requiredVersion
    )
    {
        if (candidates.Count == 0)
        {
            return Fail(ErrorCode.UntrustedKey, "No trusted certificate certifies the license signing key.");
        }

        // Step 4: the token signature.
        if (!candidates[0].Key.Verify(license))
        {
            return Fail(ErrorCode.BadSignature, "The license signature does not verify.");
        }

        if (!TryReadLicensePayload(license.Payload, out LicensePayload? payload, out LicenseKind kind))
        {
            return Fail(ErrorCode.Malformed, "The license payload is not valid.");
        }

        if (payload!.Iss != license.Header.Kid)
        {
            return Fail(ErrorCode.Malformed, "The license issuer does not match its key ID.");
        }

        DateTimeOffset now = _time.GetUtcNow();
        long nowSeconds = now.ToUnixTimeSeconds();
        long skew = (long)_options.ClockSkew.TotalSeconds;

        // Step 5: a candidate certificate whose validity period holds the token's iat (design §7.5). Several certificates for
        // the same key appear when the certificate is renewed without changing the key.
        if (!candidates.Exists(c => payload.Iat >= c.Nbf - skew && payload.Iat <= c.Exp + skew))
        {
            return Fail(ErrorCode.CertificateExpired, "The license was issued outside the validity period of its key certificates.");
        }

        // Step 6: not issued in the future.
        if (payload.Iat > nowSeconds + skew)
        {
            return Fail(ErrorCode.ClockTampered, "The license is issued in the future.");
        }

        // Step 7: the clock guard.
        if (!_clockGuard.Observe(now, DateTimeOffset.FromUnixTimeSeconds(payload.Iat), _options.ClockSkew))
        {
            return Fail(ErrorCode.ClockTampered, "The clock has been set back.");
        }

        // Step 8: kind.
        bool kindMatches =
            kind == requirement.Kind || (requirement.Kind == LicenseKind.Standard && kind == LicenseKind.Trial && _options.AcceptTrialAsStandard);
        if (!kindMatches)
        {
            return Fail(ErrorCode.WrongKind, "The license kind is not the required one.");
        }

        // Step 9: product.
        if (payload.Prd != requirement.ProductId)
        {
            return Fail(ErrorCode.WrongProduct, "The license is for another product.");
        }

        // Step 10: installation.
        LicenseError? bindingError = CheckBinding(payload, requirement.Installation);
        if (bindingError is not null)
        {
            return new LicenseVerificationResult.Invalid(bindingError);
        }

        // Step 11: expiry and grace.
        bool inGrace = false;
        if (payload.Exp is long exp)
        {
            long grace = payload.Grace ?? 0;
            if (nowSeconds > exp + grace)
            {
                return Fail(ErrorCode.Expired, "The license has expired.");
            }

            inGrace = nowSeconds > exp;
        }

        // Step 12: feature.
        if (requirement.FeatureId is not null)
        {
            LicenseError? featureError = CheckFeature(payload, requirement.FeatureId, requiredVersion, nowSeconds);
            if (featureError is not null)
            {
                return new LicenseVerificationResult.Invalid(featureError);
            }
        }

        var features = new List<LicensedFeature>(payload.Feat.Count);
        foreach (FeatureClaim feature in payload.Feat)
        {
            DateTimeOffset? featureExpiry = feature.Exp is long featureExp ? DateTimeOffset.FromUnixTimeSeconds(featureExp) : null;
            features.Add(new LicensedFeature(feature.Id, feature.Ver, feature.Seats, featureExpiry));
        }

        return new LicenseVerificationResult.Valid(new VerifiedLicense(payload, kind, features), inGrace);
    }

    private static LicenseVerificationResult.Invalid Fail(ErrorCode code, string message) => new(new LicenseError(code, message));

    private static bool TryReadEnvelope(ReadOnlySpan<byte> bytes, out LicenseFileEnvelope? envelope)
    {
        envelope = null;
        LicenseFileEnvelope? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(bytes, LicensingJsonContext.Default.LicenseFileEnvelope);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is null || parsed.Format != LicenseFileFormat || parsed.Version != 1)
        {
            return false;
        }

        if (parsed.Certificates.Count is 0 or > Limits.MaxCertificates || parsed.Certificates.Contains(null!))
        {
            return false;
        }

        envelope = parsed;
        return true;
    }

    private static bool TryReadLicensePayload(byte[] bytes, out LicensePayload? payload, out LicenseKind kind)
    {
        payload = null;
        kind = default;
        LicensePayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(bytes, LicensingJsonContext.Default.LicensePayload);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is null || !LicensePayloadRules.IsValid(parsed, out kind))
        {
            return false;
        }

        payload = parsed;
        return true;
    }

    private static LicenseError? CheckBinding(LicensePayload payload, InstallationIdentity? installation)
    {
        if (payload.Cnf is not null)
        {
            if (installation is null || installation.KeyThumbprint != payload.Cnf.Jkt)
            {
                return new LicenseError(ErrorCode.WrongInstallation, "The license is bound to another installation key.");
            }
        }

        if (payload.Fp is not { Count: > 0 } fingerprints || payload.Fpm == "ignore" || installation is { ChecksFingerprints: false })
        {
            return null;
        }

        IReadOnlyList<string> current = installation?.Fingerprints ?? [];
        if (current.Count == 0)
        {
            return new LicenseError(ErrorCode.FingerprintUnavailable, "No machine fingerprint is available on this machine.");
        }

        bool matches =
            payload.Fpm == "all"
                ? fingerprints.All(f => current.Contains(f, StringComparer.Ordinal))
                : fingerprints.Any(f => current.Contains(f, StringComparer.Ordinal));
        return matches ? null : new LicenseError(ErrorCode.FingerprintMismatch, "The machine fingerprints do not match the license.");
    }

    private static LicenseError? CheckFeature(LicensePayload payload, string featureId, FeatureVersion? requiredVersion, long nowSeconds)
    {
        FeatureClaim? feature = payload.Feat.Find(f => f.Id == featureId);
        if (feature is null)
        {
            return new LicenseError(ErrorCode.FeatureNotLicensed, "The feature is not in the license.");
        }

        if (requiredVersion is FeatureVersion version && feature.Ver is not null)
        {
            // The range was validated when the payload was read, so parsing it again does not fail.
            if (!FeatureVersionRange.TryParse(feature.Ver, out FeatureVersionRange? range) || !range!.Contains(version))
            {
                return new LicenseError(ErrorCode.FeatureVersionNotLicensed, "The feature version is outside the licensed range.");
            }
        }

        if (feature.Exp is long featureExp && nowSeconds > featureExp)
        {
            return new LicenseError(ErrorCode.FeatureExpired, "The feature has expired.");
        }

        return null;
    }
}
