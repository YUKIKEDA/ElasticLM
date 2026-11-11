using System.Security.Cryptography;
using ElasticLM.Licensing;

namespace ElasticLM.Client;

// design §10.2.

/// <summary>
/// Where an installation key is kept: for the user, or for the whole machine.
/// </summary>
public enum KeyScope
{
    /// <summary>For the current user. Needs no administrator rights.</summary>
    User,

    /// <summary>For the machine, for apps that run as a service. Writing needs administrator rights.</summary>
    Machine,
}

// design §10.2.

/// <summary>
/// The key pair an installation creates on its first activation, lease, or trial: the installation's real identifier.
/// It signs request proofs.
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

    // design §7.4.

    /// <summary>Gets the key's JWK thumbprint, which licenses and leases bind to as <c>cnf.jkt</c>.</summary>
    public string Thumbprint { get; }

    /// <summary>
    /// Returns the identity the verifier compares with a node-locked license: this key's thumbprint and the machine fingerprints.
    /// </summary>
    /// <param name="fingerprints">The machine fingerprints, from <see cref="MachineFingerprint.Compute"/>.</param>
    /// <returns>The identity.</returns>
    public InstallationIdentity Identity(IReadOnlyList<string> fingerprints) => new(Thumbprint, fingerprints);

    // design §11.4 step 3.

    /// <summary>
    /// Returns a clock guard that keeps its state in a file and signs it with a copy of this key, as the
    /// clients and <c>elm-server</c> do. The guard owns the copy; dispose the guard to destroy it.
    /// </summary>
    /// <param name="path">The state file path, usually <c>clock</c> in the key's folder. The directory must exist.</param>
    /// <returns>The guard.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException">The key is disposed.</exception>
    public InstallationClockGuard CreateClockGuard(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new InstallationClockGuard(path, CopyForClockGuard());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _key.Dispose();
        }
    }

    internal ECParameters PublicParameters() => _key.ExportParameters(false);

    // design §11.4 step 3.

    /// <summary>
    /// Returns a copy of the key for the clock guard to sign its state with. The guard keeps its key, so
    /// it gets its own; the caller disposes it.
    /// </summary>
    internal ECDsa CopyForClockGuard() =>
        WithPrivateKey(k =>
        {
            ECParameters parameters = k.ExportParameters(true);
            try
            {
                return ECDsa.Create(parameters);
            }
            finally
            {
                // .cursor/rules/security.mdc: the exported private value does not stay in memory.
                CryptographicOperations.ZeroMemory(parameters.D);
            }
        });

    /// <summary>
    /// Runs an operation with the private key under the key's lock. The operation must not keep the key.
    /// </summary>
    internal T WithPrivateKey<T>(Func<ECDsa, T> use)
    {
        lock (_lock)
        {
            return use(_key);
        }
    }

    internal byte[] Sign(byte[] data)
    {
        lock (_lock)
        {
            return _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
    }
}
