using System.Runtime.InteropServices;

namespace ElasticLM.Issuing;

// design §18.1 (V1d16, the owner's answers a to c on Issue #335): a log file takes its name by a rename, which a crash can
// undo until the folder's entry is on the disk.
// - Windows: MoveFileExW with MOVEFILE_WRITE_THROUGH, which returns after the move is flushed to the disk
//   (https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-movefileexw). Without MOVEFILE_REPLACE_EXISTING it
//   fails when the new name exists.
// - Linux and macOS: File.Move, then the folder is opened and flushed. .NET opens no folder (File.OpenHandle on a folder
//   throws UnauthorizedAccessException, measured with .NET 8 on Ubuntu 22.04 in WSL), so this calls libc. The flush is the
//   one .NET makes for a file (dotnet/runtime release/10.0, src/native/libs/System.Native/pal_io.c, SystemNative_FSync):
//   on macOS fcntl(F_FULLFSYNC) and, when that fails, fsync; elsewhere fsync; each again after EINTR.
//   FileStreamHelpers.Unix.cs (FlushToDisk) of the same branch ignores EROFS, EINVAL, and ENOTSUP from it; here EINVAL and
//   ENOTSUP pass (a file system that takes no flush of a folder), and every other error refuses the write.
// - The C library is named as UnixOwner of ElasticLM.Client names it (V1d24, PR #447): libc.so.6 on Linux, which glibc has
//   and musl's loader answers with musl itself, and /usr/lib/libSystem.B.dylib on macOS. The bare "libc" would become
//   libc.so under NativeAOT (NativeLibrary.NativeAot.cs and LibraryNameVariation.Unix.cs of dotnet/runtime release/10.0),
//   which a glibc system without its development package, such as the chiseled image of elm-service, does not have (the
//   owner's review S1 of PR #453). A library or call that cannot be found is reported as a failed flush, not thrown.
// - The constants are read in the headers: O_CLOEXEC 02000000 (Linux, bits/fcntl-linux.h) and 0x01000000 (macOS, xnu
//   bsd/sys/fcntl.h), F_FULLFSYNC 51 (xnu bsd/sys/fcntl.h), EINTR 4 and EINVAL 22 (both), ENOTSUP 95 (Linux, EOPNOTSUPP in
//   asm-generic/errno.h) and 45 (macOS, xnu bsd/sys/errno.h).

/// <summary>
/// Gives a file a new name, as <see cref="DurableRename.Move"/> does.
/// </summary>
/// <param name="from">The file.</param>
/// <param name="to">The new path.</param>
/// <param name="flushProblem">Why the folder could not be flushed, or <see langword="null"/>.</param>
/// <returns><see langword="false"/> when the file has its new name but the folder could not be flushed.</returns>
internal delegate bool DurableMove(string from, string to, out string? flushProblem);

/// <summary>
/// Renames a file so that its new name is on the disk when the call returns.
/// </summary>
internal static partial class DurableRename
{
    private const string LinuxLibc = "libc.so.6";
    private const string MacLibc = "/usr/lib/libSystem.B.dylib";
    private const uint WriteThrough = 0x8;
    private const int ReadOnly = 0;
    private const int FullFsync = 51;
    private const int Interrupted = 4;
    private const int InvalidArgument = 22;

    private static int CloseOnExec => OperatingSystem.IsMacOS() ? 0x0100_0000 : 0x8_0000;

    private static int NotSupported => OperatingSystem.IsMacOS() ? 45 : 95;

    /// <summary>Gives a file a new name in the same folder; fails when the name exists.</summary>
    /// <param name="from">The file.</param>
    /// <param name="to">The new path.</param>
    /// <param name="flushProblem">Why the folder could not be flushed, or <see langword="null"/>.</param>
    /// <returns><see langword="false"/> when the file has its new name but the folder could not be flushed (Linux and
    /// macOS).</returns>
    /// <exception cref="IOException">The file could not be renamed.</exception>
    public static bool Move(string from, string to, out string? flushProblem)
    {
        flushProblem = null;
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileEx(Extended(from), Extended(to), WriteThrough))
            {
                throw new IOException($"The file cannot be renamed (Windows error {Marshal.GetLastPInvokeError()}).");
            }

            return true;
        }

        File.Move(from, to, overwrite: false);
        flushProblem = FlushFolder(Path.GetDirectoryName(to)!);
        return flushProblem is null;
    }

    // MoveFileExW takes a path of 260 characters or more only with the \\?\ prefix, which .NET's own file calls add.
    private static string Extended(string path)
    {
        string full = Path.GetFullPath(path);
        return full.Length < 248 || full.StartsWith(@"\\", StringComparison.Ordinal) ? full : @"\\?\" + full;
    }

    /// <summary>Flushes a folder's entries to the disk (Linux and macOS).</summary>
    /// <param name="directory">The folder.</param>
    /// <returns><see langword="null"/>, or why the folder could not be flushed.</returns>
    internal static string? FlushFolder(string directory)
    {
        try
        {
            return OperatingSystem.IsMacOS()
                ? Flush(directory, MacOpen, MacFcntl, MacFsync, MacClose)
                : Flush(directory, LinuxOpen, null, LinuxFsync, LinuxClose);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return "The C library cannot be called to flush the folder of the log: " + e.Message;
        }
    }

    private static string? Flush(
        string directory,
        Func<string, int, int> open,
        Func<int, int, int>? fcntl,
        Func<int, int> fsync,
        Func<int, int> close
    )
    {
        int descriptor = open(directory, ReadOnly | CloseOnExec);
        if (descriptor < 0)
        {
            return $"The folder of the log cannot be opened to flush it (errno {Marshal.GetLastPInvokeError()}).";
        }

        try
        {
            int result = -1;
            int error = 0;
            bool full = fcntl is not null;
            while (full)
            {
                result = fcntl!(descriptor, FullFsync);
                error = Marshal.GetLastPInvokeError();
                full = result < 0 && error == Interrupted;
            }

            while (result < 0)
            {
                result = fsync(descriptor);
                error = Marshal.GetLastPInvokeError();
                if (result < 0 && error != Interrupted)
                {
                    break;
                }
            }

            return result >= 0 || error == InvalidArgument || error == NotSupported
                ? null
                : $"The folder of the log cannot be flushed (errno {error}).";
        }
        finally
        {
            _ = close(descriptor);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string from, string to, uint flags);

    [LibraryImport(LinuxLibc, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LinuxOpen(string path, int flags);

    [LibraryImport(LinuxLibc, EntryPoint = "fsync", SetLastError = true)]
    private static partial int LinuxFsync(int descriptor);

    [LibraryImport(LinuxLibc, EntryPoint = "close")]
    private static partial int LinuxClose(int descriptor);

    [LibraryImport(MacLibc, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MacOpen(string path, int flags);

    [LibraryImport(MacLibc, EntryPoint = "fcntl", SetLastError = true)]
    private static partial int MacFcntl(int descriptor, int command);

    [LibraryImport(MacLibc, EntryPoint = "fsync", SetLastError = true)]
    private static partial int MacFsync(int descriptor);

    [LibraryImport(MacLibc, EntryPoint = "close")]
    private static partial int MacClose(int descriptor);
}
