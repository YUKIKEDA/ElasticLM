using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using ElasticLM.Issuing;
using ElasticLM.Leasing;
using ElasticLM.Licensing;
using Microsoft.Extensions.Logging;

namespace ElasticLM.Server;

/// <summary>
/// The record of borrows and early returns (design §13.4): a signed log, stream <c>borrows</c>, signed after every record and
/// flushed to the disk before the borrow or the return takes effect. Each new file (at each start and each UTC day) carries
/// forward the borrows that have not expired and the early returns of the last 30 days, so the newest file alone restores
/// the state.
/// </summary>
/// <remarks>
/// The record's lock is always taken before the log's: a new file carries records forward under the log's lock, so every
/// call into the log goes through the record's lock first. A new file, on a new UTC day or after
/// <see cref="DefaultCompactAfter"/> lines, is started only by <see cref="Seal"/> (the server's 60-second timer): its carried
/// lines are built outside both locks, and only the records written meanwhile, the writes, the signatures, and the rename
/// happen under them (design §13.4, V1d16).
/// </remarks>
internal sealed class BorrowJournal : IBorrowJournal, IDisposable
{
    public const string BaseName = "borrows";
    public const string BorrowStarted = "borrow.started";
    public const string BorrowReturned = "borrow.returned";

    /// <summary>The borrows and returns written one at a time, each with its own signature, after which the next run of the
    /// timer starts a new file (design §13.4, V1d16).</summary>
    public const int DefaultCompactAfter = 1_000;

    private const int MaxFingerprints = 8;
    private static readonly TimeSpan ReturnWindow = TimeSpan.FromDays(30);

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly List<(BorrowRecord Record, string LeaseId, long Expires)> _borrows = [];
    private readonly List<BorrowReturn> _returns = [];
    private SignedLog? _log;
    private ILogger? _operational;

    // The lines written while a new file is being built, for that file; null otherwise. Under _lock.
    private List<LogLine>? _since;

    /// <summary>Gets or sets the borrows and returns written one at a time after which a new file starts; tests make it small.</summary>
    internal int CompactAfter { get; set; } = DefaultCompactAfter;

    /// <summary>Gets or sets a step run after the lines of a new file are built and before it is written, for tests.</summary>
    internal Action? Built { get; set; }

    /// <summary>Gets a value indicating whether the calling thread holds the record's lock, for tests.</summary>
    internal bool IsLockHeld => _lock.IsHeldByCurrentThread;

    private BorrowJournal(string directory, TimeProvider time)
    {
        _directory = directory;
        _time = time;
    }

    /// <summary>Gets the borrows read at start, oldest first.</summary>
    public IReadOnlyList<BorrowRecord> Borrows => [.. _borrows.Select(b => b.Record)];

    /// <summary>Gets the early returns read at start.</summary>
    public IReadOnlyList<BorrowReturn> Returns => [.. _returns];

    /// <summary>Gets the file the operator set aside at this start, or <see langword="null"/>.</summary>
    public string? SetAside { get; private set; }

