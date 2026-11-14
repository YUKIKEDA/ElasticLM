using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ElasticLM.Licensing;

namespace ElasticLM.Issuing;

/// <summary>
/// Runs an operation with a signing key under the key's own lock, so the key is never kept by the caller.
/// </summary>
/// <param name="use">The operation.</param>
/// <returns>What the operation returned.</returns>
public delegate string SigningKeyAccess(Func<ECDsa, string> use);

/// <summary>
/// The line types of a signed log (design §8.4.6, §13.4, §18.1).
/// </summary>
public static class LogLineTypes
{
    /// <summary>The first line of a file that continues the chain of the previous file.</summary>
    public const string ChainStart = "chain.start";

    /// <summary>The first line of a file whose chain could not be continued; it says why.</summary>
    public const string ChainReset = "chain.reset";

    /// <summary>A new server license or key; the signature that follows it is still made with the old key.</summary>
    public const string KeyChanged = "key.changed";

    /// <summary>A signature over the lines since the previous one.</summary>
    public const string Auth = "auth";
}

/// <summary>
/// An event line to write: its type and a function that writes its other members.
/// </summary>
/// <param name="Type">The event type.</param>
/// <param name="WriteFields">Writes the members after <c>type</c> and <c>ts</c>.</param>
public readonly record struct LogLine(string Type, Action<Utf8JsonWriter> WriteFields);

/// <summary>
/// Event lines made ready for the start of a new file (design §13.4, §18.1, V1d16): their bytes, their number, and the
/// SHA-256 over them so far, built without the log's lock.
/// </summary>
public sealed class CarriedLines : IDisposable
{
    private IncrementalHash? _digest;

    internal CarriedLines(byte[] bytes, int count, IncrementalHash digest)
    {
        Bytes = bytes;
        Count = count;
        _digest = digest;
    }

    /// <summary>Gets the number of lines.</summary>
    public int Count { get; }

    internal byte[] Bytes { get; }

    /// <summary>Releases the hash unless a file took it.</summary>
    public void Dispose() => _digest?.Dispose();

    // The hash goes to the file that writes the lines; it is released with that file's.
    internal IncrementalHash TakeDigest()
    {
        IncrementalHash digest = _digest ?? throw new InvalidOperationException("The lines were written already.");
        _digest = null;
        return digest;
    }
}

/// <summary>
/// An append-only log in JSON Lines, closed every so often by a signed line (design §8.4.6, §18.1). Files rotate each UTC day
/// and at each start; each file starts with a line that carries what verifies its signatures (<see cref="LogKeyMaterial"/>:
/// the server license and the server's public key, or the certificates of <c>elm-service</c>'s signing key) and, when it
/// can, a link to the last signature of the previous file.
/// </summary>
/// <remarks>
/// Every line is one compact JSON object followed by a line feed. A signature's <c>digest</c> is the SHA-256 of the bytes of
/// the lines it covers, line feeds included; its <c>prev</c> is the SHA-256 of the previous signature line, line feed
/// included. Each signature is flushed to the disk before the call that wrote it returns. A write that fails closes the
/// file; the next call starts a new file with <c>chain.reset</c> that names the failure, so a half-written line is not
/// taken for tampering. A new file's name never sorts before the newest file of the log, even when the clock went back.
/// A new file is written under its name with <c>.partial</c> added until its first line is signed and the lines it
/// carries forward are signed and on the disk; then it takes its name, so a stop in the middle leaves the previous file
/// the newest (design §18.1, V1d16).
/// A file whose name has the form of a log file's name but no real date and time (month 13) is not a file of the log: it is
/// not read, and it does not decide the next name. The class is safe to call from several threads.
/// </remarks>
public sealed partial class SignedLog : IDisposable
{
    private const int TailSearchBytes = 1024 * 1024;

    // The first signature line of a file is its second line: the first line is signed alone, before any line carried
    // forward (design §18.1, V1d16). So it is found in two lines of at most 1 MiB, each with its line feed.
    private const int HeadSearchBytes = (2 * LogVerifier.MaxLineLength) + 2;

