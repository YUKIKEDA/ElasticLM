using System.Text.Json;

namespace ElasticLM.Issuing;

/// <summary>
/// What a signed log writes on the first line of each file, and on a key change, so that its signatures can be verified
/// (design §18.1): an on-premises server's license and public key, or the certificates of <c>elm-service</c>'s signing key
/// (<c>use: log</c>, decision 0.95).
/// </summary>
public sealed class LogKeyMaterial
{
    private readonly byte[]? _licenseFile;
    private readonly byte[]? _keyJwk;
    private readonly string[]? _certificates;
    private readonly string? _node;

    private LogKeyMaterial(byte[]? licenseFile, byte[]? keyJwk, string[]? certificates, string? node)
    {
        _licenseFile = licenseFile;
        _keyJwk = keyJwk;
        _certificates = certificates;
        _node = node;
    }

    /// <summary>
    /// Returns the material of an on-premises server: its server license file and its public key as a JWK.
    /// </summary>
    /// <param name="licenseFile">The server license file.</param>
    /// <param name="keyJwk">The server's public key as a JWK.</param>
    /// <returns>The material.</returns>
    public static LogKeyMaterial ServerLicense(byte[] licenseFile, byte[] keyJwk)
    {
        ArgumentNullException.ThrowIfNull(licenseFile);
        ArgumentNullException.ThrowIfNull(keyJwk);
        return new LogKeyMaterial(licenseFile, keyJwk, null, null);
    }

    /// <summary>
    /// Returns the material of <c>elm-service</c>: the certificates of its signing key, one of which allows <c>log</c>, and
    /// the node that writes the log, so that a file of another node cannot be put into its chain (decision 0.95).
    /// </summary>
    /// <param name="certificates">The certificates (<c>elm-cert+jws</c>), one to four.</param>
    /// <param name="node">The node's name, written as <c>node</c> on the first line of each file, or <see langword="null"/>.</param>
    /// <returns>The material.</returns>
    /// <exception cref="ArgumentException">There are no certificates, more than four, or an empty one; or the node is empty.</exception>
    public static LogKeyMaterial Certificates(IReadOnlyList<string> certificates, string? node = null)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        if (certificates.Count is 0 or > 4 || certificates.Any(string.IsNullOrEmpty) || node is { Length: 0 })
        {
            throw new ArgumentException("A log needs one to four certificates, none empty, and a node that is not empty.", nameof(certificates));
        }

        return new LogKeyMaterial(null, null, [.. certificates], node);
    }

    internal void Write(Utf8JsonWriter writer)
    {
        if (_certificates is not null)
        {
            if (_node is not null)
            {
                writer.WriteString("node", _node);
            }

            writer.WriteStartArray("certificates");
            foreach (string certificate in _certificates)
            {
                writer.WriteStringValue(certificate);
            }

            writer.WriteEndArray();
            return;
        }

        writer.WritePropertyName("license");
        writer.WriteRawValue(_licenseFile!);
        writer.WritePropertyName("key");
        writer.WriteRawValue(_keyJwk!);
    }
}