    /// <summary>
    /// Reads the newest file of the record and verifies it, and that its last key is this server's key or a key of the same
    /// server license (design §13.4). A broken record stops the start, unless the operator asked to set it aside
    /// (<c>--set-aside-broken-records</c>): the file is renamed and the server starts without its borrows. Early returns after
    /// the last signature are dropped: the client was not told they succeeded.
    /// </summary>
    /// <exception cref="ServerStartException">The record is broken and was not to be set aside.</exception>
    public static BorrowJournal Load(string directory, TrustStore trust, ServerIdentity server, bool setAsideBroken, TimeProvider time)
    {
        var journal = new BorrowJournal(directory, time);
        IReadOnlyList<string> files = SignedLog.FindFiles(directory, BaseName);
        if (files.Count == 0)
        {
            return journal;
        }

        string newest = files[^1];

        // design §18.1 (V1d23): the verifier hands out the event lines of the newest file; they are used only when the file
        // verified.
        var entries = new List<LogEntry>();
        LogReport report = LogVerifier.Verify([newest], trust, server.License.ProductId, BaseName, onEntry: entries.Add);
        string? problems = report.Intact
            ? null
            : string.Join(" ", report.Issues.Where(i => i.Severity == LogIssueSeverity.Error).Select(i => $"line {i.Line}: {i.Message}"));
        if (problems is null && report.Files.Count == 0)
        {
            problems = "it names no server license.";
        }
        else if (problems is null && report.Files[^1] is LogFileKey last && last.KeyId != server.KeyId && last.LicenseId != server.License.LicenseId)
        {
            problems = $"it is signed by the key of server license {last.LicenseId}, not by this server ({server.License.LicenseId}).";
        }

        if (problems is not null)
        {
            if (!setAsideBroken)
            {
                throw new ServerStartException(
                    $"The borrow record '{newest}' is broken ({problems}). Start with --set-aside-broken-records to move it aside; "
                        + "the borrows in it are then not counted until they expire."
                );
            }

            string aside = Path.Combine(
                directory,
                $"{BaseName}-broken-{time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}.jsonl"
            );
            try
            {
                File.Move(newest, aside);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ServerStartException($"The broken borrow record '{newest}' cannot be moved aside: {e.Message}");
            }

            journal.SetAside = aside;
            return journal;
        }

        foreach (LogEntry entry in entries)
        {
            if (entry.IsSigned || entry.Type != BorrowReturned)
            {
                journal.Read(entry.Type, entry.Value);
            }
        }

        return journal;
    }

    /// <summary>
    /// Opens the record for writing: a new file that carries forward what is still needed. A file with the name of a record
    /// file but no real date and time is reported in the operational log, at this start and at each new UTC day. A borrow or
    /// a return that cannot be written is reported there too, with its cause, before the engine refuses it.
    /// </summary>
    public void Open(SigningKeyAccess key, byte[] licenseFile, byte[] keyJwk, ILogger operational)
    {
        string? reset = SetAside is null ? null : $"the broken record {Path.GetFileName(SetAside)} was set aside by the operator";
        lock (_lock)
        {
            _operational = operational;
            _log = SignedLog.Open(
                _directory,
                BaseName,
                BaseName,
                key,
                licenseFile,
                keyJwk,
                _time,
                sealEvery: 1,
                CarryForward,
                reset,
                name => ServerLog.NotALogFile(operational, name, "borrow record"),
                rotateOnWrite: false,
                name => ServerLog.UnfinishedRecordRemoved(operational, name)
            );
        }
    }

