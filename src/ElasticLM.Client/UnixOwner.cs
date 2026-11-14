using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ElasticLM.Client;

// design §10.2 (V1d24): .NET 10 has no API for the owner of a Unix file, so libc is called. glibc's library is libc.so.6,
// and musl's loader answers a dlopen of that name with musl itself (the reserved names of load_library in ldso/dynlink.c). A
// NativeAOT executable resolves the name at run time: the bare "libc" would become libc.so, which a glibc system has only as
// a linker script, if at all (NativeLibrary.NativeAot.cs and LibraryNameVariation.Unix.cs of dotnet/runtime release/10.0
// map no name to libc.so.6). macOS keeps libc in libSystem; on x64, stat with the 64-bit inode layout is stat$INODE64.

/// <summary>
/// The owner of a file on Linux and macOS, and the user this process runs as.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static unsafe partial class UnixOwner
{
    private const string LinuxLibc = "libc.so.6";
    private const string MacLibc = "/usr/lib/libSystem.B.dylib";

    // statx(2): AT_FDCWD, STATX_UID, and stx_uid at byte 20 of the 256-byte struct statx (<linux/stat.h>: stx_mask,
    // stx_blksize, stx_attributes, stx_nlink, stx_uid).
    private const int CurrentDirectory = -100;
    private const uint StatxUid = 0x8;
    private const int StatxUidOffset = 20;

    // stat(2) of macOS with 64-bit inodes (<sys/stat.h>, struct stat): st_dev, st_mode, st_nlink, st_ino, then st_uid at
    // byte 16 of 144.
    private const int MacUidOffset = 16;

    private const int BufferLength = 256;
    private const int RealPathLength = 4096;

    private const int NoEntry = 2; // ENOENT: acl_get_file's answer for a file without an extended access list.
    private const int AclTypeExtended = 0x100;
    private const int AclFirstEntry = 0;
    private const int AclNextEntry = -1;
    private const int AclExtendedAllow = 1;
    private const int AclDelete = 1 << 4;
    private const int AclDeleteChild = 1 << 6;
    private const int AclWriteSecurity = 1 << 12;
    private const int AclChangeOwner = 1 << 13;
    private const int AclEntryOnlyInherit = 1 << 8;
    private const int IdTypeUid = 0;

    /// <summary>Gets the effective user ID of this process.</summary>
    /// <exception cref="IOException">The C library cannot be called.</exception>
    public static uint EffectiveUserId
    {
        get
        {
            try
            {
                return OperatingSystem.IsMacOS() ? MacGeteuid() : LinuxGeteuid();
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                throw new IOException("The C library cannot be called to read this process's user: " + e.Message, e);
            }
        }
    }

    /// <summary>Reads the user ID of a file's owner, following a symbolic link as <c>stat</c> does.</summary>
    /// <param name="path">The file or folder.</param>
    /// <returns>The owner's user ID.</returns>
    /// <exception cref="IOException">The owner cannot be read, also when the C library has no such call.</exception>
    public static uint OwnerOf(string path)
    {
        byte* buffer = stackalloc byte[BufferLength];
        new Span<byte>(buffer, BufferLength).Clear();
        int status;
        try
        {
            status = OperatingSystem.IsMacOS()
                ? (RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacStatX64(path, buffer) : MacStat(path, buffer))
                : LinuxStatx(CurrentDirectory, path, 0, StatxUid, buffer);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // A C library without statx (glibc before 2.28, musl before 1.2.5) or without the library name.
            throw new IOException($"The C library cannot be called to read the owner of '{path}': {e.Message}", e);
        }

        if (status != 0)
        {
            throw new IOException($"The owner of '{path}' cannot be read (error {Marshal.GetLastPInvokeError()}).");
        }

        // statx fills only what the file system gives; without STATX_UID in stx_mask the owner is unknown, and errno is
        // not set.
        if (!OperatingSystem.IsMacOS() && (Unsafe.ReadUnaligned<uint>(buffer) & StatxUid) == 0)
        {
            throw new IOException($"The owner of '{path}' cannot be read: the file system did not give it.");
        }

        return Unsafe.ReadUnaligned<uint>(buffer + (OperatingSystem.IsMacOS() ? MacUidOffset : StatxUidOffset));
    }

    /// <summary>Reads the path of a folder with every symbolic link on the way resolved, as <c>realpath</c> gives it.</summary>
    /// <param name="path">The folder, as a full path.</param>
    /// <returns>The resolved path.</returns>
    /// <exception cref="IOException">The path cannot be resolved.</exception>
    public static string RealPath(string path)
    {
        // PATH_MAX is 4096 on Linux and 1024 on macOS; the larger fits both.
        byte* buffer = stackalloc byte[RealPathLength];
        nint result;
        try
        {
            result = OperatingSystem.IsMacOS() ? MacRealPath(path, buffer) : LinuxRealPath(path, buffer);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new IOException($"The C library cannot be called to resolve '{path}': {e.Message}", e);
        }

        return result != 0
            ? Marshal.PtrToStringUTF8((nint)buffer)!
            : throw new IOException($"Where '{path}' leads cannot be read (error {Marshal.GetLastPInvokeError()}).");
    }

    // design §10.2 (V1d24, the owner's K9): macOS keeps an extended access list beside the mode (chmod +a), whose allow entries
    // can let another user delete or rename what is in a folder whatever its mode. <sys/acl.h> of Apple's Libc: ACL_TYPE_EXTENDED
    // 0x100, ACL_FIRST_ENTRY 0, ACL_NEXT_ENTRY -1, ACL_EXTENDED_ALLOW 1, ACL_DELETE 1<<4, ACL_DELETE_CHILD 1<<6,
    // ACL_WRITE_SECURITY 1<<12, ACL_CHANGE_OWNER 1<<13, ACL_ENTRY_ONLY_INHERIT 1<<8; <membership.h>: ID_TYPE_UID 0.

    /// <summary>
    /// On macOS, finds an allow entry of a folder's extended access list for anyone other than root and the given user.
    /// </summary>
    /// <param name="path">The folder.</param>
    /// <param name="self">The user ID of this process.</param>
    /// <param name="anyRight"><see langword="true"/> to count an entry that allows anything; <see langword="false"/> to count
    /// only an entry that allows deleting the folder or what is in it, changing its access list, or changing its owner.</param>
    /// <returns>Whom the entry names (<c>user 501</c>, <c>group 20</c>), or <see langword="null"/> when there is none.</returns>
    /// <exception cref="IOException">The access list cannot be read.</exception>
    [SupportedOSPlatform("macos")]
    public static string? MacAllowedOther(string path, uint self, bool anyRight)
    {
        nint acl;
        try
        {
            acl = AclGetFile(path, AclTypeExtended);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new IOException($"The C library cannot be called to read the access list of '{path}': {e.Message}", e);
        }

        if (acl == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            return error == NoEntry ? null : throw new IOException($"The access list of '{path}' cannot be read (error {error}).");
        }

        try
        {
            for (int entryId = AclFirstEntry; AclGetEntry(acl, entryId, out nint entry) == 0; entryId = AclNextEntry)
            {
                if (AclGetTagType(entry, out int tag) != 0)
                {
                    throw new IOException($"An entry of the access list of '{path}' cannot be read.");
                }

                if (tag != AclExtendedAllow)
                {
                    continue;
                }

                if (AclGetPermset(entry, out nint permset) != 0 || AclGetFlagsetNp(entry, out nint flagset) != 0)
                {
                    throw new IOException($"An entry of the access list of '{path}' cannot be read.");
                }

                // An entry only inherited by what is made in the folder later does not apply to the folder; above, it is not
                // counted, as Windows skips an entry that is only inherited. For the folder itself it is counted: what the
                // program makes in it would take it.
                if (!anyRight && Has(path, AclGetFlagNp(flagset, AclEntryOnlyInherit)))
                {
                    continue;
                }

                bool counts =
                    anyRight
                    || Has(path, AclGetPermNp(permset, AclDelete))
                    || Has(path, AclGetPermNp(permset, AclDeleteChild))
                    || Has(path, AclGetPermNp(permset, AclWriteSecurity))
                    || Has(path, AclGetPermNp(permset, AclChangeOwner));
                if (!counts)
                {
                    continue;
                }

                nint qualifier = AclGetQualifier(entry);
                if (qualifier == 0)
                {
                    return "an entry without a user or group";
                }

                try
                {
                    if (MbrUuidToId((byte*)qualifier, out uint id, out int idType) != 0)
                    {
                        return "a user or group that cannot be named";
                    }

                    if (idType == IdTypeUid && (id == self || id == 0))
                    {
                        continue;
                    }

                    return (idType == IdTypeUid ? "user " : "group ") + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                finally
                {
                    _ = AclFree(qualifier);
                }
            }

            return null;
        }
        finally
        {
            _ = AclFree(acl);
        }
    }

    // acl_get_perm_np and acl_get_flag_np answer 1 for set, 0 for not set, and -1 for an error.
    private static bool Has(string path, int answer) =>
        answer switch
        {
            1 => true,
            0 => false,
            _ => throw new IOException($"An entry of the access list of '{path}' cannot be read."),
        };

    [LibraryImport(MacLibc, EntryPoint = "acl_get_flagset_np")]
    private static partial int AclGetFlagsetNp(nint entry, out nint flagset);

    [LibraryImport(MacLibc, EntryPoint = "acl_get_flag_np")]
    private static partial int AclGetFlagNp(nint flagset, int flag);

    [LibraryImport(LinuxLibc, EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint LinuxRealPath(string path, byte* resolved);

    [LibraryImport(MacLibc, EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint MacRealPath(string path, byte* resolved);

    [LibraryImport(MacLibc, EntryPoint = "acl_get_file", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint AclGetFile(string path, int type);

    [LibraryImport(MacLibc, EntryPoint = "acl_get_entry")]
    private static partial int AclGetEntry(nint acl, int entryId, out nint entry);

    [LibraryImport(MacLibc, EntryPoint = "acl_get_tag_type")]
    private static partial int AclGetTagType(nint entry, out int tag);

    [LibraryImport(MacLibc, EntryPoint = "acl_get_permset")]
    private static partial int AclGetPermset(nint entry, out nint permset);

    [LibraryImport(MacLibc, EntryPoint = "acl_get_perm_np")]
    private static partial int AclGetPermNp(nint permset, int perm);

    [LibraryImport(MacLibc, EntryPoint = "acl_get_qualifier")]
    private static partial nint AclGetQualifier(nint entry);

    [LibraryImport(MacLibc, EntryPoint = "acl_free")]
    private static partial int AclFree(nint value);

    [LibraryImport(MacLibc, EntryPoint = "mbr_uuid_to_id")]
    private static partial int MbrUuidToId(byte* uuid, out uint id, out int idType);

    [LibraryImport(LinuxLibc, EntryPoint = "geteuid")]
    private static partial uint LinuxGeteuid();

    [LibraryImport(LinuxLibc, EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LinuxStatx(int directory, string path, int flags, uint mask, byte* result);

    [LibraryImport(MacLibc, EntryPoint = "geteuid")]
    private static partial uint MacGeteuid();

    [LibraryImport(MacLibc, EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MacStat(string path, byte* result);

    [LibraryImport(MacLibc, EntryPoint = "stat$INODE64", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MacStatX64(string path, byte* result);
}
