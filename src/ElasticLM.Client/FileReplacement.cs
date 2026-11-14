namespace ElasticLM.Client;

// design §13.7 (V1d2), §12.6.2 (V1d2a).

/// <summary>
/// Puts a file written beside its target in the target's place, so a reader never sees half a file. On Windows the move is
/// refused while another handle has the old file open, and is tried again for a short time.
/// </summary>
internal static class FileReplacement
{
    // File.Move with overwrite throws UnauthorizedAccessException (HResult 0x80070005) on Windows while a reader has the
    // old file open, whatever sharing the reader allows: measured on Windows 11 with .NET 10.0.14 for FileShare.Read
    // (V1d2) and with .NET 10.0.15 for FileShare.ReadWrite | FileShare.Delete (V1d2a). It passes once the reader has
    // closed the file. A reader of these files holds them for one read.
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Writes a file through a new temporary file beside it, then puts that file in its place with
    /// <see cref="Move(string, string, TimeProvider)"/>. When writing or replacing fails, the temporary file is deleted.
    /// </summary>
    /// <param name="temporary">The temporary file, in the same folder as <paramref name="path"/>; see
    /// <see cref="CreateNew"/>.</param>
    /// <param name="path">The file to replace; it need not exist.</param>
    /// <param name="content">What the file holds.</param>
    /// <param name="time">The clock that counts the wait on Windows: a try every 100 milliseconds, for at most 2 seconds.</param>
    /// <exception cref="IOException">The file cannot be written or moved, also when something was put at
    /// <paramref name="temporary"/> after it was deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied, on Windows also after the last try.</exception>
    public static void Write(string temporary, string path, ReadOnlySpan<byte> content, TimeProvider time)
    {
        CreateNew(temporary, content);
        try
        {
            Move(temporary, path, time);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(temporary);
            throw;
        }
    }

    /// <summary>
    /// Creates a new file that only this user can read on Linux and macOS (mode 0600), and that takes the folder's access
    /// rules on Windows. Whatever is at the path first (a file left by a write that failed, a link, a file another user put
    /// there) is deleted, never opened, so the new content goes to no other file and keeps no other file's access rules.
    /// When writing fails after the file was created, the file is deleted.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="content">What the file holds.</param>
    /// <exception cref="IOException">The file cannot be created or written, also when something was put at the path after it
    /// was deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public static void CreateNew(string path, ReadOnlySpan<byte> content)
    {
        // design §12.6.2 (V1d24): File.WriteAllBytes and FileMode.Create open what is there, a symbolic link included, and on
        // Windows keep the access rules of a file another user created; CreateNew refuses anything at the path (O_EXCL).
        File.Delete(path);
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

        bool created = false;
        try
        {
            using var stream = new FileStream(path, options);
            created = true;
            stream.Write(content);
        }
        catch (Exception e) when (created && e is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(path);
            throw;
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

    /// <summary>Deletes a file, if it can. A file it cannot delete stays; when its name is fixed, the next write of the same
    /// file deletes it before it creates its own.</summary>
    /// <param name="path">The file.</param>
    public static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // It stays; a fixed name is deleted by the next write, and what else stays is V1d24a's (design §12.6.2).
        }
    }
}
