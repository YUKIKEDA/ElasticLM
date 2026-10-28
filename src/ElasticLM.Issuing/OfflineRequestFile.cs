using System.Security.Cryptography;
using ElasticLM.Licensing;

namespace ElasticLM.Issuing;

/// <summary>
/// An offline request file as the service receives it (design §8.4.4, §8.4.5, §12.8.4): an <c>elm-actreq+jws</c> whose
/// signature is checked with the key it carries, or an <c>elm-deact+jws</c> that the caller checks with the activation's key.
/// The payload's claims are the caller's to read.
/// </summary>
public sealed class OfflineRequestFile
{
    private readonly CompactJws _jws;

    private OfflineRequestFile(CompactJws jws, bool deactivation, ECParameters? key)
    {
        _jws = jws;
        IsDeactivation = deactivation;
        Key = key;
    }

    /// <summary>Gets a value indicating whether the file is a deactivation proof (<c>elm-deact+jws</c>).</summary>
    public bool IsDeactivation { get; }

    /// <summary>Gets the header's key ID: the thumbprint of the installation key that signed the file.</summary>
    public string KeyId => _jws.Header.Kid;

    /// <summary>Gets the public key of an activation request, checked against its signature; <see langword="null"/> for a
    /// deactivation proof.</summary>
    public ECParameters? Key { get; }

    /// <summary>Gets the payload's JSON bytes.</summary>
    public ReadOnlyMemory<byte> Payload => _jws.Payload;

    /// <summary>
    /// Returns the SHA-256 of the signing input (<c>header.payload</c>), which names the request: an ECDSA signature changes
    /// each time it is made and can be altered by anyone, so it is never compared (design §8.2).
    /// </summary>
    /// <returns>The 32-byte hash.</returns>
    public byte[] SigningInputHash() => SHA256.HashData(_jws.SigningInput);

    /// <summary>
    /// Reads a request file. An activation request's signature is verified with the payload's <c>jwk</c>, whose thumbprint
    /// must be the header's <c>kid</c> (design §8.2 rule 6, exception); a deactivation proof is only parsed.
    /// </summary>
    /// <param name="text">The file's text: a compact JWS, without the white space around it.</param>
    /// <param name="file">The file, when it was read.</param>
    /// <param name="error"><c>ELM_MALFORMED</c> for a text that is neither type, <c>ELM_PROOF_INVALID</c> for an activation
    /// request whose signature does not verify.</param>
    /// <returns>Whether the file was read.</returns>
    public static bool TryRead(string text, out OfflineRequestFile? file, out LicenseError? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        file = null;
        if (CompactJws.TryParse(text, TokenTypes.ActivationRequest, out CompactJws? request, out _))
        {
            if (!request!.TryVerifyWithEmbeddedKey(out ECParameters key))
            {
                error = new LicenseError(ErrorCode.ProofInvalid, "The request's signature does not verify with its key.");
                return false;
            }

            file = new OfflineRequestFile(request, false, key);
            error = null;
            return true;
        }

        if (CompactJws.TryParse(text, TokenTypes.DeactivationProof, out CompactJws? proof, out _))
        {
            file = new OfflineRequestFile(proof!, true, null);
            error = null;
            return true;
        }

        error = new LicenseError(ErrorCode.Malformed, "The file is not an elm-actreq+jws or elm-deact+jws.");
        return false;
    }

    /// <summary>
    /// Verifies the signature with a known public key, such as an activation's installation key. The key's thumbprint must be
    /// the header's <c>kid</c>.
    /// </summary>
    /// <param name="key">The public key.</param>
    /// <returns>Whether the file is signed by that key.</returns>
    public bool Verify(ECParameters key)
    {
        if (!VerificationKey.TryCreate(key, out VerificationKey? verifier))
        {
            return false;
        }

        using (verifier)
        {
            return verifier!.KeyId == KeyId && verifier.Verify(_jws);
        }
    }
}
