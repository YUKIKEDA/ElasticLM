using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using ElasticLM.Licensing;

namespace ElasticLM.Client.Tests;

/// <summary>
/// Tests of <see cref="InstallationKeyStore"/> (design §10.2), run on every OS of the CI.
/// </summary>
public sealed class InstallationKeyStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        ElasticLM.Service.Tests.TestFolders.Root,
        "elm-key-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6))
    );

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
        // Inside the test folder: the temporary folder itself (/tmp) is writable by others, which a machine-scope folder's
        // parent must not be.
        string folder = Path.Combine(_dir, "machine");
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.Machine, folder).TryLoadOrCreate(out InstallationKey? created, out _));
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.Machine, folder).TryLoad(out InstallationKey? read, out _));
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
        Assert.Contains("(S-1-5-32-545).", opened!.Message, StringComparison.Ordinal);
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
    /// On Linux and macOS, a machine-scope folder that another user owns is refused even with mode 0700, and so is a folder
    /// whose parent another user owns: the folder's owner could read and replace what is in it, and the parent's owner could
    /// replace the folder (design §10.2, V1d24).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux or macOS, not as root, with sudo that lets this user run chown without a password, as the CI runners do; on
    ///   GitHub Actions the test fails without it, elsewhere it is skipped and says so. Windows checks owners in its own tests.
    /// - folder: a new folder of mode 0700 in a parent of mode 0755, both made by this user; then, with sudo, the folder
    ///   given to the user nobody.
    /// - parent: the same two folders; then the parent given to nobody.
    /// - parent-trailing: as parent, with the folder's path given with a separator at its end (`…/machine/`), whose
    ///   parent must still be the parent folder, not the folder itself.
    ///
    /// Steps:
    /// - Create a machine-scope key in the folder. Give the folders back to this user at the end.
    ///
    /// Expected:
    /// - Refused with ELM_INSTALLATION_KEY_UNREADABLE and the folder's path: folder, "it is owned by another user."; parent,
    ///   "its parent folder '…' is owned by another user." with the parent's path. No key file is in the folder.
    /// </remarks>
    /// <param name="given">The folder given to nobody.</param>
    [Theory]
    [InlineData("folder")]
    [InlineData("parent")]
    [InlineData("parent-trailing")]
    public void MachineScope_OwnedByAnotherUser_IsRefusedOnUnix(string given)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix owners are Linux and macOS only.");
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "The test runs as a user that is not root.");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        (string parent, string folder) = MachineFolders();
        string other = given == "folder" ? folder : parent;
        GiveTo("nobody", other);
        try
        {
            string storePath = given == "parent-trailing" ? folder + Path.DirectorySeparatorChar : folder;
            Assert.False(new InstallationKeyStore("acme-cad", KeyScope.Machine, storePath).TryLoadOrCreate(out _, out LicenseError? error));
            Assert.Equal(ErrorCode.InstallationKeyUnreadable, error!.Code);
            Assert.StartsWith($"The folder '{folder}' is not safe for secrets: ", error.Message, StringComparison.Ordinal);
            Assert.Contains(
                given == "folder" ? "it is owned by another user." : $"its parent folder '{parent}' is owned by another user.",
                error.Message,
                StringComparison.Ordinal
            );
        }
        finally
        {
            GiveTo(UnixOwner.EffectiveUserId.ToString(System.Globalization.CultureInfo.InvariantCulture), other);
        }

        Assert.Empty(Directory.GetFiles(folder));
    }

    /// <summary>
    /// A machine-scope folder given with a separator at its end is the folder itself: the store names it without the
    /// separator, and on Windows the folder is made with its own access list, whose parent is the folder above (design
    /// §10.2, V1d24).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A path of a new folder inside the test folder, followed by the OS's separator.
    ///
    /// Steps:
    /// - Create a machine-scope key there; read it with a new store of the path without the separator.
    ///
    /// Expected:
    /// - Both succeed with the same thumbprint; the store's Directory has no separator at its end; on Windows the folder's
    ///   access list is not inherited.
    /// </remarks>
    [Fact]
    public void MachineScope_PathEndingInASeparator_IsTheFolder()
    {
        string folder = Path.Combine(_dir, "trailing");
        var store = new InstallationKeyStore("acme-cad", KeyScope.Machine, folder + Path.DirectorySeparatorChar);

        Assert.True(store.TryLoadOrCreate(out InstallationKey? created, out LicenseError? error), error?.Message);
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.Machine, folder).TryLoad(out InstallationKey? read, out _));
        using (created)
        using (read)
        {
            Assert.Equal(created!.Thumbprint, read!.Thumbprint);
        }

        Assert.Equal(folder, store.Directory);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(new DirectoryInfo(folder).GetAccessControl().AreAccessRulesProtected);
        }
    }

    /// <summary>
    /// On Linux and macOS, a machine-scope folder or parent that root owns passes the owner check: root is trusted (design
    /// §10.2, V1d24).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - As the test before, with sudo: a folder of mode 0700 in a parent of mode 0755; then the folder, or the parent, given
    ///   to root.
    ///
    /// Steps:
    /// - Check the folder as a machine-scope folder. Give the folders back to this user at the end.
    ///
    /// Expected:
    /// - No problem is found.
    /// </remarks>
    /// <param name="given">The folder given to root.</param>
    [Theory]
    [InlineData("folder")]
    [InlineData("parent")]
    public void MachineScope_OwnedByRoot_PassesOnUnix(string given)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix owners are Linux and macOS only.");
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "The test runs as a user that is not root.");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        (string parent, string folder) = MachineFolders();
        string other = given == "folder" ? folder : parent;
        GiveTo("root", other);
        try
        {
            Assert.Null(ProtectedFiles.CheckDirectory(folder, KeyScope.Machine, out _));
        }
        finally
        {
            GiveTo(UnixOwner.EffectiveUserId.ToString(System.Globalization.CultureInfo.InvariantCulture), other);
        }
    }

    /// <summary>
    /// On Linux and macOS, every folder above a machine-scope folder, up to the root, must be owned by this user or root and
    /// not writable by group or others: a folder two levels up that another user owns or that group or others can write is
    /// refused, and so is a folder under /tmp, which every user can write (design §10.2, V1d24).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux or macOS, not as root. A folder of mode 0700 in a parent of mode 0755 in the test folder (made by this user):
    ///   owned-above: the test folder, two levels up, given to nobody with sudo (as the owner tests; skipped without it,
    ///   failed on GitHub Actions);
    ///   group-writable-above: the test folder given mode 0775;
    ///   tmp: the same two folders made in /tmp instead of the test folder.
    ///
    /// Steps:
    /// - Create a machine-scope key in the folder. Give the test folder back at the end, or remove the folders in /tmp.
    ///
    /// Expected:
    /// - Refused with ELM_INSTALLATION_KEY_UNREADABLE: owned-above, "the folder '{test folder}' above it is owned by another
    ///   user."; group-writable-above, "the folder '{test folder}' above it is writable by group or others."; tmp, "the
    ///   folder '/tmp' above it is writable by group or others.".
    /// </remarks>
    /// <param name="kind">The case.</param>
    [Theory]
    [InlineData("owned-above")]
    [InlineData("group-writable-above")]
    [InlineData("tmp")]
    public void MachineScope_FolderAboveTheParentNotSafe_IsRefusedOnUnix(string kind)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix owners and modes are Linux and macOS only.");
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "The test runs as a user that is not root.");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string top = kind == "tmp" ? Path.Combine("/tmp", "elm-key-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6))) : _dir;
        string parent = Path.Combine(top, "vendor");
        Directory.CreateDirectory(parent, (UnixFileMode)0b111_101_101);
        File.SetUnixFileMode(top, (UnixFileMode)0b111_101_101);
        string folder = Path.Combine(parent, "machine");
        Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (kind == "owned-above")
        {
            GiveTo("nobody", top);
        }
        else if (kind == "group-writable-above")
        {
            File.SetUnixFileMode(top, (UnixFileMode)0b111_111_101);
        }

        try
        {
            Assert.False(new InstallationKeyStore("acme-cad", KeyScope.Machine, folder).TryLoadOrCreate(out _, out LicenseError? error));
            Assert.Equal(ErrorCode.InstallationKeyUnreadable, error!.Code);
            Assert.Contains(
                kind switch
                {
                    "owned-above" => $"the folder '{top}' above it is owned by another user.",
                    "group-writable-above" => $"the folder '{top}' above it is writable by group or others.",
                    _ => "the folder '/tmp' above it is writable by group or others.",
                },
                error.Message,
                StringComparison.Ordinal
            );
        }
        finally
        {
            if (kind == "owned-above")
            {
                GiveTo(UnixOwner.EffectiveUserId.ToString(System.Globalization.CultureInfo.InvariantCulture), top);
            }
            else if (kind == "tmp")
            {
                Directory.Delete(top, recursive: true);
            }
        }
    }

    /// <summary>
    /// The default machine-scope folder passes the check, with every folder above it as the OS ships them: on Windows
    /// <c>C:\ProgramData\ElasticLM\{product}</c>, on Linux <c>/var/lib/elasticlm/{product}</c>, on macOS
    /// <c>/Library/Application Support/ElasticLM/{product}</c> (design §10.2, V1d24).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A product ID of this test run only. On Windows the test makes the folder itself (every user may create in
    ///   ProgramData). On Linux and macOS sudo makes the folder (with its parents, as root, mode 0755) and gives it to this
    ///   user with mode 0700, as an installer would for the service's user; skipped without sudo, failed on GitHub Actions.
    ///
    /// Steps:
    /// - Create a machine-scope key in the default folder; read it with a new store. Remove the product's folder at the end.
    ///
    /// Expected:
    /// - Both succeed with the same thumbprint.
    /// </remarks>
    [Fact]
    public void MachineScope_DefaultFolder_Passes()
    {
        string product = "elm-v1d24-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var store = new InstallationKeyStore(product, KeyScope.Machine);
        string vendorFolder = Path.GetDirectoryName(store.Directory)!;
        bool vendorFolderExisted = Directory.Exists(vendorFolder);
        if (!OperatingSystem.IsWindows())
        {
            Sudo("mkdir", "-p", store.Directory);
            Sudo("chown", UnixOwner.EffectiveUserId.ToString(System.Globalization.CultureInfo.InvariantCulture), store.Directory);
            Sudo("chmod", "700", store.Directory);
        }

        try
        {
            Assert.True(store.TryLoadOrCreate(out InstallationKey? created, out LicenseError? error), error?.Message);
            Assert.True(new InstallationKeyStore(product, KeyScope.Machine).TryLoad(out InstallationKey? read, out error), error?.Message);
            using (created)
            using (read)
            {
                Assert.Equal(created!.Thumbprint, read!.Thumbprint);
            }
        }
        finally
        {
            // The test removes what it made: the product's folder, and the ElasticLM folder above it when it was not there.
            if (OperatingSystem.IsWindows())
            {
                Directory.Delete(store.Directory, recursive: true);
                if (!vendorFolderExisted)
                {
                    Directory.Delete(vendorFolder);
                }
            }
            else
            {
                Sudo("rm", "-rf", store.Directory);
                if (!vendorFolderExisted)
                {
                    Sudo("rmdir", vendorFolder);
                }
            }
        }
    }

    /// <summary>
    /// On Windows, a folder two levels above a machine-scope folder that lets Authenticated Users delete, rename, or change
    /// the access of what is in it is refused, with a message that sends the reader to that folder; one that lets them only
    /// create folders in it passes (design §10.2, V1d24).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows. A machine-scope key made in a folder of a parent of a top folder in the test folder; then the top folder
    ///   given a rule for Authenticated Users (S-1-5-11) on itself only:
    ///   modify: Modify, which holds Delete;
    ///   delete-child: DeleteSubdirectoriesAndFiles only;
    ///   change-permissions: ChangePermissions (WRITE_DAC) only;
    ///   take-ownership: TakeOwnership (WRITE_OWNER) only;
    ///   generic-all: the raw right GENERIC_ALL (0x10000000) only;
    ///   create-only: CreateDirectories and ReadAndExecute.
    ///
    /// Steps:
    /// - Read the key with a new store.
    ///
    /// Expected:
    /// - Every case but create-only: ELM_INSTALLATION_KEY_UNREADABLE with "the folder '{top}' above it lets", "(S-1-5-11)
    ///   delete or replace what is in it.", and "Correct the owner or the access of that folder"; create-only: the key is read.
    /// </remarks>
    /// <param name="kind">The case.</param>
    [Theory]
    [InlineData("modify")]
    [InlineData("delete-child")]
    [InlineData("change-permissions")]
    [InlineData("take-ownership")]
    [InlineData("generic-all")]
    [InlineData("create-only")]
    public void MachineScope_FolderAboveGivesOthersRights_IsCheckedOnWindows(string kind)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Access lists are Windows only.");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string top = Path.Combine(_dir, "top");
        string folder = Path.Combine(top, "vendor", "machine");
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.Machine, folder).TryLoadOrCreate(out InstallationKey? created, out _));
        created!.Dispose();
        System.Security.AccessControl.FileSystemRights rights = kind switch
        {
            "modify" => System.Security.AccessControl.FileSystemRights.Modify,
            "delete-child" => System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles,
            "change-permissions" => System.Security.AccessControl.FileSystemRights.ChangePermissions,
            "take-ownership" => System.Security.AccessControl.FileSystemRights.TakeOwnership,
            _ => System.Security.AccessControl.FileSystemRights.CreateDirectories | System.Security.AccessControl.FileSystemRights.ReadAndExecute,
        };
        if (kind == "generic-all")
        {
            // .NET refuses GENERIC_ALL in a FileSystemAccessRule; icacls writes it (GA).
            Icacls(top, "/grant", "*S-1-5-11:(GA)");
        }
        else
        {
            Grant(
                top,
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
                rights
            );
        }

        bool read = new InstallationKeyStore("acme-cad", KeyScope.Machine, folder).TryLoad(out InstallationKey? key, out LicenseError? error);
        key?.Dispose();

        if (kind == "create-only")
        {
            Assert.True(read, error?.Message);
        }
        else
        {
            Assert.False(read);
            Assert.Contains($"the folder '{top}' above it lets ", error!.Message, StringComparison.Ordinal);
            Assert.Contains("(S-1-5-11) delete or replace what is in it.", error.Message, StringComparison.Ordinal);
            Assert.EndsWith(
                "Correct the owner or the access of that folder, or use a folder elsewhere; removing this folder does not help.",
                error.Message,
                StringComparison.Ordinal
            );
        }
    }

    /// <summary>
    /// On Windows, a folder two levels above a machine-scope folder that another account owns is refused (design §10.2,
    /// V1d24).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows, as an administrator who may set another owner (icacls /setowner, as on the GitHub runners); elsewhere the
    ///   test is skipped and says so, and on GitHub Actions it fails without it.
    /// - A machine-scope key made in a folder of a parent of a top folder in the test folder; then the top folder given to
    ///   LOCAL SERVICE (S-1-5-19).
    ///
    /// Steps:
    /// - Read the key with a new store. Give the top folder back to this user at the end.
    ///
    /// Expected:
    /// - ELM_INSTALLATION_KEY_UNREADABLE with "the folder '{top}' above it is owned by another user.".
    /// </remarks>
    [Fact]
    public void MachineScope_FolderAboveOwnedByAnotherAccount_IsRefusedOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Access lists are Windows only.");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string top = Path.Combine(_dir, "top");
        string folder = Path.Combine(top, "vendor", "machine");
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.Machine, folder).TryLoadOrCreate(out InstallationKey? created, out _));
        created!.Dispose();
        Icacls(top, "/setowner", "*S-1-5-19");
        try
        {
            Assert.False(new InstallationKeyStore("acme-cad", KeyScope.Machine, folder).TryLoad(out _, out LicenseError? error));
            Assert.Contains($"the folder '{top}' above it is owned by another user.", error!.Message, StringComparison.Ordinal);
        }
        finally
        {
            Icacls(top, "/setowner", "*" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
        }
    }

    /// <summary>
    /// A machine-scope folder reached through a junction (Windows) or a symbolic link (Linux and macOS) is checked where it
    /// leads too: a folder above the real folder that others may replace is refused, though every folder on the way to the
    /// link passes (design §10.2, V1d24).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A machine-scope key made in open/vendor/machine in the test folder; then open given, on Windows, a rule that lets
    ///   Authenticated Users modify it, and on Linux and macOS mode 0777.
    /// - safe/machine: a link to that folder (a junction made with mklink /J on Windows, a symbolic link elsewhere), in a
    ///   folder safe that passes every check; on Windows the junction is given the real folder's access list, so that the
    ///   junction's own access list passes.
    ///
    /// Steps:
    /// - Read the key through safe/machine with a new store.
    ///
    /// Expected:
    /// - ELM_INSTALLATION_KEY_UNREADABLE with "it leads to '{real folder}'" and "the folder '{open}' above it".
    /// </remarks>
    [Fact]
    public void MachineScope_LinkToAFolderUnderAnOpenFolder_IsRefused()
    {
        string open = Path.Combine(_dir, "open");
        string real = Path.Combine(open, "vendor", "machine");
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.Machine, real).TryLoadOrCreate(out InstallationKey? created, out _));
        created!.Dispose();
        string safe = Path.Combine(_dir, "safe");
        string link = Path.Combine(safe, "machine");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(safe);
            using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, real]) { RedirectStandardOutput = true })!)
            {
                mklink.WaitForExit();
                Assert.Equal(0, mklink.ExitCode);
            }

            string me = "*" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            Icacls(link, "/inheritance:r", "/grant:r", me + ":(F)", "*S-1-5-18:(F)", "*S-1-5-32-544:(F)", "/L");
            Grant(
                open,
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
                System.Security.AccessControl.FileSystemRights.Modify
            );
        }
        else
        {
            Directory.CreateDirectory(safe, (UnixFileMode)0b111_101_101);
            File.CreateSymbolicLink(link, real);
            File.SetUnixFileMode(open, (UnixFileMode)0b111_111_111);
        }

        try
        {
            Assert.False(new InstallationKeyStore("acme-cad", KeyScope.Machine, link).TryLoad(out _, out LicenseError? error));
            Assert.Contains($"it leads to '{real}'", error!.Message, StringComparison.Ordinal);
            Assert.Contains($"the folder '{open}' above it", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            // The recursive delete of the test folder does not remove a junction with an access list of its own.
            Directory.Delete(link);
        }
    }

    /// <summary>
    /// On Windows, a machine-scope folder named by a UNC path is refused as a network location (design §10.2, V1d24).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows, as an administrator who may open the drive's administrative share (\\localhost\C$, as on the GitHub
    ///   runners); elsewhere the test is skipped and says so, and on GitHub Actions it fails without it.
    /// - A machine-scope key made in a folder of the test folder.
    ///
    /// Steps:
    /// - Read the key through \\localhost\{drive}$\{the rest of the folder's path}.
    ///
    /// Expected:
    /// - ELM_INSTALLATION_KEY_UNREADABLE with "it is on a network location".
    /// </remarks>
    [Fact]
    public void MachineScope_FolderOnANetworkPath_IsRefusedOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "UNC paths are Windows only.");
        string folder = Path.Combine(_dir, "unc", "machine");
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.Machine, folder).TryLoadOrCreate(out InstallationKey? created, out _));
        created!.Dispose();
        string unc = @"\\localhost\" + folder[0] + "$" + folder[2..];
        if (!Directory.Exists(unc))
        {
            Assert.False(
                Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
                "On GitHub Actions this test must run: " + unc + " cannot be opened."
            );
            Assert.Skip("This user cannot open the administrative share " + unc + ".");
        }

        Assert.False(new InstallationKeyStore("acme-cad", KeyScope.Machine, unc).TryLoad(out _, out LicenseError? error));
        Assert.Contains("it is on a network location", error!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// On macOS, an entry of an extended access list (chmod +a) that lets another user or group delete, rename, or change the
    /// access of what is in a folder two levels above a machine-scope folder is refused, and so is one that lets anyone else
    /// into the folder itself; a deny entry, an entry that allows only adding or reading, an entry only inherited by what is
    /// made later, and an entry for this user are not (design §10.2, V1d24, the owner's K9, review K2 and K7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - macOS. A folder of mode 0700 in a parent of mode 0755 in the test folder (made by this user); then one entry:
    ///   above-delete, above-delete-child, above-writesecurity, above-chown: the test folder given "group:staff allow"
    ///   that right;
    ///   above-deny: the test folder given "group:staff deny delete_child";
    ///   above-add-file: the test folder given "group:staff allow add_file,add_subdirectory";
    ///   above-read: the test folder given "group:staff allow list,search";
    ///   above-only-inherit: the test folder given "group:staff allow delete_child,file_inherit,directory_inherit,only_inherit";
    ///   folder-read: the folder given "user:nobody allow list";
    ///   folder-self: the folder given an entry that allows this user to list it.
    ///
    /// Steps:
    /// - Check the folder as a machine-scope folder. Remove the entry at the end.
    ///
    /// Expected:
    /// - The four above-… rights: "the folder '{test folder}' above it has an access list that lets group "; folder-read:
    ///   "its access list gives access to user "; the others: no problem.
    /// </remarks>
    /// <param name="kind">The case.</param>
    [Theory]
    [InlineData("above-delete")]
    [InlineData("above-delete-child")]
    [InlineData("above-writesecurity")]
    [InlineData("above-chown")]
    [InlineData("above-deny")]
    [InlineData("above-add-file")]
    [InlineData("above-read")]
    [InlineData("above-only-inherit")]
    [InlineData("folder-read")]
    [InlineData("folder-self")]
    public void MachineScope_ExtendedAccessList_IsCheckedOnMacOS(string kind)
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Extended access lists of chmod +a are macOS only.");
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        (string _, string folder) = MachineFolders();
        string target = kind.StartsWith("folder-", StringComparison.Ordinal) ? folder : _dir;
        string entry = kind switch
        {
            "above-delete" => "group:staff allow delete",
            "above-delete-child" => "group:staff allow delete_child",
            "above-writesecurity" => "group:staff allow writesecurity",
            "above-chown" => "group:staff allow chown",
            "above-deny" => "group:staff deny delete_child",
            "above-add-file" => "group:staff allow add_file,add_subdirectory",
            "above-read" => "group:staff allow list,search",
            "above-only-inherit" => "group:staff allow delete_child,file_inherit,directory_inherit,only_inherit",
            "folder-read" => "user:nobody allow list",
            _ => "user:" + Environment.UserName + " allow list",
        };
        Chmod("+a", entry, target);
        try
        {
            string? problem = ProtectedFiles.CheckDirectory(folder, KeyScope.Machine, out _);
            switch (kind)
            {
                case "above-delete" or "above-delete-child" or "above-writesecurity" or "above-chown":
                    Assert.Contains($"the folder '{_dir}' above it has an access list that lets group ", problem, StringComparison.Ordinal);
                    break;
                case "folder-read":
                    Assert.Contains("its access list gives access to user ", problem, StringComparison.Ordinal);
                    break;
                default:
                    Assert.Null(problem);
                    break;
            }
        }
        finally
        {
            Chmod("-a", entry, target);
        }
    }

    /// <summary>
    /// A machine-scope folder reached through two links in a row is checked where it ends, and the key store goes on in that
    /// folder only: a folder on the way that others may change is not used again, so changing the second link later does not
    /// move the key (design §10.2, V1d24, review K1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - In the test folder: end/machine, a folder that passes every check; open, a folder that others may change (on Linux
    ///   and macOS mode 0777, on Windows a rule that lets Authenticated Users modify it); open/second, a link to end/machine;
    ///   safe/first, a link to open/second. Links are symbolic links on Linux and macOS and junctions on Windows, where the
    ///   first junction gets an access list of its own that passes. other/machine, a second folder that passes.
    ///
    /// Steps:
    /// - Create a machine-scope key through safe/first. Point open/second at other/machine. Read the key with the same store.
    ///
    /// Expected:
    /// - The key is created; the store's Directory is end/machine; the key file is in end/machine; after the change the same
    ///   store reads the same key, and other/machine holds no key.
    /// </remarks>
    [Fact]
    public void MachineScope_TwoLinksInARow_UsesTheFolderWhereTheyEnd()
    {
        string end = Path.Combine(_dir, "end", "machine");
        string other = Path.Combine(_dir, "other", "machine");
        foreach (string made in new[] { end, other })
        {
            Assert.True(new InstallationKeyStore("acme-cad", KeyScope.Machine, made).TryLoadOrCreate(out InstallationKey? created, out _));
            created!.Dispose();
            File.Delete(Path.Combine(made, "installation.key"));
        }

        string open = Path.Combine(_dir, "open");
        string second = Path.Combine(open, "second");
        string safe = Path.Combine(_dir, "safe");
        string first = Path.Combine(safe, "first");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(open);
            Directory.CreateDirectory(safe);
        }
        else
        {
            Directory.CreateDirectory(open, (UnixFileMode)0b111_101_101);
            Directory.CreateDirectory(safe, (UnixFileMode)0b111_101_101);
        }

        MakeLink(second, end);
        MakeLink(first, second);
        if (OperatingSystem.IsWindows())
        {
            string me = "*" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            Icacls(first, "/inheritance:r", "/grant:r", me + ":(F)", "*S-1-5-18:(F)", "*S-1-5-32-544:(F)", "/L");
            Grant(
                open,
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
                System.Security.AccessControl.FileSystemRights.Modify
            );
        }
        else
        {
            File.SetUnixFileMode(open, (UnixFileMode)0b111_111_111);
        }

        try
        {
            var store = new InstallationKeyStore("acme-cad", KeyScope.Machine, first);
            Assert.True(store.TryLoadOrCreate(out InstallationKey? created, out LicenseError? error), error?.Message);
            using (created)
            {
                Assert.Equal(end, store.Directory);
                Assert.True(File.Exists(Path.Combine(end, "installation.key")));
                Directory.Delete(second);
                MakeLink(second, other);
                Assert.True(store.TryLoad(out InstallationKey? read, out error), error?.Message);
                using (read)
                {
                    Assert.Equal(created!.Thumbprint, read!.Thumbprint);
                }

                Assert.False(File.Exists(Path.Combine(other, "installation.key")));
            }
        }
        finally
        {
            Directory.Delete(first);
            Directory.Delete(second);
        }
    }

    /// <summary>
    /// The kind of drive a machine-scope folder is on decides as the design says, on every OS: a network drive is refused,
    /// and so is a kind that is not known, which .NET answers when statfs fails or names a file system it does not list
    /// (design §10.2, V1d24, the owner's K8, review K3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Each value of DriveType.
    ///
    /// Steps:
    /// - Decide on it.
    ///
    /// Expected:
    /// - Network: "it is on a network location"; NoRootDirectory and Unknown: "its access cannot be read: the kind of its
    ///   file system is not known"; Fixed, Ram, Removable, and CDRom: no problem.
    /// </remarks>
    /// <param name="type">The kind of drive.</param>
    [Theory]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.NoRootDirectory)]
    [InlineData(DriveType.Unknown)]
    [InlineData(DriveType.Fixed)]
    [InlineData(DriveType.Ram)]
    [InlineData(DriveType.Removable)]
    [InlineData(DriveType.CDRom)]
    public void DriveTypeProblem_EachKind_IsDecided(DriveType type)
    {
        string? problem = ProtectedFiles.DriveTypeProblem(type);

        switch (type)
        {
            case DriveType.Network:
                Assert.StartsWith("it is on a network location", problem, StringComparison.Ordinal);
                break;
            case DriveType.NoRootDirectory or DriveType.Unknown:
                Assert.StartsWith("its access cannot be read: the kind of its file system is not known", problem, StringComparison.Ordinal);
                break;
            default:
                Assert.Null(problem);
                break;
        }
    }

    /// <summary>
    /// On Windows, a machine-scope folder reached through a local symbolic link that leads to a UNC path, and one on a
    /// network drive letter, are refused as network locations (design §10.2, V1d24, review K2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows, as an administrator who may open \\localhost\C$, make a symbolic link, and map a drive (as on the GitHub
    ///   runners); elsewhere the test is skipped and says so, and on GitHub Actions it fails without it.
    /// - A machine-scope key made in a folder of the test folder:
    ///   link: a symbolic link (mklink /D) in the test folder to the folder's path under \\localhost\C$;
    ///   mapped: the first free drive letter from Z down mapped (net use) to \\localhost\C$, and the folder's path on it.
    ///
    /// Steps:
    /// - Read the key through that path. Remove the link or the mapping at the end.
    ///
    /// Expected:
    /// - ELM_INSTALLATION_KEY_UNREADABLE with "it is on a network location".
    /// </remarks>
    /// <param name="kind">The case.</param>
    [Theory]
    [InlineData("link")]
    [InlineData("mapped")]
    public void MachineScope_FolderLeadingToTheNetwork_IsRefusedOnWindows(string kind)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "UNC paths and drive letters are Windows only.");
        string folder = Path.Combine(_dir, "net", "machine");
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.Machine, folder).TryLoadOrCreate(out InstallationKey? created, out _));
        created!.Dispose();
        string unc = @"\\localhost\" + folder[0] + "$" + folder[2..];
        string path;
        string drive = string.Empty;
        if (kind == "link")
        {
            path = Path.Combine(_dir, "to-net");
            Run("cmd.exe", "/c", "mklink", "/D", path, unc);
        }
        else
        {
            drive = Enumerable.Range('D', 'Z' - 'D' + 1).Reverse().Select(c => (char)c + ":").First(d => !Directory.Exists(d + @"\"));
            Run("net", "use", drive, @"\\localhost\" + folder[0] + "$");
            path = drive + folder[2..];
        }

        try
        {
            Assert.False(new InstallationKeyStore("acme-cad", KeyScope.Machine, path).TryLoad(out _, out LicenseError? error));
            Assert.Contains("it is on a network location", error!.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (kind == "link")
            {
                Directory.Delete(path);
            }
            else
            {
                Run("net", "use", drive, "/delete", "/y");
            }
        }
    }

    // Makes a symbolic link (Linux, macOS) or a junction (Windows) to a folder.
    internal static void MakeLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            Run("cmd.exe", "/c", "mklink", "/J", link, target);
        }
        else
        {
            File.CreateSymbolicLink(link, target);
        }
    }

    // Runs a command; skips the test where this user may not, except on GitHub Actions.
    private static void Run(string program, params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(program, arguments) { RedirectStandardError = true, RedirectStandardOutput = true })!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            string why = $"{program} {string.Join(' ', arguments.Take(3))} exited with {process.ExitCode}: {output.Trim()}";
            Assert.False(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "On GitHub Actions this test must run. " + why);
            Assert.Skip("This user may not run it. " + why);
        }
    }

    // Adds a rule on a folder itself only.
    [SupportedOSPlatform("windows")]
    private static void Grant(string path, System.Security.Principal.SecurityIdentifier who, System.Security.AccessControl.FileSystemRights rights)
    {
        var info = new DirectoryInfo(path);
        System.Security.AccessControl.DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(
            new System.Security.AccessControl.FileSystemAccessRule(
                who,
                rights,
                System.Security.AccessControl.InheritanceFlags.None,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow
            )
        );
        info.SetAccessControl(security);
    }

    // Runs icacls; skips the test where this user may not, except on GitHub Actions.
    internal static void Icacls(params string[] arguments)
    {
        using var icacls = Process.Start(new ProcessStartInfo("icacls", arguments) { RedirectStandardError = true, RedirectStandardOutput = true })!;
        string output = icacls.StandardOutput.ReadToEnd() + icacls.StandardError.ReadToEnd();
        icacls.WaitForExit();
        if (icacls.ExitCode != 0)
        {
            string why = $"icacls {arguments[1]} exited with {icacls.ExitCode}: {output.Trim()}";
            Assert.False(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "On GitHub Actions this test must run. " + why);
            Assert.Skip("This user may not run icacls so. " + why);
        }
    }

    // Runs chmod +a or -a on macOS and fails the test when it does not succeed.
    private static void Chmod(string operation, string entry, string path)
    {
        using var chmod = Process.Start(new ProcessStartInfo("chmod", [operation, entry, path]) { RedirectStandardError = true })!;
        string error = chmod.StandardError.ReadToEnd();
        chmod.WaitForExit();
        Assert.True(chmod.ExitCode == 0, $"chmod {operation} exited with {chmod.ExitCode}: {error.Trim()}");
    }

    // Runs a command with sudo; skips the test where this user may not, except on GitHub Actions.
    private static void Sudo(params string[] arguments)
    {
        using var sudo = Process.Start(
            new ProcessStartInfo("sudo", ["-n", .. arguments]) { RedirectStandardError = true, RedirectStandardOutput = true }
        )!;
        string error = sudo.StandardError.ReadToEnd();
        sudo.WaitForExit();
        if (sudo.ExitCode != 0)
        {
            string why = $"sudo -n {arguments[0]} exited with {sudo.ExitCode}: {error.Trim()}";
            Assert.False(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "On GitHub Actions this test must run. " + why);
            Assert.Skip("This machine does not let the test run " + arguments[0] + " as root. " + why);
        }
    }

    // A folder of mode 0700 in a parent of mode 0755, both made by this user.
    [UnsupportedOSPlatform("windows")]
    private (string Parent, string Folder) MachineFolders()
    {
        string parent = Path.Combine(_dir, "vendor");
        Directory.CreateDirectory(parent, (UnixFileMode)0b111_101_101);
        string folder = Path.Combine(parent, "machine");
        Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (parent, folder);
    }

    // Gives a folder to a user with sudo chown; skips the test where this user may not, except on GitHub Actions.
    private static void GiveTo(string user, string path)
    {
        using var sudo = Process.Start(
            new ProcessStartInfo("sudo", ["-n", "chown", user, path]) { RedirectStandardError = true, RedirectStandardOutput = true }
        )!;
        string error = sudo.StandardError.ReadToEnd();
        sudo.WaitForExit();
        if (sudo.ExitCode != 0)
        {
            string why = $"sudo -n chown exited with {sudo.ExitCode}: {error.Trim()}";
            Assert.False(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "On GitHub Actions this test must run. " + why);
            Assert.Skip("This machine does not let the test run chown as root. " + why);
        }
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
