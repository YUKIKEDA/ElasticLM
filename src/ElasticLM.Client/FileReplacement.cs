using System.Buffers;
using System.Security.Cryptography;
using ElasticLM.Licensing;

namespace ElasticLM.Client;

// design §13.7 (V1d2), §12.6.2 (V1d2a, V1d24, V1d24a).

/// <summary>
/// Puts a file written beside its target in the target's place, so a reader never sees half a file. On Windows the move is
/// refused while another handle has the old file open, and is tried again for a short time. Temporary files that a write
/// left behind are removed by a later write, once no writer holds them.
/// </summary>
internal static class FileReplacement
{
    /// <summary>The most leftover temporary files that one clean looks at.</summary>
    public const int MaxLeftovers = 1000;

    // The random part of a temporary file's name: 8 bytes as 16 lowercase hex digits.
    private const int RandomLength = 16;
    private const string TemporaryExtension = ".tmp";

    /// <summary>The sharing of a reader of a file that a writer deletes, moves, or replaces.</summary>
    internal const FileShare ShareWithWriters = FileShare.ReadWrite | FileShare.Delete;

    /// <summary>The lowercase hex digits of a temporary file's name.</summary>
    internal static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    // File.Move with overwrite throws UnauthorizedAccessException (HResult 0x80070005) on Windows while a reader has the
    // old file open, whatever sharing the reader allows: measured on Windows 11 with .NET 10.0.14 for FileShare.Read
    // (V1d2) and with .NET 10.0.15 for FileShare.ReadWrite | FileShare.Delete (V1d2a). It passes once the reader has
    // closed the file. A reader of these files holds them for one read.
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    /// <summary>Returns a new temporary path beside a file: <c>&lt;file&gt;.&lt;16 hex digits&gt;.tmp</c>.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The temporary path, in the same folder.</returns>
    public static string TemporaryPath(string path) =>
        path + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(RandomLength / 2)) + TemporaryExtension;

    /// <summary>
    /// Writes a file through a new temporary file beside it (<see cref="TemporaryPath"/>), which it holds open until it is in
    /// place, then puts that file in its place with <see cref="Move(string, string, TimeProvider)"/>. Before that, temporary
    /// files of the same file that no writer holds are removed. When writing or replacing fails, the temporary file is
    /// deleted.
    /// </summary>
    /// <param name="path">The file to replace; it need not exist.</param>
    /// <param name="content">What the file holds.</param>
    /// <param name="time">The clock that counts the wait on Windows: a try every 100 milliseconds, for at most 2 seconds.</param>
    /// <exception cref="IOException">The file cannot be written or moved, also when another process removed the temporary
    /// file before it was in place.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied, on Windows also after the last try.</exception>
    public static void Write(string path, ReadOnlySpan<byte> content, TimeProvider time)
    {
        string full = Path.GetFullPath(path);
        RemoveLeftovers(full);
        string temporary = TemporaryPath(full);
        FileStream held = OpenNew(temporary);
        try
        {
            held.Write(content);
            held.Flush();
            Move(temporary, full, time);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            held.Dispose();
            DeleteQuietly(temporary);
            throw;
        }
        finally
        {
            held.Dispose();
        }
    }

    /// <summary>
    /// Creates a new file to write and hold until it is in its place: only this user can read it on Linux and macOS (mode
    /// 0600), and it takes the folder's access rules on Windows. Whatever is at the path first (a file left by a write that
    /// failed, a link, a file another user put there) is deleted, never opened, so the new content goes to no other file and
    /// keeps no other file's access rules. While the caller holds the stream, readers may open the file and the caller may
    /// move it, and a clean of leftovers cannot open it (<see cref="RemoveLeftovers(string)"/>).
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The open file, which the caller disposes once the file is in place.</returns>
    /// <exception cref="IOException">The file cannot be created, also when something was put at the path after it was
    /// deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public static FileStream OpenNew(string path)
    {
        // design §12.6.2 (V1d24): File.WriteAllBytes and FileMode.Create open what is there, a symbolic link included, and on
        // Windows keep the access rules of a file another user created; CreateNew refuses anything at the path (O_EXCL).
        // design §12.6.2 (V1d24a): Read lets readers open the file once it is in place, before this handle closes; Delete lets
        // this process move it on Windows. Any open handle refuses an open with FileShare.None: on Windows by the share mode,
        // on Linux and macOS by the shared flock .NET takes (measured on Windows 11 and Ubuntu 24.04, .NET 10.0.14).
        File.Delete(path);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read | FileShare.Delete,

            // Unbuffered: a write that fails (a full disk) throws from Write, not again from Dispose, so a caller that
            // disposes the stream after a failure can still delete the file.
            BufferSize = 0,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    /// <summary>
    /// Removes the temporary files of one file that no writer holds: <c>&lt;file&gt;.&lt;16 lowercase hex digits&gt;.tmp</c>
    /// beside it (<see cref="TemporaryPath"/>). A file that cannot be removed stays; nothing is thrown.
    /// </summary>
    /// <param name="path">The file whose temporary files are removed.</param>
    public static void RemoveLeftovers(string path)
    {
        string full = Path.GetFullPath(path);
        string name = Path.GetFileName(full);
        RemoveLeftovers(Path.GetDirectoryName(full)!, name + ".*" + TemporaryExtension, candidate => IsTemporaryOf(candidate, name));
    }

    /// <summary>
    /// Removes the files of a folder that a predicate takes for leftover temporary files and that no writer holds: each is
    /// opened with <see cref="FileShare.None"/> first, and one that cannot be opened stays. A link is removed itself, not
    /// opened. At most <see cref="MaxLeftovers"/> files are looked at; a file that cannot be removed stays; nothing is thrown.
    /// </summary>
    /// <param name="folder">The folder; it need not exist.</param>
    /// <param name="searchPattern">The pattern of the folder listing; the predicate decides.</param>
    /// <param name="isLeftover">Whether a file name is one of the writer's temporary files, compared case-sensitively.</param>
    public static void RemoveLeftovers(string folder, string searchPattern, Func<string, bool> isLeftover)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            int seen = 0;
            foreach (string candidate in Directory.EnumerateFiles(folder, searchPattern))
            {
                if (!isLeftover(Path.GetFileName(candidate)))
                {
                    continue;
                }

                if (++seen > MaxLeftovers)
                {
                    return;
                }

                RemoveIfNotHeld(candidate);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The folder cannot be listed; its leftovers stay for a later clean.
        }
    }

    /// <summary>Returns whether a file name is one of <see cref="TemporaryPath"/>'s names for a file.</summary>
    /// <param name="candidate">The file name.</param>
    /// <param name="name">The name of the file whose temporary files are looked for.</param>
    /// <returns><see langword="true"/> for <c>&lt;name&gt;.&lt;16 lowercase hex digits&gt;.tmp</c>.</returns>
    public static bool IsTemporaryOf(string candidate, string name) =>
        candidate.Length == name.Length + 1 + RandomLength + TemporaryExtension.Length
        && candidate.StartsWith(name + ".", StringComparison.Ordinal)
        && candidate.EndsWith(TemporaryExtension, StringComparison.Ordinal)
        && !candidate.AsSpan(name.Length + 1, RandomLength).ContainsAnyExcept(LowerHex);

    // design §12.6.2 (V1d24a): a writer holds its temporary file until it is in place, so an exclusive open fails while one
    // is at work. A link is not opened: File.Delete removes the link, not what it points to. On Linux and macOS a FileStream
    // open of a FIFO waits for a writer, so ElasticLM.Licensing's UnixLeftover, which the clock guard uses too, opens without
    // waiting and leaves anything that is not a regular file (review of PR #475, V1d24d).
    private static void RemoveIfNotHeld(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            ElasticLM.Licensing.UnixLeftover.TryRemove(path);
            return;
        }

        try
        {
            if (new FileInfo(path).LinkTarget is null)
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            }

            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Held by a writer, unreadable, or refused: it stays.
        }
    }

    /// <summary>Moves the new file over the old one, waiting on the calling thread between the tries.</summary>
    /// <param name="temporary">The new file, in the same folder as <paramref name="path"/>.</param>
    /// <param name="path">The file to replace; it need not exist.</param>
    /// <param name="time">The clock that counts the wait on Windows: a try every 100 milliseconds, for at most 2 seconds.</param>
    /// <exception cref="IOException">The file cannot be moved.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied, on Windows also after the last try.</exception>
    public static void Move(string temporary, string path, TimeProvider time)
    {
        long started = time.GetTimestamp();
        while (true)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && time.GetElapsedTime(started) < Limit)
            {
                using var elapsed = new ManualResetEventSlim();
                using ITimer timer = time.CreateTimer(_ => elapsed.Set(), null, Interval, Timeout.InfiniteTimeSpan);
                elapsed.Wait();
            }
        }
    }

    /// <summary>Moves the new file over the old one, without holding a thread between the tries.</summary>
    /// <param name="temporary">The new file, in the same folder as <paramref name="path"/>.</param>
    /// <param name="path">The file to replace; it need not exist.</param>
    /// <param name="time">The clock that counts the wait on Windows: a try every 100 milliseconds, for at most 2 seconds.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that ends when the file is in place.</returns>
    /// <exception cref="IOException">The file cannot be moved.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied, on Windows also after the last try.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public static async Task MoveAsync(string temporary, string path, TimeProvider time, CancellationToken cancellationToken)
    {
        long started = time.GetTimestamp();
        while (true)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && time.GetElapsedTime(started) < Limit)
            {
                await Task.Delay(Interval, time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // design §12.6.2 (V1d24a): with FileShare.Read or ReadWrite, File.Delete and File.Move of the file throw IOException
    // 0x80070020 on Windows while this handle is open; with Delete added they pass (Windows 11, .NET 10.0.14). design §8.6
    // (V1d24c): the limit is checked while the file is read (BoundedFile).

    /// <summary>
    /// Reads a whole file that a writer may delete, move, or replace while it is read: the file is opened with
    /// <see cref="FileShare.ReadWrite"/> and <see cref="FileShare.Delete"/>, so on Windows a delete of the file and a move of
    /// it to another name are not refused for this reader. A move over it is refused on Windows whatever the sharing; the
    /// writer tries again (<see cref="Move(string, string, TimeProvider)"/>). At most one byte past the limit is read, whatever
    /// length the file has or reaches while it is read, and a file that cannot seek (a pipe, a device) is read the same way.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="maxLength">The most bytes the file may hold.</param>
    /// <returns>Its bytes, or <see langword="null"/> when it holds more than <paramref name="maxLength"/> bytes.</returns>
    /// <exception cref="IOException">The file is missing or cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public static byte[]? ReadShared(string path, int maxLength) => BoundedFile.Read(path, maxLength, ShareWithWriters);

    /// <summary>Deletes a file, if it can. A temporary file it cannot delete stays for <see cref="RemoveLeftovers(string)"/>.</summary>
    /// <param name="path">The file.</param>
    public static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // It stays; a later write removes it (design §12.6.2, V1d24a).
        }
    }
}
