using System.Security.Cryptography;

namespace ElasticLM.Client;

/// <summary>
/// The administrator token of an on-premises server (design §13.7): 256 random bits in a file next to the server's
/// installation key, protected as the key is. <c>elm-server</c> creates it at the first start; <c>elm server</c> reads it on
/// the same machine and sends it as <c>Authorization: Bearer</c>.
/// </summary>
internal static class AdminToken
{
    public const string FileName = "admin.token";

    private const int Length = 32;
    private static readonly byte[] Purpose = "ElasticLM admin token v1"u8.ToArray();

    /// <summary>
    /// Reads the token of a server data folder, creating it when there is none.
    /// </summary>
    /// <returns>The token as base64url, or <see langword="null"/> when it cannot be created or read.</returns>
    public static string? LoadOrCreate(string directory)
    {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            byte[] secret = RandomNumberGenerator.GetBytes(Length);
            try
            {
                ProtectedFiles.CreateDirectory(directory, KeyScope.Machine);
                ProtectedFiles.TryCreateSecret(path, secret, KeyScope.Machine, Purpose);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }

        return Read(directory);
    }

    /// <summary>
    /// Reads the token of a server data folder.
    /// </summary>
    /// <returns>The token as base64url, or <see langword="null"/> when there is none or it cannot be read.</returns>
    public static string? Read(string directory)
    {
        byte[]? secret = ProtectedFiles.TryReadSecret(Path.Combine(directory, FileName), 4 * 1024, Purpose);
        if (secret is not { Length: Length })
        {
            return null;
        }

        try
        {
            return System.Buffers.Text.Base64Url.EncodeToString(secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}
