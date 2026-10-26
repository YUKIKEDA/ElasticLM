using System.Security.Cryptography;

namespace ElasticLM.Licensing;

/// <summary>
/// A P-256 public key kept as one <see cref="ECDsa"/> object for repeated verification. Creating the object costs about
/// 275 µs under NativeAOT on Windows, against about 110 µs for one verification (measured for M1, Issue #7), so keys that
/// verify many tokens are created once. The documentation does not promise that one <see cref="ECDsa"/> may verify on
/// several threads at once, so verification takes a lock.
/// </summary>
internal sealed class VerificationKey
{
    private readonly ECDsa _key;
    private readonly Lock _lock = new();

    private VerificationKey(ECDsa key, string keyId)
    {
        _key = key;
        KeyId = keyId;
    }

    public string KeyId { get; }

    public static bool TryCreate(ECParameters parameters, out VerificationKey? key)
    {
        key = null;
        if (!EcKeys.TryCreateVerifier(parameters, out ECDsa? ecdsa))
        {
            return false;
        }

        key = new VerificationKey(ecdsa!, EcPublicJwk.Thumbprint(parameters));
        return true;
    }

    public bool Verify(CompactJws jws)
    {
        lock (_lock)
        {
            return jws.Verify(_key);
        }
    }
}
