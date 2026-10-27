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
/// The server license and key that sign the end of a verified file.
/// </summary>
/// <param name="File">The file name.</param>
/// <param name="LicenseId">The server license ID.</param>
/// <param name="KeyId">The thumbprint of the server's key (the license's <c>cnf.jkt</c>).</param>
public sealed record LogFileKey(string File, string LicenseId, string KeyId);

/// <summary>
/// The result of <see cref="LogVerifier.Verify"/>.
/// </summary>
/// <param name="Issues">The findings, in file and line order.</param>
/// <param name="Entries">The event lines (not the chain, key, or signature lines), in order.</param>
/// <param name="Files">The license and key at the end of each file whose first line verified, in order.</param>
public sealed record LogReport(IReadOnlyList<LogIssue> Issues, IReadOnlyList<LogEntry> Entries, IReadOnlyList<LogFileKey> Files)
{
    /// <summary>Gets a value indicating whether there is no error (warnings may remain).</summary>
    public bool Intact => !Issues.Any(i => i.Severity == LogIssueSeverity.Error);
}

/// <summary>
/// Verifies the files of a signed log (design §8.4.6, §18.1): each file's first line and the server license in it, every
/// signature, the numbering and the links between signatures and between files, key changes signed by the old key, and that
/// each file is signed by the key that signed the end of the file before it. A file signed by another key of the same
/// server license (a server moved to a new machine) is a warning; a key of another server license is an error.
/// </summary>
public static class LogVerifier
{
    /// <summary>The longest line read; a longer line is an error.</summary>
    public const int MaxLineLength = 1024 * 1024;

    /// <summary>
    /// Verifies log files given oldest first.
    /// </summary>
    /// <param name="files">The file paths, oldest first.</param>
    /// <param name="trust">The vendor's trust store.</param>
    /// <param name="productId">The product of the server license.</param>
    /// <param name="stream">The expected stream name, such as <c>usage</c>.</param>
    /// <returns>The findings and the event lines.</returns>
    public static LogReport Verify(IReadOnlyList<string> files, TrustStore trust, string productId, string stream)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentException.ThrowIfNullOrEmpty(productId);
        ArgumentException.ThrowIfNullOrEmpty(stream);
        var issues = new List<LogIssue>();
        var entries = new List<LogEntry>();
        var state = new ChainState();
        for (int i = 0; i < files.Count; i++)
        {
            VerifyFile(files[i], i == 0 ? null : Path.GetFileName(files[i - 1]), trust, productId, stream, state, issues, entries);
        }

