using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace ElasticLM.Client;

// design §10.2.

/// <summary>
/// Folders and secret files that only their owner can read: the installation key, the pins, the borrowed
/// leases, and the server's administrator token.
/// </summary>
/// <remarks>
/// On Linux and macOS a folder is created with mode 0700 and a secret file with 0600. On Windows a secret is protected with
/// DPAPI; a machine-scope folder also gets an access list of Administrators, SYSTEM, and the creating user only, because
/// DPAPI's machine scope can be opened by every local user and <c>ProgramData</c> is readable by every local user. A
/// machine-scope folder that exists already is used only when <see cref="CheckDirectory"/> finds that no other user could
/// have read or replaced it: any user can create a folder under <c>ProgramData</c> before the program first runs, and on
/// Linux and macOS another user can create a folder of mode 0700 wherever they may write.
/// </remarks>
internal static class ProtectedFiles
{
    // NT SERVICE\TrustedInstaller, the owner of C:\ on Windows 11.
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    // DELETE, FILE_DELETE_CHILD, WRITE_DAC, and WRITE_OWNER: what lets a holder rename, delete, or re-own a folder or what is
    // in it. Creating in a folder is not among them. A generic right (GENERIC_ALL) is kept only in an entry that is only
    // inherited, which the check skips: Windows stores it as the rights it stands for in an entry of the folder itself
    // (icacls /grant (GA) on Windows 11 build 26300 wrote FullControl, 0x1F01FF; docs/rows/V1d24.md).
    private const int ReplaceRights = 0x10000 | 0x40 | 0x40000 | 0x80000;

    // design §10.2.

    /// <summary>
    /// Checks that a machine-scope folder can be read and changed only by Administrators, SYSTEM, and the user running this
    /// program. On Windows the folder's owner must be one of them, the folder must not inherit its access list, and every
    /// rule that allows access must name one of them; every folder above it must be owned by one of them or by
    /// TrustedInstaller, and must let no one else delete, rename, or change the access to what is in it. On Linux and macOS
    /// the folder must be owned by the user running this program or root and give no access to group or others; every
    /// folder above it up to the root must be owned by that user or root and must not be writable by group or others; on
    /// macOS no entry of an extended access list may let anyone else into the folder, or delete or change what is in a
    /// folder above it. When a symbolic link or a junction leads elsewhere, the folder it leads to is checked in the same way.
    /// A folder on a network location is refused.
    /// </summary>
    /// <param name="path">The folder.</param>
    /// <param name="scope">The scope; a user-scope folder is protected by the user's profile and is not checked.</param>
    /// <param name="folder">The folder to use from now on: in the machine scope, when the folder passes, its path with every
    /// symbolic link and junction resolved, so that a link changed later does not lead elsewhere; otherwise the full path.</param>
    /// <returns><see langword="null"/> when the folder is safe or does not exist yet, otherwise what is wrong and how to fix it.</returns>
    public static string? CheckDirectory(string path, KeyScope scope, out string folder)
    {
        string full = Path.GetFullPath(path);
        folder = full;
        if (scope == KeyScope.User || !Directory.Exists(path))
        {
            return null;
        }

        Problem? problem;
        try
        {
            (problem, string resolved) = Check(full);
            if (problem is null)
            {
                folder = resolved;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            problem = new Problem("its access cannot be read: " + e.Message, Above: false);
        }

        // design §10.2 (V1d24, review K6): removing the folder loses its installation key and does not mend a folder above it.
        return problem switch
        {
            null => null,
            { Above: true } =>
                $"The folder '{full}' is not safe for secrets: {problem.Text} Correct the owner or the access of that folder, or use a folder elsewhere; removing this folder does not help.",
            _ =>
                $"The folder '{full}' is not safe for secrets: {problem.Text} Another user may have created it; remove it or restrict it, then start again.",
        };
    }

    /// <summary>
    /// Decides on the kind of drive that a machine-scope folder is on.
    /// </summary>
    /// <param name="type">The kind, as <see cref="DriveInfo.DriveType"/> gives it.</param>
    /// <returns><see langword="null"/> for a kind that may hold the folder, otherwise what is wrong.</returns>
    internal static string? DriveTypeProblem(DriveType type) =>
        type switch
        {
            DriveType.Network => "it is on a network location; a machine-scope folder must be on a local disk.",

            // design §10.2 (V1d24, review K3): on Linux and macOS DriveInfo answers NoRootDirectory or Unknown instead of an
            // error when statfs fails, and Unknown for a file system its table does not name; neither is known to be local.
            DriveType.NoRootDirectory or DriveType.Unknown =>
                "its access cannot be read: the kind of its file system is not known, so it may be on a network location.",
            _ => null,
        };

    // design §10.2 (V1d24, review K1 and K3, the owner's K8): the folder as named and, when a symbolic link or a junction leads
    // elsewhere, the folder it leads to with every folder above that one, as OpenSSH's safe_path checks the real path; the
    // folder it leads to may not be on a network location. The caller goes on with the resolved path only.
    private static (Problem? Problem, string Resolved) Check(string full)
    {
        string resolved = OperatingSystem.IsWindows() ? WindowsPaths.FinalPath(full) : UnixOwner.RealPath(full);
        if (DriveTypeProblem(KindOf(resolved)) is string kind)
        {
            return (new Problem(kind, Above: false), resolved);
        }

        Problem? problem = OperatingSystem.IsWindows() ? CheckWindows(full) : CheckUnix(full);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (problem is null && !string.Equals(resolved, full, comparison))
        {
            problem = OperatingSystem.IsWindows() ? CheckWindows(resolved) : CheckUnix(resolved);
            if (problem is not null)
            {
                problem = problem with { Text = $"it leads to '{resolved}', and {problem.Text}" };
            }
        }

        return (problem, resolved);
    }

    // The kind of drive of a resolved path. On Windows a UNC path (\\server\share\…, which is also where a network drive or a
    // SUBST onto a network path leads) is a network one, and a drive letter's kind is GetDriveType's; on Linux and macOS it is
    // the kind .NET gives the file system that statfs names (Interop.MountPoints.FormatInfo.cs, dotnet/runtime release/10.0).
    private static DriveType KindOf(string resolved) =>
        OperatingSystem.IsWindows()
            ? resolved.StartsWith(@"\\", StringComparison.Ordinal)
                ? DriveType.Network
                : new DriveInfo(Path.GetPathRoot(resolved)!).DriveType
            : new DriveInfo(resolved).DriveType;

    /// <summary>
    /// Creates a folder for secrets if it does not exist.
    /// </summary>
    /// <exception cref="IOException">The folder cannot be created (also <see cref="UnauthorizedAccessException"/>).</exception>
    public static void CreateDirectory(string path, KeyScope scope)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }

