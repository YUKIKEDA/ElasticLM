using System.Security.Cryptography;
using System.Text.Json;
using ElasticLM.Client;

namespace ElasticLM.Cli;

/// <summary>
/// The login <c>elm admin login</c> keeps (design §16, M5a).
/// </summary>
/// <param name="Url">The service URL the login was made for; the tokens go only to its origin.</param>
/// <param name="Issuer">The identity provider's issuer.</param>
/// <param name="ClientId">The CLI's client ID.</param>
/// <param name="AccessToken">The access token.</param>
/// <param name="RefreshToken">The refresh token, or <see langword="null"/>.</param>
/// <param name="ExpiresAt">When the access token expires, in UNIX seconds.</param>
internal sealed record AdminLogin(string Url, string Issuer, string ClientId, string AccessToken, string? RefreshToken, long ExpiresAt);

/// <summary>
/// The login file (design §16): a JSON file kept as the installation key is (§10.2), in the user's scope, replaced by writing a
/// new protected file and moving it over the old one, and changed only under a lock file.
/// </summary>
internal static class AdminLoginFile
{
    /// <summary>The largest login file on disk.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The variable that names another login file.</summary>
    public const string PathVariable = "ELM_ADMIN_LOGIN";

    private static readonly byte[] Purpose = "ElasticLM admin login v1"u8.ToArray();
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockRetry = TimeSpan.FromMilliseconds(100);

    /// <summary>Returns the login file: <c>ELM_ADMIN_LOGIN</c>, or <c>login</c> in elm's folder of the user's scope.</summary>
    public static string PathOf(CliHost host) =>
        host.GetEnvironmentVariable(PathVariable) is { Length: > 0 } path
            ? path
            : Path.Combine(InstallationKeyStore.DefaultDirectory("elm", KeyScope.User), "login");

    /// <summary>
    /// Reads the login; <see langword="null"/> when there is none, or when the file cannot be read, is protected for another
    /// user, is larger than 64 KiB, or is not a login of version 1.
    /// </summary>
    public static AdminLogin? Read(string path)
    {
        byte[]? bytes = ProtectedFiles.TryReadSecret(path, MaxBytes, Purpose);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out JsonElement version)
                || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != 1
            )
            {
                return null;
            }

            string? Text(string name) =>
                root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            long? expires =
                root.TryGetProperty("expiresAt", out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long seconds)
                    ? seconds
                    : null;
            return
                Text("url") is string url
                && Text("issuer") is string issuer
                && Text("clientId") is string clientId
                && Text("accessToken") is string access
                && expires is long at
                ? new AdminLogin(url, issuer, clientId, access, Text("refreshToken"), at)
                : null;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Writes the login, replacing any older one.</summary>
    /// <exception cref="CliException">The file cannot be written.</exception>
    public static void Write(string path, AdminLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("url", login.Url);
            writer.WriteString("issuer", login.Issuer);
            writer.WriteString("clientId", login.ClientId);
            writer.WriteString("accessToken", login.AccessToken);
            if (login.RefreshToken is null)
            {
                writer.WriteNull("refreshToken");
            }
            else
            {
                writer.WriteString("refreshToken", login.RefreshToken);
            }

            writer.WriteNumber("expiresAt", login.ExpiresAt);
            writer.WriteEndObject();
        }

        byte[] bytes = buffer.ToArray();
        try
        {
            // DPAPI makes the file larger than the text; half the limit leaves room for it.
            if (bytes.Length > MaxBytes / 2)
            {
                throw new CliException("The login is too large to keep.", ExitCodes.VerificationFailed);
            }

            string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            ProtectedFiles.CreateDirectory(folder, KeyScope.User);
            string temporary = path + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + ".tmp";
            if (!ProtectedFiles.TryCreateSecret(temporary, bytes, KeyScope.User, Purpose))
            {
                throw new CliException($"The login file cannot be written: '{temporary}' exists.", ExitCodes.InvalidInput);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CliException($"The login file '{path}' cannot be written: {e.Message}", ExitCodes.InvalidInput);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Removes the login file; nothing happens when there is none.</summary>
    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CliException($"The login file '{path}' cannot be removed: {e.Message}", ExitCodes.InvalidInput);
        }
    }

    /// <summary>
    /// Holds the lock file beside the login, waiting up to 10 seconds, so two elm processes do not use one refresh token at once.
    /// </summary>
    /// <exception cref="CliException">Another process holds the lock for 10 seconds.</exception>
    public static IDisposable Lock(CliHost host, string path)
    {
        ArgumentNullException.ThrowIfNull(host);
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        long start = host.Time.GetTimestamp();
        while (true)
        {
            try
            {
                // design §16: the folder is made with mode 0700 on Linux and macOS before the lock file is made in it.
                ProtectedFiles.CreateDirectory(folder, KeyScope.User);
                return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (host.Time.GetElapsedTime(start) < LockWait)
            {
                host.Pause(LockRetry);
            }
            catch (IOException e)
            {
                throw new CliException("Another elm process holds the login for more than 10 seconds: " + e.Message, ExitCodes.VerificationFailed);
            }
        }
    }
}
