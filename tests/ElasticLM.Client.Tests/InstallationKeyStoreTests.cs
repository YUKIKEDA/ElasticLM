using System.Security.Cryptography;
using ElasticLM.Licensing;

namespace ElasticLM.Client.Tests;

/// <summary>
/// Tests of <see cref="InstallationKeyStore"/> (design §10.2), run on every OS of the CI.
/// </summary>
public sealed class InstallationKeyStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "elm-key-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// The first call creates a key; later calls, also from a new store, read the same key.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty directory (the user scope; on Windows the key is protected with DPAPI).
    ///
    /// Steps:
    /// - TryLoadOrCreate twice on one store and once on a new store for the same directory.
    ///
    /// Expected:
    /// - All succeed with the same thumbprint.
    /// </remarks>
    [Fact]
    public void TryLoadOrCreate_RepeatedCalls_ReturnSameKey()
    {
        var store = new InstallationKeyStore("acme-cad", KeyScope.User, _dir);

        Assert.True(store.TryLoadOrCreate(out InstallationKey? first, out _));
        Assert.True(store.TryLoadOrCreate(out InstallationKey? second, out _));
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.User, _dir).TryLoad(out InstallationKey? third, out _));
        using (first)
        using (second)
        using (third)
        {
            Assert.Equal(first!.Thumbprint, second!.Thumbprint);
            Assert.Equal(first.Thumbprint, third!.Thumbprint);
        }
    }

    /// <summary>
    /// A damaged key file is reported as unreadable and is not replaced (a new key would use one more activation).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A created key; its file then overwritten with other bytes.
    ///
    /// Steps:
    /// - TryLoadOrCreate.
    ///
    /// Expected:
    /// - False with ELM_INSTALLATION_KEY_UNREADABLE; the file keeps the damaged bytes.
    /// </remarks>
    [Fact]
    public void TryLoadOrCreate_DamagedFile_IsNotReplaced()
    {
        var store = new InstallationKeyStore("acme-cad", KeyScope.User, _dir);
        Assert.True(store.TryLoadOrCreate(out InstallationKey? created, out _));
        created!.Dispose();
        string path = Path.Combine(_dir, "installation.key");
        byte[] damaged = [1, 2, 3, 4];
        File.WriteAllBytes(path, damaged);

        Assert.False(store.TryLoadOrCreate(out InstallationKey? key, out LicenseError? error));

        Assert.Null(key);
        Assert.Equal(ErrorCode.InstallationKeyUnreadable, error!.Code);
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    /// <summary>
    /// TryLoad without a key, and after Delete, reports unreadable; Delete without a key does nothing.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty directory.
    ///
    /// Steps:
    /// - TryLoad; Delete; create; Delete; TryLoad.
    ///
    /// Expected:
    /// - Both loads fail with ELM_INSTALLATION_KEY_UNREADABLE; no exception.
    /// </remarks>
    [Fact]
    public void TryLoad_NoKey_IsUnreadable()
    {
        var store = new InstallationKeyStore("acme-cad", KeyScope.User, _dir);

        Assert.False(store.TryLoad(out _, out LicenseError? before));
        store.Delete();
        Assert.True(store.TryLoadOrCreate(out InstallationKey? key, out _));
        key!.Dispose();
        store.Delete();
        Assert.False(store.TryLoad(out _, out LicenseError? after));

        Assert.Equal(ErrorCode.InstallationKeyUnreadable, before!.Code);
        Assert.Equal(ErrorCode.InstallationKeyUnreadable, after!.Code);
    }

    /// <summary>
    /// Product IDs that are not lowercase letters, digits, and '-' are refused, so a product ID cannot reach outside the
    /// key directory.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Create stores for "../x", "Acme", "a/b", "", and 65 characters.
    ///
    /// Expected:
    /// - ArgumentException each time.
    /// </remarks>
    [Theory]
    [InlineData("../x")]
    [InlineData("Acme")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Constructor_InvalidProductId_Throws(string productId)
    {
        Assert.ThrowsAny<ArgumentException>(() => new InstallationKeyStore(productId, KeyScope.User, _dir));
    }

    /// <summary>
    /// On Linux and macOS the key file is 0600 and its directory 0700; on Windows the stored bytes are not a plain PKCS#8 key.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A created key.
    ///
    /// Steps:
    /// - Read the file mode and directory mode (Linux, macOS), or try to import the file as PKCS#8 (Windows).
    ///
    /// Expected:
    /// - 0600 and 0700; on Windows the import fails (the bytes are DPAPI-protected).
    /// </remarks>
    [Fact]
    public void Create_KeyFile_IsProtected()
    {
        var store = new InstallationKeyStore("acme-cad", KeyScope.User, _dir);
        Assert.True(store.TryLoadOrCreate(out InstallationKey? key, out _));
        key!.Dispose();
        string path = Path.Combine(_dir, "installation.key");

        if (OperatingSystem.IsWindows())
        {
            using var ecdsa = ECDsa.Create();
            Assert.ThrowsAny<CryptographicException>(() => ecdsa.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _));
            return;
        }

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_dir));
    }

    /// <summary>
    /// The machine scope protects and reads back a key too (on Windows, DPAPI with the local-machine flag).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A directory given explicitly, so no administrator rights are needed.
    ///
    /// Steps:
    /// - Create with KeyScope.Machine; read with a new store.
    ///
    /// Expected:
    /// - The same thumbprint.
    /// </remarks>
    [Fact]
    public void MachineScope_RoundTrip_Works()
    {
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.Machine, _dir).TryLoadOrCreate(out InstallationKey? created, out _));
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.Machine, _dir).TryLoad(out InstallationKey? read, out _));
        using (created)
        using (read)
        {
            Assert.Equal(created!.Thumbprint, read!.Thumbprint);
        }
    }
}
