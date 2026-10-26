using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ElasticLM.Licensing;

namespace ElasticLM.Client;

/// <summary>
/// Creates and reads the installation key of a product (design §10.2). Windows protects the key with DPAPI; Linux and
/// macOS keep it in a file only the owner can read. A key that exists but cannot be read is never replaced, because a new
/// key would use one more activation.
/// </summary>
public sealed partial class InstallationKeyStore
{
    private const string KeyFileName = "installation.key";
    private const int MaxKeyFileLength = 16 * 1024;

    /// <summary>
    /// Initializes a new instance of the <see cref="InstallationKeyStore"/> class.
    /// </summary>
    /// <param name="productId">The product ID: lowercase letters, digits, and '-', at most 64 characters (design §9.1).</param>
    /// <param name="scope">For the user or the machine.</param>
    /// <param name="directory">A directory to use instead of the default of design §10.2, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="productId"/> is not a product ID.</exception>
    public InstallationKeyStore(string productId, KeyScope scope = KeyScope.User, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(productId);
        if (!ProductIdPattern().IsMatch(productId))
        {
            throw new ArgumentException("The product ID must be lowercase letters, digits, and '-', at most 64 characters.", nameof(productId));
        }

        if (scope is not (KeyScope.User or KeyScope.Machine))
        {
            throw new ArgumentException("The key scope is not defined.", nameof(scope));
        }

        Scope = scope;
        Directory = Path.GetFullPath(directory ?? DefaultDirectory(productId, scope));
    }

    /// <summary>Gets the directory that holds the key.</summary>
    public string Directory { get; }

    /// <summary>Gets the scope.</summary>
    public KeyScope Scope { get; }

    private string KeyPath => Path.Combine(Directory, KeyFileName);

    /// <summary>
    /// Reads the key, or creates it when none exists.
    /// </summary>
    /// <param name="key">The key, which the caller disposes; <see langword="null"/> on failure.</param>
    /// <param name="error"><c>ELM_INSTALLATION_KEY_UNREADABLE</c> when a key exists but cannot be read (damaged, protected for
    /// another user, no access), or cannot be created.</param>
    /// <returns><see langword="true"/> when a key was read or created.</returns>
    public bool TryLoadOrCreate(out InstallationKey? key, out LicenseError? error)
    {
        if (File.Exists(KeyPath))
        {
            return TryLoad(out key, out error);
        }

        using ECDsa created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] stored = Protect(created.ExportPkcs8PrivateKey());
        try
        {
            CreateDirectory();

            // A folder that another user prepared is refused before the key is written into it (design §10.2).
            if (ProtectedFiles.CheckDirectory(Directory, Scope) is string unsafeFolder)
            {
                key = null;
                error = Unreadable(unsafeFolder);
                return false;
            }

            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(KeyPath, options))
            {
                stream.Write(stored);
            }
        }
        catch (IOException) when (File.Exists(KeyPath))
        {
            // Another process created the key first; use that one.
            return TryLoad(out key, out error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            key = null;
            error = new LicenseError(ErrorCode.InstallationKeyUnreadable, "The installation key cannot be created: " + e.Message);
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stored);
        }

        return TryLoad(out key, out error);
    }

    /// <summary>
    /// Reads the key.
    /// </summary>
    /// <param name="key">The key, which the caller disposes; <see langword="null"/> on failure.</param>
    /// <param name="error"><c>ELM_INSTALLATION_KEY_UNREADABLE</c> when there is no key or it cannot be read, or when another
    /// user could have read or replaced the machine-scope folder (design §10.2).</param>
    /// <returns><see langword="true"/> when the key was read.</returns>
    public bool TryLoad(out InstallationKey? key, out LicenseError? error)
    {
        key = null;
        if (ProtectedFiles.CheckDirectory(Directory, Scope) is string unsafeFolder)
        {
            error = Unreadable(unsafeFolder);
            return false;
        }

        byte[] stored;
        try
        {
            var info = new FileInfo(KeyPath);
            if (!info.Exists || info.Length > MaxKeyFileLength)
            {
                error = Unreadable("The installation key does not exist or is too large.");
                return false;
            }

            stored = File.ReadAllBytes(KeyPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = Unreadable("The installation key cannot be read: " + e.Message);
            return false;
        }

        byte[]? pkcs8 = Unprotect(stored);
        if (pkcs8 is null)
        {
            error = Unreadable("The installation key cannot be unprotected; it may belong to another user or machine.");
            return false;
        }

        ECDsa ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportPkcs8PrivateKey(pkcs8, out int read);
            if (read != pkcs8.Length || !IsP256(ecdsa))
            {
                ecdsa.Dispose();
                error = Unreadable("The installation key is damaged.");
                return false;
            }
        }
        catch (CryptographicException)
        {
            ecdsa.Dispose();
            error = Unreadable("The installation key is damaged.");
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        key = new InstallationKey(ecdsa);
        error = null;
        return true;
    }

    /// <summary>
    /// Deletes the key, as after a deactivation (design §12.6). Nothing happens when there is no key.
    /// </summary>
    public void Delete()
    {
        if (File.Exists(KeyPath))
        {
            File.Delete(KeyPath);
        }
    }

    private static string DefaultDirectory(string productId, KeyScope scope)
    {
        if (OperatingSystem.IsWindows())
        {
            Environment.SpecialFolder folder =
                scope == KeyScope.User ? Environment.SpecialFolder.LocalApplicationData : Environment.SpecialFolder.CommonApplicationData;
            return Path.Combine(Environment.GetFolderPath(folder), "ElasticLM", productId);
        }

        if (OperatingSystem.IsMacOS())
        {
            string root =
                scope == KeyScope.User
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                    : "/Library/Application Support";
            return Path.Combine(root, "ElasticLM", productId);
        }

        if (scope == KeyScope.Machine)
        {
            return Path.Combine("/var/lib/elasticlm", productId);
        }

        string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string data = string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdg;
        return Path.Combine(data, "elasticlm", productId);
    }

    private static bool IsP256(ECDsa key)
    {
        try
        {
            _ = KeyIds.Of(key);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static LicenseError Unreadable(string message) => new(ErrorCode.InstallationKeyUnreadable, message);

    [GeneratedRegex("^[a-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductIdPattern();

    private void CreateDirectory() => ProtectedFiles.CreateDirectory(Directory, Scope);

    private byte[] Protect(byte[] pkcs8)
    {
        try
        {
            return OperatingSystem.IsWindows() ? Dpapi.Protect(pkcs8, Scope == KeyScope.Machine) : [.. pkcs8];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private static byte[]? Unprotect(byte[] stored) => OperatingSystem.IsWindows() ? Dpapi.Unprotect(stored) : stored;
}
