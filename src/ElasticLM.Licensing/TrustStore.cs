using System.Security.Cryptography;

namespace ElasticLM.Licensing;

/// <summary>
/// The root keys an app trusts, and the key IDs it has revoked (design §7.1, §7.5). Apps build it from keys compiled into
/// the app.
/// </summary>
public sealed class TrustStore
{
    private readonly Dictionary<string, VerificationKey> _roots;
    private readonly HashSet<string> _revoked;

    private TrustStore(Dictionary<string, VerificationKey> roots, HashSet<string> revoked)
    {
        _roots = roots;
        _revoked = revoked;
    }

    /// <summary>
    /// Gets the key IDs (JWK thumbprints, design §7.4) of the trusted root keys.
    /// </summary>
    public IReadOnlyCollection<string> RootKeyIds => _roots.Keys;

    /// <summary>
    /// Creates a trust store.
    /// </summary>
    /// <param name="rootPublicKeysPem">One or more P-256 public keys as PEM <c>PUBLIC KEY</c> blocks.</param>
    /// <param name="revokedKeyIds">Key IDs whose certificates and tokens are rejected (design §7.5).</param>
    /// <returns>The trust store.</returns>
    /// <exception cref="ArgumentException">No root key is given, or a key is not a P-256 public key in PEM.</exception>
    public static TrustStore Create(ReadOnlySpan<string> rootPublicKeysPem, ReadOnlySpan<string> revokedKeyIds)
    {
        if (rootPublicKeysPem.IsEmpty)
        {
            throw new ArgumentException("At least one root key is required.", nameof(rootPublicKeysPem));
        }

        var roots = new Dictionary<string, VerificationKey>(StringComparer.Ordinal);
        foreach (string pem in rootPublicKeysPem)
        {
            if (
                pem is null
                || !EcKeys.TryReadPublicKeyPem(pem, out ECParameters parameters)
                || !VerificationKey.TryCreate(parameters, out VerificationKey? key)
            )
            {
                throw new ArgumentException("A root key is not a P-256 public key in PEM.", nameof(rootPublicKeysPem));
            }

            roots[key!.KeyId] = key;
        }

        var revoked = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in revokedKeyIds)
        {
            ArgumentException.ThrowIfNullOrEmpty(id, nameof(revokedKeyIds));
            revoked.Add(id);
        }

        return new TrustStore(roots, revoked);
    }

    internal bool IsRevoked(string keyId) => _revoked.Contains(keyId);

    internal bool TryGetRoot(string keyId, out VerificationKey? key) => _roots.TryGetValue(keyId, out key);
}
