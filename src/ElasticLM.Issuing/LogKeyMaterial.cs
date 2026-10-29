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

    private LogKeyMaterial(byte[]? licenseFile, byte[]? keyJwk, string[]? certificates)
    {
        _licenseFile = licenseFile;
        _keyJwk = keyJwk;
        _certificates = certificates;
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
        return new LogKeyMaterial(licenseFile, keyJwk, null);
    }

    /// <summary>
    /// Returns the material of <c>elm-service</c>: the certificates of its signing key, one of which allows <c>log</c>.
    /// </summary>
    /// <param name="certificates">The certificates (<c>elm-cert+jws</c>), one to four.</param>
    /// <returns>The material.</returns>
    /// <exception cref="ArgumentException">There are no certificates, more than four, or an empty one.</exception>
    public static LogKeyMaterial Certificates(IReadOnlyList<string> certificates)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        if (certificates.Count is 0 or > 4 || certificates.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException("A log needs one to four certificates, none empty.", nameof(certificates));
        }

        return new LogKeyMaterial(null, null, [.. certificates]);
    }

    internal void Write(Utf8JsonWriter writer)
    {
        if (_certificates is not null)
        {
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