    // The suffix of a new file until its first lines are on the disk (design §18.1, V1d16).
    private const string Unfinished = ".partial";
    private const string FileTimeFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    private readonly string _directory;
    private readonly string _baseName;
    private readonly string _stream;
    private readonly TimeProvider _time;
    private readonly int _sealEvery;
    private readonly Lock _lock = new();
    private readonly Func<IReadOnlyList<LogLine>>? _carry;
    private readonly bool _rotateOnWrite;
    private readonly Action<string>? _notALogFile;
    private SigningKeyAccess _key;
    private LogKeyMaterial _material;
    private FileStream? _file;
    private string _fileName = string.Empty;

    // The path of the file being started under its unfinished name, until it takes its name.
    private string? _partial;
    private DateOnly _fileDay;
    private DateOnly _reportedDay;
    private IncrementalHash _digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int _pending;
    private long _n;
    private string _prev = string.Empty;

    // The file being written and the hash of its first signature line, empty until that line is written (design §18.1).
    private string _headFile = string.Empty;
    private string _headAuth = string.Empty;
    private string? _failure;
    private bool _disposed;

    private SignedLog(
        string directory,
        string baseName,
        string stream,
        SigningKeyAccess key,
        LogKeyMaterial material,
        TimeProvider time,
        int sealEvery,
        Func<IReadOnlyList<LogLine>>? carry,
        bool rotateOnWrite,
        Action<string>? notALogFile
    )
    {
        _directory = directory;
        _baseName = baseName;
        _stream = stream;
        _key = key;
        _material = material;
        _time = time;
        _sealEvery = sealEvery;
        _carry = carry;
        _rotateOnWrite = rotateOnWrite;
        _notALogFile = notALogFile;
    }

    /// <summary>Gets the name of the file being written.</summary>
    public string FileName
    {
        get
        {
            lock (_lock)
            {
                return _fileName;
            }
        }
    }

    /// <summary>Gets why the chain could not be continued from the previous file at the start, or <see langword="null"/>.</summary>
    public string? ResetReason { get; private set; }

    /// <summary>
    /// Opens a log: continues the chain from the newest file of <paramref name="baseName"/> in the folder, or starts a new chain
    /// with a <c>chain.reset</c> line when that file cannot be read or holds no signature.
    /// </summary>
    /// <param name="directory">The folder of the files.</param>
    /// <param name="baseName">The file name prefix, such as <c>usage</c>; files are named <c>{baseName}-{UTC time}.jsonl</c>.</param>
    /// <param name="stream">The stream name in the signatures, such as <c>usage</c> or <c>borrows</c>.</param>
    /// <param name="key">Signs with the server's key.</param>
    /// <param name="licenseFile">The server license file, written to the first line of each file.</param>
    /// <param name="keyJwk">The server's public key as a JWK, written beside it.</param>
    /// <param name="time">The time provider.</param>
    /// <param name="sealEvery">The number of lines after which a signature is written; 1 signs every line.</param>
    /// <param name="carry">Called for each new file, under the log's lock, for the lines it carries forward after its signed
    /// first line; they are signed together. A new UTC day takes them from <see cref="StartNewDay"/> instead.</param>
    /// <param name="resetReason">When not <see langword="null"/>, the new file starts a new chain with this reason instead of
    /// continuing the newest file (for a record the operator set aside).</param>
    /// <param name="notALogFile">Called at the start and at the first new file of each UTC day, under the log's lock, with the
    /// name of each file in the folder whose name has the form of the log's file names but no real date and time; such a
    /// file is not a file of the log.</param>
    /// <param name="rotateOnWrite">Whether a write or a signature on a new UTC day starts the new file; when not, only
    /// <see cref="StartNewDay"/> does, and the lines until then go to the file of the day before.</param>
    /// <param name="unfinishedRemoved">Called at the start with the name of each file of the log that was left unfinished
    /// (<c>.partial</c>), which the start removes.</param>
    /// <returns>The open log.</returns>
    /// <exception cref="IOException">The folder or the new file cannot be written, or the newest file has the last name a
    /// file can have (<c>99991231T235959999Z</c>).</exception>
    public static SignedLog Open(
        string directory,
        string baseName,
        string stream,
        SigningKeyAccess key,
        byte[] licenseFile,
        byte[] keyJwk,
        TimeProvider time,
        int sealEvery = 100,
        Func<IReadOnlyList<LogLine>>? carry = null,
        string? resetReason = null,
        Action<string>? notALogFile = null,
        bool rotateOnWrite = true,
        Action<string>? unfinishedRemoved = null
    ) =>
        Open(
            directory,
            baseName,
            stream,
            key,
            LogKeyMaterial.ServerLicense(licenseFile, keyJwk),
            time,
            sealEvery,
            carry,
            resetReason,
            notALogFile,
            rotateOnWrite,
            unfinishedRemoved
        );

