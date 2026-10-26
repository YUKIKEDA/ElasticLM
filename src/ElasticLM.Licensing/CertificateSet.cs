using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace ElasticLM.Licensing;

/// <summary>
/// A key certified by a root for one use, with the certificate's validity period (design §8.4.1).
/// </summary>
internal sealed record CertifiedKey(VerificationKey Key, long Nbf, long Exp);

/// <summary>
/// Verifies key certificates against a trust store for one use (<c>license</c> or <c>lease</c>), and keeps the ones that
/// verified (design §11.2 step 3).
/// </summary>
internal sealed class CertificateSet(TrustStore trust, string use)
{
    public const string UseLicense = "license";
    public const string UseLease = "lease";
    private const int MaxCachedCertificates = 16;

    private readonly TrustStore _trust = trust;
    private readonly string _use = use;

    // Certificates that verified, keyed by their exact text. The same bytes always give the same result for this set,
    // because the trust store cannot change. Bounded so that a stream of distinct certificates cannot grow it.
    private readonly ConcurrentDictionary<string, CertifiedKey> _certificates = new(StringComparer.Ordinal);

    /// <summary>Gets the number of certificates kept, for tests of the cache.</summary>
    internal int CachedCount => _certificates.Count;

    public List<CertifiedKey> CollectCandidates(List<string> certificates, string tokenKeyId, out List<CertifiedKey>? uncached)
    {
        uncached = null;
        var candidates = new List<CertifiedKey>(1);
        foreach (string text in certificates)
        {
            if (!_certificates.TryGetValue(text, out CertifiedKey? entry))
            {
                if (!TryVerifyCertificate(text, out entry))
                {
                    // A certificate that fails any check is skipped, so an old certificate kept for rotation cannot fail the file.
                    continue;
                }

                // The cache keeps the first certificates it sees and never evicts, so a cached key is never disposed while
                // another call uses it. Certificates beyond the limit are verified per call and disposed by the caller.
                if (_certificates.Count >= MaxCachedCertificates || !_certificates.TryAdd(text, entry!))
                {
                    (uncached ??= []).Add(entry!);
                }
            }

            if (entry!.Key.KeyId == tokenKeyId)
            {
                candidates.Add(entry);
            }
        }

        return candidates;
    }

    private bool TryVerifyCertificate(string text, out CertifiedKey? entry)
    {
        entry = null;
        if (!CompactJws.TryParse(text, TokenTypes.KeyCertificate, out CompactJws? jws, out _))
        {
            return false;
        }

        string rootId = jws!.Header.Kid;
        if (_trust.IsRevoked(rootId) || !_trust.TryGetRoot(rootId, out VerificationKey? root) || !root!.Verify(jws))
        {
            return false;
        }

        KeyCertificatePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(jws.Payload, LicensingJsonContext.Default.KeyCertificatePayload);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is null || payload.Ver != 1 || _trust.IsRevoked(payload.Sub))
        {
            return false;
        }

        if (!Limits.IsTime(payload.Nbf) || !Limits.IsTime(payload.Exp) || !Limits.IsTime(payload.Iat))
        {
            return false;
        }

        if (!payload.Use.Contains(_use, StringComparer.Ordinal) || !payload.Jwk.TryGetParameters(out ECParameters parameters))
        {
            return false;
        }

        if (!VerificationKey.TryCreate(parameters, out VerificationKey? key) || key!.KeyId != payload.Sub)
        {
            return false;
        }

        entry = new CertifiedKey(key, payload.Nbf, payload.Exp);
        return true;
    }
}
