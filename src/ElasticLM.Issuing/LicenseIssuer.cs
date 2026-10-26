using System.Security.Cryptography;
using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.Issuing;

/// <summary>
/// A license file issued by <see cref="LicenseIssuer"/>.
/// </summary>
/// <param name="File">The license file bytes (design §8.5).</param>
/// <param name="TokenId">The token ID (<c>jti</c>).</param>
/// <param name="Warnings">Warnings for the person issuing, such as a token without expiry.</param>
public sealed record IssuedLicense(byte[] File, string TokenId, IReadOnlyList<string> Warnings);

/// <summary>
/// Issues license tokens and license files (design §8.4.2, §8.5).
/// </summary>
public static class LicenseIssuer
{
    /// <summary>
    /// Issues a license file signed by a signing key and carrying its certificates.
    /// </summary>
    /// <param name="signingKey">The signing key with its private part.</param>
    /// <param name="certificates">Certificates of the signing key (<c>elm-cert+jws</c>), at most four. At least one must allow
    /// <c>license</c> and cover the issue time.</param>
    /// <param name="spec">The license.</param>
    /// <param name="time">The time provider, for the issue time.</param>
    /// <returns>The issued license.</returns>
    /// <exception cref="ArgumentException">The spec breaks a rule of design §8.4.2, §8.6, or §9.3, or no certificate covers the signing key for licenses at the issue time.</exception>
    public static IssuedLicense Issue(ECDsa signingKey, IReadOnlyList<string> certificates, LicenseSpec spec, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(time);

        long now = time.GetUtcNow().ToUnixTimeSeconds();
        string kid = KeyFiles.KeyId(signingKey);
        if (certificates.Count is 0 or > Limits.MaxCertificates)
        {
            throw new ArgumentException("Between one and four certificates are required.", nameof(certificates));
        }

        if (!certificates.Any(c => Certifies(c, kid, now)))
        {
            throw new ArgumentException("No certificate allows the signing key to sign licenses at the issue time.", nameof(certificates));
        }

        LicensePayload payload = ToPayload(spec, kid, now);
        if (!LicensePayloadRules.IsValid(payload, out _))
        {
            throw new ArgumentException(
                "The license breaks a rule of the token format: check the kind, the features, the limits, and the times.",
                nameof(spec)
            );
        }

        var warnings = new List<string>();
        if (payload.Exp is null)
        {
            warnings.Add("The license has no expiry. It cannot be revoked once delivered (design 5.4).");
        }

        if (spec.Kind == LicenseKind.Server && payload.Exp is null)
        {
            warnings.Add("A server license without expiry is never refreshed, so a cloned server cannot be detected (design 9.6).");
        }

        string token = JwsWriter.Sign(signingKey, TokenTypes.License, payload, LicensingJsonContext.Default.LicensePayload);
        var envelope = new LicenseFileEnvelope
        {
            Format = "elasticlm-license",
            Version = 1,
            License = token,
            Certificates = [.. certificates],
        };
        byte[] file = JsonSerializer.SerializeToUtf8Bytes(envelope, LicensingJsonContext.Default.LicenseFileEnvelope);
        if (file.Length > Limits.MaxLicenseFileLength || token.Length > Limits.MaxTokenLength)
        {
            throw new ArgumentException("The license file is larger than the limit.", nameof(spec));
        }

        return new IssuedLicense(file, payload.Jti, warnings);
    }

    private static LicensePayload ToPayload(LicenseSpec spec, string kid, long now)
    {
        string kind = spec.Kind switch
        {
            LicenseKind.Standard => "standard",
            LicenseKind.Trial => "trial",
            LicenseKind.Server => "server",
            _ => throw new ArgumentException("The license kind is not defined.", nameof(spec)),
        };

        bool hasFingerprints = spec.Fingerprints.Count > 0;
        return new LicensePayload
        {
            Ver = 1,
            Iss = kid,
            Iat = now,
            Exp = spec.ExpiresAt?.ToUnixTimeSeconds(),
            Jti = JwsWriter.NewTokenId(),
            Lic = spec.LicenseId ?? string.Empty,
            Prd = spec.ProductId ?? string.Empty,
            Kind = kind,
            Feat =
            [
                .. spec.Features.Select(f => new FeatureClaim
                {
                    Id = f?.Id ?? string.Empty,
                    Ver = f?.VersionRange,
                    Seats = f?.Seats,
                    Exp = f?.ExpiresAt?.ToUnixTimeSeconds(),
                }),
            ],
            Cnf = spec.InstallationKeyThumbprint is { } jkt ? new ConfirmationClaim { Jkt = jkt } : null,
            Fp = hasFingerprints ? [.. spec.Fingerprints] : null,
            Fpm = hasFingerprints
                ? spec.FingerprintMatching switch
                {
                    FingerprintMatching.Any => "any",
                    FingerprintMatching.All => "all",
                    FingerprintMatching.Ignore => "ignore",
                    _ => throw new ArgumentException("The fingerprint matching is not defined.", nameof(spec)),
                }
                : null,
            Grace = spec.ExpiresAt is null ? null : Seconds(spec.GracePeriod),
            Refresh = spec.RefreshWindow is { } refresh ? Seconds(refresh) : null,
            Seq = spec.Sequence,
            Areq = spec.OfflineRequestNonce,
            Lease = spec.LeaseDuration is { } lease ? Seconds(lease) : null,
            Borrow = spec.BorrowLimit is { } borrow ? Seconds(borrow) : null,
            Overage = spec.OveragePercent,
            Meta = spec.Metadata is null ? null : new Dictionary<string, string>(spec.Metadata, StringComparer.Ordinal),
        };
    }

    private static long Seconds(TimeSpan span) => (long)Math.Floor(span.TotalSeconds);

    private static bool Certifies(string certificate, string kid, long now)
    {
        if (!CompactJws.TryParse(certificate, TokenTypes.KeyCertificate, out CompactJws? jws, out _))
        {
            return false;
        }

        // The issuer's own certificate file: its root signature is checked by the verifier, not here.
        KeyCertificatePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(jws!.Payload, LicensingJsonContext.Default.KeyCertificatePayload);
        }
        catch (JsonException)
        {
            return false;
        }

        return payload is not null
            && payload.Sub == kid
            && payload.Use.Contains("license", StringComparer.Ordinal)
            && payload.Nbf <= now
            && now <= payload.Exp;
    }
}
