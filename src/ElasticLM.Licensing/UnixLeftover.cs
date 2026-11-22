using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ElasticLM.Licensing;

// design §11.4 step 8 (V1d24d, the owner's 24d-6 and 24d-6a): a FileStream open waits in open(2) for a writer when the path
// is a FIFO, .NET has no non-blocking open, and .NET 10 tells no FIFO from a regular file (FileInfo.Attributes of a FIFO is
// Normal: measured on Debian with .NET 10.0.14 in V1d24d). So libc is called: open with O_NONBLOCK and O_NOFOLLOW, the type of
// what was opened from that descriptor, an exclusive flock that fails at once while a writer's FileStream holds its shared
// flock, and unlink. The library names are UnixOwner's in ElasticLM.Client: libc.so.6 on Linux (musl answers that name with
// itself), libSystem on macOS, where fstat with the 64-bit inode layout is fstat$INODE64 on x64.

/// <summary>
/// Removes a leftover temporary file on Linux and macOS without waiting and without following a link.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static unsafe partial class UnixLeftover
{
    private const string LinuxLibc = "libc.so.6";
    private const string MacLibc = "/usr/lib/libSystem.B.dylib";

    // <fcntl.h>: O_RDONLY is 0 on both. Linux (asm-generic/fcntl.h, x64): O_NONBLOCK 04000, O_NOFOLLOW 0400000, O_CLOEXEC
    // 02000000; arm64 (arch/arm64/include/uapi/asm/fcntl.h) moves O_NOFOLLOW to 0100000. macOS (sys/fcntl.h): O_NONBLOCK
    // 0x4, O_NOFOLLOW 0x100, O_CLOEXEC 0x1000000.
    private const int LinuxNonBlock = 0x800;
    private const int LinuxNoFollowX64 = 0x20000;
    private const int LinuxNoFollowArm64 = 0x8000;
    private const int LinuxCloseOnExec = 0x80000;
    private const int MacNonBlock = 0x4;
    private const int MacNoFollow = 0x100;
    private const int MacCloseOnExec = 0x1000000;

    // <sys/file.h> of both: LOCK_EX 2, LOCK_NB 4.
    private const int LockExclusiveNow = 2 | 4;

    // statx(2) of the descriptor itself: AT_EMPTY_PATH, STATX_TYPE, and stx_mode at byte 28 of the 256-byte struct statx
    // (<linux/stat.h>: stx_mask, stx_blksize, stx_attributes, stx_nlink, stx_uid, stx_gid, stx_mode). glibc has statx from
    // 2.28, the oldest glibc the release supports, and fstat only from 2.33.
    private const int EmptyPath = 0x1000;
    private const uint StatxType = 0x1;
    private const int StatxModeOffset = 28;

    // fstat(2) of macOS with 64-bit inodes (<sys/stat.h>, struct stat): st_dev (4 bytes), then st_mode at byte 4 of 144.
    private const int MacModeOffset = 4;

    private const int BufferLength = 256;

    // <sys/stat.h> of both: S_IFMT and S_IFREG.
    private const int TypeMask = 0xF000;
    private const int RegularFile = 0x8000;

    /// <summary>
    /// Removes a file if it is a regular file that no other open file holds a lock on, or a link itself. Anything else (a
    /// FIFO, a socket, a device, a file a writer holds) stays, as does a file that cannot be opened, and every file on an
    /// architecture other than x64 and Arm64. Nothing is thrown.
    /// </summary>
    /// <param name="path">The file.</param>
    public static void TryRemove(string path)
    {
        if (OpenFlags is not int flags)
        {
            return;
        }

        try
        {
            int file = OperatingSystem.IsMacOS() ? MacOpen(path, flags) : LinuxOpen(path, flags);
            if (file < 0)
            {
                // O_NOFOLLOW refuses a link: the link itself is removed, never what it points to.
                if (new FileInfo(path).LinkTarget is not null)
                {
                    Unlink(path);
                }

                return;
            }

            try
            {
                if (IsRegularFile(file) && Lock(file) == 0)
                {
                    Unlink(path);
                }
            }
            finally
            {
                _ = OperatingSystem.IsMacOS() ? MacClose(file) : LinuxClose(file);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            // A C library without one of the calls (statx: glibc before 2.28, musl before 1.2.5): the file stays.
        }
    }

    // The flags of open, or null on an architecture whose O_NOFOLLOW is not one of the two read above: 32-bit ARM and ppc64le
    // give 0x20000 to O_LARGEFILE and O_DIRECT and use 0x8000, and others are not read, so nothing is cleaned there.
    private static int? OpenFlags =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 or Architecture.Arm64 when OperatingSystem.IsMacOS() => MacNonBlock | MacNoFollow | MacCloseOnExec,
            Architecture.X64 => LinuxNonBlock | LinuxNoFollowX64 | LinuxCloseOnExec,
            Architecture.Arm64 => LinuxNonBlock | LinuxNoFollowArm64 | LinuxCloseOnExec,
            _ => null,
        };

    private static bool IsRegularFile(int file)
    {
        byte* buffer = stackalloc byte[BufferLength];
        new Span<byte>(buffer, BufferLength).Clear();
        int status = OperatingSystem.IsMacOS()
            ? (RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacFstatX64(file, buffer) : MacFstat(file, buffer))
            : LinuxStatx(file, string.Empty, EmptyPath, StatxType, buffer);

        // statx fills only what the file system gives; without STATX_TYPE in stx_mask the type is unknown.
        if (status != 0 || (!OperatingSystem.IsMacOS() && (Unsafe.ReadUnaligned<uint>(buffer) & StatxType) == 0))
        {
            return false;
        }

        int mode = Unsafe.ReadUnaligned<ushort>(buffer + (OperatingSystem.IsMacOS() ? MacModeOffset : StatxModeOffset));
        return (mode & TypeMask) == RegularFile;
    }

    private static int Lock(int file) => OperatingSystem.IsMacOS() ? MacFlock(file, LockExclusiveNow) : LinuxFlock(file, LockExclusiveNow);

    private static void Unlink(string path) => _ = OperatingSystem.IsMacOS() ? MacUnlink(path) : LinuxUnlink(path);

    [LibraryImport(LinuxLibc, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinuxOpen(string path, int flags);

    [LibraryImport(LinuxLibc, EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinuxStatx(int directory, string path, int flags, uint mask, byte* result);

    [LibraryImport(LinuxLibc, EntryPoint = "flock")]
    private static partial int LinuxFlock(int file, int operation);

    [LibraryImport(LinuxLibc, EntryPoint = "unlink", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinuxUnlink(string path);

    [LibraryImport(LinuxLibc, EntryPoint = "close")]
    private static partial int LinuxClose(int file);

    [LibraryImport(MacLibc, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MacOpen(string path, int flags);

    [LibraryImport(MacLibc, EntryPoint = "fstat")]
    private static partial int MacFstat(int file, byte* result);

    [LibraryImport(MacLibc, EntryPoint = "fstat$INODE64")]
    private static partial int MacFstatX64(int file, byte* result);

    [LibraryImport(MacLibc, EntryPoint = "flock")]
    private static partial int MacFlock(int file, int operation);

    [LibraryImport(MacLibc, EntryPoint = "unlink", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MacUnlink(string path);

    [LibraryImport(MacLibc, EntryPoint = "close")]
    private static partial int MacClose(int file);
}
