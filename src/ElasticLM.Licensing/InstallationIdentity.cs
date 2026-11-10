namespace ElasticLM.Licensing;

// design §10, §11.1.

/// <summary>
/// Identifies this installation for node-locked licenses: the installation key thumbprint and the current machine
/// fingerprints.
/// </summary>
public sealed class InstallationIdentity
{
    // design §7.4.

    /// <summary>
    /// Initializes a new instance of the <see cref="InstallationIdentity"/> class.
    /// </summary>
    /// <param name="keyThumbprint">The JWK thumbprint of the installation key.</param>
    /// <param name="fingerprints">The current machine fingerprints, from <see cref="MachineFingerprint.Compute"/>. May be empty.</param>
    /// <exception cref="ArgumentException"><paramref name="keyThumbprint"/> is empty.</exception>
    public InstallationIdentity(string keyThumbprint, IReadOnlyList<string> fingerprints)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyThumbprint);
        ArgumentNullException.ThrowIfNull(fingerprints);
        KeyThumbprint = keyThumbprint;
        Fingerprints = fingerprints;
    }

    /// <summary>
    /// Gets the JWK thumbprint of the installation key.
    /// </summary>
    public string KeyThumbprint { get; }

    /// <summary>
    /// Gets the current machine fingerprints.
    /// </summary>
    public IReadOnlyList<string> Fingerprints { get; }

    // design §13.5.

    /// <summary>
    /// Gets a value indicating whether the license's fingerprints are compared. A client that verifies a server's license
    /// knows the server's key but not the server machine's fingerprints; the server checks those itself.
    /// </summary>
    internal bool ChecksFingerprints { get; private init; } = true;

    /// <summary>
    /// Returns the identity of another machine's server, known by its key only.
    /// </summary>
    internal static InstallationIdentity ForRemoteServer(string keyThumbprint) => new(keyThumbprint, []) { ChecksFingerprints = false };
}