    /// <summary>
    /// Opens a log signed with a key that <paramref name="material"/> lets a reader verify, as
    /// <see cref="Open(string, string, string, SigningKeyAccess, byte[], byte[], TimeProvider, int, Func{IReadOnlyList{LogLine}}?, string?, Action{string}?, bool, Action{string}?)"/>
    /// does.
    /// </summary>
    /// <param name="directory">The folder of the files.</param>
    /// <param name="baseName">The file name prefix; files are named <c>{baseName}-{UTC time}.jsonl</c>.</param>
    /// <param name="stream">The stream name in the signatures.</param>
    /// <param name="key">Signs with the key.</param>
    /// <param name="material">What verifies the key, written to the first line of each file.</param>
    /// <param name="time">The time provider.</param>
    /// <param name="sealEvery">The number of lines after which a signature is written; 1 signs every line.</param>
    /// <param name="carry">Called for each new file, under the log's lock, for the lines it carries forward.</param>
    /// <param name="resetReason">When not <see langword="null"/>, the new file starts a new chain with this reason.</param>
    /// <param name="notALogFile">Called at the start and at the first new file of each UTC day, under the log's lock, with the
    /// name of each file in the folder whose name has the form of the log's file names but no real date and time.</param>
    /// <param name="rotateOnWrite">Whether a write or a signature on a new UTC day starts the new file.</param>
    /// <param name="unfinishedRemoved">Called at the start with the name of each unfinished file it removes.</param>
    /// <returns>The open log.</returns>
    /// <exception cref="IOException">The folder or the new file cannot be written, or the newest file has the last name a
    /// file can have.</exception>
    public static SignedLog Open(
        string directory,
        string baseName,
        string stream,
        SigningKeyAccess key,
        LogKeyMaterial material,
        TimeProvider time,
        int sealEvery = 100,
        Func<IReadOnlyList<LogLine>>? carry = null,
        string? resetReason = null,
        Action<string>? notALogFile = null,
        bool rotateOnWrite = true,
        Action<string>? unfinishedRemoved = null
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(baseName);
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(sealEvery, 1);
        Directory.CreateDirectory(directory);
        RemoveUnfinished(directory, baseName, unfinishedRemoved);
        var log = new SignedLog(directory, baseName, stream, key, material, time, sealEvery, carry, rotateOnWrite, notALogFile);
        lock (log._lock)
        {
            try
            {
                log.StartFile(previous: null, continueChain: true, resetReason);
            }
            catch
            {
                // The log is not returned, so nothing else would release its file and its hash.
                log._file?.Dispose();
                log._digest.Dispose();
                throw;
            }
        }

        return log;
    }

    /// <summary>
    /// Returns the files of a log in the folder, oldest first, and the names of the files that are not files of the log
    /// although their names have the form of a log file's name: their digits are no real date and time.
    /// </summary>
    /// <param name="directory">The folder.</param>
    /// <param name="baseName">The file name prefix.</param>
    /// <param name="notLogFiles">The file names that have the form but no real date and time, in name order.</param>
    /// <returns>The full paths.</returns>
    public static IReadOnlyList<string> FindFiles(string directory, string baseName, out IReadOnlyList<string> notLogFiles)
    {
        (List<(string Path, DateTimeOffset Time)> files, List<string> others) = Scan(directory, baseName);
        notLogFiles = others;
        return [.. files.Select(f => f.Path)];
    }

