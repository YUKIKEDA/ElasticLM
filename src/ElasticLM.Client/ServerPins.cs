using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.Client;

/// <summary>
/// The pins of design §13.5 step 3: for each server URL, the server license ID and the server key thumbprint seen first (or
/// accepted under a configured license ID). They live next to the installation key, one file per URL, in <c>servers/</c>.
/// </summary>
internal sealed class ServerPins(string directory)
{
    private const int MaxPinFileLength = 4 * 1024;

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

            pin = JsonSerializer.Deserialize(File.ReadAllBytes(path), ClientWireContext.Default.PinFile);
            return pin is not null && pin.Url == HttpTargetUri.Normalize(server);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            pin = null;
            return false;
        }
    }

    /// <summary>
    /// Writes the pin of a server: a temporary file renamed over the old one, so a reader never sees half a pin.
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
        string temporary = path + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) + ".tmp";
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

            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(pin, ClientWireContext.Default.PinFile));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            return false;
        }
    }

    /// <summary>
    /// Deletes the pin of a server.
    /// </summary>
    /// <returns><see langword="true"/> when a pin was deleted.</returns>
    public bool Delete(Uri server)
    {
        string path = PathOf(server);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing more to do; the file is a leftover temporary file.
        }
    }

    private string PathOf(Uri server) =>
        Path.Combine(Folder, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(HttpTargetUri.Normalize(server)))) + ".json");
}
