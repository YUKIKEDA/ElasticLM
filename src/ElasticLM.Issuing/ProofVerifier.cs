using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.Issuing;

/// <summary>
/// The outcome of a proof check: either <see cref="Valid"/> or <see cref="Invalid"/>.
/// </summary>
public abstract record ProofResult
{
    private ProofResult() { }

    /// <summary>
    /// The proof is valid. Its <c>jti</c> has been recorded.
    /// </summary>
    /// <param name="KeyId">The installation key thumbprint of the caller.</param>
    /// <param name="Key">The caller's public key.</param>
    /// <param name="ProofId">The proof's <c>jti</c>. A server keeps it with what the request produced, so a retry that names
    /// it can be told from a clone (design §10.4).</param>
    public sealed record Valid(string KeyId, ECParameters Key, string ProofId) : ProofResult;

    /// <summary>
    /// The proof is invalid.
    /// </summary>
    /// <param name="Error">The error: <c>ELM_PROOF_INVALID</c>, <c>ELM_NONCE_REQUIRED</c>, <c>ELM_REPLAYED</c>, or <c>ELM_SERVER_BUSY</c>.</param>
    public sealed record Invalid(LicenseError Error) : ProofResult;
}

/// <summary>
/// Checks the <c>ELM-Proof</c> of a request (design §12.2): the signature with the caller's installation key, the method,
/// the normalized target URI, the body digest, the server's nonce, and a <c>jti</c> used once.
/// </summary>
public sealed class ProofVerifier
{
    private const int MaxJtiLength = 64;
    private const int MaxCachedKeys = 10_000;

    private readonly NonceService _nonces;
    private readonly IReplayStore _replays;

    // Keys that verified a proof, by kid. Creating an ECDsa costs about 275 µs against 110 µs for a verification (M1), and a
    // client sends a proof with every request. Never evicted, so a cached key is never disposed while another call uses it.
    private readonly ConcurrentDictionary<string, CachedKey> _keys = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="ProofVerifier"/> class.
    /// </summary>
    /// <param name="nonces">The nonce service.</param>
    /// <param name="replays">The store of used <c>jti</c> values.</param>
    public ProofVerifier(NonceService nonces, IReplayStore replays)
    {
        ArgumentNullException.ThrowIfNull(nonces);
        ArgumentNullException.ThrowIfNull(replays);
        _nonces = nonces;
        _replays = replays;
    }

    /// <summary>
    /// Checks a proof. The <c>jti</c> is recorded only when every other check passes.
    /// </summary>
    /// <param name="proof">The <c>ELM-Proof</c> header value, or <see langword="null"/> when absent.</param>
    /// <param name="method">The request method.</param>
    /// <param name="target">The request target as the server's public URL names it (design §12.1); it is normalized here.</param>
    /// <param name="body">The request body bytes as received.</param>
    /// <param name="knownKey">The public key of the <c>kid</c>, when the server already knows the caller; otherwise the
    /// proof must carry its key in <c>jwk</c> (design §8.2 rule 6, exception).</param>
    /// <returns>The result.</returns>
    public ProofResult Verify(string? proof, string method, Uri target, ReadOnlySpan<byte> body, Func<string, ECParameters?>? knownKey = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Verify(proof, method, [target], body, knownKey);
    }

    /// <summary>
    /// Verifies a request proof whose <c>htu</c> may be any of several URIs: a server reachable under several public URLs
    /// (design §12.1, §13.8) accepts each.
    /// </summary>
    /// <param name="proof">The <c>ELM-Proof</c> header value.</param>
    /// <param name="method">The request method.</param>
    /// <param name="targets">The request URI under each public URL; at least one.</param>
    /// <param name="body">The request body bytes, empty when there is none.</param>
    /// <param name="knownKey">Returns the public key for a key ID the server already knows, or <see langword="null"/>.</param>
    /// <returns>The key of the proof, or why it was refused.</returns>
    public ProofResult Verify(
        string? proof,
        string method,
        IReadOnlyList<Uri> targets,
        ReadOnlySpan<byte> body,
        Func<string, ECParameters?>? knownKey = null
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            throw new ArgumentException("At least one target URI is required.", nameof(targets));
        }

        if (string.IsNullOrEmpty(proof) || !CompactJws.TryParse(proof, TokenTypes.Proof, out CompactJws? jws, out _))
        {
            return Invalid(ErrorCode.ProofInvalid, "The request proof is missing or not a valid elm-proof+jws.");
        }

        if (!TryVerifySignature(jws!, knownKey, out ECParameters key))
        {
            return Invalid(ErrorCode.ProofInvalid, "The request proof signature does not verify.");
        }

        ProofPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(jws!.Payload, LicensingJsonContext.Default.ProofPayload);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null || payload.Ver != 1 || payload.Jti.Length is 0 or > MaxJtiLength || !Limits.IsTime(payload.Iat))
        {
            return Invalid(ErrorCode.ProofInvalid, "The request proof payload is not valid.");
        }

        if (payload.Htm != method || !targets.Any(t => payload.Htu == HttpTargetUri.Normalize(t)))
        {
            return Invalid(ErrorCode.ProofInvalid, "The request proof is for another method or URI.");
        }

        string digest = Base64UrlCodec.Encode(SHA256.HashData(body));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(payload.Bdy), Encoding.ASCII.GetBytes(digest)))
        {
            return Invalid(ErrorCode.ProofInvalid, "The request proof is for another body.");
        }

        // The proof's iat is not used for freshness: the client's clock may be off, and the nonce is the server's own clock.
        if (!_nonces.IsValid(payload.Nonce, out DateTimeOffset nonceExpires))
        {
            return Invalid(ErrorCode.NonceRequired, "The nonce is missing or expired; get a new one.");
        }

        return _replays.TryUse(payload.Jti, nonceExpires) switch
        {
            ReplayCheck.Accepted => new ProofResult.Valid(jws!.Header.Kid, key, payload.Jti),
            ReplayCheck.Replayed => Invalid(ErrorCode.Replayed, "The request proof was already used."),
            _ => Invalid(ErrorCode.ServerBusy, "The server cannot record more proofs now."),
        };
    }

    private static ProofResult.Invalid Invalid(ErrorCode code, string message) => new(new LicenseError(code, message));

    private bool TryVerifySignature(CompactJws jws, Func<string, ECParameters?>? knownKey, out ECParameters key)
    {
        string kid = jws.Header.Kid;
        if (_keys.TryGetValue(kid, out CachedKey? cached))
        {
            key = cached.Parameters;
            return cached.Key.Verify(jws);
        }

        if (knownKey?.Invoke(kid) is ECParameters known)
        {
            key = known;
        }
        else if (!jws.TryReadEmbeddedKey(out key))
        {
            return false;
        }

        // The kid is the key's thumbprint, so a key is cached under the one kid it can have.
        if (!VerificationKey.TryCreate(key, out VerificationKey? verifier) || verifier!.KeyId != kid || !verifier.Verify(jws))
        {
            verifier?.Dispose();
            return false;
        }

        if (_keys.Count >= MaxCachedKeys || !_keys.TryAdd(kid, new CachedKey(verifier, key)))
        {
            verifier.Dispose();
        }

        return true;
    }

    private sealed record CachedKey(VerificationKey Key, ECParameters Parameters);
}
