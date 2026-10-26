using System.Text;

namespace ElasticLM.Licensing;

/// <summary>
/// The shape and limit rules of a license payload, shared by the verifier and the issuer.
/// </summary>
internal static class LicensePayloadRules
{
    /// <summary>
    /// Returns whether a license payload has every claim its kind needs and stays inside the limits of design §8.6 and the
    /// rules of §8.4.2 and §9.3. The verifier applies it after the signature is verified; the issuer applies it before
    /// signing, so it never issues a token the verifier rejects.
    /// </summary>
    public static bool IsValid(LicensePayload payload, out LicenseKind kind)
    {
        kind = default;
        if (payload.Ver != 1 || payload.Lic.Length == 0 || payload.Prd.Length == 0 || payload.Jti.Length == 0 || payload.Iss.Length == 0)
        {
            return false;
        }

        switch (payload.Kind)
        {
            case "standard":
                kind = LicenseKind.Standard;
                break;
            case "trial":
                kind = LicenseKind.Trial;
                break;
            case "server":
                kind = LicenseKind.Server;
                break;
            default:
                return false;
        }

        return IsValid(payload, kind);
    }

    private static bool IsValid(LicensePayload payload, LicenseKind kind)
    {
        if (payload.Feat.Count > Limits.MaxFeatures || (payload.Fp?.Count ?? 0) > Limits.MaxFingerprints)
        {
            return false;
        }

        // Collection elements are outside RespectNullableAnnotations, so a JSON null element is rejected here.
        if (payload.Feat.Contains(null!) || (payload.Fp?.Contains(null!) ?? false) || (payload.Meta?.ContainsValue(null!) ?? false))
        {
            return false;
        }

        foreach (FeatureClaim feature in payload.Feat)
        {
            if (
                feature.Id.Length == 0
                || !Limits.IsTime(feature.Exp)
                || (feature.Ver is not null && !FeatureVersionRange.TryParse(feature.Ver, out _))
            )
            {
                return false;
            }

            if (kind == LicenseKind.Server ? feature.Seats is null or < 1 : feature.Seats is not null)
            {
                return false;
            }
        }

        if (payload.Meta is { } meta)
        {
            if (meta.Count > Limits.MaxMetaEntries)
            {
                return false;
            }

            foreach ((string key, string value) in meta)
            {
                if (Encoding.UTF8.GetByteCount(key) > Limits.MaxMetaKeyBytes || Encoding.UTF8.GetByteCount(value) > Limits.MaxMetaValueBytes)
                {
                    return false;
                }
            }
        }

        // Times and durations stay inside what DateTimeOffset can hold, so later arithmetic cannot overflow or throw.
        if (!Limits.IsTime(payload.Iat) || !Limits.IsTime(payload.Exp) || !Limits.IsDuration(payload.Grace) || !Limits.IsDuration(payload.Refresh))
        {
            return false;
        }

        if (
            !Limits.IsDuration(payload.Lease)
            || !Limits.IsDuration(payload.Borrow)
            || payload.Overage is < 0 or > 1000
            || payload.Brl is < 0 or > Limits.MaxBorrowReturns
        )
        {
            return false;
        }

        if (payload.Clone is not (null or "deny" or "flag"))
        {
            return false;
        }

        if (payload.Fp is { Count: > 0 } && payload.Fpm is not ("any" or "all" or "ignore"))
        {
            return false;
        }

        if (
            kind == LicenseKind.Server
            && (
                payload.Lease is null or < 1
                || payload.Borrow is null or < 0
                || payload.Overage is null or < 0
                || payload.Cnf is null
                || payload.Clone is null
                || payload.Brl is null
            )
        )
        {
            return false;
        }

        if (kind == LicenseKind.Trial && payload.Grace is > 0)
        {
            return false;
        }

        return true;
    }
}
