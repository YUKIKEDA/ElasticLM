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
/// An append-only log in JSON Lines, closed every so often by a signed line (design §8.4.6, §18.1). Files rotate each UTC day
/// and at each start; each file starts with a line that carries what verifies its signatures (the server license and the
/// server's public key) and, when it can, a link to the last signature of the previous file.
/// </summary>
/// <remarks>
/// Every line is one compact JSON object followed by a line feed. A signature's <c>digest</c> is the SHA-256 of the bytes of
/// the lines it covers, line feeds included; its <c>prev</c> is the SHA-256 of the previous signature line, line feed
/// included. Each signature is flushed to the disk before the call that wrote it returns. A write that fails closes the
/// file; the next call starts a new file with <c>chain.reset</c> that names the failure, so a half-written line is not
/// taken for tampering. A new file's name never sorts before the newest file of the log, even when the clock went back.
/// The class is safe to call from several threads.
/// </remarks>
public sealed partial class SignedLog : IDisposable
{
    private const int TailSearchBytes = 1024 * 1024;

    private readonly string _directory;
    private readonly string _baseName;
    private readonly string _stream;
    private readonly TimeProvider _time;
    private readonly int _sealEvery;
    private readonly Lock _lock = new();
    private readonly Action<SignedLog>? _fileStarted;
    private SigningKeyAccess _key;
    private byte[] _licenseFile;
    private byte[] _keyJwk;
    private FileStream? _file;
    private string _fileName = string.Empty;
    private DateOnly _fileDay;
    private IncrementalHash _digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int _pending;
    private long _n;
    private string _prev = string.Empty;
    private string? _failure;
    private bool _disposed;