        if (scope == KeyScope.User || Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            return;
        }

        string full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) is string parent)
        {
            Directory.CreateDirectory(parent);
        }

        new DirectoryInfo(full).Create(OwnerOnly());
    }

    /// <summary>
    /// Writes a new secret file; an existing file is not replaced. A file that this call created but could not fill is deleted.
    /// </summary>
    /// <returns><see langword="false"/> when the file exists already.</returns>
    /// <exception cref="IOException">The file cannot be written (also <see cref="UnauthorizedAccessException"/>).</exception>
    public static bool TryCreateSecret(string path, byte[] secret, KeyScope scope, byte[] purpose)
    {
        byte[] stored = OperatingSystem.IsWindows() ? Dpapi.Protect(secret, scope == KeyScope.Machine, purpose) : [.. secret];
        bool created = false;
        try
        {
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

            using var stream = new FileStream(path, options);
            created = true;
            stream.Write(stored);
            return true;
        }
        catch (Exception e) when (created && e is IOException or UnauthorizedAccessException)
        {
            // design §12.6.2 (V1d24): a file this call made but could not fill holds part of a secret; it does not stay.
            FileReplacement.DeleteQuietly(path);
            throw;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stored);
        }
    }

    /// <summary>
    /// Reads a secret file.
    /// </summary>
    /// <returns>The secret, or <see langword="null"/> when the file is missing, larger than <paramref name="maxLength"/>,
    /// unreadable, or protected for another user, machine, or purpose.</returns>
    public static byte[]? TryReadSecret(string path, int maxLength, byte[] purpose)
    {
        byte[] stored;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maxLength)
            {
                return null;
            }

            stored = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return OperatingSystem.IsWindows() ? Dpapi.Unprotect(stored, purpose) : stored;
    }

    [SupportedOSPlatform("windows")]
    private static Problem? CheckWindows(string path)
    {
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        SecurityIdentifier[] allowed =
        [
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            current.User!,
        ];
        DirectorySecurity security = new DirectoryInfo(path).GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !allowed.Contains(owner))
        {
            return new Problem("it is owned by another user.", Above: false);
        }

        if (!security.AreAccessRulesProtected)
        {
            return new Problem("it inherits access from its parent folder.", Above: false);
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && !allowed.Contains((SecurityIdentifier)rule.IdentityReference))
            {
                return new Problem($"it gives access to {Named((SecurityIdentifier)rule.IdentityReference)}.", Above: false);
            }
        }

        // design §10.2 (V1d24): every folder above may be owned by TrustedInstaller too (C:\ is), and may let others create in
        // it, as C:\ and ProgramData do; a rule that lets anyone else delete, rename, or change a folder above would let them
        // put another folder in this one's place. Windows renames a folder only with DELETE on it or FILE_DELETE_CHILD on its
        // parent (measured on Windows 11 build 26300, docs/rows/V1d24.md).
        SecurityIdentifier[] trustedAbove = [.. allowed, new SecurityIdentifier(TrustedInstaller)];
        for (string? above = Path.GetDirectoryName(path); above is not null; above = Path.GetDirectoryName(above))
        {
            string where = Path.GetDirectoryName(path) == above ? $"its parent folder '{above}'" : $"the folder '{above}' above it";
            DirectorySecurity aboveSecurity = new DirectoryInfo(above).GetAccessControl();
            if (aboveSecurity.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier aboveOwner || !trustedAbove.Contains(aboveOwner))
            {
                return new Problem($"{where} is owned by another user.", Above: true);
            }

            foreach (
                FileSystemAccessRule rule in aboveSecurity.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            )
            {
                if (
                    rule.AccessControlType == AccessControlType.Allow
                    && !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)
                    && ((int)rule.FileSystemRights & ReplaceRights) != 0
                    && !trustedAbove.Contains((SecurityIdentifier)rule.IdentityReference)
                )
                {
                    return new Problem(
                        $"{where} lets {Named((SecurityIdentifier)rule.IdentityReference)} delete or replace what is in it.",
                        Above: true
                    );
                }
            }
        }

        return null;
    }

    // An account's name beside its SID, as "NT AUTHORITY\Authenticated Users (S-1-5-11)"; the SID alone when it has no name.
    [SupportedOSPlatform("windows")]
    private static string Named(SecurityIdentifier sid)
    {
        // Translate throws IdentityNotMappedException for a SID without a name, and a SystemException (that type itself) for a
        // Win32 error (SecurityIdentifier.Translate's documentation).
        try
        {
            return $"{sid.Translate(typeof(NTAccount)).Value} ({sid.Value})";
        }
        catch (Exception e) when (e is IdentityNotMappedException || e.GetType() == typeof(SystemException))
        {
            return sid.Value;
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static Problem? CheckUnix(string path)
    {
        const uint Root = 0;
        const UnixFileMode Others =
            UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute;

        // design §10.2 (V1d24): the owner of the folder could change its mode and replace what is in it, and the owner of the
        // parent could replace the folder; only this user and root are trusted with either.
        uint self = UnixOwner.EffectiveUserId;
        uint owner = UnixOwner.OwnerOf(path);
        if (owner != self && owner != Root)
        {
            return new Problem("it is owned by another user.", Above: false);
        }

        if ((File.GetUnixFileMode(path) & Others) != 0)
        {
            return new Problem("it gives access to group or others (it must be mode 0700).", Above: false);
        }

        if (OperatingSystem.IsMacOS() && UnixOwner.MacAllowedOther(path, self, anyRight: true) is string other)
        {
            return new Problem($"its access list gives access to {other}.", Above: false);
        }

        // design §10.2 (V1d24): every folder up to / as OpenSSH's safe_path checks them; who may write to one of them may
        // rename what is in it. The sticky bit is not an exception, so a folder under /tmp is refused.
        for (string? above = Path.GetDirectoryName(path); above is not null; above = Path.GetDirectoryName(above))
        {
            string where = Path.GetDirectoryName(path) == above ? $"its parent folder '{above}'" : $"the folder '{above}' above it";
            uint aboveOwner = UnixOwner.OwnerOf(above);
            if (aboveOwner != self && aboveOwner != Root)
            {
                return new Problem($"{where} is owned by another user.", Above: true);
            }

            if ((File.GetUnixFileMode(above) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                return new Problem($"{where} is writable by group or others.", Above: true);
            }

            if (OperatingSystem.IsMacOS() && UnixOwner.MacAllowedOther(above, self, anyRight: false) is string replacer)
            {
                return new Problem($"{where} has an access list that lets {replacer} delete or replace what is in it.", Above: true);
            }
        }

        return null;
    }

    // What is wrong, and whether it is a folder above the one checked, which the reader mends there and not by removing this one.
    private sealed record Problem(string Text, bool Above);

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity OwnerOnly()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        foreach (
            IdentityReference who in new IdentityReference[]
            {
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                current.User!,
            }
        )
        {
            security.AddAccessRule(
                new FileSystemAccessRule(who, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow)
            );
        }

        return security;
    }
}