    /// <summary>
    /// Returns the files of a log in the folder, oldest first. A file whose name has the form of a log file's name but no
    /// real date and time is not one of them.
    /// </summary>
    /// <param name="directory">The folder.</param>
    /// <param name="baseName">The file name prefix.</param>
    /// <returns>The full paths.</returns>
    public static IReadOnlyList<string> FindFiles(string directory, string baseName) => FindFiles(directory, baseName, out _);

    /// <summary>
    /// Appends an event line <c>{"type": ..., "ts": ..., ...}</c> and signs when <c>sealEvery</c> lines are pending. A new UTC
    /// day starts a new file first.
    /// </summary>
    /// <param name="type">The event type.</param>
    /// <param name="writeFields">Writes the other members of the line.</param>
    /// <exception cref="IOException">The line or its signature cannot be written; the next call starts a new file.</exception>
    public void Append(string type, Action<Utf8JsonWriter> writeFields)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(writeFields);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Guarded(() =>
            {
                RotateIfNewDay();
                WriteLine(type, writeFields);
                if (_pending >= _sealEvery)
                {
                    WriteAuth();
                }
            });
        }
    }

    /// <summary>
    /// Writes a signature over the pending lines, if any, and flushes the file to the disk.
    /// </summary>
    /// <exception cref="IOException">The signature cannot be written; the next call starts a new file.</exception>
    public void Seal()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            Guarded(() =>
            {
                RotateIfNewDay();
                if (_pending > 0)
                {
                    WriteAuth();
                }
            });
        }
    }

    /// <summary>
    /// Records a new server license and key (design §18.1): a <c>key.changed</c> line, signed with the old key, after which
    /// the new key signs. When the line cannot be written, <paramref name="takeOnFailure"/> decides whether the new license
    /// and key are taken anyway (the next file then starts with <c>chain.reset</c> that carries them) or the old ones stay.
    /// </summary>
    /// <param name="licenseFile">The new server license file.</param>
    /// <param name="keyJwk">The new public key as a JWK.</param>
    /// <param name="key">Signs with the new key.</param>
    /// <param name="takeOnFailure">Whether a change that cannot be written is still taken: true for a log whose writer uses the
    /// new license regardless (the usage log), false for one whose writer keeps the old license then (the borrow record).</param>
    /// <exception cref="IOException">The change cannot be written.</exception>
    public void ChangeKey(byte[] licenseFile, byte[] keyJwk, SigningKeyAccess key, bool takeOnFailure)
    {
        ArgumentNullException.ThrowIfNull(key);
        LogKeyMaterial material = LogKeyMaterial.ServerLicense(licenseFile, keyJwk);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                Guarded(() =>
                {
                    WriteLine(LogLineTypes.KeyChanged, material.Write);
                    WriteAuth();
                });
            }
            catch (IOException) when (!takeOnFailure)
            {
                throw;
            }
            catch (IOException)
            {
                Take(material, key);
                throw;
            }

            Take(material, key);
        }
    }

    /// <summary>
    /// Signs the pending lines, flushes, and closes the file.
    /// </summary>
    /// <exception cref="IOException">The last signature cannot be written; the file is closed anyway.</exception>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (_file is not null && _pending > 0)
                {
                    WriteAuth();
                }
            }
            finally
            {
                _file?.Dispose();
                _digest.Dispose();
            }
        }
    }

    /// <summary>Returns whether the UTC day is no longer that of the file being written.</summary>
    /// <returns><see langword="true"/> on a new UTC day.</returns>
    public bool IsNewDay()
    {
        lock (_lock)
        {
            return !_disposed && IsNewDayLocked();
        }
    }

    /// <summary>
    /// Builds event lines for the start of a new file, with the time now, and their SHA-256, without any lock (design §13.4,
    /// V1d16).
    /// </summary>
    /// <param name="lines">The lines.</param>
    /// <param name="time">The time provider.</param>
    /// <returns>The lines, for <see cref="StartNewDay"/>.</returns>
    public static CarriedLines Prepare(IEnumerable<LogLine> lines, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(time);
        long now = time.GetUtcNow().ToUnixTimeSeconds();
        using var buffer = new MemoryStream();
        var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int count = 0;
        foreach (LogLine line in lines)
        {
            byte[] bytes = Line(line.Type, now, line.WriteFields);
            buffer.Write(bytes);
            digest.AppendData(bytes);
            count++;
        }

        return new CarriedLines(buffer.ToArray(), count, digest);
    }

    /// <summary>
    /// Starts the file of a new UTC day (design §13.4, §18.1, V1d16): signs and closes the file being written, then writes the
    /// new file under its unfinished name with its first line signed alone, the carried lines and then
    /// <paramref name="since"/> under one more signature, flushes it, and gives it its name. Nothing happens when the file
    /// being written is already of this day.
    /// </summary>
    /// <param name="carried">The lines built by <see cref="Prepare"/>.</param>
    /// <param name="since">The lines written to the file being closed since <paramref name="carried"/> was built.</param>
    /// <exception cref="IOException">The new file cannot be written; the next write starts a new file.</exception>
    public void StartNewDay(CarriedLines carried, IReadOnlyList<LogLine> since)
    {
        ArgumentNullException.ThrowIfNull(carried);
        ArgumentNullException.ThrowIfNull(since);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Guarded(() =>
            {
                // After a failed write, the file Guarded started is of this day and carries the lines already.
                if (!IsNewDayLocked())
                {
                    return;
                }

                if (_pending > 0)
                {
                    WriteAuth();
                }

                _file!.Flush(flushToDisk: true);
                _file.Dispose();
                StartFile(Path.Combine(_directory, _fileName), continueChain: false, prepared: carried, since: since);
            });
        }
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => Base64UrlCodec.Encode(SHA256.HashData(bytes));

    // Removes the files of a log left unfinished by a stop while they were started: their lines are all in the previous
    // file, which stayed the newest (design §18.1, V1d16).
    private static void RemoveUnfinished(string directory, string baseName, Action<string>? removed)
    {
        foreach (string path in Directory.EnumerateFiles(directory, baseName + "-*" + Unfinished).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            if (!name.StartsWith(baseName + "-", StringComparison.Ordinal) || !name.EndsWith(".jsonl" + Unfinished, StringComparison.Ordinal))
            {
                continue;
            }

            File.Delete(path);
            removed?.Invoke(name);
        }
    }

    private static byte[] Line(string type, long ts, Action<Utf8JsonWriter> writeFields)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteNumber("ts", ts);
            writeFields(writer);
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    // The last signature of a file, read from its end: (the hash of the line, its n), or a reason it has none.
    private static (string? Hash, long N, string? Problem) ReadLastAuth(string path)
    {
        byte[] tail;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long start = Math.Max(0, stream.Length - TailSearchBytes);
            stream.Position = start;
            tail = new byte[stream.Length - start];
            stream.ReadExactly(tail);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The exception's message is left out: it can hold the file's full path, and the reason is read outside this machine.
            return (null, 0, $"the previous file {Path.GetFileName(path)} cannot be read");
        }

        int end = tail.Length;
        while (end > 0)
        {
            int lineEnd = Array.LastIndexOf(tail, (byte)'\n', end - 1);
            if (lineEnd < 0)
            {
                break;
            }

            int lineStart = lineEnd == 0 ? 0 : Array.LastIndexOf(tail, (byte)'\n', lineEnd - 1) + 1;
            ReadOnlySpan<byte> line = tail.AsSpan(lineStart, lineEnd - lineStart + 1);
            if (TryReadAuth(line, out LogPayload? payload))
            {
                return (Hash(line), payload!.N, null);
            }

            end = lineStart;
        }

        return (null, 0, $"no signature was found at the end of the previous file {Path.GetFileName(path)}");
    }

    // The hash of a file's first signature line, read from its head; empty when the head holds none, and null when the file
    // cannot be read (design §18.1, V1d23).
    private static string? ReadFirstAuth(string path)
    {
        byte[] head;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            head = new byte[(int)Math.Min(stream.Length, HeadSearchBytes)];
            stream.ReadExactly(head);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        int start = 0;
        int end;
        while ((end = Array.IndexOf(head, (byte)'\n', start)) >= 0)
        {
            ReadOnlySpan<byte> line = head.AsSpan(start, end - start + 1);
            if (TryReadAuth(line, out _))
            {
                return Hash(line);
            }

            start = end + 1;
        }

        return string.Empty;
    }

    // Reads the payload of an auth line without verifying it: the writer only continues the numbering. A line that is not
    // JSON, not an object, or whose text cannot be read is not a signature. JsonElement throws InvalidOperationException
    // from TryGetProperty on a value that is not an object ("[]"), from GetString on a lone surrogate escape ("\ud800") or
    // on bytes that are not UTF-8, and from TryGetProperty when it passes a member name of that kind (measured on .NET
    // 10.0.15, Windows).
    private static bool TryReadAuth(ReadOnlySpan<byte> line, out LogPayload? payload)
    {
        payload = null;
        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            if (
                !document.RootElement.TryGetProperty("type", out JsonElement type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != LogLineTypes.Auth
                || !document.RootElement.TryGetProperty("sig", out JsonElement sig)
                || sig.ValueKind != JsonValueKind.String
                || !CompactJws.TryParse(sig.GetString()!, TokenTypes.Log, out CompactJws? jws, out _)
            )
            {
                return false;
            }

            payload = JsonSerializer.Deserialize(jws!.Payload, LicensingJsonContext.Default.LogPayload);
            return payload is not null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    // The form of a log file's name after the prefix. \d also takes digits outside ASCII, so that such a name is reported
    // rather than passed over.
    [GeneratedRegex(@"^\d{8}T\d{9}Z\.jsonl$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    // The files of a log, oldest first, each with the time in its name, and the names that have the form but no real date
    // and time. DateTimeOffset.TryParseExact refuses month 13, February 31, hour 25, year 0, and digits outside ASCII
    // (measured on .NET 10.0.15, Windows), so the names it takes sort by time in ordinal order.
    private static (List<(string Path, DateTimeOffset Time)> Files, List<string> NotLogFiles) Scan(string directory, string baseName)
    {
        var files = new List<(string Path, DateTimeOffset Time)>();
        var notLogFiles = new List<string>();
        if (!Directory.Exists(directory))
        {
            return (files, notLogFiles);
        }

        Regex pattern = FileNamePattern();
        foreach (string path in Directory.EnumerateFiles(directory, baseName + "-*.jsonl").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            if (!name.StartsWith(baseName + "-", StringComparison.Ordinal) || !pattern.IsMatch(name[(baseName.Length + 1)..]))
            {
                continue;
            }

            if (
                DateTimeOffset.TryParseExact(
                    name.AsSpan((baseName.Length + 1)..^".jsonl".Length),
                    FileTimeFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset time
                )
            )
            {
                files.Add((path, time));
            }
            else
            {
                notLogFiles.Add(name);
            }
        }

        return (files, notLogFiles);
    }

    // The time of the name after a name. The last name a file can have is 99991231T235959999Z: one millisecond more is past
    // DateTimeOffset.MaxValue, and AddMilliseconds throws ArgumentOutOfRangeException there.
    private static DateTimeOffset NextFileTime(DateTimeOffset time, string baseName)
    {
        if (time.UtcTicks > DateTimeOffset.MaxValue.UtcTicks - TimeSpan.TicksPerMillisecond)
        {
            string name = $"{baseName}-{time.UtcDateTime.ToString(FileTimeFormat, CultureInfo.InvariantCulture)}.jsonl";
            throw new IOException($"No file name comes after {name}, the newest file of the log.");
        }

        return time.AddMilliseconds(1);
    }

    private void Take(LogKeyMaterial material, SigningKeyAccess key)
    {
        _key = key;
        _material = material;
    }

    // Runs a write. After a failure the file is closed and the next write starts a new file with chain.reset; the lines
    // written so far stay as they are, and the verifier reports them as unsigned (a warning), not as changed.
    private void Guarded(Action write)
    {
        // Cleared first, so that a failure of the file started for it is told apart from the failure it names.
        string? failure = _failure;
        _failure = null;
        try
        {
            if (failure is not null)
            {
                StartFile(previous: null, continueChain: false, forcedReset: failure);
            }

            write();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The exception's message is left out of the next file's chain.reset: it can hold a full path. A file that failed
            // while it was started is removed (design §18.1, V1d16), so a reset that started it keeps naming the earlier
            // failure; otherwise the reason names the file, which may be the one removed.
            _failure = _partial is not null && failure is not null ? failure : $"a write to {_fileName} failed";
            try
            {
                _file?.Dispose();
            }
            catch (IOException)
            {
                // The file is given up; its unwritten bytes are lost either way.
            }

            _file = null;
            RemovePartial();
            throw new IOException($"The log {_fileName} cannot be written: {e.Message}", e);
        }
    }

    private void RotateIfNewDay()
    {
        if (!_rotateOnWrite || !IsNewDayLocked())
        {
            return;
        }

        if (_pending > 0)
        {
            WriteAuth();
        }

        _file!.Flush(flushToDisk: true);
        _file.Dispose();
        StartFile(Path.Combine(_directory, _fileName), continueChain: false);
    }

    // Opens a new file and writes its first line: chain.start linked to the previous file's last signature, or chain.reset.
    private void StartFile(
        string? previous,
        bool continueChain,
        string? forcedReset = null,
        CarriedLines? prepared = null,
        IReadOnlyList<LogLine>? since = null
    )
    {
        DateTimeOffset now = _time.GetUtcNow();
        (List<(string Path, DateTimeOffset Time)> existing, List<string> notLogFiles) = Scan(_directory, _baseName);

        // Reported at a start and once in a UTC day: a write that fails comes here again at each later write, and would
        // repeat the report with every request.
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (_notALogFile is not null && (continueChain || today != _reportedDay))
        {
            foreach (string name in notLogFiles)
            {
                _notALogFile(name);
            }
        }

        _reportedDay = today;

        // At a start the chain continues from the newest file of the folder.
        if (continueChain && existing.Count > 0)
        {
            previous = existing[^1].Path;
        }

        string? reset = forcedReset;
        string? previousName = previous is null ? null : Path.GetFileName(previous);
        if (reset is null && continueChain && previous is not null)
        {
            (string? hash, long n, string? problem) = ReadLastAuth(previous);
            if (hash is null)
            {
                reset = problem;
            }
            else
            {
                _prev = hash;
                _n = n;
            }
        }

        if (reset is not null)
        {
            _prev = string.Empty;
            _n = 0;
            if (continueChain)
            {
                ResetReason = reset;
            }
        }

        // A clock set back must not name a file that sorts before the newest one: the newest file is the one read at start.
        DateTimeOffset named = now;
        if (existing.Count > 0 && existing[^1].Time >= named)
        {
            named = NextFileTime(existing[^1].Time, _baseName);
        }

        // design §18.1 (V1d23): a reset names the file before it, the newest of the folder, with the hash of that file's
        // first signature line, so that the verifier finds the file missing or replaced. The hash is kept in memory for the
        // file this log wrote once that line reached the disk; otherwise it is read from the file's head (a line written whose
        // flush failed may be there), and it is left out when that file cannot be read.
        string? before = reset is not null && existing.Count > 0 ? existing[^1].Path : null;
        string? beforeFirst =
            before is null ? null
            : Path.GetFileName(before) == _headFile && _headAuth.Length > 0 ? _headAuth
            : ReadFirstAuth(before);

        _file = CreateFile(named);
        _headFile = _fileName;
        _headAuth = string.Empty;
        _fileDay = DateOnly.FromDateTime(now.UtcDateTime);
        _digest.Dispose();
        _digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _pending = 0;
        if (reset is null)
        {
            string prevHash = _prev;
            WriteLine(
                LogLineTypes.ChainStart,
                w =>
                {
                    w.WriteString("stream", _stream);
                    if (previousName is not null)
                    {
                        w.WriteStartObject("prev");
                        w.WriteString("file", previousName);
                        w.WriteString("auth", prevHash);
                        w.WriteEndObject();
                    }

                    _material.Write(w);
                }
            );
        }
        else
        {
            WriteLine(
                LogLineTypes.ChainReset,
                w =>
                {
                    w.WriteString("stream", _stream);
                    if (before is not null)
                    {
                        w.WriteStartObject("prev");
                        w.WriteString("file", Path.GetFileName(before));
                        if (beforeFirst is not null)
                        {
                            w.WriteString("first", beforeFirst);
                        }

                        w.WriteEndObject();
                    }

                    w.WriteString("reason", reset);
                    _material.Write(w);
                }
            );
        }

        // design §18.1 (V1d16): the first line is signed alone, so the first signature is always the second line, and the
        // lines carried forward come after it under one signature.
        WriteAuth();
        CarriedLines? built = prepared is null && _carry is not null ? Prepare(_carry(), _time) : null;
        try
        {
            if ((prepared ?? built) is { Count: > 0 } carried)
            {
                _file!.Write(carried.Bytes);
                _digest.Dispose();
                _digest = carried.TakeDigest();
                _pending = carried.Count;
            }
        }
        finally
        {
            built?.Dispose();
        }

        foreach (LogLine line in since ?? [])
        {
            WriteLine(line.Type, line.WriteFields);
        }

        if (_pending > 0)
        {
            WriteAuth();
        }

        // The signatures flushed the file to the disk; only now does it take its name.
        File.Move(_partial!, Path.Combine(_directory, _fileName), overwrite: false);
        _partial = null;
    }

    // Removes the unfinished file of a start that failed; the start of the next file names the failure.
    private void RemovePartial()
    {
        if (_partial is null)
        {
            return;
        }

        try
        {
            File.Delete(_partial);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The next start of the log removes it.
        }

        _partial = null;
    }

    private FileStream CreateFile(DateTimeOffset first)
    {
        DateTimeOffset time = first;
        for (int attempt = 0; ; attempt++)
        {
            string stamp = time.UtcDateTime.ToString(FileTimeFormat, CultureInfo.InvariantCulture);
            _fileName = $"{_baseName}-{stamp}.jsonl";
            string path = Path.Combine(_directory, _fileName);
            if (!File.Exists(path))
            {
                try
                {
                    // FileShare.Delete lets the file be renamed while this handle writes it: without it, File.Move of the open
                    // file failed on Windows 11 (.NET 10.0, BorrowJournalTests); Linux and macOS rename an open file anyway.
                    var file = new FileStream(path + Unfinished, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
                    _partial = path + Unfinished;
                    return file;
                }
                catch (IOException) when (attempt < 1000 && File.Exists(path + Unfinished))
                {
                    // Two files in one millisecond: take the next one.
                }
            }
            else if (attempt >= 1000)
            {
                throw new IOException($"No new file name was found after {_fileName}.");
            }

            time = NextFileTime(time, _baseName);
        }
    }

    private bool IsNewDayLocked() => DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime) != _fileDay;

    private void WriteLine(string type, Action<Utf8JsonWriter> writeFields)
    {
        byte[] line = Line(type, _time.GetUtcNow().ToUnixTimeSeconds(), writeFields);
        _file!.Write(line);
        _digest.AppendData(line);
        _pending++;
    }

    // design §18.1 (V1d23): the digest is read without a reset, and the digest and the count of unsigned lines are reset only
    // after the signature line is on the disk. A signature that fails leaves them as they were, and the next signature line
    // covers the same lines.
    private void WriteAuth()
    {
        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        var payload = new LogPayload
        {
            Ver = 1,
            Iat = now,
            Jti = JwsWriter.NewTokenId(),
            Stream = _stream,
            N = _n + 1,
            Prev = _prev,
            Digest = Base64UrlCodec.Encode(_digest.GetCurrentHash()),
            Lines = _pending,
        };
        string signature = _key(ecdsa => JwsWriter.Sign(ecdsa, TokenTypes.Log, payload, LicensingJsonContext.Default.LogPayload));
        byte[] line = Encoding.UTF8.GetBytes(
            $"{{\"type\":\"{LogLineTypes.Auth}\",\"ts\":{now.ToString(CultureInfo.InvariantCulture)},\"sig\":\"{signature}\"}}\n"
        );
        _file!.Write(line);

        // design §13.4: a borrow or a return takes effect only after its signature is on the disk.
        _file.Flush(flushToDisk: true);
        _prev = Hash(line);
        if (_headAuth.Length == 0)
        {
            _headAuth = _prev;
        }

        _n++;
        _ = _digest.GetHashAndReset();
        _pending = 0;
    }
}