        return new LogReport(issues, entries, state.Files);
    }

    private static void VerifyFile(
        string path,
        string? previousName,
        TrustStore trust,
        string productId,
        string stream,
        ChainState state,
        List<LogIssue> issues,
        List<LogEntry> entries
    )
    {
        string name = Path.GetFileName(path);
        void Issue(int line, LogIssueSeverity severity, string message) => issues.Add(new LogIssue(name, line, severity, message));

        List<byte[]> lines;
        try
        {
            lines = ReadLines(path, out bool partial);
            if (partial)
            {
                Issue(lines.Count + 1, LogIssueSeverity.Warning, "The file ends in the middle of a line (crash or truncation).");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Issue(0, LogIssueSeverity.Error, "The file cannot be read: " + e.Message);
            state.Broken = true;
            return;
        }

        if (lines.Count == 0)
        {
            // An empty file, or one cut inside its first line: a crash while the file was started, or a file emptied by hand.
            Issue(1, LogIssueSeverity.Error, "The file does not start with chain.start or chain.reset.");
            state.Broken = true;
            return;
        }

        ServerIdentity? key = null;
        ServerIdentity? nextKey = null;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int covered = 0;
        var pendingEntries = new List<LogEntry>();
        try
        {
            for (int index = 0; index < lines.Count; index++)
            {
                int number = index + 1;
                byte[] bytes = lines[index];
                JsonElement value;
                string type;
                try
                {
                    using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                    value = document.RootElement.Clone();
                    type =
                        value.ValueKind == JsonValueKind.Object
                        && value.TryGetProperty("type", out JsonElement t)
                        && t.ValueKind == JsonValueKind.String
                            ? t.GetString()!
                            : string.Empty;
                }
                catch (JsonException)
                {
                    Issue(number, LogIssueSeverity.Error, "The line is not a JSON object.");
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

                    foreach (LogEntry entry in pendingEntries)
                    {
                        entries.Add(entry with { IsSigned = true });
                    }

                    pendingEntries.Clear();
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
                    nextKey = ReadKey(value, trust, productId, out string? keyProblem);
                    if (nextKey is null)
                    {
                        Issue(number, LogIssueSeverity.Error, keyProblem!);
                        return;
                    }
                }
                else if (index > 0)
                {
                    pendingEntries.Add(new LogEntry(name, number, type, value, IsSigned: false));
                }
            }

            if (pendingEntries.Count > 0 || covered > 0)
            {
                Issue(lines.Count, LogIssueSeverity.Warning, $"{covered} line(s) after the last signature are not signed (crash or tampering).");
                entries.AddRange(pendingEntries);
            }
        }
        finally
        {
            if (key is not null)
            {
                state.Files.Add(new LogFileKey(name, key.License.LicenseId, key.KeyId));
            }

            key?.Dispose();
            nextKey?.Dispose();
        }
    }

    // The first line of a file: chain.start (linked to the previous file when it is given) or chain.reset; either names the
    // server license and key that verify the signatures.
    private static bool StartChain(
        JsonElement value,
        string type,
        string name,
        string? previousName,
        TrustStore trust,
        string productId,
        string stream,
        ChainState state,
        Action<int, LogIssueSeverity, string> issue,
        out ServerIdentity? key
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

        if (type == LogLineTypes.ChainReset)
        {
            string reason = value.TryGetProperty("reason", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "no reason";
            issue(1, LogIssueSeverity.Warning, "The chain was reset: " + reason);
            state.Prev = string.Empty;
            state.N = 0;
        }
        else
        {
            string? linkFile = null;
            string? linkAuth = null;
            if (value.TryGetProperty("prev", out JsonElement link) && link.ValueKind == JsonValueKind.Object)
            {
                linkFile = link.TryGetProperty("file", out JsonElement f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                linkAuth = link.TryGetProperty("auth", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            }

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
        key = ReadKey(value, trust, productId, out string? problem);
        if (key is null)
        {
            issue(1, LogIssueSeverity.Error, problem!);
            return false;
        }

        // design §18.1: a later file cannot be signed again with another server's key. Only the old key can endorse a new
        // one (key.changed); a new machine of the same license cannot, so that is reported for the operator to explain.
        if (previousName is not null && state.Files.Count > 0 && state.Files[^1] is LogFileKey last && last.KeyId != key.KeyId)
        {
            if (last.LicenseId == key.License.LicenseId)
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
                    $"The file is signed by the key of server license {key.License.LicenseId}, not by the key of {last.LicenseId} that signed '{last.File}'."
                );
            }
        }

        return true;
    }

    // The server license and key on a chain or key line, verified at the time the line was written.
    private static ServerIdentity? ReadKey(JsonElement value, TrustStore trust, string productId, out string? problem)
    {
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
        return identity;
    }

    private static bool CheckAuth(
        JsonElement value,
        byte[] bytes,
        IncrementalHash digest,
        int covered,
        string stream,
        ServerIdentity key,
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

        if (jws!.Header.Kid != key.KeyId || !key.Key.Verify(jws))
        {
            problem = "The signature is not made with the server's key.";
            return false;
        }

        LogPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(jws.Payload, LicensingJsonContext.Default.LogPayload);
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

    // Splits a file into lines, each with its line feed. A last line without one is reported as partial and left out.
    private static List<byte[]> ReadLines(string path, out bool partial)
    {
        var lines = new List<byte[]>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var current = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            int start = 0;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    continue;
                }

                // The limit is on the line without its line feed; a whole line is checked here, a line still being read below.
                if (current.Length + (i - start) > MaxLineLength)
                {
                    throw new InvalidDataException($"A line is longer than {MaxLineLength} bytes.");
                }

                current.Write(buffer, start, i - start + 1);
                lines.Add(current.ToArray());
                current.SetLength(0);
                start = i + 1;
            }

            current.Write(buffer, start, read - start);
            if (current.Length > MaxLineLength)
            {
                throw new InvalidDataException($"A line is longer than {MaxLineLength} bytes.");
            }
        }

        partial = current.Length > 0;
        return lines;
    }

    private sealed class ChainState
    {
        public string Prev { get; set; } = string.Empty;

        // -1 when the numbering is not known yet (the first file given, or after a gap).
        public long N { get; set; } = -1;

        public bool Broken { get; set; }

        public List<LogFileKey> Files { get; } = [];
    }

    // A clock fixed at the time a line was written, so a server license is judged as of then.
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
