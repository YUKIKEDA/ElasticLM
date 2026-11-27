using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.Client;

// design §13.5 step 3.

/// <summary>
/// The server pins: for each server URL, the server license ID and the server key thumbprint seen first (or
/// accepted under a configured license ID). They live next to the installation key, one file per URL, in <c>servers/</c>.
/// </summary>
internal sealed class ServerPins(string directory, TimeProvider time)
{
    private const int MaxPinFileLength = 4 * 1024;
    private const string PinExtension = ".json";

    // The SHA-256 of the URL as 64 hex digits, and ".json".
    private const int PinNameLength = 64 + 5;

    private string Folder => Path.Combine(directory, "servers");

    /// <summary>
    /// Reads the pin of a server.
    /// </summary>
    /// <returns><see langword="false"/> when the pin file exists but cannot be read or is damaged; the caller refuses the
    /// server, since a damaged pin cannot vouch for it.</returns>
    public bool TryRead(Uri server, out PinFile? pin)
    {
        pin = null;
        string path = PathOf(server);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return true;
            }

            if (info.Length > MaxPinFileLength)
            {
                return false;
            }

            // design §12.6.2 (V1d24a): ResetServerPin deletes the file, and on Windows a reader without Delete would refuse it.
            if (FileReplacement.ReadShared(path, MaxPinFileLength) is not byte[] bytes)
            {
                return false;
            }

            pin = JsonSerializer.Deserialize(bytes, ClientWireContext.Default.PinFile);
            return pin is not null && pin.Url == HttpTargetUri.Normalize(server);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            pin = null;
            return false;
        }
    }

    /// <summary>
    /// Writes the pin of a server: a new temporary file renamed over the old one, so a reader never sees half a pin.
    /// </summary>
    /// <returns><see langword="false"/> when the file cannot be written.</returns>
    public bool TryWrite(Uri server, string licenseId, string keyId)
    {
        var pin = new PinFile
        {
            Url = HttpTargetUri.Normalize(server),
            Lic = licenseId,
            Jkt = keyId,
        };
        string path = PathOf(server);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(Folder);
            }
            else
            {
                Directory.CreateDirectory(Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // design §12.6.2 (V1d24a): the temporary files of every pin are cleaned, not only this one's.
            FileReplacement.RemoveLeftovers(Folder, "*.json.*.tmp", IsPinTemporary);
            FileReplacement.Write(path, JsonSerializer.SerializeToUtf8Bytes(pin, ClientWireContext.Default.PinFile), time);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes the pin of a server.
    /// </summary>
    /// <returns><see langword="true"/> when a pin was deleted; <see langword="false"/> when there was none or it cannot be
    /// deleted (a read-only folder, a file another process holds).</returns>
    public bool Delete(Uri server)
    {
        string path = PathOf(server);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // A pin's temporary file: <64 lowercase hex digits>.json.<16 lowercase hex digits>.tmp.
    internal static bool IsPinTemporary(string name) =>
        name.Length > PinNameLength
        && !name.AsSpan(0, PinNameLength - PinExtension.Length).ContainsAnyExcept(FileReplacement.LowerHex)
        && FileReplacement.IsTemporaryOf(name, name[..PinNameLength])
        && name.AsSpan(PinNameLength - PinExtension.Length, PinExtension.Length).SequenceEqual(PinExtension);

    private string PathOf(Uri server) =>
        Path.Combine(Folder, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(HttpTargetUri.Normalize(server)))) + ".json");
}
