using System.Security.Cryptography;
using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.Issuing;

/// <summary>
/// How serious a finding of <see cref="LogVerifier"/> is.
/// </summary>
public enum LogIssueSeverity
{
    /// <summary>Something that a crash can also cause: a chain reset, or unsigned lines after the last signature.</summary>
    Warning,

    /// <summary>The log was changed, cut, or signed by a key the trust store does not vouch for.</summary>
    Error,
}

/// <summary>
/// A finding of <see cref="LogVerifier"/>.
/// </summary>
/// <param name="File">The file name.</param>
/// <param name="Line">The 1-based line number, or 0 for the file as a whole.</param>
/// <param name="Severity">How serious it is.</param>
/// <param name="Message">What was found.</param>
public sealed record LogIssue(string File, int Line, LogIssueSeverity Severity, string Message);

/// <summary>
/// An event line of a verified log.
/// </summary>
/// <param name="File">The file name.</param>
/// <param name="Line">The 1-based line number.</param>
/// <param name="Type">The event type.</param>
/// <param name="Value">The whole line as JSON.</param>
/// <param name="IsSigned">Whether a valid signature covers the line; unsigned lines come after the last signature of a file.</param>
public sealed record LogEntry(string File, int Line, string Type, JsonElement Value, bool IsSigned);

/// <summary>
/// The key that signs the end of a verified file.
/// </summary>
/// <param name="File">The file name.</param>
/// <param name="LicenseId">The server license ID, or <see langword="null"/> for a log of <c>elm-service</c>, whose key is
/// certified for <c>log</c> instead (decision 0.95).</param>
/// <param name="KeyId">The thumbprint of the key: the server license's <c>cnf.jkt</c>, or the service's signing key.</param>
public sealed record LogFileKey(string File, string? LicenseId, string KeyId);

/// <summary>
/// The result of <see cref="LogVerifier.Verify"/>.
/// </summary>
/// <param name="Issues">The findings, in file and line order.</param>
/// <param name="EntryCount">The number of event lines (not the chain, key, or signature lines) handed out, signed or not.</param>
/// <param name="Files">The license and key at the end of each file whose first line verified, in order.</param>
public sealed record LogReport(IReadOnlyList<LogIssue> Issues, long EntryCount, IReadOnlyList<LogFileKey> Files)
{
    /// <summary>Gets a value indicating whether there is no error (warnings may remain).</summary>
    public bool Intact => !Issues.Any(i => i.Severity == LogIssueSeverity.Error);
}

/// <summary>
/// Verifies the files of a signed log (design §8.4.6, §18.1): each file's first line and what it carries (a server license
/// and key, or the certificates of <c>elm-service</c>'s signing key), every signature, the numbering and the links between
/// signatures and between files, key changes signed by the old key, and that each file is signed by the key that signed the
/// end of the file before it. A file signed by another key of the same server license (a server moved to a new machine), or
/// by another key certified for <c>log</c> (a replaced signing key of the service), is a warning; a key of another server
/// license, or a change between the two kinds, is an error. The files are read a line at a time, and the event lines are
/// handed to the caller, not kept.
/// </summary>
public static class LogVerifier
{
    /// <summary>The longest line read; a longer line is an error.</summary>
    public const int MaxLineLength = 1024 * 1024;

    private const string UnreadableText = "The line holds a string that is not valid Unicode.";

    /// <summary>
    /// Verifies log files given oldest first.
    /// </summary>
    /// <param name="files">The file paths, oldest first.</param>
    /// <param name="trust">The vendor's trust store.</param>
    /// <param name="productId">The product of the server license, needed for a server's log; <see langword="null"/> for a
    /// log of <c>elm-service</c>, which serves many products. A server's log without it is an error.</param>
    /// <param name="stream">The expected stream name, such as <c>usage</c>.</param>
    /// <param name="node">The node of <c>elm-service</c> whose log this is, checked against the <c>node</c> of each file's
    /// first line; <see langword="null"/> to check only that the files of a chain name one node.</param>
    /// <param name="onEntry">Receives each event line, in order: a line when a signature covers it, and the lines that no
    /// signature covers when their file ends. A file whose rest is not read after an error hands out only the lines its
    /// signatures covered. <see langword="null"/> to count the lines only.</param>
    /// <returns>The findings, the number of event lines, and the key at the end of each file.</returns>
    public static LogReport Verify(
        IReadOnlyList<string> files,
        TrustStore trust,
        string? productId,
        string stream,
        string? node = null,
        Action<LogEntry>? onEntry = null
    )
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentException.ThrowIfNullOrEmpty(stream);
        var issues = new List<LogIssue>();
        var state = new ChainState { Node = node, OnEntry = onEntry };
        for (int i = 0; i < files.Count; i++)
        {
            VerifyFile(files[i], i == 0 ? null : Path.GetFileName(files[i - 1]), trust, productId, stream, state, issues);
        }

