using System.Security.Cryptography;
using ElasticLM.Licensing;

namespace ElasticLM.Issuing;

/// <summary>
/// What a key certificate allows its key to sign (design §8.4.1).
/// </summary>
[Flags]
public enum KeyUses
{
    /// <summary>Nothing. Not a valid certificate use.</summary>
    None = 0,

    /// <summary>License tokens.</summary>
    License = 1,

    /// <summary>Lease tokens.</summary>
    Lease = 2,

    /// <summary>Log signatures.</summary>
    Log = 4,
}

/// <summary>
/// Issues key certificates with a root key (design §7.1, §8.4.1).
/// </summary>
public static class CertificateIssuer
{
    /// <summary>The default validity of a certificate.</summary>
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromDays(365);

    /// <summary>The longest validity of a certificate.</summary>
    public static readonly TimeSpan MaxValidity = TimeSpan.FromDays(731);

    /// <summary>
    /// Issues a certificate for a signing key.
    /// </summary>
    /// <param name="rootKey">The root key with its private part.</param>
    /// <param name="signingPublicKey">The signing key to certify (its public part is used).</param>
    /// <param name="uses">What the signing key may sign.</param>
    /// <param name="notBefore">The start of the validity period.</param>
    /// <param name="validity">The length of the validity period, at most <see cref="MaxValidity"/>.</param>
    /// <param name="time">The time provider, for the issue time.</param>
    /// <returns>The certificate as an <c>elm-cert+jws</c> token.</returns>
    /// <exception cref="ArgumentException">No use is given, the validity is not positive or longer than <see cref="MaxValidity"/>, the period lies outside the times of design §8.6, or a key is not P-256.</exception>
    public static string Issue(ECDsa rootKey, ECDsa signingPublicKey, KeyUses uses, DateTimeOffset notBefore, TimeSpan validity, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(signingPublicKey);
        ArgumentNullException.ThrowIfNull(time);
        if (uses == KeyUses.None || (uses & ~(KeyUses.License | KeyUses.Lease | KeyUses.Log)) != 0)
        {
            throw new ArgumentException("At least one known use is required.", nameof(uses));
        }

        if (validity <= TimeSpan.Zero || validity > MaxValidity)
        {
            throw new ArgumentException("The validity must be positive and at most two years.", nameof(validity));
        }

        long notBeforeSeconds = notBefore.ToUnixTimeSeconds();
        if (!Limits.IsTime(notBeforeSeconds) || !Limits.IsTime(notBeforeSeconds + (long)validity.TotalSeconds))
        {
            throw new ArgumentException("The validity period must lie between 1970 and 9999 (design §8.6).", nameof(notBefore));
        }

        ECParameters signing = signingPublicKey.ExportParameters(false);
        if (!EcKeys.IsP256(signing.Curve) || !EcKeys.IsP256(rootKey.ExportParameters(false).Curve))
        {
            throw new ArgumentException("Both keys must be P-256 keys.", nameof(signingPublicKey));
        }

        var useList = new List<string>(3);
        if (uses.HasFlag(KeyUses.License))
        {
            useList.Add("license");
        }

        if (uses.HasFlag(KeyUses.Lease))
        {
            useList.Add("lease");
        }

        if (uses.HasFlag(KeyUses.Log))
        {
            useList.Add("log");
        }

        var payload = new KeyCertificatePayload
        {
            Ver = 1,
            Iat = time.GetUtcNow().ToUnixTimeSeconds(),
            Jti = JwsWriter.NewTokenId(),
            Sub = EcPublicJwk.Thumbprint(signing),
            Jwk = EcPublicJwk.FromParameters(signing),
            Use = useList,
            Nbf = notBefore.ToUnixTimeSeconds(),
            Exp = (notBefore + validity).ToUnixTimeSeconds(),
        };
        return JwsWriter.Sign(rootKey, TokenTypes.KeyCertificate, payload, LicensingJsonContext.Default.KeyCertificatePayload);
    }
}