    public void Borrowed(BorrowRecord borrow)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        (string leaseId, long expires) = ReadToken(borrow.Token);
        lock (_lock)
        {
            Recorded(() => _log!.Append(BorrowStarted, BorrowFields(borrow)));
            _borrows.Add((borrow, leaseId, expires));
            _since?.Add(new LogLine(BorrowStarted, BorrowFields(borrow)));
        }
    }

    public void Returned(BorrowReturn returned)
    {
        ArgumentNullException.ThrowIfNull(returned);
        lock (_lock)
        {
            Recorded(() => _log!.Append(BorrowReturned, ReturnFields(returned)));
            _borrows.RemoveAll(b => b.LeaseId == returned.LeaseId);
            _returns.Add(returned);
            _since?.Add(new LogLine(BorrowReturned, ReturnFields(returned)));
        }
    }

    // Writes a borrow or a return. The engine refuses the request when the write throws, and the next file's chain.reset
    // names only the file, so the cause is kept here, in the operational log.
    private void Recorded(Action write)
    {
        try
        {
            write();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (_operational is not null)
            {
                ServerLog.BorrowRecordFailed(_operational, e);
            }

            throw;
        }
    }

    /// <summary>Records a new server license; the old key signs the change. When the change cannot be written, the record keeps
    /// the old license, as the server does (design §13.1).</summary>
    public void ChangeKey(byte[] licenseFile, byte[] keyJwk, SigningKeyAccess key)
    {
        lock (_lock)
        {
            _log?.ChangeKey(licenseFile, keyJwk, key, takeOnFailure: false);
        }
    }

    /// <summary>
    /// Drops the borrows that have expired and the early returns older than 30 days, which a new file would not carry forward
    /// (design §12.7.4, V1d16). The server calls it every 30 seconds.
    /// </summary>
    public void Purge()
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_lock)
        {
            Drop(now);
        }
    }

    /// <summary>
    /// Starts a new file when the UTC day changed, or when the file being written holds <see cref="CompactAfter"/> borrows and
    /// returns written one at a time (design §13.4, V1d16): the lines it carries forward are built without the record's lock,
    /// from the borrows and returns at that moment; the records written meanwhile go to the file being closed and, after the
    /// carried lines, to the new file. The server's 60-second timer calls it; requests never start a file.
    /// </summary>
    /// <exception cref="IOException">The new file cannot be written; the next borrow or return starts a new file.</exception>
    public void Seal()
    {
        LogLine[] lines;
        lock (_lock)
        {
            if (_log is null || !(_log.IsNewDay() || _log.AppendedLines >= CompactAfter))
            {
                _log?.Seal();
                return;
            }

            Drop(_time.GetUtcNow());
            lines = [.. Lines()];
            _since = [];
        }

        using CarriedLines carried = SignedLog.Prepare(lines, _time);
        Built?.Invoke();
        lock (_lock)
        {
            try
            {
                Recorded(() => _log.StartNewFile(carried, _since));
            }
            finally
            {
                _since = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _log?.Dispose();
        }
    }

    // The lease ID and expiry of a token, read without verifying it: the engine verifies tokens when it restores them; here
    // they only decide what to carry forward. JsonElement throws InvalidOperationException from TryGetProperty on a payload
    // that is not an object ("[]") and from GetString on a lone surrogate escape (measured on .NET 10.0.15, Windows).
    private static (string LeaseId, long Expires) ReadToken(string token)
    {
        string[] parts = token.Split('.');
        if (parts.Length == 3 && Base64Url.IsValid(parts[1]))
        {
            try
            {
                using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
                if (
                    payload.RootElement.TryGetProperty("lse", out JsonElement lse)
                    && lse.ValueKind == JsonValueKind.String
                    && payload.RootElement.TryGetProperty("exp", out JsonElement exp)
                    && exp.ValueKind == JsonValueKind.Number
                    && exp.TryGetInt64(out long expires)
                )
                {
                    return (lse.GetString()!, expires);
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            {
                // Not a token: read with the expiry 0. The engine does not restore it, and the next file does not carry it
                // forward (CarryForward drops what has expired).
            }
        }

        return (string.Empty, 0);
    }

    private static void WriteFingerprints(Utf8JsonWriter writer, IReadOnlyList<string> fingerprints)
    {
        writer.WriteStartArray("fp");
        foreach (string fingerprint in fingerprints)
        {
            writer.WriteStringValue(fingerprint);
        }

        writer.WriteEndArray();
    }

    // The fingerprints of a line: an array of at most 8 strings; anything else reads as none.
    private static string[] ReadFingerprints(JsonElement line) =>
        line.TryGetProperty("fp", out JsonElement fp)
        && fp.ValueKind == JsonValueKind.Array
        && fp.GetArrayLength() <= MaxFingerprints
        && fp.EnumerateArray().All(f => f.ValueKind == JsonValueKind.String)
            ? [.. fp.EnumerateArray().Select(f => f.GetString()!)]
            : [];

    private static Action<Utf8JsonWriter> BorrowFields(BorrowRecord borrow) =>
        w =>
        {
            w.WriteString("lease", borrow.Token);
            w.WriteString("lic", borrow.LicenseId);
            w.WriteNumber("seats", borrow.FeatureSeats);
            w.WriteNumber("borrow", (long)borrow.FeatureBorrowLimit.TotalSeconds);
            WriteFingerprints(w, borrow.Fingerprints);
        };

    private static Action<Utf8JsonWriter> ReturnFields(BorrowReturn returned) =>
        w =>
        {
            w.WriteString("id", returned.LeaseId);
            w.WriteString("key", returned.KeyId);
            WriteFingerprints(w, returned.Fingerprints);
            w.WriteNumber("at", returned.At.ToUnixTimeSeconds());
        };

    // One recorded line; lines of another shape are skipped (the verifier has checked the signatures over them). A line with
    // a string or a member name that is not text (a lone surrogate escape, bytes that are not UTF-8) is of another shape:
    // JsonElement throws InvalidOperationException from GetString and TryGetProperty there (measured on .NET 10.0.15,
    // Windows), before the line changes anything.
    private void Read(string type, JsonElement line)
    {
        try
        {
            ReadLine(type, line);
        }
        catch (InvalidOperationException)
        {
            // Skipped.
        }
    }

    private void ReadLine(string type, JsonElement line)
    {
        if (
            type == BorrowStarted
            && line.TryGetProperty("lease", out JsonElement token)
            && token.ValueKind == JsonValueKind.String
            && line.TryGetProperty("lic", out JsonElement lic)
            && lic.ValueKind == JsonValueKind.String
            && line.TryGetProperty("seats", out JsonElement seats)
            && seats.ValueKind == JsonValueKind.Number
            && seats.TryGetInt32(out int seatCount)
            && line.TryGetProperty("borrow", out JsonElement borrow)
            && borrow.ValueKind == JsonValueKind.Number
            && borrow.TryGetInt64(out long borrowSeconds)
            && borrowSeconds is >= 0 and <= 3_162_240_000
        )
        {
            var record = new BorrowRecord(
                token.GetString()!,
                lic.GetString()!,
                seatCount,
                TimeSpan.FromSeconds(borrowSeconds),
                ReadFingerprints(line)
            );
            (string leaseId, long expires) = ReadToken(record.Token);
            _borrows.Add((record, leaseId, expires));
        }
        else if (
            type == BorrowReturned
            && line.TryGetProperty("id", out JsonElement id)
            && id.ValueKind == JsonValueKind.String
            && line.TryGetProperty("key", out JsonElement key)
            && key.ValueKind == JsonValueKind.String
            && line.TryGetProperty("at", out JsonElement at)
            && at.ValueKind == JsonValueKind.Number
            && at.TryGetInt64(out long atSeconds)
            && atSeconds is >= 0 and <= 253_402_300_799
        )
        {
            var returned = new BorrowReturn(id.GetString()!, key.GetString()!, ReadFingerprints(line), DateTimeOffset.FromUnixTimeSeconds(atSeconds));
            _borrows.RemoveAll(b => b.LeaseId == returned.LeaseId);
            _returns.Add(returned);
        }
    }

    // The borrows that have expired and the early returns older than 30 days, which are not carried forward.
    private void Drop(DateTimeOffset now)
    {
        _borrows.RemoveAll(b => b.Expires < now.ToUnixTimeSeconds());
        _returns.RemoveAll(r => now - r.At >= ReturnWindow);
    }

    // A new file carries forward the borrows that have not expired and the early returns of the last 30 days: at a start,
    // and after a failed write. It runs under the log's lock, which the callers reach through the record's lock (see the
    // remarks).
    private IReadOnlyList<LogLine> CarryForward()
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_lock)
        {
            Drop(now);
            return [.. Lines()];
        }
    }

    // The lines of the borrows and returns held now; under _lock.
    private IEnumerable<LogLine> Lines() =>
        _borrows
            .Select(b => new LogLine(BorrowStarted, BorrowFields(b.Record)))
            .Concat(_returns.Select(r => new LogLine(BorrowReturned, ReturnFields(r))));
}
