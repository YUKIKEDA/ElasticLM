using System.Text;
using System.Text.Json;
using ElasticLM.Client;
using ElasticLM.Issuing;
using ElasticLM.Issuing.Tests;
using ElasticLM.Leasing;
using ElasticLM.Licensing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Server.Tests;

/// <summary>
/// The file of a new UTC day of the borrow record (design §13.4, §18.1, V1d16): built outside the record's lock, started
/// only by <see cref="BorrowJournal.Seal"/>, its first line signed alone and its carried lines under one more signature,
/// and written under an unfinished name until it is on the disk.
/// </summary>
public sealed class BorrowJournalTests : IAsyncDisposable
{
    private static readonly DateTimeOffset LastMinute = Deployment.Start.AddHours(23).AddMinutes(59);
    private static readonly string[] Prints = ["aa00000000000000000000000000000a"];

    private readonly Deployment _deployment = new();
    private readonly List<IDisposable> _owned = [];

    public async ValueTask DisposeAsync()
    {
        foreach (IDisposable owned in Enumerable.Reverse(_owned))
        {
            owned.Dispose();
        }

        await _deployment.DisposeAsync();
    }

    /// <summary>
    /// A new UTC day's file is started by the timer's seal only: a return written after midnight goes to the file of the day
    /// before; the seal builds the carried lines without the record's lock, a return written while they are built goes to
    /// both files, and the new file holds its first line, a signature, the carried lines and that return, and one signature.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server data folder with its license; the record opened at 23:59 UTC on its own clock; a borrow and the early
    ///   return x written.
    ///
    /// Steps:
    /// - At 00:00:10 write the return y; count the files. Seal, with a step after the build that notes whether the record's
    ///   lock is held and writes the return z. Read both files; verify them; load the record again.
    ///
    /// Expected:
    /// - One file after y. Two files after the seal, none of them unfinished; the lock was not held while the lines were
    ///   built. The first file ends with the return z and a signature. The new file's lines are chain.start, auth,
    ///   borrow.started, borrow.returned x, y, and z, and auth. Both files verify with no error. The loaded record has the
    ///   borrow and the returns x, y, and z.
    /// </remarks>
    [Fact]
    public void Seal_NewDay_BuildsOutsideTheLockAndCarriesWhatCameMeanwhile()
    {
        string data = NewServerFolder();
        var clock = new FakeTimeProvider(LastMinute);
        BorrowJournal journal = Open(data, clock, NullLog());
        journal.Borrowed(Borrow("a"));
        journal.Returned(Return("x", clock));

        clock.Advance(TimeSpan.FromSeconds(70));
        journal.Returned(Return("y", clock));
        Assert.Single(Files(data));

        bool? held = null;
        journal.Built = () =>
        {
            held = journal.IsLockHeld;
            journal.Returned(Return("z", clock));
        };
        journal.Seal();

        IReadOnlyList<string> files = Files(data);
        Assert.Equal(2, files.Count);
        Assert.Empty(Directory.GetFiles(data, "*.partial"));
        Assert.False(held);
        string[] old = Lines(files[0]);
        Assert.Equal(["borrow.returned", "auth"], old[^2..].Select(Type));
        Assert.Equal("z", JsonDocument.Parse(old[^2]).RootElement.GetProperty("id").GetString());
        Assert.Equal(
            ["chain.start", "auth", "borrow.started", "borrow.returned", "borrow.returned", "borrow.returned", "auth"],
            Lines(files[1]).Select(Type)
        );
        Assert.Equal(["x", "y", "z"], Lines(files[1]).Where(l => Type(l) == "borrow.returned").Select(Id));
        VerifiedLog verified = VerifiedLog.Of(files, _deployment.Trust, "acme-cad", "borrows");
        Assert.DoesNotContain(verified.Issues, i => i.Severity == LogIssueSeverity.Error);

        BorrowJournal again = Load(data, clock);
        Assert.Single(again.Borrows);
        Assert.Equal(["x", "y", "z"], again.Returns.Select(r => r.LeaseId));
    }

    /// <summary>
    /// Within a day, the timer's seal starts a new file once the file being written holds the count of borrows and returns
    /// written one at a time (1,000 by default), the same way as on a new day; the count starts again in the new file
    /// (design §13.4, V1d16 Q16-6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A record opened at noon UTC, with the count set to 3.
    ///
    /// Steps:
    /// - Write two returns and seal; write a third and seal; write a fourth and seal; verify the files.
    ///
    /// Expected:
    /// - The default count is 1,000. One file after the first seal; two after the second, the new one holding chain.start,
    ///   auth, the three returns, and auth; still two after the third; no error.
    /// </remarks>
    [Fact]
    public void Seal_AtTheCountOfSignedLines_StartsANewFile()
    {
        Assert.Equal(1_000, BorrowJournal.DefaultCompactAfter);
        string data = NewServerFolder();
        var clock = new FakeTimeProvider(Deployment.Start.AddHours(12));
        BorrowJournal journal = Open(data, clock, NullLog());
        journal.CompactAfter = 3;

        journal.Returned(Return("x", clock));
        journal.Returned(Return("y", clock));
        journal.Seal();
        Assert.Single(Files(data));

        journal.Returned(Return("z", clock));
        journal.Seal();
        IReadOnlyList<string> files = Files(data);
        Assert.Equal(2, files.Count);
        Assert.Equal(["chain.start", "auth", "borrow.returned", "borrow.returned", "borrow.returned", "auth"], Lines(files[1]).Select(Type));

        journal.Returned(Return("w", clock));
        journal.Seal();
        Assert.Equal(2, Files(data).Count);
        Assert.DoesNotContain(
            VerifiedLog.Of(Files(data), _deployment.Trust, "acme-cad", "borrows").Issues,
            i => i.Severity == LogIssueSeverity.Error
        );
    }

