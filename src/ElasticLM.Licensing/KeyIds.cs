using System.Security.Cryptography;

namespace ElasticLM.Licensing;

/// <summary>
/// Computes key IDs: the RFC 7638 JWK thumbprint of a P-256 public key (design §7.4). Licenses and leases name an
/// installation key by this value in <c>cnf.jkt</c>.
/// </summary>
public static class KeyIds
{
    /// <summary>
    /// Returns the key ID of a P-256 key.
    /// </summary>
    /// <param name="key">The key; only its public part is used.</param>
    /// <returns>The key ID, base64url.</returns>
    /// <exception cref="ArgumentException">The key is not a P-256 key.</exception>
    public static string Of(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ECParameters parameters = key.ExportParameters(false);
        return EcKeys.IsP256(parameters.Curve)
            ? EcPublicJwk.Thumbprint(parameters)
            : throw new ArgumentException("The key is not a P-256 key.", nameof(key));
    }
}
