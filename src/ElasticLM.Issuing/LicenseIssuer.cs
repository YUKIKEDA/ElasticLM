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
    /// <param name="trust">The root keys the apps trust, or <see langword="null"/>. When given, the issued file is run
    /// through <see cref="LicenseVerifier"/> before it is returned, so a certificate that the root did not sign, or one whose
    /// key or period does not match, is found here rather than on a customer's machine.</param>
    /// <returns>The issued license.</returns>
    /// <exception cref="ArgumentException">The spec breaks a rule of design §8.4.2, §8.6, or §9.3; no certificate covers the
    /// signing key for licenses at the issue time; or the issued file does not pass verification with <paramref name="trust"/>.</exception>
    public static IssuedLicense Issue(
        ECDsa signingKey,
        IReadOnlyList<string> certificates,
        LicenseSpec spec,
        TimeProvider time,
        TrustStore? trust = null
    )
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

        string[] trimmed = [.. certificates.Select(c => c?.Trim() ?? string.Empty)];
        CertificateCheck best = trimmed.Select(c => Check(c, kid, now)).Min();
        if (best != CertificateCheck.Certifies)
        {
            throw new ArgumentException(
                best switch
                {
                    CertificateCheck.OutsidePeriod => "The certificates for the signing key do not cover the issue time.",
                    CertificateCheck.NoLicenseUse => "The certificates for the signing key do not allow it to sign licenses (use license).",
                    CertificateCheck.OtherKey => $"No certificate is for the signing key {kid}.",
                    _ => "No certificate can be read as an elm-cert+jws token.",
                },
                nameof(certificates)
            );
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
            Certificates = [.. trimmed],
        };
        byte[] file = JsonSerializer.SerializeToUtf8Bytes(envelope, LicensingJsonContext.Default.LicenseFileEnvelope);
        if (file.Length > Limits.MaxLicenseFileLength || token.Length > Limits.MaxTokenLength)
        {
            throw new ArgumentException("The license file is larger than the limit.", nameof(spec));
        }

        if (trust is not null)
        {
            VerifyIssued(file, spec, trust, time);
        }

        return new IssuedLicense(file, payload.Jti, warnings);
    }

    private static void VerifyIssued(byte[] file, LicenseSpec spec, TrustStore trust, TimeProvider time)
    {
        // An unbound license with fingerprints still needs an identity to carry them; the key is not checked without cnf.
        InstallationIdentity? installation =
            spec.InstallationKeyThumbprint is not null || spec.Fingerprints.Count > 0
                ? new InstallationIdentity(spec.InstallationKeyThumbprint ?? "unbound", spec.Fingerprints)
                : null;
        var requirement = new LicenseRequirement(spec.ProductId, spec.Kind, null, null, installation);
        var verifier = new LicenseVerifier(trust, time, StatelessClockGuard.Instance, new LicenseVerifierOptions { AcceptTrialAsStandard = false });
        if (verifier.Verify(file, requirement) is LicenseVerificationResult.Invalid invalid)
        {
            throw new ArgumentException(
                $"The issued license does not pass verification with the trust store: {invalid.Error.WireName}: {invalid.Error.Message}",
                nameof(trust)
            );
        }
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

    private static CertificateCheck Check(string certificate, string kid, long now)
    {
        if (!CompactJws.TryParse(certificate, TokenTypes.KeyCertificate, out CompactJws? jws, out _))
        {
            return CertificateCheck.Unreadable;
        }

        // The issuer's own certificate file: its root signature is checked by the verifier (see the trust parameter).
        KeyCertificatePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(jws!.Payload, LicensingJsonContext.Default.KeyCertificatePayload);
        }
        catch (JsonException)
        {
            return CertificateCheck.Unreadable;
        }

        if (payload is null)
        {
            return CertificateCheck.Unreadable;
        }

        if (payload.Sub != kid)
        {
            return CertificateCheck.OtherKey;
        }

        if (!payload.Use.Contains("license", StringComparer.Ordinal))
        {
            return CertificateCheck.NoLicenseUse;
        }

        return payload.Nbf <= now && now <= payload.Exp ? CertificateCheck.Certifies : CertificateCheck.OutsidePeriod;
    }

    // Ordered from the best result to the worst, so the minimum over the certificates is the closest one to usable.
    private enum CertificateCheck
    {
        Certifies,
        OutsidePeriod,
        NoLicenseUse,
        OtherKey,
        Unreadable,
    }
}