    /// <summary>
    /// After a write that failed, the next seal of a new day starts one file only: the file started for the failure, with
    /// chain.reset, already carries every record (design §18.1, V1d16).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A record with a borrow and the early return x; a signing step that fails while armed.
    ///
    /// Steps:
    /// - On the next day, arm it and write the return y, which throws; disarm it and seal; load the record again.
    ///
    /// Expected:
    /// - Two files, the second starting with chain.reset; the loaded record has the borrow and the return x (y was refused).
    /// </remarks>
    [Fact]
    public void Seal_NewDayAfterAFailedWrite_StartsOneFile()
    {
        string data = NewServerFolder();
        var clock = new FakeTimeProvider(LastMinute);
        bool fail = false;
        BorrowJournal journal = Open(data, clock, NullLog(), failWhen: () => fail);
        journal.Borrowed(Borrow("a"));
        journal.Returned(Return("x", clock));
        clock.Advance(TimeSpan.FromSeconds(70));
        fail = true;
        Assert.Throws<IOException>(() => journal.Returned(Return("y", clock)));
        fail = false;

        journal.Seal();

        IReadOnlyList<string> files = Files(data);
        Assert.Equal(2, files.Count);
        Assert.Equal("chain.reset", Type(Lines(files[1])[0]));
        BorrowJournal again = Load(data, clock);
        Assert.Single(again.Borrows);
        Assert.Equal(["x"], again.Returns.Select(r => r.LeaseId));
    }

