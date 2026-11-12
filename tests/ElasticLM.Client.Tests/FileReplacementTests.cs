using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Client.Tests;

/// <summary>
/// Tests of <see cref="FileReplacement"/> (design §12.6.2, §13.7): a file written beside its target replaces it, and on
/// Windows a reader that holds the old file open delays the replacement, for at most 2 seconds.
/// </summary>
public sealed class FileReplacementTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "elm-replace-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));

    public FileReplacementTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Old, "old");
        File.WriteAllText(Fresh, "new");
    }

    private string Old => Path.Combine(_dir, "file.json");

    private string Fresh => Path.Combine(_dir, "file.json.tmp");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// With no reader, both forms replace the file at the first try and make no timer.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A folder with file.json ("old") and file.json.tmp ("new"); a fake clock that counts the timers of 100 milliseconds.
    ///
    /// Steps:
    /// - Move the new file over the old one, with Move or with MoveAsync. Then write the new file again and move it to a
    ///   path where no file is.
    ///
    /// Expected:
    /// - file.json holds "new" and file.json.tmp is gone; the second target holds "new"; no timer was made.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Move_NoReader_ReplacesAtOnce(bool asynchronous)
    {
        var clock = new CountingTime(Start);
        string other = Path.Combine(_dir, "other.json");

        await MoveAsync(asynchronous, Fresh, Old, clock);
        File.WriteAllText(Fresh, "new");
        await MoveAsync(asynchronous, Fresh, other, clock);

        Assert.Equal(("new", "new", false, 0), (File.ReadAllText(Old), File.ReadAllText(other), File.Exists(Fresh), clock.Timers));
    }

    /// <summary>
    /// On Windows, while a reader holds the old file open, the replacement is tried again every 100 milliseconds of the clock
    /// and succeeds once the reader has closed, up to the try at 2 seconds.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows; file.json opened by the test for reading, with the sharing given: Read, as File.ReadAllBytes opens a file,
    ///   or ReadWrite and Delete, as the activation file is read; the counting fake clock.
    /// - t0 is the first try. Each failed try makes one timer due 100 milliseconds later.
    ///
    /// Steps:
    /// - Start the move on another thread. 19 times: wait for the next timer, then move the clock 100 milliseconds (the tries
    ///   at t0 + 0.1 s to t0 + 1.9 s fail). Wait for the 20th timer; close the reader; move the clock 100 milliseconds (the
    ///   try at t0 + 2 s).
    ///
    /// Expected:
    /// - The move has not ended while the reader holds the file, and file.json holds "old"; after the last move of the clock
    ///   it ends, file.json holds "new", file.json.tmp is gone, and exactly 20 timers were made.
    /// </remarks>
    [Theory]
    [InlineData(false, FileShare.Read)]
    [InlineData(false, FileShare.ReadWrite | FileShare.Delete)]
    [InlineData(true, FileShare.Read)]
    [InlineData(true, FileShare.ReadWrite | FileShare.Delete)]
    public async Task Move_ReaderHoldsTheOldFileUntilTwoSeconds_Replaces(bool asynchronous, FileShare share)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file that a reader holds open.");
        var clock = new CountingTime(Start);
        var reader = new FileStream(Old, FileMode.Open, FileAccess.Read, share);
        Task move = Task.Run(() => MoveAsync(asynchronous, Fresh, Old, clock), TestContext.Current.CancellationToken);
        try
        {
            for (int timer = 1; timer <= 19; timer++)
            {
                await clock.WaitForTimersAsync(timer);
                Assert.False(move.IsCompleted);
                clock.Advance(TimeSpan.FromMilliseconds(100));
            }

            await clock.WaitForTimersAsync(20);
            Assert.False(move.IsCompleted);
            Assert.Equal("old", ReadShared(Old));
            reader.Dispose();
            clock.Advance(TimeSpan.FromMilliseconds(100));

            await move.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(("new", false, 20), (File.ReadAllText(Old), File.Exists(Fresh), clock.Timers));
        }
        finally
        {
            reader.Dispose();
            await EndAsync(move, clock);
        }
    }

    /// <summary>
    /// On Windows, a reader that still holds the old file at the try 2 seconds after the first makes the replacement fail.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - As the test before: file.json held open for reading, shared for reading, and the counting fake clock. t0 is the
    ///   first try.
    ///
    /// Steps:
    /// - Start the move on another thread. 20 times: wait for the next timer, then move the clock 100 milliseconds. The last
    ///   move brings the try at t0 + 2 s, which is the last one.
    ///
    /// Expected:
    /// - The move has not ended before the 20th move of the clock; after it, UnauthorizedAccessException, with exactly 20
    ///   timers made; file.json holds "old", and file.json.tmp is still there for the caller.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Move_ReaderHoldsTheOldFilePastTwoSeconds_Throws(bool asynchronous)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file that a reader holds open.");
        var clock = new CountingTime(Start);
        using var reader = new FileStream(Old, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task move = Task.Run(() => MoveAsync(asynchronous, Fresh, Old, clock), TestContext.Current.CancellationToken);
        try
        {
            for (int timer = 1; timer <= 20; timer++)
            {
                await clock.WaitForTimersAsync(timer);
                Assert.False(move.IsCompleted);
                clock.Advance(TimeSpan.FromMilliseconds(100));
            }

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                move.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
            );
            Assert.Equal(("old", true, 20), (ReadShared(Old), File.Exists(Fresh), clock.Timers));
        }
        finally
        {
            reader.Dispose();
            await EndAsync(move, clock);
        }
    }

    /// <summary>
    /// On Linux and macOS a reader of the old file does not delay the replacement.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux or macOS; file.json opened by the test for reading; the counting fake clock.
    ///
    /// Steps:
    /// - Move the new file over the old one, with Move or with MoveAsync, while the reader is open. Read the rest of the
    ///   reader.
    ///
    /// Expected:
    /// - The move ends without a timer; file.json holds "new"; the reader, opened before, still reads "old".
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Move_ReaderHoldsTheOldFileOnUnix_ReplacesAtOnce(bool asynchronous)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows refuses the replacement while a reader holds the file.");
        var clock = new CountingTime(Start);
        using var reader = new FileStream(Old, FileMode.Open, FileAccess.Read, FileShare.Read);

        await MoveAsync(asynchronous, Fresh, Old, clock);

        using var text = new StreamReader(reader);
        Assert.Equal(("new", "old", 0), (File.ReadAllText(Old), await text.ReadToEndAsync(TestContext.Current.CancellationToken), clock.Timers));
    }

    /// <summary>
    /// A cancelled wait ends MoveAsync with the cancellation and leaves both files as they were.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows; file.json held open for reading; the counting fake clock.
    ///
    /// Steps:
    /// - Start MoveAsync with a token; wait for the first timer; cancel the token.
    ///
    /// Expected:
    /// - OperationCanceledException; file.json holds "old" and file.json.tmp is still there; one timer was made.
    /// </remarks>
    [Fact]
    public async Task MoveAsync_CancelledWhileAReaderHoldsTheOldFile_Throws()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file that a reader holds open.");
        var clock = new CountingTime(Start);
        using var reader = new FileStream(Old, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task move = FileReplacement.MoveAsync(Fresh, Old, clock, cancel.Token);

        await clock.WaitForTimersAsync(1);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            move.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
        );
        Assert.Equal(("old", true, 1), (ReadShared(Old), File.Exists(Fresh), clock.Timers));
    }

    /// <summary>
    /// A failure that is not a refused access is not tried again, on any OS.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - file.json; no file.json.tmp (the test deletes it); the counting fake clock.
    ///
    /// Steps:
    /// - Move the missing new file over the old one, with Move or with MoveAsync.
    ///
    /// Expected:
    /// - FileNotFoundException at once, with no timer made; file.json holds "old".
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Move_NewFileMissing_ThrowsWithoutWaiting(bool asynchronous)
    {
        var clock = new CountingTime(Start);
        File.Delete(Fresh);

        await Assert.ThrowsAsync<FileNotFoundException>(() => MoveAsync(asynchronous, Fresh, Old, clock));

        Assert.Equal(("old", 0), (File.ReadAllText(Old), clock.Timers));
    }

    /// <summary>
    /// On Linux and macOS a refused access is not tried again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux or macOS, not as root; the folder of the two files has mode 0500, so a file in it cannot be renamed; the
    ///   counting fake clock.
    ///
    /// Steps:
    /// - Move the new file over the old one, with Move or with MoveAsync.
    ///
    /// Expected:
    /// - UnauthorizedAccessException at once, with no timer made; file.json holds "old".
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Move_FolderNotWritableOnUnix_ThrowsWithoutWaiting(bool asynchronous)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Modes are Linux and macOS only.");
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "Root renames in a folder of any mode.");
        var clock = new CountingTime(Start);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => MoveAsync(asynchronous, Fresh, Old, clock));
            Assert.Equal(("old", 0), (File.ReadAllText(Old), clock.Timers));
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    /// <summary>
    /// On Windows, the writers of the server license file and of a server pin wait for a reader of the old file on the clock
    /// they are given, and write once the reader has closed.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows; a server license file written once with LicenseFiles.Write, or a pin written once with
    ///   ServerPins.TryWrite; that file opened by the test for reading, as File.ReadAllBytes opens it; the counting fake
    ///   clock given to the writer.
    ///
    /// Steps:
    /// - Write again on another thread. 3 times: wait for the next timer, then move the clock 100 milliseconds. Wait for the
    ///   4th timer; close the reader; move the clock 100 milliseconds.
    ///
    /// Expected:
    /// - The write has not ended while the reader holds the file; after the last move of the clock it ends (the pin's write
    ///   returns true), the file holds the second content, and exactly 4 timers were made.
    /// </remarks>
    [Theory]
    [InlineData("license")]
    [InlineData("pin")]
    public async Task Writers_ReaderHoldsTheOldFile_WaitOnTheirClock(string writer)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file that a reader holds open.");
        var clock = new CountingTime(Start);
        var url = new Uri("https://licenses.example/");
        var pins = new ServerPins(_dir, clock);
        string license = Path.Combine(_dir, "server.elmlic");
        Action first =
            writer == "license"
                ? () => LicenseFiles.Write(license, "one"u8.ToArray(), clock)
                : () => Assert.True(pins.TryWrite(url, "lic-1", "key-1"));
        Action second =
            writer == "license"
                ? () => LicenseFiles.Write(license, "two"u8.ToArray(), clock)
                : () => Assert.True(pins.TryWrite(url, "lic-2", "key-2"));
        first();
        string target = writer == "license" ? license : Directory.GetFiles(Path.Combine(_dir, "servers")).Single();
        var reader = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task write = Task.Run(second, TestContext.Current.CancellationToken);
        try
        {
            for (int timer = 1; timer <= 3; timer++)
            {
                await clock.WaitForTimersAsync(timer);
                Assert.False(write.IsCompleted);
                clock.Advance(TimeSpan.FromMilliseconds(100));
            }

            await clock.WaitForTimersAsync(4);
            Assert.False(write.IsCompleted);
            reader.Dispose();
            clock.Advance(TimeSpan.FromMilliseconds(100));

            await write.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Contains(writer == "license" ? "two" : "lic-2", File.ReadAllText(target), StringComparison.Ordinal);
            Assert.Equal(4, clock.Timers);
        }
        finally
        {
            reader.Dispose();
            await EndAsync(write, clock);
        }
    }

    private static Task MoveAsync(bool asynchronous, string temporary, string path, TimeProvider time)
    {
        if (asynchronous)
        {
            return FileReplacement.MoveAsync(temporary, path, time, TestContext.Current.CancellationToken);
        }

        FileReplacement.Move(temporary, path, time);
        return Task.CompletedTask;
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var text = new StreamReader(stream);
        return text.ReadToEnd();
    }

    // A move that a failed assertion left waiting ends once its reader is closed and the clock passes the last try.
    private static async Task EndAsync(Task move, FakeTimeProvider clock)
    {
        for (int i = 0; i < 300 && !move.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(10, CancellationToken.None);
        }

        try
        {
            await move;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // The test has asserted what it expects of the move.
        }
    }

    private sealed class CountingTime(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private int _timers;

        public int Timers => Volatile.Read(ref _timers);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromMilliseconds(100))
            {
                Interlocked.Increment(ref _timers);
            }

            return timer;
        }

        // Waits, in real time and with a bound, until the move has made its next timer: only then does a move of the clock
        // reach it.
        public async Task WaitForTimersAsync(int count)
        {
            for (int i = 0; i < 3_000 && Timers < count; i++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.Equal(count, Timers);
        }
    }
}
