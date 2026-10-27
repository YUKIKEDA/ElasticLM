using System.Security.Cryptography;
using System.Text;

namespace ElasticLM.Service;

/// <summary>
/// License keys (design §9.5): <c>ELM-</c> and four groups of five Crockford base32 characters, 100 random bits. Only the
/// SHA-256 of the key's normal form is stored; the normal form ignores case and <c>-</c>, as the design says input does.
/// </summary>
internal static class LicenseKeys
{
    // Crockford's base32: digits and letters without I, L, O, and U (https://www.crockford.com/base32.html).
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int Characters = 20;

    /// <summary>Makes a new key from 100 random bits.</summary>
    public static string NewKey() => Format(RandomNumberGenerator.GetBytes(13));

    /// <summary>
    /// Writes a key from random bytes: the first 100 of their bits, 5 per character, most significant first.
    /// </summary>
    /// <param name="random">At least 13 bytes (104 bits).</param>
    public static string Format(byte[] random)
    {
        ArgumentNullException.ThrowIfNull(random);
        ArgumentOutOfRangeException.ThrowIfLessThan(random.Length, 13);
        var text = new StringBuilder("ELM");
        for (int i = 0; i < Characters; i++)
        {
            if (i % 5 == 0)
            {
                text.Append('-');
            }

            text.Append(Alphabet[Bits(random, i * 5)]);
        }

        return text.ToString();
    }

    /// <summary>
    /// Returns the normal form of a key as typed: upper case, without <c>-</c>; or <see langword="null"/> when it is not a
    /// key.
    /// </summary>
    public static string? Normalize(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);
        string compact = typed.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        return compact.Length == 3 + Characters && compact.StartsWith("ELM", StringComparison.Ordinal) && compact[3..].All(Alphabet.Contains)
            ? compact
            : null;
    }

    /// <summary>Returns the hash a key is stored and looked up by: the SHA-256 of its normal form.</summary>
    /// <exception cref="ArgumentException">The text is not a key.</exception>
    public static byte[] HashOf(string key) =>
        SHA256.HashData(Encoding.ASCII.GetBytes(Normalize(key) ?? throw new ArgumentException("The text is not a license key.", nameof(key))));

    // Reads 5 bits starting at a bit position, most significant bit first.
    private static int Bits(byte[] bytes, int position)
    {
        int value = 0;
        for (int i = 0; i < 5; i++)
        {
            int bit = position + i;
            value = (value << 1) | ((bytes[bit / 8] >> (7 - (bit % 8))) & 1);
        }

        return value;
    }
}