    /// <summary>
    /// A stop at any step of a new day's file leaves it under its unfinished name: the next start reads the file of the day
    /// before, which holds every record, removes the unfinished file with a warning in the operational log, and continues the
    /// chain from the file of the day before.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A record whose seal on a new day made a second file of 7 lines (chain.start, auth, a borrow and three returns,
    ///   auth); the record closed. The second file replaced by an unfinished file holding its first 1, 2, 6, or 7 lines: a
    ///   stop after the first line, after the first signature, after the carried lines, and before the rename.
    ///
    /// Steps:
    /// - Load the record and open it, with the operational log recorded; verify the files.
    ///
    /// Expected:
    /// - The borrow and the returns x, y, and z; no unfinished file; one Warning "The borrow record file (name).partial was
    ///   left unfinished by a stop while it was started; it was removed, and the record was read from the file before it.";
    ///   two files, the new one starting with chain.start that names the first; no error.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(7)]
    public void Start_AfterAStopInANewDaysFile_ReadsTheDayBefore(int kept)
    {
        string data = NewServerFolder();
        var clock = new FakeTimeProvider(LastMinute);
        string second;
        using (BorrowJournal journal = Open(data, clock, NullLog(), own: false))
        {
            journal.Borrowed(Borrow("a"));
            journal.Returned(Return("x", clock));
            clock.Advance(TimeSpan.FromSeconds(70));
            journal.Returned(Return("y", clock));
            journal.Built = () => journal.Returned(Return("z", clock));
            journal.Seal();
            second = Files(data)[1];
        }

        string[] lines = Lines(second);
        Assert.Equal(7, lines.Length);
        File.Delete(second);
        File.WriteAllText(second + ".partial", string.Concat(lines.Take(kept).Select(l => l + "\n")));
        clock.Advance(TimeSpan.FromMinutes(1));
        using var log = new RecordingLoggerProvider();

        BorrowJournal opened = Open(data, clock, log.CreateLogger("elm-server"));

        Assert.Single(opened.Borrows);
        Assert.Equal(["x", "y", "z"], opened.Returns.Select(r => r.LeaseId));
        Assert.Empty(Directory.GetFiles(data, "*.partial"));
        string name = Path.GetFileName(second) + ".partial";
        Assert.Equal(
            [
                $"The borrow record file {name} was left unfinished by a stop while it was started; it was removed, and the record was read from the file before it.",
            ],
            log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message)
        );
        IReadOnlyList<string> files = Files(data);
        Assert.Equal(2, files.Count);
        Assert.Contains($"\"file\":\"{Path.GetFileName(files[0])}\"", Lines(files[1])[0], StringComparison.Ordinal);
        Assert.Equal("chain.start", Type(Lines(files[1])[0]));
        Assert.DoesNotContain(VerifiedLog.Of(files, _deployment.Trust, "acme-cad", "borrows").Issues, i => i.Severity == LogIssueSeverity.Error);
    }

    /// <summary>
    /// A new day's file that cannot be written is removed and the file of the day before stays the newest; the next return
    /// starts a file that continues the chain from it with chain.start and carries every record forward (design §18.1,
    /// V1d16 review K8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A record with a borrow and the early return x; a signing step that fails with IOException at its first or second
    ///   signature after it is armed: the new file's signature of its first line, or the one over its carried lines.
    ///
    /// Steps:
    /// - On the next day, arm it and seal; disarm it, write the return y; load the record again.
    ///
    /// Expected:
    /// - The seal throws IOException; one file and no unfinished file; then two files, the second starting with chain.start
    ///   that names the first; the files verify with no issue; the loaded record has the borrow and the returns x and y.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Seal_NewDayThatCannotBeWritten_LeavesTheDayBeforeNewest(int failing)
    {
        string data = NewServerFolder();
        var clock = new FakeTimeProvider(LastMinute);
        int signatures = -1;
        BorrowJournal journal = Open(data, clock, NullLog(), failWhen: () => signatures >= 0 && ++signatures == failing);
        journal.Borrowed(Borrow("a"));
        journal.Returned(Return("x", clock));
        clock.Advance(TimeSpan.FromSeconds(70));

        signatures = 0;
        Assert.Throws<IOException>(journal.Seal);
        Assert.Single(Files(data));
        Assert.Empty(Directory.GetFiles(data, "*.partial"));

        signatures = -1;
        journal.Returned(Return("y", clock));
        IReadOnlyList<string> files = Files(data);
        Assert.Equal(2, files.Count);
        Assert.Equal("chain.start", Type(Lines(files[1])[0]));
        Assert.Contains($"\"file\":\"{Path.GetFileName(files[0])}\"", Lines(files[1])[0], StringComparison.Ordinal);
        Assert.Empty(VerifiedLog.Of(files, _deployment.Trust, "acme-cad", "borrows").Issues);
        BorrowJournal again = Load(data, clock);
        Assert.Single(again.Borrows);
        Assert.Equal(["x", "y"], again.Returns.Select(r => r.LeaseId));
    }

    private static string Type(string line) => JsonDocument.Parse(line).RootElement.GetProperty("type").GetString()!;

    private static string Id(string line) => JsonDocument.Parse(line).RootElement.GetProperty("id").GetString()!;

    // The lines of a file that the record may still hold open.
    private static string[] Lines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static IReadOnlyList<string> Files(string data) => SignedLog.FindFiles(data, BorrowJournal.BaseName);

    private static Microsoft.Extensions.Logging.Abstractions.NullLogger NullLog() => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    private static BorrowRecord Borrow(string id) =>
        new(
            $"a.{System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{{\"lse\":\"{id}\",\"exp\":4102444800}}"))}.c",
            "lic-srv",
            2,
            TimeSpan.FromDays(7),
            Prints
        );

    private static BorrowReturn Return(string id, TimeProvider clock) => new(id, "key-" + id, Prints, clock.GetUtcNow());

    private string NewServerFolder()
    {
        string data = _deployment.NewFolder();
        _deployment.WriteServerLicense(data);
        return data;
    }

    private ServerIdentity Identity(string data, TimeProvider clock, InstallationKey key)
    {
        var verifier = new LicenseVerifier(_deployment.Trust, clock, StatelessClockGuard.Instance);
        Assert.True(
            ServerIdentity.TryVerify(
                verifier,
                File.ReadAllBytes(Path.Combine(data, "server.elmlic")),
                key.WithPrivateKey(KeyFiles.ExportPublicJwk),
                "acme-cad",
                out ServerIdentity? identity,
                out _
            )
        );
        _owned.Add(identity!);
        return identity!;
    }

    private BorrowJournal Load(string data, TimeProvider clock)
    {
        InstallationKey key = Key(data);
        BorrowJournal journal = BorrowJournal.Load(data, _deployment.Trust, Identity(data, clock, key), setAsideBroken: false, clock);
        _owned.Add(journal);
        return journal;
    }

    // Loads and opens the record of a data folder with its own key, as the server does; the signing fails while failWhen
    // says so.
    private BorrowJournal Open(string data, TimeProvider clock, ILogger log, Func<bool>? failWhen = null, bool own = true)
    {
        InstallationKey key = Key(data);
        BorrowJournal journal = BorrowJournal.Load(data, _deployment.Trust, Identity(data, clock, key), setAsideBroken: false, clock);
        if (own)
        {
            _owned.Add(journal);
        }

        byte[] license = File.ReadAllBytes(Path.Combine(data, "server.elmlic"));
        journal.Open(
            use => failWhen?.Invoke() == true ? throw new IOException("The disk is full.") : key.WithPrivateKey(use),
            license,
            key.WithPrivateKey(KeyFiles.ExportPublicJwk),
            log
        );
        return journal;
    }

    private InstallationKey Key(string data)
    {
        var store = new InstallationKeyStore("acme-cad", KeyScope.Machine, data);
        Assert.True(store.TryLoad(out InstallationKey? key, out _));
        _owned.Add(key!);
        return key!;
    }
}
