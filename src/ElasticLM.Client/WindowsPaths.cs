using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ElasticLM.Client;

// design §10.2 (V1d24): a junction or a symbolic link leads elsewhere, and the access list that .NET reads for its path is
// the link's own; the folder it leads to is found with GetFinalPathNameByHandleW on a handle opened with
// FILE_FLAG_BACKUP_SEMANTICS, which a folder needs.

/// <summary>
/// The final path of a folder on Windows, after every junction and symbolic link on the way.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsPaths
{
    private const uint ShareAll = 0x1 | 0x2 | 0x4; // FILE_SHARE_READ, FILE_SHARE_WRITE, FILE_SHARE_DELETE
    private const uint OpenExisting = 3; // OPEN_EXISTING
    private const uint BackupSemantics = 0x02000000; // FILE_FLAG_BACKUP_SEMANTICS
    private const int MaxPath = 32_767;

    /// <summary>Reads the final path of a folder.</summary>
    /// <param name="path">The folder, as a full path.</param>
    /// <returns>The final path, as a drive path (<c>C:\…</c>) or a UNC path (<c>\\server\share\…</c>).</returns>
    /// <exception cref="IOException">The folder cannot be opened or its final path read.</exception>
    public static string FinalPath(string path)
    {
        using SafeFileHandle handle = CreateFileW(path, 0, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        if (handle.IsInvalid)
        {
            throw new IOException($"The folder '{path}' cannot be opened to read where it leads (error {Marshal.GetLastPInvokeError()}).");
        }

        char[] buffer = new char[MaxPath];
        uint length;
        fixed (char* chars = buffer)
        {
            length = GetFinalPathNameByHandleW(handle, chars, (uint)buffer.Length, 0);
        }

        if (length == 0 || length >= buffer.Length)
        {
            throw new IOException($"Where the folder '{path}' leads cannot be read (error {Marshal.GetLastPInvokeError()}).");
        }

        string final = new(buffer, 0, (int)length);
        string resolved =
            final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + final[8..]
            : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..]
            : final;

        // A volume without a drive letter answers \\?\Volume{GUID}\…, which is no drive path once its prefix is gone.
        return Path.IsPathFullyQualified(resolved)
            ? resolved
            : throw new IOException($"The folder '{path}' leads to '{final}', which has no drive letter.");
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile
    );

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle file, char* path, uint length, uint flags);
}
