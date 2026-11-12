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
