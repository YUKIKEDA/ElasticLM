using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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
    /// - While the write waits, clean the leftovers of the file, as another writer does before it writes (design §12.6.2,
    ///   V1d24a).
    ///
    /// Expected:
    /// - The write has not ended while the reader holds the file, and its one temporary file is still there after the clean:
    ///   the writer holds it while it waits. After the last move of the clock the write ends (the pin's write returns true),
    ///   the file holds the second content, no temporary file is left, and exactly 4 timers were made.
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
            string folder = Path.GetDirectoryName(target)!;
            string temporary = Assert.Single(Directory.GetFiles(folder, Path.GetFileName(target) + ".*.tmp"));
            FileReplacement.RemoveLeftovers(target);
            Assert.True(File.Exists(temporary));
            reader.Dispose();
            clock.Advance(TimeSpan.FromMilliseconds(100));

            await write.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Contains(writer == "license" ? "two" : "lic-2", File.ReadAllText(target), StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(folder, Path.GetFileName(target) + ".*.tmp"));
            Assert.Equal(4, clock.Timers);
        }
        finally
        {
            reader.Dispose();
            await EndAsync(write, clock);
        }
    }

    /// <summary>
    /// A writer removes a symbolic link that has the name of one of its temporary files, and leaves the file the link points
    /// to alone (design §12.6.2, V1d24a; V1d24 for the fixed names before).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A file victim.txt ("victim") in another folder, and a symbolic link to it at a name of the writer's temporary
    ///   files, &lt;file&gt;.0123456789abcdef.tmp:
    ///   activation: beside the activation file that ActivationStore.Write writes;
    ///   license: beside the server license file that LicenseFiles.Write writes;
    ///   pin: in servers/, beside the pin that ServerPins.TryWrite writes.
    /// - Where the test cannot make a link (Windows without the right to), it is skipped and says so; GitHub's runners can.
    ///
    /// Steps:
    /// - Write.
    ///
    /// Expected:
    /// - victim.txt still holds "victim"; the link is gone; the written file is a regular file, not a link, and holds what
    ///   was written; no temporary file of it is left.
    /// </remarks>
    /// <param name="writer">The writer.</param>
    [Theory]
    [InlineData("activation")]
    [InlineData("license")]
    [InlineData("pin")]
    public void Writers_LinkWithATemporaryName_IsRemovedAndItsTargetLeftAlone(string writer)
    {
        string victim = Path.Combine(_dir, "elsewhere", "victim.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(victim)!);
        File.WriteAllText(victim, "victim");
        (string target, Action write) = Writer(writer);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string link = target + ".0123456789abcdef.tmp";
        try
        {
            File.CreateSymbolicLink(link, victim);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.False(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "On GitHub Actions this test must run: " + e.Message);
            Assert.Skip("This user cannot make a symbolic link: " + e.Message);
        }

        write();

        Assert.Equal("victim", File.ReadAllText(victim));
        Assert.False(Path.Exists(link));
        Assert.Null(new FileInfo(target).LinkTarget);
        Assert.Contains(Written(writer), File.ReadAllText(target), StringComparison.Ordinal);
        Assert.Empty(Temporaries(target));
    }

    /// <summary>
    /// A temporary file that an earlier write left, and that no writer holds, is removed by the next write; the written file
    /// does not take its access (design §12.6.2, V1d24a).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A file at a name of the writer's temporary files (the three writers of the test before) that every user may read:
    ///   on Windows with an explicit rule that allows Everyone to read, on Linux and macOS with mode 0666.
    ///
    /// Steps:
    /// - Write.
    ///
    /// Expected:
    /// - The left file is gone and no temporary file of the target is left; the written file has no rule for Everyone on
    ///   Windows, and mode 0600 on Linux and macOS.
    /// </remarks>
    /// <param name="writer">The writer.</param>
    [Theory]
    [InlineData("activation")]
    [InlineData("license")]
    [InlineData("pin")]
    public void Writers_TemporaryFileLeftBehind_IsRemoved(string writer)
    {
        (string target, Action write) = Writer(writer);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string left = target + ".0123456789abcdef.tmp";
        File.WriteAllText(left, "left");
        if (OperatingSystem.IsWindows())
        {
            var file = new FileInfo(left);
            FileSecurity rules = file.GetAccessControl();
            rules.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Read, AccessControlType.Allow));
            file.SetAccessControl(rules);
        }
        else
        {
            File.SetUnixFileMode(left, (UnixFileMode)0b110_110_110);
        }

        write();

        Assert.False(File.Exists(left));
        Assert.Empty(Temporaries(target));
        if (OperatingSystem.IsWindows())
        {
            var named = new List<string>();
            foreach (FileSystemAccessRule rule in new FileInfo(target).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                named.Add(rule.IdentityReference.Value);
            }

            Assert.DoesNotContain(Everyone.Value, named);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));
        }
    }

    /// <summary>
    /// A temporary file that another writer holds open (as a writer holds its own until it is in place) is not removed by a
    /// write of the same file; once that writer has let it go, the next write removes it (design §12.6.2, V1d24a).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A file at a name of the writer's temporary files (the three writers of the tests before), made and held open by
    ///   FileReplacement.OpenNew, as another process's writer holds it: on Windows its share mode, on Linux and macOS
    ///   .NET's shared flock refuse an open with FileShare.None.
    ///
    /// Steps:
    /// - Write; close the held file; write again.
    ///
    /// Expected:
    /// - The first write puts its content in place and leaves the held file; after the second write the held file is gone and
    ///   no temporary file of the target is left.
    /// </remarks>
    /// <param name="writer">The writer.</param>
    [Theory]
    [InlineData("activation")]
    [InlineData("license")]
    [InlineData("pin")]
    public void Writers_TemporaryFileHeldByAnotherWriter_IsLeftAlone(string writer)
    {
        (string target, Action write) = Writer(writer);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string held = target + ".0123456789abcdef.tmp";
        using (FileStream other = FileReplacement.OpenNew(held))
        {
            other.Write("other"u8);
            other.Flush();

            write();

            Assert.Contains(Written(writer), File.ReadAllText(target), StringComparison.Ordinal);
            Assert.Equal(new[] { held }, Temporaries(target));
        }

        write();

        Assert.Empty(Temporaries(target));
    }

    /// <summary>
    /// On Linux and macOS a clean neither waits on a FIFO nor follows a link to one: a FIFO with a temporary file's name
    /// stays, and a link with such a name to a FIFO elsewhere is removed itself (design §12.6.2, V1d24a, review of PR
    /// #475).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux or macOS; mkfifo; a FIFO at server.elmlic.0123456789abcdef.tmp beside the server license file, and a FIFO
    ///   elsewhere/fifo with a symbolic link to it at server.elmlic.fedcba9876543210.tmp. No process opens either FIFO for
    ///   writing, so an open that waits for a writer never returns.
    ///
    /// Steps:
    /// - Write the server license file with LicenseFiles.Write on another thread, and wait for it for at most 30 seconds.
    ///
    /// Expected:
    /// - The write ends and the file holds what was written; the FIFO at the temporary name stays; the link is gone; the
    ///   FIFO it pointed to stays.
    /// </remarks>
    [Fact]
    public async Task Clean_FifoAndLinkToAFifo_DoesNotWaitAndRemovesTheLinkOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "FIFOs that a path names are Linux and macOS only.");
        string license = Path.Combine(_dir, "server.elmlic");
        string fifo = license + ".0123456789abcdef.tmp";
        string elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        string target = Path.Combine(elsewhere, "fifo");
        MakeFifo(fifo);
        MakeFifo(target);
        string link = license + ".fedcba9876543210.tmp";
        File.CreateSymbolicLink(link, target);

        Task write = Task.Run(
            () => LicenseFiles.Write(license, "written"u8.ToArray(), new FakeTimeProvider(Start)),
            TestContext.Current.CancellationToken
        );
        await write.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal("written", File.ReadAllText(license));
        Assert.Equal((true, false, true), (Path.Exists(fifo), Path.Exists(link) || new FileInfo(link).LinkTarget is not null, Path.Exists(target)));
    }

    /// <summary>
    /// On Linux and macOS a file with a temporary file's name that the clean cannot open, and that is not a link, stays
    /// (design §12.6.2, V1d24a, review of PR #474).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux or macOS, not as root; a file at server.elmlic.0123456789abcdef.tmp beside the server license file, with mode
    ///   0000, so open(2) fails with EACCES.
    ///
    /// Steps:
    /// - Write the server license file with LicenseFiles.Write.
    ///
    /// Expected:
    /// - The file holds what was written; the file of mode 0000 stays.
    /// </remarks>
    [Fact]
    public void Clean_FileThatCannotBeOpened_Stays()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Modes are Linux and macOS only.");
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "Root opens a file of any mode.");
        string license = Path.Combine(_dir, "server.elmlic");
        string closed = license + ".0123456789abcdef.tmp";
        File.WriteAllText(closed, "left");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(closed, UnixFileMode.None);
        }

        LicenseFiles.Write(license, "written"u8.ToArray(), new FakeTimeProvider(Start));

        Assert.Equal(("written", true), (File.ReadAllText(license), File.Exists(closed)));
    }

    /// <summary>
    /// A link with a temporary file's name that points nowhere is removed itself, on every OS: the clean does not open it
    /// (on Windows an open would fail, and the link would stay) (design §12.6.2, V1d24a, review of PR #474).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A symbolic link at server.elmlic.0123456789abcdef.tmp to a path where nothing is. Where the test cannot make a link
    ///   (Windows without the right to), it is skipped and says so; GitHub's runners can.
    ///
    /// Steps:
    /// - Write the server license file with LicenseFiles.Write.
    ///
    /// Expected:
    /// - The file holds what was written; the link is gone.
    /// </remarks>
    [Fact]
    public void Clean_LinkThatPointsNowhere_IsRemoved()
    {
        string license = Path.Combine(_dir, "server.elmlic");
        string link = license + ".0123456789abcdef.tmp";
        try
        {
            File.CreateSymbolicLink(link, Path.Combine(_dir, "nowhere"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.False(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "On GitHub Actions this test must run: " + e.Message);
            Assert.Skip("This user cannot make a symbolic link: " + e.Message);
        }

        LicenseFiles.Write(license, "written"u8.ToArray(), new FakeTimeProvider(Start));

        Assert.Equal(("written", false), (File.ReadAllText(license), new FileInfo(link).LinkTarget is not null));
    }

    /// <summary>
    /// ReadShared reads a file that cannot seek (a pipe) to its end, and gives null when it holds more than the limit
    /// (review of PR #475: Length throws NotSupportedException on such a file, where File.ReadAllBytes reads it).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pipe that a path names: on Windows a named pipe \\.\pipe\elm-…; on Linux and macOS a FIFO made with mkfifo. A
    ///   writer on another thread writes the given number of bytes and closes it.
    ///
    /// Steps:
    /// - Read it with ReadShared and a limit of 16 bytes, once with 16 bytes written and once with 17.
    ///
    /// Expected:
    /// - The 16 bytes; then null.
    /// </remarks>
    /// <param name="written">The bytes the writer writes.</param>
    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    public async Task ReadShared_FileThatCannotSeek_IsReadToItsEndWithinTheLimit(int written)
    {
        byte[] content = [.. Enumerable.Range(0, written).Select(i => (byte)('a' + (i % 26)))];
        string path;
        Task writer;
        System.IO.Pipes.NamedPipeServerStream? server = null;
        if (OperatingSystem.IsWindows())
        {
            string name = "elm-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
            server = new System.IO.Pipes.NamedPipeServerStream(
                name,
                System.IO.Pipes.PipeDirection.Out,
                1,
                System.IO.Pipes.PipeTransmissionMode.Byte,
                System.IO.Pipes.PipeOptions.Asynchronous
            );
            path = @"\\.\pipe\" + name;
            writer = Task.Run(
                async () =>
                {
                    await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
                    await server.WriteAsync(content, TestContext.Current.CancellationToken);
                    if (OperatingSystem.IsWindows())
                    {
                        server.WaitForPipeDrain();
                    }

                    server.Dispose();
                },
                TestContext.Current.CancellationToken
            );
        }
        else
        {
            path = Path.Combine(_dir, "pipe");
            MakeFifo(path);
            writer = Task.Run(() => File.WriteAllBytes(path, content), TestContext.Current.CancellationToken);
        }

        try
        {
            byte[]? read = await Task.Run(() => FileReplacement.ReadShared(path, 16), TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await writer.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(written <= 16 ? content : null, read);
        }
        finally
        {
            server?.Dispose();
        }
    }

    /// <summary>
    /// A pin's write removes the left temporary files of every pin in servers/, not only of its own (design §12.6.2,
    /// V1d24a).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - In servers/: a left temporary file of another server's pin (&lt;64 hex&gt;.json.0123456789abcdef.tmp) that no one
    ///   holds, and other.tmp.
    ///
    /// Steps:
    /// - Write the pin of https://licenses.example/.
    ///
    /// Expected:
    /// - The write returns true; the other pin's temporary file is gone; other.tmp stays.
    /// </remarks>
    [Fact]
    public void ServerPins_TemporaryFileOfAnotherPin_IsRemoved()
    {
        string servers = Path.Combine(_dir, "servers");
        Directory.CreateDirectory(servers);
        string left = Path.Combine(servers, new string('a', 64) + ".json.0123456789abcdef.tmp");
        string other = Path.Combine(servers, "other.tmp");
        File.WriteAllText(left, "left");
        File.WriteAllText(other, "other");

        Assert.True(new ServerPins(_dir, new FakeTimeProvider(Start)).TryWrite(new Uri("https://licenses.example/"), "lic-1", "key-1"));

        Assert.Equal((false, true), (File.Exists(left), File.Exists(other)));
    }

    /// <summary>
    /// A clean removes only the names of the writer's own temporary files: &lt;file&gt;.&lt;16 lowercase hex digits&gt;.tmp
    /// for one file, the pins' temporary files in servers/, and &lt;lease ID&gt;.json.tmp in borrows/ (design §12.6.2,
    /// V1d24a).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The names below, each a file no writer holds.
    ///
    /// Steps:
    /// - Ask each predicate about each name.
    ///
    /// Expected:
    /// - Only the names the theory marks as the writer's are taken. Not taken: uppercase or a character that is not a hex
    ///   digit, 15 or 17 digits, another file's name, a name that starts with more, the --force output's uppercase name, a
    ///   pin name of 63 digits or with an uppercase digit, a lease ID of 21 or 23 characters or with a character outside
    ///   [A-Za-z0-9_-].
    /// </remarks>
    /// <param name="kind">Which predicate: file (for file.json), pin, or borrow.</param>
    /// <param name="name">The file name.</param>
    /// <param name="taken">Whether it is one of the writer's temporary files.</param>
    [Theory]
    [InlineData("file", "file.json.0123456789abcdef.tmp", true)]
    [InlineData("file", "file.json.0123456789ABCDEF.tmp", false)]
    [InlineData("file", "file.json.0123456789abcdeg.tmp", false)]
    [InlineData("file", "file.json.0123456789abcde.tmp", false)]
    [InlineData("file", "file.json.0123456789abcdef0.tmp", false)]
    [InlineData("file", "file.json.0123456789abcdef.tmpx", false)]
    [InlineData("file", "other.json.0123456789abcdef.tmp", false)]
    [InlineData("file", "xfile.json.0123456789abcdef.tmp", false)]
    [InlineData("file", "file.json.tmp", false)]
    [InlineData("pin", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.json.0123456789abcdef.tmp", true)]
    [InlineData("pin", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde.json.0123456789abcdef.tmp", false)]
    [InlineData("pin", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeF.json.0123456789abcdef.tmp", false)]
    [InlineData("pin", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.jsox.0123456789abcdef.tmp", false)]
    [InlineData("pin", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.json.0123456789abcdeF.tmp", false)]
    [InlineData("borrow", "AbCdEfGhIjKlMnOpQr_-09.json.tmp", true)]
    [InlineData("borrow", "AbCdEfGhIjKlMnOpQr_-0.json.tmp", false)]
    [InlineData("borrow", "AbCdEfGhIjKlMnOpQr_-099.json.tmp", false)]
    [InlineData("borrow", "AbCdEfGhIjKlMnOpQr_.09.json.tmp", false)]
    [InlineData("borrow", "AbCdEfGhIjKlMnOpQr_-09.json", false)]
    public void Leftovers_OnlyTheWritersNames_AreTaken(string kind, string name, bool taken)
    {
        bool result = kind switch
        {
            "file" => FileReplacement.IsTemporaryOf(name, "file.json"),
            "pin" => ServerPins.IsPinTemporary(name),
            _ => LicenseClient.IsBorrowTemporary(name),
        };

        Assert.Equal(taken, result);
    }

    /// <summary>
    /// A clean of one file removes its temporary files only, and looks at no more than 1,000 of them (design §12.6.2,
    /// V1d24a).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,001 files file.json.&lt;16 hex&gt;.tmp that no writer holds; beside them file.json.&lt;16 HEX&gt;.tmp (the
    ///   --force output's form) and other.json.0123456789abcdef.tmp.
    ///
    /// Steps:
    /// - Clean the leftovers of file.json once, then once more.
    ///
    /// Expected:
    /// - After the first clean, exactly 1 of the 1,001 is left; after the second, none; the two files of other forms stay.
    /// </remarks>
    [Fact]
    public void RemoveLeftovers_MoreThanTheLimit_LooksAtTheLimit()
    {
        for (int i = 0; i < FileReplacement.MaxLeftovers + 1; i++)
        {
            File.WriteAllText(Path.Combine(_dir, $"file.json.{i:x16}.tmp"), "left");
        }

        string upper = Path.Combine(_dir, "file.json.0123456789ABCDEF.tmp");
        string other = Path.Combine(_dir, "other.json.0123456789abcdef.tmp");
        File.WriteAllText(upper, "force");
        File.WriteAllText(other, "other");

        FileReplacement.RemoveLeftovers(Old);
        int afterFirst = Temporaries(Old).Length;
        FileReplacement.RemoveLeftovers(Old);

        Assert.Equal((1, 0, true, true), (afterFirst, Temporaries(Old).Length, File.Exists(upper), File.Exists(other)));
    }

    /// <summary>
    /// When the replacement fails, Write deletes its temporary file and leaves the target as it was (design §12.6.2, V1d24).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A folder with a file in it at the target path, so no file can replace it; a clock that moves 1 second each time it
    ///   is read and fires each timer at once, so the tries on Windows end without a wait.
    ///
    /// Steps:
    /// - Write a file to that path.
    ///
    /// Expected:
    /// - IOException or UnauthorizedAccessException; no temporary file of the target is left; the folder and its file are
    ///   still there.
    /// </remarks>
    [Fact]
    public void Write_ReplacementFails_DeletesTheTemporaryFile()
    {
        string target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "inside.txt"), "inside");

        Exception error = Assert.ThrowsAny<Exception>(() => FileReplacement.Write(target, "new"u8, new JumpingTime()));

        Assert.True(error is IOException or UnauthorizedAccessException, error.ToString());
        Assert.Empty(Temporaries(target));
        Assert.Equal("inside", File.ReadAllText(Path.Combine(target, "inside.txt")));
    }

    /// <summary>
    /// ReadShared opens a file with the sharing that lets a writer delete it on Windows while it reads: the test reads a file
    /// through it while a writer holds it open for writing, and deletes the file while a handle with the same sharing is
    /// open (design §12.6.2, V1d24a).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - file.json ("old") held open by FileReplacement.OpenNew's shape on another name moved in place: a writer that holds a
    ///   new file it has just moved over file.json.
    ///
    /// Steps:
    /// - Read file.json with ReadShared while the writer holds it; close the writer; open file.json with ReadShared's
    ///   sharing (FileReplacement.ShareWithWriters) and delete it while that handle is open.
    ///
    /// Expected:
    /// - The read returns the writer's content; the delete passes.
    /// </remarks>
    [Fact]
    public void ReadShared_WriterHoldsTheNewFile_ReadsAndLetsADeletePass()
    {
        string fresh = Path.Combine(_dir, "file.json.0123456789abcdef.tmp");
        using (FileStream writer = FileReplacement.OpenNew(fresh))
        {
            writer.Write("new"u8);
            writer.Flush();
            File.Move(fresh, Old, overwrite: true);

            Assert.Equal("new", System.Text.Encoding.UTF8.GetString(FileReplacement.ReadShared(Old, 16)!));
        }

        using (new FileStream(Old, FileMode.Open, FileAccess.Read, FileReplacement.ShareWithWriters))
        {
            File.Delete(Old);
        }

        Assert.False(File.Exists(Old));
    }

    /// <summary>
    /// When a pin cannot replace the old one, TryWrite returns false and leaves no temporary file (design §12.6.2, V1d24).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A folder with a file in it at the pin's path of https://licenses.example/, so no file can replace it; the clock that
    ///   moves 1 second each time it is read.
    ///
    /// Steps:
    /// - Write the pin.
    ///
    /// Expected:
    /// - false; servers/ holds no .tmp file; the folder at the pin's path is still there with its file.
    /// </remarks>
    [Fact]
    public void ServerPins_ReplacementFails_LeavesNoTemporaryFile()
    {
        var url = new Uri("https://licenses.example/");
        string servers = Path.Combine(_dir, "servers");
        string pin = Path.Combine(
            servers,
            Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ElasticLM.Licensing.HttpTargetUri.Normalize(url)))) + ".json"
        );
        Directory.CreateDirectory(pin);
        File.WriteAllText(Path.Combine(pin, "inside.txt"), "inside");

        Assert.False(new ServerPins(_dir, new JumpingTime()).TryWrite(url, "lic-1", "key-1"));

        Assert.Empty(Directory.GetFiles(servers, "*.tmp"));
        Assert.Equal("inside", File.ReadAllText(Path.Combine(pin, "inside.txt")));
    }

    /// <summary>
    /// A file that a writer created but could not fill is deleted: the temporary file of Write, a secret file, and the
    /// installation key (design §12.6.2, V1d24).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux, not as root, with sudo that lets this user mount without a password, as the CI runners do; on GitHub Actions
    ///   the test fails without it, elsewhere it is skipped and says so. Other OSes have no tmpfs.
    /// - A tmpfs of 4 KiB mounted on a new folder and owned by this user; a key folder made in it; then a file of 4096
    ///   bytes, so no page is left for another file's content:
    ///   create-new: FileReplacement.Write of 64 bytes to file, through its temporary file;
    ///   secret: ProtectedFiles.TryCreateSecret of 64 bytes at admin.token.new;
    ///   key: InstallationKeyStore.TryLoadOrCreate in the key folder (user scope).
    ///
    /// Steps:
    /// - Write. Unmount at the end.
    ///
    /// Expected:
    /// - create-new and secret throw IOException; key fails with ELM_INSTALLATION_KEY_UNREADABLE "The installation key
    ///   cannot be created: …". In each case no file is left at the path, and for create-new no temporary file of it.
    /// </remarks>
    /// <param name="writer">The writer.</param>
    [Theory]
    [InlineData("create-new")]
    [InlineData("secret")]
    [InlineData("key")]
    public void Writers_DiskFullAfterCreating_DeleteTheNewFile(string writer)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "tmpfs is Linux only.");
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "The test runs as a user that is not root.");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string disk = Path.Combine(_dir, "disk");
        Directory.CreateDirectory(disk);
        string uid = UnixOwner.EffectiveUserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Sudo("mount", "-t", "tmpfs", "-o", $"size=4k,uid={uid},mode=0700", "tmpfs", disk);
        try
        {
            string keys = Path.Combine(disk, "keys");
            Directory.CreateDirectory(keys);
            File.WriteAllBytes(Path.Combine(disk, "filler"), new byte[4096]);
            string path = writer switch
            {
                "create-new" => Path.Combine(disk, "file"),
                "secret" => Path.Combine(disk, "admin.token.new"),
                _ => Path.Combine(keys, "installation.key"),
            };

            if (writer == "key")
            {
                Assert.False(
                    new InstallationKeyStore("acme-cad", KeyScope.User, keys).TryLoadOrCreate(out _, out ElasticLM.Licensing.LicenseError? error)
                );
                Assert.Equal(ElasticLM.Licensing.ErrorCode.InstallationKeyUnreadable, error!.Code);
                Assert.StartsWith("The installation key cannot be created: ", error.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.Throws<IOException>(() =>
                {
                    if (writer == "create-new")
                    {
                        FileReplacement.Write(path, new byte[64], new FakeTimeProvider(Start));
                    }
                    else
                    {
                        ProtectedFiles.TryCreateSecret(path, new byte[64], KeyScope.User, "test"u8.ToArray())?.Dispose();
                    }
                });
            }

            Assert.False(File.Exists(path));
            Assert.Empty(Temporaries(path));
        }
        finally
        {
            Sudo("umount", disk);
        }
    }

    // Makes a FIFO at a path with mkfifo, which Linux and macOS have.
    private static void MakeFifo(string path)
    {
        using var mkfifo = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("mkfifo", [path]) { RedirectStandardError = true, RedirectStandardOutput = true }
        )!;
        string error = mkfifo.StandardError.ReadToEnd();
        mkfifo.WaitForExit();
        Assert.True(mkfifo.ExitCode == 0, "mkfifo failed: " + error);
    }

    // Runs a command with sudo; skips the test where this user may not, except on GitHub Actions.
    private static void Sudo(params string[] arguments)
    {
        using var sudo = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("sudo", ["-n", .. arguments]) { RedirectStandardError = true, RedirectStandardOutput = true }
        )!;
        string error = sudo.StandardError.ReadToEnd();
        sudo.WaitForExit();
        if (sudo.ExitCode != 0)
        {
            string why = $"sudo -n {arguments[0]} exited with {sudo.ExitCode}: {error.Trim()}";
            Assert.False(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "On GitHub Actions this test must run. " + why);
            Assert.Skip("This machine does not let the test run " + arguments[0] + " as root. " + why);
        }
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier Everyone => new(WellKnownSidType.WorldSid, null);

    // The target and the write of each writer that cleans the leftovers of its file.
    private (string Target, Action Write) Writer(string writer)
    {
        if (writer == "activation")
        {
            string folder = Path.Combine(_dir, "activation");
            Directory.CreateDirectory(folder);
            var store = new ActivationStore(folder, new FakeTimeProvider(Start));
            return (Path.Combine(folder, "activation.json"), () => store.Write(new StoredActivation(null, 0, null, null, ["p"])));
        }

        if (writer == "pin")
        {
            var url = new Uri("https://licenses.example/");
            var pins = new ServerPins(_dir, new FakeTimeProvider(Start));
            string hash = Convert.ToHexStringLower(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ElasticLM.Licensing.HttpTargetUri.Normalize(url)))
            );
            string pin = Path.Combine(_dir, "servers", hash + ".json");
            return (pin, () => Assert.True(pins.TryWrite(url, "lic-written", "key-1")));
        }

        string license = Path.Combine(_dir, "server.elmlic");
        return (license, () => LicenseFiles.Write(license, "written"u8.ToArray(), new FakeTimeProvider(Start)));
    }

    // What each writer of Writer puts in its file.
    private static string Written(string writer) =>
        writer switch
        {
            "activation" => "\"retry\"",
            "pin" => "lic-written",
            _ => "written",
        };

    // The temporary files of a file beside it, by the writer's own form.
    private static string[] Temporaries(string path)
    {
        string folder = Path.GetDirectoryName(path)!;
        string name = Path.GetFileName(path);
        return Directory.Exists(folder)
            ? [.. Directory.GetFiles(folder, name + ".*.tmp").Where(f => FileReplacement.IsTemporaryOf(Path.GetFileName(f), name))]
            : [];
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

    // A clock that moves 1 second each time it is read and fires each timer at once: a wait on it ends without waiting.
    private sealed class JumpingTime : TimeProvider
    {
        private long _seconds;

        public override long TimestampFrequency => 1;

        public override long GetTimestamp() => Interlocked.Increment(ref _seconds);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            callback(state);
            return new FakeTimeProvider().CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
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
