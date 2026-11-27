using System.Security.Cryptography;
using System.Text;

namespace ElasticLM.Licensing;

// design §8.6 (V1d24c, Issue #386): the one reader of every file that ElasticLM reads whole. The limit is checked on what
// the open file gives while it is read, never on a length read before or after the open.

/// <summary>
/// Reads a whole file that may hold at most a given number of bytes.
/// </summary>
internal static class BoundedFile
{
    /// <summary>
    /// Reads a file of at most <paramref name="maxLength"/> bytes. At most one byte past the limit is read, whatever length
    /// the file has or reaches while it is read; a file that cannot seek (a pipe, a device) is read the same way.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="maxLength">The most bytes the file may hold.</param>
    /// <param name="share">What the reader lets other handles do with the file while it is open.</param>
    /// <returns>Its bytes, or <see langword="null"/> when it holds more than <paramref name="maxLength"/> bytes.</returns>
    /// <exception cref="IOException">The file is missing or cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied, or the path is a folder.</exception>
    public static byte[]? Read(string path, int maxLength, FileShare share)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);

        // bufferSize 0: the default buffer reads 4,096 bytes from the file whatever count is asked.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share, bufferSize: 0);
        byte[] buffer = new byte[maxLength + 1];
        try
        {
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return read > maxLength ? null : buffer[..read];
        }
        finally
        {
            // The file may hold a secret, and the caller clears only the copy it gets.
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>
    /// Reads a text file of at most <paramref name="maxLength"/> bytes as <see cref="File.ReadAllText(string)"/> decodes it:
    /// UTF-8 unless a byte order mark names another encoding, the mark left out, bytes that do not decode replaced.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="maxLength">The most bytes the file may hold.</param>
    /// <returns>Its text, or <see langword="null"/> when it holds more than <paramref name="maxLength"/> bytes.</returns>
    /// <exception cref="IOException">The file is missing or cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied, or the path is a folder.</exception>
    public static string? ReadAllText(string path, int maxLength)
    {
        byte[]? bytes = Read(path, maxLength, FileShare.Read);
        if (bytes is null)
        {
            return null;
        }

        // File.ReadAllText(path) reads through new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true)
        // (File.cs, dotnet/runtime release/10.0).
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        try
        {
            return reader.ReadToEnd();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