        return new LogReport(issues, state.EntryCount, state.Files);
    }

    private static void VerifyFile(
        string path,
        string? previousName,
        TrustStore trust,
        string? productId,
        string stream,
        ChainState state,
        List<LogIssue> issues
    )
    {
        string name = Path.GetFileName(path);
        void Issue(int line, LogIssueSeverity severity, string message) => issues.Add(new LogIssue(name, line, severity, message));

        LineReader reader;
        try
        {
            reader = new LineReader(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Issue(0, LogIssueSeverity.Error, "The file cannot be read: " + e.Message);
            state.Broken = true;
            state.FirstAuth = null;
            return;
        }

        LogSigner? key = null;

        // design §18.1 (V1d23): the hash of the file's first signature line, which a chain.reset of the next file names.
        string? firstAuth = null;
        bool readToTheEnd = false;
        LogSigner? nextKey = null;

        // design §18.1, 0.95: a service's file is compared with the previous file by the key that signs it, known at its
        // first signature.
        LogFileKey? previousKey = previousName is not null && state.Files.Count > 0 ? state.Files[^1] : null;
        bool firstSignature = true;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int covered = 0;

        // design §18.1 (V1d23): the event lines after the last signature wait for the next one only when the caller takes
        // them; otherwise they are counted.
        var pendingEntries = new List<LogEntry>();
        long pendingCount = 0;
        int number = 0;
        try
        {
            while (true)
            {
                byte[]? bytes;
                try
                {
                    bytes = reader.ReadLine();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Issue(0, LogIssueSeverity.Error, "The file cannot be read: " + e.Message);
                    state.Broken = true;
                    return;
                }

                if (bytes is null)
                {
                    break;
                }

                int index = number;
                number++;
                JsonElement value;
                string type;
                try
                {
                    using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                    value = document.RootElement.Clone();
                    if (value.ValueKind != JsonValueKind.Object)
                    {
                        Issue(number, LogIssueSeverity.Error, "The line is not a JSON object.");
                        return;
                    }

                    type = value.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : string.Empty;
                }
                catch (JsonException)
                {
                    Issue(number, LogIssueSeverity.Error, "The line is not a JSON object.");
                    return;
                }
                catch (InvalidOperationException)
                {
                    // The type, or a member name passed on the way to it, is a lone surrogate escape or not UTF-8.
                    Issue(number, LogIssueSeverity.Error, UnreadableText);
                    return;
                }

                // The lines this verifier reads further (the chain line, a signature, a key change) hold no text it cannot
                // read; an event line's other members are its reader's to check.
                if ((index == 0 || type == LogLineTypes.Auth || type == LogLineTypes.KeyChanged) && !JsonText.IsReadable(value))
                {
                    Issue(number, LogIssueSeverity.Error, UnreadableText);
                    return;
                }

                if (index == 0)
                {
                    if (!StartChain(value, type, name, previousName, trust, productId, stream, state, Issue, out key))
                    {
                        return;
                    }
                }
                else if (type == LogLineTypes.ChainStart || type == LogLineTypes.ChainReset)
                {
                    Issue(number, LogIssueSeverity.Error, "A chain line appears after the first line.");
                    return;
                }

                if (type == LogLineTypes.Auth)
                {
                    if (!CheckAuth(value, bytes, digest, covered, stream, key!, state, out string? problem))
                    {
                        Issue(number, LogIssueSeverity.Error, problem!);
                        return;
                    }

                    if (firstSignature && key!.IsService && previousKey is { LicenseId: null } before && before.KeyId != key.KeyId)
                    {
                        Issue(
                            number,
                            LogIssueSeverity.Warning,
                            $"The file is signed by another key certified for log than {before.KeyId}: the signing key was replaced."
                        );
                    }

                    firstSignature = false;
                    firstAuth ??= SignedLog.Hash(bytes);
                    foreach (LogEntry entry in pendingEntries)
                    {
                        state.OnEntry!(entry with { IsSigned = true });
                    }

                    state.EntryCount += pendingCount;
                    pendingEntries.Clear();
                    pendingCount = 0;
                    covered = 0;
                    if (nextKey is not null)
                    {
                        key!.Dispose();
                        key = nextKey;
                        nextKey = null;
                    }

                    continue;
                }

                if (nextKey is not null)
                {
                    Issue(number, LogIssueSeverity.Error, "A key change is not followed by a signature of the old key.");
                    return;
                }

                digest.AppendData(bytes);
                covered++;
                if (type == LogLineTypes.KeyChanged)
                {
                    // design §18.1, 0.95: the service reads its key only at the start, so its files change no key; and a
                    // server's log does not turn into the service's.
                    if (key!.IsService)
                    {
                        Issue(number, LogIssueSeverity.Error, "A log of elm-service changes no key while it runs, and this file has a key change.");
                        return;
                    }

                    nextKey = ReadKey(value, trust, productId, out string? keyProblem);
                    if (nextKey is null)
                    {
                        Issue(number, LogIssueSeverity.Error, keyProblem!);
                        return;
                    }

                    if (nextKey.IsService)
                    {
                        Issue(number, LogIssueSeverity.Error, "A key change turns a server's log into a log of elm-service.");
                        return;
                    }
                }
                else if (index > 0)
                {
                    pendingCount++;
                    if (state.OnEntry is not null)
                    {
                        pendingEntries.Add(new LogEntry(name, number, type, value, IsSigned: false));
                    }
                }
            }

            readToTheEnd = true;
            if (reader.Partial)
            {
                Issue(number + 1, LogIssueSeverity.Warning, "The file ends in the middle of a line (crash or truncation).");
            }

            if (number == 0)
            {
                // An empty file, or one cut inside its first line: a crash while the file was started, or a file emptied by hand.
                Issue(1, LogIssueSeverity.Error, "The file does not start with chain.start or chain.reset.");
                state.Broken = true;
                return;
            }

            if (pendingCount > 0 || covered > 0)
            {
                Issue(number, LogIssueSeverity.Warning, $"{covered} line(s) after the last signature are not signed (crash or tampering).");
                foreach (LogEntry entry in pendingEntries)
                {
                    state.OnEntry!(entry);
                }

                state.EntryCount += pendingCount;
            }
        }
        finally
        {
            // Unknown when the file was not read as far as its first signature.
            state.FirstAuth = firstAuth ?? (readToTheEnd ? string.Empty : null);
            reader.Dispose();
            if (key is not null)
            {
                state.Files.Add(new LogFileKey(name, key.LicenseId, key.KeyId));
            }

            key?.Dispose();
            nextKey?.Dispose();
        }
    }

    // The first line of a file: chain.start (linked to the previous file when it is given) or chain.reset; either names what
    // verifies the signatures.
    private static bool StartChain(
        JsonElement value,
        string type,
        string name,
        string? previousName,
        TrustStore trust,
        string? productId,
        string stream,
        ChainState state,
        Action<int, LogIssueSeverity, string> issue,
        out LogSigner? key
    )
    {
        key = null;
        if (type != LogLineTypes.ChainStart && type != LogLineTypes.ChainReset)
        {
            issue(1, LogIssueSeverity.Error, "The file does not start with chain.start or chain.reset.");
            return false;
        }

        if (!value.TryGetProperty("stream", out JsonElement s) || s.ValueKind != JsonValueKind.String || s.GetString() != stream)
        {
            issue(1, LogIssueSeverity.Error, $"The file is not of the stream '{stream}'.");
            return false;
        }

        string? linkFile = null;
        string? linkAuth = null;
        if (value.TryGetProperty("prev", out JsonElement link) && link.ValueKind == JsonValueKind.Object)
        {
            linkFile = link.TryGetProperty("file", out JsonElement f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            linkAuth = link.TryGetProperty("auth", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        }

        if (type == LogLineTypes.ChainReset)
        {
            string reason = value.TryGetProperty("reason", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "no reason";
            issue(1, LogIssueSeverity.Warning, "The chain was reset: " + reason);

            // design §18.1 (V1d23): a reset names the file before it and the hash of that file's first signature line, so a
            // file removed before a reset is found missing, and another file renamed to its name is found replaced.
            string? linkFirst =
                link.ValueKind == JsonValueKind.Object
                && link.TryGetProperty("first", out JsonElement first)
                && first.ValueKind == JsonValueKind.String
                    ? first.GetString()
                    : null;
            if (previousName is not null && linkFile != previousName)
            {
                issue(1, LogIssueSeverity.Error, $"The file follows '{linkFile ?? "nothing"}', not '{previousName}': a file is missing.");
            }
            else if (previousName is not null && linkFirst is not null && state.FirstAuth is not null && linkFirst != state.FirstAuth)
            {
                issue(1, LogIssueSeverity.Error, $"The file follows another file named '{previousName}': that file was replaced or cut.");
            }

            state.Prev = string.Empty;
            state.N = 0;
        }
        else
        {
            if (previousName is null)
            {
                // The first file given: trust its link as the starting point.
                state.Prev = linkAuth ?? string.Empty;
                state.N = -1;
            }
            else if (linkFile != previousName)
            {
                issue(1, LogIssueSeverity.Error, $"The file follows '{linkFile ?? "nothing"}', not '{previousName}': a file is missing.");
                state.Prev = linkAuth ?? string.Empty;
                state.N = -1;
            }
            else if (state.Broken || linkAuth != state.Prev)
            {
                issue(1, LogIssueSeverity.Error, $"The link to '{previousName}' does not match its last signature: that file was cut or changed.");
                state.Prev = linkAuth ?? string.Empty;
                state.N = -1;
            }
        }

        state.Broken = false;

        // design §18.1, 0.95: a service's files name their node on the signed first line, one node for the whole chain.
        if (value.TryGetProperty("node", out JsonElement nodeValue))
        {
            string? fileNode = nodeValue.ValueKind == JsonValueKind.String ? nodeValue.GetString() : null;
            if (fileNode is null || (state.Node is not null && fileNode != state.Node))
            {
                issue(1, LogIssueSeverity.Error, $"The file is of node '{fileNode ?? "?"}', not of node '{state.Node}'.");
                return false;
            }

            state.Node = fileNode;
        }
        else if (value.TryGetProperty("certificates", out _))
        {
            // design §18.1 (V1d23): elm-service names its node on the first line of every file, whatever was given.
            issue(1, LogIssueSeverity.Error, "The file of elm-service names no node on its first line.");
            return false;
        }

        key = ReadKey(value, trust, productId, out string? problem);
        if (key is null)
        {
            issue(1, LogIssueSeverity.Error, problem!);
            return false;
        }

        // design §18.1: a later file cannot be signed again with another server's key. Only the old key can endorse a new
        // one (key.changed); a new machine of the same license cannot, so that is reported for the operator to explain. A
        // service's file is compared at its first signature, by the key that made it (0.95).
        if (previousName is not null && state.Files.Count > 0 && state.Files[^1] is LogFileKey last)
        {
            if ((last.LicenseId is null) != key.IsService)
            {
                issue(
                    1,
                    LogIssueSeverity.Error,
                    $"The file is signed by another kind of key than '{last.File}': a server's and the service's logs are mixed."
                );
            }
            else if (key.IsService || last.KeyId == key.KeyId)
            {
                // Compared at the first signature, or the same server key.
            }
            else if (last.LicenseId == key.LicenseId)
            {
                issue(
                    1,
                    LogIssueSeverity.Warning,
                    $"The file is signed by another key ({key.KeyId}) of the same server license {last.LicenseId}: the server moved."
                );
            }
            else
            {
                issue(
                    1,
                    LogIssueSeverity.Error,
                    $"The file is signed by the key of server license {key.LicenseId}, not by the key of {last.LicenseId} that signed '{last.File}'."
                );
            }
        }

        return true;
    }

    // What verifies the signatures on a chain or key line, verified at the time the line was written: the certificates of the
    // service's signing key (use: log), or a server license and key.
    private static LogSigner? ReadKey(JsonElement value, TrustStore trust, string? productId, out string? problem)
    {
        if (value.TryGetProperty("certificates", out JsonElement certificates))
        {
            return ReadCertificates(value, certificates, trust, out problem);
        }

        if (string.IsNullOrEmpty(productId))
        {
            problem = "The line carries a server license, and no product was given to verify it.";
            return null;
        }

        if (
            !value.TryGetProperty("license", out JsonElement license)
            || license.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("key", out JsonElement jwk)
            || jwk.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("ts", out JsonElement ts)
            || ts.ValueKind != JsonValueKind.Number
            || !ts.TryGetInt64(out long seconds)
            || seconds < 0
            || seconds > 253_402_300_799
        )
        {
            problem = "The line does not carry a server license, a key, and a time.";
            return null;
        }

        var verifier = new LicenseVerifier(trust, new FixedTime(DateTimeOffset.FromUnixTimeSeconds(seconds)), StatelessClockGuard.Instance);
        if (
            !ServerIdentity.TryVerify(
                verifier,
                System.Text.Encoding.UTF8.GetBytes(license.GetRawText()),
                System.Text.Encoding.UTF8.GetBytes(jwk.GetRawText()),
                productId,
                out ServerIdentity? identity,
                out LicenseError? error
            )
        )
        {
            problem = $"The server license on the line does not verify ({ErrorCodes.ToWireName(error!.Code)}): {error.Message}";
            return null;
        }

        problem = null;
        return new LogSigner(identity!);
    }

    // design §18.1, 0.95: one to four certificates, of which those the trust store vouches for with use log at the line's
    // time give the keys that may sign.
    private static LogSigner? ReadCertificates(JsonElement value, JsonElement certificates, TrustStore trust, out string? problem)
    {
        problem = "The line does not carry one to four certificates and a time.";
        if (
            certificates.ValueKind != JsonValueKind.Array
            || certificates.GetArrayLength() is 0 or > 4
            || certificates.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.String)
            || !value.TryGetProperty("ts", out JsonElement ts)
            || ts.ValueKind != JsonValueKind.Number
            || !ts.TryGetInt64(out long seconds)
        )
        {
            return null;
        }

        var keys = new List<VerificationKey>();
        foreach (JsonElement certificate in certificates.EnumerateArray())
        {
            if (CertificateSet.TryVerify(trust, CertificateSet.UseLog, certificate.GetString()!, out CertifiedKey? entry))
            {
                if (seconds >= entry!.Nbf && seconds <= entry.Exp && !keys.Exists(k => k.KeyId == entry.Key.KeyId))
                {
                    keys.Add(entry.Key);
                }
                else
                {
                    entry.Key.Dispose();
                }
            }
        }

        if (keys.Count == 0)
        {
            problem = "No certificate on the line is certified for log by the trust store at the line's time.";
            return null;
        }

        problem = null;
        return new LogSigner(keys);
    }

    private static bool CheckAuth(
        JsonElement value,
        byte[] bytes,
        IncrementalHash digest,
        int covered,
        string stream,
        LogSigner key,
        ChainState state,
        out string? problem
    )
    {
        problem = null;
        if (
            !value.TryGetProperty("sig", out JsonElement sig)
            || sig.ValueKind != JsonValueKind.String
            || !CompactJws.TryParse(sig.GetString()!, TokenTypes.Log, out CompactJws? jws, out _)
        )
        {
            problem = "The signature line is not an elm-log+jws.";
            return false;
        }

        if (key.Verify(jws!) is string keyProblem)
        {
            problem = keyProblem;
            return false;
        }

        LogPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(jws!.Payload, LicensingJsonContext.Default.LogPayload);
        }
        catch (JsonException)
        {
            payload = null;
        }

        string computed = Base64UrlCodec.Encode(digest.GetHashAndReset());
        if (payload is null || payload.Ver != 1 || payload.Stream != stream)
        {
            problem = "The signature's payload is not valid.";
        }
        else if (payload.Digest != computed || payload.Lines != covered)
        {
            problem = "The lines before the signature were changed, added, or removed.";
        }
        else if (payload.Prev != state.Prev)
        {
            problem = "The signature does not follow the previous signature.";
        }
        else if (state.N >= 0 && payload.N != state.N + 1)
        {
            problem = $"The signature is number {payload.N}, not {state.N + 1}: signatures are missing.";
        }

        if (problem is not null)
        {
            return false;
        }

        state.N = payload!.N;
        state.Prev = SignedLog.Hash(bytes);
        return true;
    }

    // Reads a file a line at a time, each line with its line feed (design §18.1, V1d23). A last line without one is not
    // returned, and Partial tells it was there.
    private sealed class LineReader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly byte[] _buffer = new byte[64 * 1024];
        private readonly MemoryStream _current = new();
        private int _start;
        private int _end;

        public LineReader(string path) => _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        public bool Partial { get; private set; }

        // The next line, or null at the end of the file. The limit is on the line without its line feed, checked on the
        // whole line when its end is found and on the part read so far otherwise.
        public byte[]? ReadLine()
        {
            while (true)
            {
                if (_start == _end)
                {
                    _end = _stream.Read(_buffer);
                    _start = 0;
                    if (_end == 0)
                    {
                        Partial = _current.Length > 0;
                        return null;
                    }
                }

                int feed = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                if (feed < 0)
                {
                    _current.Write(_buffer, _start, _end - _start);
                    _start = _end;
                    if (_current.Length > MaxLineLength)
                    {
                        throw new InvalidDataException($"A line is longer than {MaxLineLength} bytes.");
                    }

                    continue;
                }

                if (_current.Length + (feed - _start) > MaxLineLength)
                {
                    throw new InvalidDataException($"A line is longer than {MaxLineLength} bytes.");
                }

                _current.Write(_buffer, _start, feed - _start + 1);
                _start = feed + 1;
                byte[] line = _current.ToArray();
                _current.SetLength(0);
                return line;
            }
        }

        public void Dispose()
        {
            _stream.Dispose();
            _current.Dispose();
        }
    }

    // What may sign a file: a server's identity (its license and key), or the service's keys certified for log.
    private sealed class LogSigner : IDisposable
    {
        private readonly ServerIdentity? _server;
        private readonly List<VerificationKey> _keys = [];
        private string? _signedBy;

        public LogSigner(ServerIdentity server) => _server = server;

        public LogSigner(List<VerificationKey> keys) => _keys = keys;

        public bool IsService => _server is null;

        public string? LicenseId => _server?.License.LicenseId;

        // The key that signed last, or the first key that may sign.
        public string KeyId => _server?.KeyId ?? _signedBy ?? _keys[0].KeyId;

        // Verifies a signature; null when it holds, otherwise why not. The first signature of a service's file fixes the key
        // that may sign the rest of it (0.95): a file is not signed by two keys.
        public string? Verify(CompactJws jws)
        {
            if (_server is not null)
            {
                return jws.Header.Kid == _server.KeyId && _server.Key.Verify(jws) ? null : "The signature is not made with the server's key.";
            }

            if (_signedBy is not null && jws.Header.Kid != _signedBy)
            {
                return "The signature is made with another key than the earlier signatures of the file.";
            }

            VerificationKey? key = _keys.Find(k => k.KeyId == jws.Header.Kid);
            if (key is null || !key.Verify(jws))
            {
                return "The signature is not made with a key certified for log.";
            }

            _signedBy = key.KeyId;
            return null;
        }

        public void Dispose()
        {
            _server?.Dispose();
            foreach (VerificationKey key in _keys)
            {
                key.Dispose();
            }
        }
    }

    private sealed class ChainState
    {
        public string Prev { get; set; } = string.Empty;

        // -1 when the numbering is not known yet (the first file given, or after a gap).
        public long N { get; set; } = -1;

        public bool Broken { get; set; }

        // The node of a service's chain: given by the caller, or taken from its first file that names one.
        public string? Node { get; set; }

        public List<LogFileKey> Files { get; } = [];

        // Where the event lines go, or null to count them only.
        public Action<LogEntry>? OnEntry { get; init; }

        public long EntryCount { get; set; }

        // The hash of the first signature line of the file verified last: empty when it has none, null when it was not read
        // that far.
        public string? FirstAuth { get; set; }
    }

    // A clock fixed at the time a line was written, so a server license is judged as of then.
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
