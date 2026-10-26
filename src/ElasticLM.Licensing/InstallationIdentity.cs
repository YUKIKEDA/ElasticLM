namespace ElasticLM.Licensing;

/// <summary>
/// Identifies this installation for node-locked licenses: the installation key thumbprint and the current machine
/// fingerprints (design §10, §11.1).
/// </summary>
public sealed class InstallationIdentity
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InstallationIdentity"/> class.
    /// </summary>
    /// <param name="keyThumbprint">The JWK thumbprint of the installation key (design §7.4).</param>
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
}
