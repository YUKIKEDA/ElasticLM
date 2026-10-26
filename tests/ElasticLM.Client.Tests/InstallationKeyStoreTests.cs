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
    /// On Windows, a machine-scope folder is readable only by Administrators, SYSTEM, and the user who created it: DPAPI's
    /// machine scope can be opened by every local user, so the folder's access list is what keeps the key from them.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows; a machine-scope store in a folder that does not exist yet.
    ///
    /// Steps:
    /// - Create the key; read the folder's access list.
    ///
    /// Expected:
    /// - The list does not inherit, and allows exactly Administrators, SYSTEM, and the current user; no rule names Users.
    /// </remarks>
    [Fact]
    public void Create_MachineScopeFolder_IsOwnerOnlyOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Access lists are Windows only; Linux and macOS use mode 0700.");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string folder = Path.Combine(_dir, "machine");
        var store = new InstallationKeyStore("acme-cad", KeyScope.Machine, folder);
        Assert.True(store.TryLoadOrCreate(out InstallationKey? key, out _));
        key!.Dispose();

        System.Security.AccessControl.DirectorySecurity security = new DirectoryInfo(folder).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var allowedSet = new SortedSet<string>(StringComparer.Ordinal);
        foreach (
            System.Security.AccessControl.FileSystemAccessRule rule in security.GetAccessRules(
                true,
                true,
                typeof(System.Security.Principal.SecurityIdentifier)
            )
        )
        {
            allowedSet.Add(rule.IdentityReference.Value);
        }

        string[] allowed = [.. allowedSet];
        using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
        string[] expected = [.. new[] { "S-1-5-32-544", "S-1-5-18", current.User!.Value }.Distinct().Order(StringComparer.Ordinal)];
        Assert.Equal(expected, allowed);
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

    /// <summary>
    /// A machine-scope folder that exists already and is open to other users (as one another user created beforehand would
    /// be) is not used, and the error says why; a user-scope folder is not checked.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A folder made with the platform's defaults: on Windows it inherits the access list of its parent; on Linux and
    ///   macOS its mode is set to 0755.
    ///
    /// Steps:
    /// - Create a machine-scope key in it; create a user-scope key in it.
    ///
    /// Expected:
    /// - ELM_INSTALLATION_KEY_UNREADABLE saying the folder is not safe, and no key file; the user-scope key is created.
    /// </remarks>
    [Fact]
    public void MachineScope_FolderOpenToOthers_IsRefused()
    {
        string folder = Path.Combine(_dir, "open");
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode Mode0755 =
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(folder, Mode0755);
        }

        Assert.False(new InstallationKeyStore("acme-cad", KeyScope.Machine, folder).TryLoadOrCreate(out _, out LicenseError? error));
        Assert.Equal(ErrorCode.InstallationKeyUnreadable, error!.Code);
        Assert.Contains("is not safe for secrets", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(folder, "installation.key")));

        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.User, folder).TryLoadOrCreate(out InstallationKey? user, out _));
        user!.Dispose();
    }

    /// <summary>
    /// On Windows, a machine-scope folder must not inherit its access list, even from a parent that names only allowed
    /// users, and must not allow any other user, even in a protected list.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows; a protected parent of Administrators, SYSTEM, and the current user, with a child that inherits from it;
    ///   a protected folder with an added rule that allows Users to read.
    ///
    /// Steps:
    /// - Create a machine-scope key in each.
    ///
    /// Expected:
    /// - Refused: the first because it inherits, the second because it gives access to Users (S-1-5-32-545).
    /// </remarks>
    [Fact]
    public void MachineScope_InheritedOrExtraRules_AreRefusedOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Access lists are Windows only.");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string parent = Path.Combine(_dir, "parent");
        ProtectedFiles.CreateDirectory(parent, KeyScope.Machine);
        string inheriting = Path.Combine(parent, "child");
        Directory.CreateDirectory(inheriting);
        Assert.False(new InstallationKeyStore("acme-cad", KeyScope.Machine, inheriting).TryLoadOrCreate(out _, out LicenseError? inherited));
        Assert.Contains("inherits access", inherited!.Message, StringComparison.Ordinal);

        string extra = Path.Combine(_dir, "extra");
        ProtectedFiles.CreateDirectory(extra, KeyScope.Machine);
        var info = new DirectoryInfo(extra);
        System.Security.AccessControl.DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(
            new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                System.Security.AccessControl.FileSystemRights.Read,
                System.Security.AccessControl.AccessControlType.Allow
            )
        );
        info.SetAccessControl(security);
        Assert.False(new InstallationKeyStore("acme-cad", KeyScope.Machine, extra).TryLoadOrCreate(out _, out LicenseError? opened));
        Assert.Contains("gives access to S-1-5-32-545", opened!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// On Linux and macOS, a machine-scope folder whose parent other users can write is refused: they could replace it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux or macOS; a parent of mode 0777 with a new folder of mode 0700 in it.
    ///
    /// Steps:
    /// - Create a machine-scope key in the folder.
    ///
    /// Expected:
    /// - Refused because the parent is writable by group or others.
    /// </remarks>
    [Fact]
    public void MachineScope_WritableParent_IsRefusedOnUnix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Modes are Linux and macOS only.");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string parent = Path.Combine(_dir, "shared");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, (UnixFileMode)0b111_111_111);
        string folder = Path.Combine(parent, "machine");
        Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.False(new InstallationKeyStore("acme-cad", KeyScope.Machine, folder).TryLoadOrCreate(out _, out LicenseError? error));
        Assert.Contains("writable by group or others", error!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A machine-scope key is not read from a folder that was opened to other users after the key was created.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A machine-scope key created in a new folder; then the folder opened: on Windows it inherits its parent's access
    ///   list again, on Linux and macOS its mode becomes 0755.
    ///
    /// Steps:
    /// - Read the key.
    ///
    /// Expected:
    /// - ELM_INSTALLATION_KEY_UNREADABLE saying the folder is not safe.
    /// </remarks>
    [Fact]
    public void MachineScope_KeyInAFolderOpenedLater_IsNotRead()
    {
        string folder = Path.Combine(_dir, "opened");
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.Machine, folder).TryLoadOrCreate(out InstallationKey? created, out _));
        created!.Dispose();
        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(folder);
            System.Security.AccessControl.DirectorySecurity security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            info.SetAccessControl(security);
        }
        else
        {
            const UnixFileMode Mode0755 =
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(folder, Mode0755);
        }

        Assert.False(new InstallationKeyStore("acme-cad", KeyScope.Machine, folder).TryLoad(out _, out LicenseError? error));
        Assert.Contains("is not safe for secrets", error!.Message, StringComparison.Ordinal);
    }
}
