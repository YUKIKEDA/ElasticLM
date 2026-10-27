using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace ElasticLM.Client;

/// <summary>
/// Folders and secret files that only their owner can read (design §10.2): the installation key, the pins, the borrowed
/// leases, and the server's administrator token.
/// </summary>
/// <remarks>
/// On Linux and macOS a folder is created with mode 0700 and a secret file with 0600. On Windows a secret is protected with
/// DPAPI; a machine-scope folder also gets an access list of Administrators, SYSTEM, and the creating user only, because
/// DPAPI's machine scope can be opened by every local user and <c>ProgramData</c> is readable by every local user. A
/// machine-scope folder that exists already is used only when <see cref="CheckDirectory"/> finds that no other user could
/// have read or replaced it: any user can create a folder under <c>ProgramData</c> before the program first runs.
/// </remarks>
internal static class ProtectedFiles
{
    /// <summary>
    /// Checks that a machine-scope folder can be read and changed only by Administrators, SYSTEM, and the user running this
    /// program (design §10.2). On Windows the folder's owner and its parent's owner must be one of them, the folder must not
    /// inherit its access list, and every rule that allows access must name one of them. On Linux and macOS the folder must
    /// give no access to group or others, and its parent must not be writable by them.
    /// </summary>
    /// <param name="path">The folder.</param>
    /// <param name="scope">The scope; a user-scope folder is protected by the user's profile and is not checked.</param>
    /// <returns><see langword="null"/> when the folder is safe or does not exist yet, otherwise what is wrong and how to fix it.</returns>
    public static string? CheckDirectory(string path, KeyScope scope)
    {
        if (scope == KeyScope.User || !Directory.Exists(path))
        {
            return null;
        }

        string full = Path.GetFullPath(path);
        string? problem;
        try
        {
            problem = OperatingSystem.IsWindows() ? CheckWindows(full) : CheckUnix(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            problem = "its access cannot be read: " + e.Message;
        }

        return problem is null
            ? null
            : $"The folder '{full}' is not safe for secrets: {problem} Another user may have created it; remove it or restrict it, then start again.";
    }

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
    /// Writes a new secret file; an existing file is not replaced.
    /// </summary>
    /// <returns><see langword="false"/> when the file exists already.</returns>
    /// <exception cref="IOException">The file cannot be written (also <see cref="UnauthorizedAccessException"/>).</exception>
    public static bool TryCreateSecret(string path, byte[] secret, KeyScope scope, byte[] purpose)
    {
        byte[] stored = OperatingSystem.IsWindows() ? Dpapi.Protect(secret, scope == KeyScope.Machine, purpose) : [.. secret];
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
            stream.Write(stored);
            return true;
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
    private static string? CheckWindows(string path)
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
            return "it is owned by another user.";
        }

        if (!security.AreAccessRulesProtected)
        {
            return "it inherits access from its parent folder.";
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && !allowed.Contains((SecurityIdentifier)rule.IdentityReference))
            {
                return $"it gives access to {rule.IdentityReference.Value}.";
            }
        }

        if (
            Path.GetDirectoryName(path) is string parent
            && (
                new DirectoryInfo(parent).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier parentOwner
                || !allowed.Contains(parentOwner)
            )
        )
        {
            return $"its parent folder '{parent}' is owned by another user.";
        }

        return null;
    }

    [UnsupportedOSPlatform("windows")]
    private static string? CheckUnix(string path)
    {
        const UnixFileMode Others =
            UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute;
        if ((File.GetUnixFileMode(path) & Others) != 0)
        {
            return "it gives access to group or others (it must be mode 0700).";
        }

        if (Path.GetDirectoryName(path) is string parent && (File.GetUnixFileMode(parent) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            return $"its parent folder '{parent}' is writable by group or others.";
        }

        return null;
    }

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