    private SignedLog(
        string directory,
        string baseName,
        string stream,
        SigningKeyAccess key,
        byte[] licenseFile,
        byte[] keyJwk,
        TimeProvider time,
        int sealEvery,
        Action<SignedLog>? fileStarted
    )
    {
        _directory = directory;
        _baseName = baseName;
        _stream = stream;
        _key = key;
        _licenseFile = licenseFile;
        _keyJwk = keyJwk;
        _time = time;
        _sealEvery = sealEvery;
        _fileStarted = fileStarted;
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
    /// <param name="fileStarted">Called after the first line of each new file, under the log's lock, so a caller can carry
    /// records forward into it.</param>
    /// <param name="resetReason">When not <see langword="null"/>, the new file starts a new chain with this reason instead of
    /// continuing the newest file (for a record the operator set aside).</param>
    /// <returns>The open log.</returns>
    /// <exception cref="IOException">The folder or the new file cannot be written.</exception>
    public static SignedLog Open(
        string directory,
        string baseName,
        string stream,
        SigningKeyAccess key,
        byte[] licenseFile,
        byte[] keyJwk,
        TimeProvider time,
        int sealEvery = 100,
        Action<SignedLog>? fileStarted = null,
        string? resetReason = null
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(baseName);
        ArgumentException.ThrowIfNullOrEmpty(stream);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(licenseFile);
        ArgumentNullException.ThrowIfNull(keyJwk);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(sealEvery, 1);
        Directory.CreateDirectory(directory);
        var log = new SignedLog(directory, baseName, stream, key, licenseFile, keyJwk, time, sealEvery, fileStarted);
        lock (log._lock)
        {
            IReadOnlyList<string> files = FindFiles(directory, baseName);
            string? previous = files.Count > 0 ? files[^1] : null;
            log.StartFile(previous, continueChain: true, resetReason);
        }

        return log;
    }

    /// <summary>
    /// Returns the files of a log in the folder, oldest first.
    /// </summary>
    /// <param name="directory">The folder.</param>
    /// <param name="baseName">The file name prefix.</param>
    /// <returns>The full paths.</returns>
    public static IReadOnlyList<string> FindFiles(string directory, string baseName)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        Regex pattern = FileNamePattern();
        return
        [
            .. Directory
                .EnumerateFiles(directory, baseName + "-*.jsonl")
                .Where(p => Path.GetFileName(p) is string name && name.StartsWith(baseName + "-", StringComparison.Ordinal))
                .Where(p => pattern.IsMatch(Path.GetFileName(p)[(baseName.Length + 1)..]))
                .Order(StringComparer.Ordinal),
        ];
    }

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
        ArgumentNullException.ThrowIfNull(licenseFile);
        ArgumentNullException.ThrowIfNull(keyJwk);
        ArgumentNullException.ThrowIfNull(key);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                Guarded(() =>
                {
                    WriteLine(LogLineTypes.KeyChanged, w => WriteKeyMaterial(w, licenseFile, keyJwk));
                    WriteAuth();
                });
            }
            catch (IOException) when (!takeOnFailure)
            {
                throw;
            }
            catch (IOException)
            {
                Take(licenseFile, keyJwk, key);
                throw;
            }

            Take(licenseFile, keyJwk, key);
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

    internal static string Hash(ReadOnlySpan<byte> bytes) => Base64UrlCodec.Encode(SHA256.HashData(bytes));

    private static void WriteKeyMaterial(Utf8JsonWriter writer, byte[] licenseFile, byte[] keyJwk)
    {
        writer.WritePropertyName("license");
        writer.WriteRawValue(licenseFile);
        writer.WritePropertyName("key");
        writer.WriteRawValue(keyJwk);
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
            return (null, 0, $"the previous file {Path.GetFileName(path)} cannot be read: {e.Message}");
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

    // Reads the payload of an auth line without verifying it: the writer only continues the numbering.
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
        catch (JsonException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^\d{8}T\d{9}Z\.jsonl$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    // The time in the name of a log file found by FindFiles.
    private static DateTimeOffset FileTime(string path, string baseName) =>
        DateTimeOffset.ParseExact(
            Path.GetFileName(path)[(baseName.Length + 1)..^".jsonl".Length],
            "yyyyMMdd'T'HHmmssfff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal
        );

    private void Take(byte[] licenseFile, byte[] keyJwk, SigningKeyAccess key)
    {
        _key = key;
        _licenseFile = licenseFile;
        _keyJwk = keyJwk;
    }

    // Runs a write. After a failure the file is closed and the next write starts a new file with chain.reset; the lines
    // written so far stay as they are, and the verifier reports them as unsigned (a warning), not as changed.
    private void Guarded(Action write)
    {
        try
        {
            // Cleared first: the new file's first lines (records carried forward) are written through here as well.
            string? failure = _failure;
            _failure = null;
            if (failure is not null)
            {
                StartFile(previous: null, continueChain: false, forcedReset: failure);
            }

            write();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _failure = $"a write to {_fileName} failed: {e.Message}";
            try
            {
                _file?.Dispose();
            }
            catch (IOException)
            {
                // The file is given up; its unwritten bytes are lost either way.
            }

            _file = null;
            throw new IOException($"The log {_fileName} cannot be written: {e.Message}", e);
        }
    }

    private void RotateIfNewDay()
    {
        if (DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime) == _fileDay)
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
    private void StartFile(string? previous, bool continueChain, string? forcedReset = null)
    {
        DateTimeOffset now = _time.GetUtcNow();
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
        IReadOnlyList<string> existing = FindFiles(_directory, _baseName);
        if (existing.Count > 0 && FileTime(existing[^1], _baseName) is DateTimeOffset newest && newest >= named)
        {
            named = newest.AddMilliseconds(1);
        }

        _file = CreateFile(named);
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

                    WriteKeyMaterial(w, _licenseFile, _keyJwk);
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
                    w.WriteString("reason", reset);
                    WriteKeyMaterial(w, _licenseFile, _keyJwk);
                }
            );
        }

        _fileStarted?.Invoke(this);
        WriteAuth();
    }

    private FileStream CreateFile(DateTimeOffset now)
    {
        for (int attempt = 0; ; attempt++)
        {
            string stamp = now.AddMilliseconds(attempt).UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
            _fileName = $"{_baseName}-{stamp}.jsonl";
            try
            {
                return new FileStream(Path.Combine(_directory, _fileName), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            catch (IOException) when (attempt < 1000 && File.Exists(Path.Combine(_directory, _fileName)))
            {
                // Two files in one millisecond: take the next one.
            }
        }
    }

    private void WriteLine(string type, Action<Utf8JsonWriter> writeFields)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteNumber("ts", _time.GetUtcNow().ToUnixTimeSeconds());
            writeFields(writer);
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        byte[] line = buffer.ToArray();
        _file!.Write(line);
        _digest.AppendData(line);
        _pending++;
    }

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
            Digest = Base64UrlCodec.Encode(_digest.GetHashAndReset()),
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
        _n++;
        _pending = 0;
    }
}
