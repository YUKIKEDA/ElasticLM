using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ElasticLM.Licensing;

/// <summary>
/// A parsed JWS in compact serialization whose signature has not been verified yet (design §8.2). The payload is kept as
/// bytes and is not interpreted as JSON until <see cref="Verify"/> succeeds.
/// </summary>
internal sealed class CompactJws
{
    /// <summary>The only algorithm in v1.</summary>
    public const string Es256 = "ES256";

    private CompactJws(JwsHeader header, byte[] signingInput, byte[] payload, byte[] signature)
    {
        Header = header;
        SigningInput = signingInput;
        Payload = payload;
        Signature = signature;
    }

    public JwsHeader Header { get; }

    /// <summary>Gets the ASCII bytes of <c>header.payload</c> as received.</summary>
    public byte[] SigningInput { get; }

    /// <summary>Gets the decoded payload bytes. Do not parse them before <see cref="Verify"/> succeeds.</summary>
    public byte[] Payload { get; }

    public byte[] Signature { get; }

    /// <summary>
    /// Parses a compact JWS and checks the header against rules 1 to 4 of design §8.2: three base64url segments, only
    /// <c>alg</c>, <c>kid</c>, <c>typ</c>, <c>alg</c> is <c>ES256</c>, and <c>typ</c> is the one the caller expects.
    /// </summary>
    public static bool TryParse(string text, string expectedTyp, out CompactJws? jws, out LicenseError? error)
    {
        jws = null;
        if (text.Length > Limits.MaxTokenLength)
        {
            error = new LicenseError(ErrorCode.Malformed, "The token is longer than the limit.");
            return false;
        }

        int first = text.IndexOf('.', StringComparison.Ordinal);
        int second = first < 0 ? -1 : text.IndexOf('.', first + 1);
        if (first < 0 || second < 0 || text.IndexOf('.', second + 1) >= 0)
        {
            error = new LicenseError(ErrorCode.Malformed, "The token is not a JWS in compact serialization.");
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan();
        if (
            !Base64UrlCodec.TryDecode(span[..first], out byte[] headerBytes)
            || !Base64UrlCodec.TryDecode(span[(first + 1)..second], out byte[] payload)
            || !Base64UrlCodec.TryDecode(span[(second + 1)..], out byte[] signature)
        )
        {
            error = new LicenseError(ErrorCode.Malformed, "A token segment is not canonical base64url.");
            return false;
        }

        JwsHeader? header;
        try
        {
            header = JsonSerializer.Deserialize(headerBytes, LicensingJsonContext.Default.JwsHeader);
        }
        catch (JsonException)
        {
            header = null;
        }

        if (header is null)
        {
            error = new LicenseError(ErrorCode.Malformed, "The token header must hold exactly alg, kid, and typ.");
            return false;
        }

        if (header.Alg != Es256)
        {
            error = new LicenseError(ErrorCode.BadSignature, "The token algorithm is not ES256.");
            return false;
        }

        if (header.Typ != expectedTyp)
        {
            error = new LicenseError(ErrorCode.Malformed, "The token type is not the expected one.");
            return false;
        }

        if (header.Kid.Length == 0)
        {
            error = new LicenseError(ErrorCode.Malformed, "The token key ID is empty.");
            return false;
        }

        jws = new CompactJws(header, Encoding.ASCII.GetBytes(text, 0, second), payload, signature);
        error = null;
        return true;
    }

    /// <summary>
    /// Verifies the ES256 signature (IEEE P1363, 64 bytes) over the received <c>header.payload</c> bytes (design §8.2 rules 5
    /// and 6). DER signatures are rejected by their length.
    /// </summary>
    public bool Verify(ECDsa key) => VerifyEs256(SigningInput, Signature, key);

    /// <summary>
    /// Verifies an ES256 signature in IEEE P1363 form (r || s, 64 bytes) over the signing input (RFC 7518 §3.4).
    /// <see cref="ECDsa.VerifyData(byte[], byte[], HashAlgorithmName, DSASignatureFormat)"/> returns false for a signature of
    /// another length, a DER signature included (Verify_JwsRuleBroken_IsRejected, der-signature).
    /// </summary>
    internal static bool VerifyEs256(byte[] signingInput, byte[] signature, ECDsa key) =>
        key.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>
    /// Verifies the signature with the public key carried in the payload's <c>jwk</c> member. This is the exception of design
    /// §8.2 rule 6 for <c>elm-proof+jws</c> and <c>elm-actreq+jws</c> when the verifier does not know the key yet: only
    /// <c>jwk</c> is read before the signature is verified, its thumbprint must equal the header <c>kid</c>, and the point must
    /// be on the curve.
    /// </summary>
    public bool TryVerifyWithEmbeddedKey(out ECParameters key)
    {
        if (!TryReadEmbeddedKey(out key) || !EcKeys.TryCreateVerifier(key, out ECDsa? verifier))
        {
            key = default;
            return false;
        }

        using (verifier)
        {
            if (Verify(verifier!))
            {
                return true;
            }
        }

        key = default;
        return false;
    }

    /// <summary>
    /// Reads the payload's <c>jwk</c> without verifying the signature, and checks that its thumbprint is the header
    /// <c>kid</c>. Only <c>jwk</c> is read (design §8.2 rule 6, exception); the caller verifies the signature next.
    /// </summary>
    public bool TryReadEmbeddedKey(out ECParameters key)
    {
        key = default;
        EmbeddedJwkPayload? embedded;
        try
        {
            embedded = JsonSerializer.Deserialize(Payload, LicensingJsonContext.Default.EmbeddedJwkPayload);
        }
        catch (JsonException)
        {
            return false;
        }

        if (embedded?.Jwk is null || !embedded.Jwk.TryGetParameters(out ECParameters parameters))
        {
            return false;
        }

        if (
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(EcPublicJwk.Thumbprint(parameters)), Encoding.ASCII.GetBytes(Header.Kid))
        )
        {
            return false;
        }

        key = parameters;
        return true;
    }
}
