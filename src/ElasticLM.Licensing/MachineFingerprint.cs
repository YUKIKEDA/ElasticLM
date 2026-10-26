using System.Security.Cryptography;
using System.Text;

namespace ElasticLM.Licensing;

/// <summary>
/// Computes machine fingerprints: OS machine IDs hashed with a per-product value, so the raw IDs never leave the machine
/// and products cannot track a machine across each other (design §10.3).
/// </summary>
public static class MachineFingerprint
{
    private static readonly Lazy<IReadOnlyList<byte[]>> OsIds = new(MachineIdSources.ReadAll);

    /// <summary>
    /// Returns the fingerprints of this machine for a product. The OS IDs are read once per process. The list is empty when
    /// no OS ID can be read (for example, a container without <c>/etc/machine-id</c>).
    /// </summary>
    /// <param name="productId">The product ID.</param>
    /// <returns>The fingerprints, 32 lowercase hex characters each.</returns>
    /// <exception cref="ArgumentException"><paramref name="productId"/> is empty.</exception>
    public static IReadOnlyList<string> Compute(string productId)
    {
        ArgumentException.ThrowIfNullOrEmpty(productId);
        IReadOnlyList<byte[]> ids = OsIds.Value;
        var result = new List<string>(ids.Count);
        foreach (byte[] id in ids)
        {
            result.Add(Derive(id, productId));
        }

        return result;
    }

    /// <summary>
    /// Derives a fingerprint from a 16-byte OS ID: the first 16 bytes of HMAC-SHA256 keyed by the OS ID over the first 16
    /// bytes of SHA-256 of the product ID, with the UUID version 4 bits set. This is the construction of systemd's
    /// <c>sd_id128_get_machine_app_specific</c>.
    /// </summary>
    /// <param name="osId">The 16-byte OS ID.</param>
    /// <param name="productId">The product ID.</param>
    /// <returns>The fingerprint, 32 lowercase hex characters.</returns>
    /// <exception cref="ArgumentException"><paramref name="osId"/> is not 16 bytes, or <paramref name="productId"/> is empty.</exception>
    internal static string Derive(ReadOnlySpan<byte> osId, string productId)
    {
        if (osId.Length != 16)
        {
            throw new ArgumentException("The OS ID must be 16 bytes.", nameof(osId));
        }

        ArgumentException.ThrowIfNullOrEmpty(productId);
        Span<byte> appId = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(productId), appId);
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(osId, appId[..16], mac);
        Span<byte> id = mac[..16];
        id[6] = (byte)((id[6] & 0x0F) | 0x40);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);
        return Convert.ToHexStringLower(id);
    }
}
