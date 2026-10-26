using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using ElasticLM.Issuing;
using ElasticLM.Leasing;
using ElasticLM.Licensing;

namespace ElasticLM.Server;

/// <summary>
/// The record of borrows and early returns (design §13.4): a signed log, stream <c>borrows</c>, signed after every record and
/// flushed to the disk before the borrow or the return takes effect. Each new file (at each start and each UTC day) carries
/// forward the borrows that have not expired and the early returns of the last 30 days, so the newest file alone restores
/// the state.
/// </summary>
/// <remarks>
/// The record's lock is always taken before the log's: a new file carries records forward under the log's lock, so every
/// call into the log goes through the record's lock first.
/// </remarks>
internal sealed class BorrowJournal : IBorrowJournal, IDisposable
{
    public const string BaseName = "borrows";
    public const string BorrowStarted = "borrow.started";
    public const string BorrowReturned = "borrow.returned";

    private const int MaxFingerprints = 8;
    private static readonly TimeSpan ReturnWindow = TimeSpan.FromDays(30);

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly List<(BorrowRecord Record, string LeaseId, long Expires)> _borrows = [];
    private readonly List<BorrowReturn> _returns = [];
    private SignedLog? _log;

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
        LogReport report = LogVerifier.Verify([newest], trust, server.License.ProductId, BaseName);
        string? problems = report.Intact
            ? null
            : string.Join(" ", report.Issues.Where(i => i.Severity == LogIssueSeverity.Error).Select(i => $"line {i.Line}: {i.Message}"));
        if (problems is null && report.Files[^1] is LogFileKey last && last.KeyId != server.KeyId && last.LicenseId != server.License.LicenseId)
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
            File.Move(newest, aside);
            journal.SetAside = aside;
            return journal;
        }

        foreach (LogEntry entry in report.Entries)
        {
            if (entry.IsSigned || entry.Type != BorrowReturned)
            {
                journal.Read(entry.Type, entry.Value);
            }
        }

        return journal;
    }

    /// <summary>
    /// Opens the record for writing: a new file that carries forward what is still needed.
    /// </summary>
    public void Open(SigningKeyAccess key, byte[] licenseFile, byte[] keyJwk)
    {
        string? reset = SetAside is null ? null : $"the broken record {Path.GetFileName(SetAside)} was set aside by the operator";
        lock (_lock)
        {
            _log = SignedLog.Open(_directory, BaseName, BaseName, key, licenseFile, keyJwk, _time, sealEvery: 1, CarryForward, reset);
        }
    }

    public void Borrowed(BorrowRecord borrow)
    {
        ArgumentNullException.ThrowIfNull(borrow);
        (string leaseId, long expires) = ReadToken(borrow.Token);
        lock (_lock)
        {
            WriteBorrow(_log!, borrow);
            _borrows.Add((borrow, leaseId, expires));
        }
    }

    public void Returned(BorrowReturn returned)
    {
        ArgumentNullException.ThrowIfNull(returned);
        lock (_lock)
        {
            WriteReturn(_log!, returned);
            _borrows.RemoveAll(b => b.LeaseId == returned.LeaseId);
            _returns.Add(returned);
        }
    }

    /// <summary>Records a new server license; the old key signs the change.</summary>
    public void ChangeKey(byte[] licenseFile, byte[] keyJwk, SigningKeyAccess key)
    {
        lock (_lock)
        {
            _log?.ChangeKey(licenseFile, keyJwk, key);
        }
    }

    /// <summary>Starts a new file when the UTC day changed.</summary>
    public void Seal()
    {
        lock (_lock)
        {
            _log?.Seal();
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
    // they only decide what to carry forward.
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
            catch (JsonException)
            {
                // Not a token: kept with no expiry, and the engine skips it.
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

    private static void WriteBorrow(SignedLog log, BorrowRecord borrow) =>
        log.Append(
            BorrowStarted,
            w =>
            {
                w.WriteString("lease", borrow.Token);
                w.WriteString("lic", borrow.LicenseId);
                w.WriteNumber("seats", borrow.FeatureSeats);
                w.WriteNumber("borrow", (long)borrow.FeatureBorrowLimit.TotalSeconds);
                WriteFingerprints(w, borrow.Fingerprints);
            }
        );

    private static void WriteReturn(SignedLog log, BorrowReturn returned) =>
        log.Append(
            BorrowReturned,
            w =>
            {
                w.WriteString("id", returned.LeaseId);
                w.WriteString("key", returned.KeyId);
                WriteFingerprints(w, returned.Fingerprints);
                w.WriteNumber("at", returned.At.ToUnixTimeSeconds());
            }
        );

    // One recorded line; lines of another shape are skipped (the verifier has checked the signatures over them).
    private void Read(string type, JsonElement line)
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
            string leaseId = id.GetString()!;
            _borrows.RemoveAll(b => b.LeaseId == leaseId);
            _returns.Add(new BorrowReturn(leaseId, key.GetString()!, ReadFingerprints(line), DateTimeOffset.FromUnixTimeSeconds(atSeconds)));
        }
    }

    // A new file carries forward the borrows that have not expired and the early returns of the last 30 days. It runs under
    // the log's lock, which the callers reach through the record's lock (see the remarks).
    private void CarryForward(SignedLog log)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_lock)
        {
            _borrows.RemoveAll(b => b.Expires < now.ToUnixTimeSeconds());
            _returns.RemoveAll(r => now - r.At >= ReturnWindow);
            foreach ((BorrowRecord record, _, _) in _borrows)
            {
                WriteBorrow(log, record);
            }

            foreach (BorrowReturn returned in _returns)
            {
                WriteReturn(log, returned);
            }
        }
    }
}
