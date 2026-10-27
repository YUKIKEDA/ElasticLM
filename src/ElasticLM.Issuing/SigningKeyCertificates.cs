using System.Security.Cryptography;
using ElasticLM.Licensing;

namespace ElasticLM.Issuing;

/// <summary>
/// What the certificates of a service's signing key allow at a given time (design §14.8, decision 0.75).
/// </summary>
/// <param name="KeyId">The signing key's ID.</param>
/// <param name="Uses">The uses that a certificate valid at that time allows.</param>
/// <param name="ExpiresAt">The earliest end among the certificates chosen for the required uses.</param>
public sealed record SigningKeyStatus(string KeyId, KeyUses Uses, DateTimeOffset ExpiresAt);

/// <summary>
/// Checks a signing key's certificates against the vendor's trust store before a service signs with the key.
/// </summary>
public static class SigningKeyCertificates
{
    /// <summary>
    /// Checks that, for each required use, a certificate of the key that the trust store vouches for covers the time.
    /// </summary>
    /// <param name="signingKey">The signing key.</param>
    /// <param name="certificates">Its certificates (<c>elm-cert+jws</c>).</param>
    /// <param name="trust">The vendor's trust store.</param>
    /// <param name="required">The uses the key must have, such as license and lease.</param>
    /// <param name="now">The time to check.</param>
    /// <param name="status">The key's status when the check passes.</param>
    /// <param name="problem">Why it fails, in one sentence, otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when every required use is covered at <paramref name="now"/>.</returns>
    public static bool TryCheck(
        ECDsa signingKey,
        IReadOnlyList<string> certificates,
        TrustStore trust,
        KeyUses required,
        DateTimeOffset now,
        out SigningKeyStatus? status,
        out string? problem
    )
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(trust);
        status = null;
        string keyId = KeyIds.Of(signingKey);
        long seconds = now.ToUnixTimeSeconds();
        KeyUses covered = KeyUses.None;
        long? earliestEnd = null;
        foreach ((KeyUses use, string name) in Uses)
        {
            long? end = null;
            foreach (string certificate in certificates)
            {
                if (!CertificateSet.TryVerify(trust, name, certificate.Trim(), out CertifiedKey? entry))
                {
                    continue;
                }

                using (entry!.Key)
                {
                    if (entry.Key.KeyId == keyId && entry.Nbf <= seconds && seconds <= entry.Exp)
                    {
                        end = end is long known ? Math.Max(known, entry.Exp) : entry.Exp;
                    }
                }
            }

            if (end is long found)
            {
                covered |= use;
                if (required.HasFlag(use))
                {
                    earliestEnd = earliestEnd is long earliest ? Math.Min(earliest, found) : found;
                }
            }
        }

        KeyUses missing = required & ~covered;
        if (missing != KeyUses.None)
        {
            problem =
                $"No certificate that the trust store vouches for lets the signing key {keyId} sign for {missing} at {now:yyyy-MM-ddTHH:mm:ssZ}.";
            return false;
        }

        problem = null;
        status = new SigningKeyStatus(keyId, covered, DateTimeOffset.FromUnixTimeSeconds(earliestEnd ?? seconds));
        return true;
    }

    private static readonly (KeyUses Use, string Name)[] Uses =
    [
        (KeyUses.License, CertificateSet.UseLicense),
        (KeyUses.Lease, CertificateSet.UseLease),
        (KeyUses.Log, "log"),
    ];
}
