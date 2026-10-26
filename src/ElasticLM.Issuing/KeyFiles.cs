using System.Security.Cryptography;
using ElasticLM.Licensing;

namespace ElasticLM.Issuing;

/// <summary>
/// Creates, protects, and reads the P-256 keys of the issuing side (design §7.2). Private keys leave memory only as
/// PKCS#8 encrypted with a passphrase (PBES2).
/// </summary>
public static class KeyFiles
{
    /// <summary>The shortest passphrase accepted for a key file.</summary>
    public const int MinPassphraseLength = 12;

    // PBKDF2-HMAC-SHA256 with 600,000 iterations, the OWASP Password Storage Cheat Sheet figure for that hash, and AES-256-CBC.
    private static readonly PbeParameters Protection = new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);

    /// <summary>
    /// Creates a new P-256 key.
    /// </summary>
    /// <returns>The key. The caller disposes it.</returns>
    public static ECDsa CreateKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>
    /// Exports a private key as an <c>ENCRYPTED PRIVATE KEY</c> PEM block.
    /// </summary>
    /// <param name="key">The key with its private part.</param>
    /// <param name="passphrase">The passphrase, at least <see cref="MinPassphraseLength"/> characters.</param>
    /// <returns>The PEM text.</returns>
    /// <exception cref="ArgumentException">The passphrase is shorter than <see cref="MinPassphraseLength"/>, or the key is not P-256.</exception>
    public static string ExportEncrypted(ECDsa key, ReadOnlySpan<char> passphrase)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (passphrase.Length < MinPassphraseLength)
        {
            throw new ArgumentException($"The passphrase must have at least {MinPassphraseLength} characters.", nameof(passphrase));
        }

        if (!EcKeys.IsP256(key.ExportParameters(false).Curve))
        {
            throw new ArgumentException("The key is not a P-256 key.", nameof(key));
        }

        return key.ExportEncryptedPkcs8PrivateKeyPem(passphrase, Protection);
    }

    /// <summary>
    /// Reads a private key from an <c>ENCRYPTED PRIVATE KEY</c> PEM block.
    /// </summary>
    /// <param name="pem">The PEM text.</param>
    /// <param name="passphrase">The passphrase.</param>
    /// <param name="key">The key, which the caller disposes; <see langword="null"/> on failure.</param>
    /// <returns><see langword="false"/> when the text is not one encrypted P-256 private key or the passphrase is wrong.</returns>
    public static bool TryImportEncrypted(string pem, ReadOnlySpan<char> passphrase, out ECDsa? key)
    {
        ArgumentNullException.ThrowIfNull(pem);
        key = null;

        // ImportFromEncryptedPem reads only ENCRYPTED PRIVATE KEY blocks, so a plain private key or a public key is rejected
        // (KeyFiles_EncryptedRoundTrip_NeedsThePassphrase).
        ECDsa candidate = ECDsa.Create();
        try
        {
            candidate.ImportFromEncryptedPem(pem, passphrase);
        }
        catch (CryptographicException)
        {
            candidate.Dispose();
            return false;
        }
        catch (ArgumentException)
        {
            candidate.Dispose();
            return false;
        }

        if (!EcKeys.IsP256(candidate.ExportParameters(false).Curve))
        {
            candidate.Dispose();
            return false;
        }

        key = candidate;
        return true;
    }

    /// <summary>
    /// Exports the public part of a key as a <c>PUBLIC KEY</c> PEM block, the form <see cref="TrustStore.Create"/> reads.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The PEM text.</returns>
    public static string ExportPublic(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.ExportSubjectPublicKeyInfoPem();
    }

    /// <summary>
    /// Returns the key ID of a key: its JWK thumbprint (design §7.4).
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The key ID.</returns>
    public static string KeyId(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return EcPublicJwk.Thumbprint(key.ExportParameters(false));
    }

    /// <summary>
    /// Reads a public key from a PEM text with one SubjectPublicKeyInfo block.
    /// </summary>
    /// <param name="pem">The PEM text.</param>
    /// <param name="key">The key, which the caller disposes; <see langword="null"/> on failure.</param>
    /// <returns><see langword="false"/> when the text is not one P-256 public key.</returns>
    public static bool TryImportPublic(string pem, out ECDsa? key)
    {
        ArgumentNullException.ThrowIfNull(pem);
        key = null;
        if (!EcKeys.TryReadPublicKeyPem(pem, out ECParameters parameters) || !EcKeys.TryCreateVerifier(parameters, out ECDsa? created))
        {
            return false;
        }

        key = created;
        return true;
    }
}
