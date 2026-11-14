using System.Runtime.InteropServices;

namespace ElasticLM.Issuing;

// design §18.1 (V1d16): a log file takes its name by a rename, which a crash can undo until the folder's entry is on the
// disk. On Linux and macOS the folder is opened and fsync'd: .NET opens no folder (File.OpenHandle on a folder throws
// UnauthorizedAccessException, measured with .NET 8 on Ubuntu 22.04 in WSL), so this calls open, fsync, and close of libc.
// On Windows nothing is done: whether NTFS keeps a rename made by MoveFileEx without MOVEFILE_WRITE_THROUGH across a crash
// was not verified.

/// <summary>
/// Flushes a folder's entries to the disk.
/// </summary>
internal static partial class FolderFlush
{
    private const int ReadOnly = 0;

    /// <summary>Makes the names in a folder durable; does nothing on Windows.</summary>
    /// <param name="directory">The folder.</param>
    /// <exception cref="IOException">The folder cannot be opened or flushed.</exception>
    public static void Flush(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        int descriptor = Open(directory, ReadOnly);
        if (descriptor < 0)
        {
            throw new IOException($"The folder of the log cannot be opened to flush it (errno {Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            if (Fsync(descriptor) != 0)
            {
                throw new IOException($"The folder of the log cannot be flushed (errno {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int descriptor);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int descriptor);
}
