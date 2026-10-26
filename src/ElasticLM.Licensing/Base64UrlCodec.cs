using System.Buffers.Text;

namespace ElasticLM.Licensing;

/// <summary>
/// Encodes and decodes base64url without padding (RFC 7515 §2), accepting only the canonical form.
/// </summary>
internal static class Base64UrlCodec
{
    public static string Encode(ReadOnlySpan<byte> data) => Base64Url.EncodeToString(data);

    /// <summary>
    /// Decodes base64url text. Padding, whitespace, other characters, and non-canonical trailing bits are rejected, so one
    /// value has exactly one text form.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<char> text, out byte[] bytes)
    {
        bytes = [];
        foreach (char c in text)
        {
            bool valid = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
            if (!valid)
            {
                return false;
            }
        }

        if (text.Length % 4 == 1)
        {
            return false;
        }

        byte[] buffer = new byte[Base64Url.GetMaxDecodedLength(text.Length)];
        int written;
        try
        {
            if (!Base64Url.TryDecodeFromChars(text, buffer, out written))
            {
                return false;
            }
        }
        catch (FormatException)
        {
            // .NET 10.0.13 throws, instead of returning false, for non-zero unused trailing bits ("AQJ"). That is also what
            // rejects non-canonical text, so one value has one text form (TryDecode_Forms_AcceptsCanonicalOnly).
            return false;
        }

        bytes = written == buffer.Length ? buffer : buffer.AsSpan(0, written).ToArray();
        return true;
    }
}
