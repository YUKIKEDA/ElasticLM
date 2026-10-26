using System.Security.Cryptography;
using ElasticLM.Licensing;

namespace ElasticLM.Client;

/// <summary>
/// Where an installation key is kept: for the user, or for the whole machine (design §10.2).
/// </summary>
public enum KeyScope
{
    /// <summary>For the current user. Needs no administrator rights.</summary>
    User,

    /// <summary>For the machine, for apps that run as a service. Writing needs administrator rights.</summary>
    Machine,
}

/// <summary>
/// The key pair an installation creates on its first activation, lease, or trial: the installation's real identifier
/// (design §10.2). It signs request proofs.
/// </summary>
public sealed class InstallationKey : IDisposable
{
    private readonly ECDsa _key;
    private readonly Lock _lock = new();

    internal InstallationKey(ECDsa key)
    {
        _key = key;
        Thumbprint = KeyIds.Of(key);
    }

    /// <summary>Gets the key's JWK thumbprint (design §7.4), which licenses and leases bind to as <c>cnf.jkt</c>.</summary>
    public string Thumbprint { get; }

    /// <summary>
    /// Returns the identity the verifier compares with a node-locked license: this key's thumbprint and the machine fingerprints.
    /// </summary>
    /// <param name="fingerprints">The machine fingerprints, from <see cref="MachineFingerprint.Compute"/>.</param>
    /// <returns>The identity.</returns>
    public InstallationIdentity Identity(IReadOnlyList<string> fingerprints) => new(Thumbprint, fingerprints);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _key.Dispose();
        }
    }

    internal ECParameters PublicParameters() => _key.ExportParameters(false);

    internal byte[] Sign(byte[] data)
    {
        lock (_lock)
        {
            return _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
    }
}
