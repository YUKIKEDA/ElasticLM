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
}
