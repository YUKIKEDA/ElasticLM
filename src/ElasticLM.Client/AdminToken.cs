using System.Security.Cryptography;

namespace ElasticLM.Client;

// design §13.7.

/// <summary>
/// The administrator token of an on-premises server: 256 random bits in a file next to the server's
/// installation key, protected as the key is. <c>elm-server</c> makes a new one at every start, so the token of an earlier
/// start is void; <c>elm server</c> reads the file at each call, on the same machine, and sends the token as
/// <c>Authorization: Bearer</c>.
/// </summary>
internal static class AdminToken
{
    public const string FileName = "admin.token";

    private const int Length = 32;
    private static readonly byte[] Purpose = "ElasticLM admin token v1"u8.ToArray();

    /// <summary>
    /// Makes a new token for a server data folder and puts it in place of the one that is there, if any. The token is
    /// written to a new file beside the old one, which then replaces it, so a reader never sees a half-written file.
    /// </summary>
    /// <param name="directory">The server's data folder.</param>
    /// <param name="time">The clock that counts the wait for a reader of the old file on Windows: the replacement is tried
    /// again every 100 milliseconds, for at most 2 seconds.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The new token as base64url, or <see langword="null"/> when it cannot be written or put in place; the new
    /// file is then deleted.</returns>
    /// <exception cref="OperationCanceledException">The wait was cancelled; the new file is deleted.</exception>
    public static async Task<string?> CreateAsync(string directory, TimeProvider time, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(directory, FileName);
        string fresh = path + ".new";
        byte[] secret = RandomNumberGenerator.GetBytes(Length);
        try
        {
            ProtectedFiles.CreateDirectory(directory, KeyScope.Machine);

            // A start that ended between the two steps left its new file.
            File.Delete(fresh);
            if (!ProtectedFiles.TryCreateSecret(fresh, secret, KeyScope.Machine, Purpose))
            {
                return null;
            }

            try
            {
                // Refused on Windows while elm server reads the old file, as it does for a moment.
                await FileReplacement.MoveAsync(fresh, path, time, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // The new file holds a token that no server takes; it does not stay behind.
                DeleteQuietly(fresh);
                throw;
            }

            return System.Buffers.Text.Base64Url.EncodeToString(secret);
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

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The next start deletes it before it writes its own.
        }
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
