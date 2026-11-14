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

    /// <summary>Gets the effective user ID of this process.</summary>
    public static uint EffectiveUserId => OperatingSystem.IsMacOS() ? MacGeteuid() : LinuxGeteuid();

    /// <summary>Reads the user ID of a file's owner, following a symbolic link as <c>stat</c> does.</summary>
    /// <param name="path">The file or folder.</param>
    /// <returns>The owner's user ID.</returns>
    /// <exception cref="IOException">The owner cannot be read.</exception>
    public static uint OwnerOf(string path)
    {
        byte* buffer = stackalloc byte[BufferLength];
        new Span<byte>(buffer, BufferLength).Clear();
        if (OperatingSystem.IsMacOS())
        {
            int status = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacStatX64(path, buffer) : MacStat(path, buffer);
            return status == 0 ? Unsafe.ReadUnaligned<uint>(buffer + MacUidOffset) : throw Failed(path);
        }

        return LinuxStatx(CurrentDirectory, path, 0, StatxUid, buffer) == 0 && (Unsafe.ReadUnaligned<uint>(buffer) & StatxUid) != 0
            ? Unsafe.ReadUnaligned<uint>(buffer + StatxUidOffset)
            : throw Failed(path);
    }

    private static IOException Failed(string path) => new($"The owner of '{path}' cannot be read (error {Marshal.GetLastPInvokeError()}).");

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
