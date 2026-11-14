using System.Security.Cryptography;
using System.Text;
using ElasticLM.Licensing;
using ElasticLM.Licensing.Tests;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Issuing.Tests;

/// <summary>
/// The signed log of <see cref="SignedLog"/> and its check by <see cref="LogVerifier"/> (design §8.4.6, §18.1).
/// </summary>
public sealed class SignedLogTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly ECDsa _root = KeyFiles.CreateKey();
    private readonly ECDsa _signing = KeyFiles.CreateKey();
    private readonly ECDsa _server = KeyFiles.CreateKey();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "elm-log-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));
    private readonly TrustStore _trust;

    /// <summary>Gets each kind of log with each form of a token without a valid signature.</summary>
    public static TheoryData<string, string> LogsAndSignatureForms => SignatureForms.Cross("server", "service");

    public SignedLogTests()
    {
        Directory.CreateDirectory(_dir);
        _trust = TrustStore.Create([KeyFiles.ExportPublic(_root)], []);
    }

    public void Dispose()
    {
        _root.Dispose();
        _signing.Dispose();
        _server.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// A log written, signed every 3 lines, and closed verifies with no finding, and every event line is signed.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server license of the server key; a log signed every 3 lines.
    ///
    /// Steps:
    /// - Append 7 events; dispose; verify.
    ///
    /// Expected:
    /// - No issue; 7 signed entries in order.
    /// </remarks>
    [Fact]
    public void Verify_WrittenLog_IsIntact()
    {
        using (SignedLog log = Open(sealEvery: 3))
        {
            Append(log, 7);
        }

        VerifiedLog report = Verify();
        Assert.Empty(report.Issues);
        Assert.Equal(7, report.Entries.Count);
        Assert.All(report.Entries, e => Assert.True(e.IsSigned));
        Assert.Equal(Enumerable.Range(0, 7), report.Entries.Select(e => e.Value.GetProperty("i").GetInt32()));
    }

    /// <summary>
    /// The chain continues across a new UTC day and across a restart; a missing file and a file cut after its last
    /// signature are found by the link of the next file.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A log written over two days and reopened (three files).
    ///
    /// Steps:
    /// - Verify the three files; verify without the middle one; remove the last line of the first file and verify all.
    ///
    /// Expected:
    /// - No issue; an error naming the missing file; an error that the link does not match.
    /// </remarks>
    [Fact]
    public void Verify_FilesAcrossDaysAndRestarts_AreLinked()
    {
        using (SignedLog log = Open())
        {
            Append(log, 2);
            _time.Advance(TimeSpan.FromDays(1));
            Append(log, 2);
        }

        _time.Advance(TimeSpan.FromMinutes(1));
        using (SignedLog log = Open())
        {
            Append(log, 2);
        }

        IReadOnlyList<string> files = SignedLog.FindFiles(_dir, "usage");
        Assert.Equal(3, files.Count);
        Assert.Empty(Verify(files).Issues);

        Assert.Contains(
            Verify([files[0], files[2]]).Issues,
            i => i.Severity == LogIssueSeverity.Error && i.Message.Contains("a file is missing", StringComparison.Ordinal)
        );

        string[] first = File.ReadAllLines(files[0]);
        File.WriteAllLines(files[0], first[..^1]);
        Assert.Contains(
            Verify(files).Issues,
            i => i.Severity == LogIssueSeverity.Error && i.Message.Contains("does not match its last signature", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// A changed, removed, or added line, a changed signature, and a line that is not JSON are errors at the signature or
    /// line that shows them.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A closed log of 4 events signed every 2 lines.
    ///
    /// Steps:
    /// - In a copy of the file each time: change a digit of an event; remove an event; duplicate an event; change one
    ///   character of a signature; replace an event with "not json"; write the first line's time as text.
    ///
    /// Expected:
    /// - Each report is not intact.
    /// </remarks>
    [Theory]
    [InlineData("change")]
    [InlineData("remove")]
    [InlineData("add")]
    [InlineData("signature")]
    [InlineData("garbage")]
    [InlineData("ts-text")]
    public void Verify_ChangedLines_AreErrors(string change)
    {
        using (SignedLog log = Open(sealEvery: 2))
        {
            Append(log, 4);
        }

        string file = SignedLog.FindFiles(_dir, "usage")[0];
        List<string> lines = [.. File.ReadAllLines(file)];
        int eventLine = lines.FindIndex(l => l.Contains("\"i\":1", StringComparison.Ordinal));
        int authLine = lines.FindLastIndex(l => l.Contains("\"type\":\"auth\"", StringComparison.Ordinal));
        switch (change)
        {
            case "change":
                lines[eventLine] = lines[eventLine].Replace("\"i\":1", "\"i\":9", StringComparison.Ordinal);
                break;
            case "remove":
                lines.RemoveAt(eventLine);
                break;
            case "add":
                lines.Insert(eventLine, lines[eventLine]);
                break;
            case "signature":
                char[] chars = lines[authLine].ToCharArray();
                int at = lines[authLine].Length - 10;
                chars[at] = chars[at] == 'A' ? 'B' : 'A';
                lines[authLine] = new string(chars);
                break;
            case "ts-text":
                lines[0] = System.Text.RegularExpressions.Regex.Replace(lines[0], "\"ts\":([0-9]+)", "\"ts\":\"$1\"");
                break;
            default:
                lines[eventLine] = "not json";
                break;
        }

        File.WriteAllText(file, string.Join('\n', lines) + "\n");
        Assert.False(Verify().Intact);
    }

    /// <summary>
    /// A log whose last signature line carries no valid signature is not intact, for a server's log and for a log of
    /// <c>elm-service</c>: with an empty signature part, with a signature that is not 64 bytes or not the key's, and with an
    /// alg other than ES256 (design §8.2 rules 3 and 5, §18.1, §20.7 V1d14).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A closed log of 4 events signed every 2 lines, of a server or of the service, that verifies with no finding.
    /// - The JWS of its last signature line, which no later signature covers, in a form of <see cref="SignatureForms"/>
    ///   with the same payload: an empty signature part; the first 63 bytes of its signature; 64 zero bytes; its signature
    ///   and one more zero byte; alg none and an empty signature part; alg HS256 and an ES256 signature of the log's key
    ///   over the changed header.
    ///
    /// Steps:
    /// - Verify the log.
    ///
    /// Expected:
    /// - Not intact, with one error. For the four signatures: "The signature is not made with the server's key." for the
    ///   server's log, "The signature is not made with a key certified for log." for the service's. For the two algs: "The
    ///   signature line is not an elm-log+jws.".
    /// </remarks>
    [Theory]
    [MemberData(nameof(LogsAndSignatureForms))]
    public void Verify_SealWithoutAValidSignature_IsNotIntact(string log, string form)
    {
        bool service = log == "service";
        using (SignedLog written = service ? OpenService(Certificate(_signing, KeyUses.Log), _signing, sealEvery: 2) : Open(sealEvery: 2))
        {
            Append(written, 4);
        }

        string file = SignedLog.FindFiles(_dir, "usage").Single();
        VerifiedLog Report() => service ? Verify2() : Verify();
        Assert.Empty(Report().Issues);
        const string Member = "\"sig\":\"";
        List<string> lines = [.. File.ReadAllLines(file)];
        int authLine = lines.FindLastIndex(l => l.Contains("\"type\":\"auth\"", StringComparison.Ordinal));
        int start = lines[authLine].IndexOf(Member, StringComparison.Ordinal) + Member.Length;
        int end = lines[authLine].LastIndexOf('"');
        string seal = SignatureForms.Change(lines[authLine][start..end], form, SignatureForms.Es256(service ? _signing : _server));
        lines[authLine] = lines[authLine][..start] + seal + lines[authLine][end..];
        File.WriteAllText(file, string.Join('\n', lines) + "\n");

        VerifiedLog report = Report();

        Assert.False(report.Intact);
        string expected =
            SignatureForms.ChangesAlgorithm(form) ? "The signature line is not an elm-log+jws."
            : service ? "The signature is not made with a key certified for log."
            : "The signature is not made with the server's key.";
        Assert.Equal(expected, Assert.Single(report.Issues, i => i.Severity == LogIssueSeverity.Error).Message);
    }

    /// <summary>
    /// A log of <c>elm-service</c> whose certificate carries no valid signature is refused, since nothing certifies its key
    /// for log: with an empty signature part, with a signature that is not 64 bytes or not the root's, and with an alg other
    /// than ES256 (design §8.2 rules 3 and 5, §18.1, §20.7 V1d14).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A certificate of the signing key for log from the trusted root, with which a log of 2 events verifies with no
    ///   finding.
    /// - The certificate in a form of <see cref="SignatureForms"/> with the same payload: an empty signature part; the first
    ///   63 bytes of its signature; 64 zero bytes; its signature and one more zero byte; alg none and an empty signature
    ///   part; alg HS256 and an ES256 signature of the root key over the changed header.
    ///
    /// Steps:
    /// - Write a log of 2 events with the changed certificate and verify it alone with no product.
    ///
    /// Expected:
    /// - One error: "No certificate on the line is certified for log by the trust store at the line's time.".
    /// </remarks>
    [Theory]
    [MemberData(nameof(SignatureForms.All), MemberType = typeof(SignatureForms))]
    public void ServiceLog_CertificateWithoutAValidSignature_IsRefused(string form)
    {
        string certificate = Certificate(_signing, KeyUses.Log);
        using (SignedLog log = OpenService(certificate, _signing))
        {
            Append(log, 2);
        }

        Assert.Empty(Verify2().Issues);
        ClearFolder();
        using (SignedLog log = OpenService(SignatureForms.Change(certificate, form, SignatureForms.Es256(_root)), _signing))
        {
            Append(log, 2);
        }

        LogIssue error = Assert.Single(Verify2().Issues);
        Assert.Equal(
            (LogIssueSeverity.Error, "No certificate on the line is certified for log by the trust store at the line's time."),
            (error.Severity, error.Message)
        );
    }

    /// <summary>
    /// A key change is accepted only when the old key signs the line that names the new license; a line inserted by another
    /// server, signed with its own key, is an error.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A log of the server; a valid server license of another server key from the same vendor.
    ///
    /// Steps:
    /// - Change to a reissued license of the same server through the writer; verify. Then append to the file a key.changed
    ///   line for the other server and a signature of the other server's key that links correctly; verify.
    ///
    /// Expected:
    /// - The writer's change is intact; the inserted change is an error that the signature is not the server's.
    /// </remarks>
    [Fact]
    public void Verify_KeyChange_MustBeSignedByTheOldKey()
    {
        using (SignedLog log = Open())
        {
            Append(log, 1);
            log.ChangeKey(LicenseFile(_server), KeyFiles.ExportPublicJwk(_server), Access(_server), takeOnFailure: true);
            Append(log, 1);
        }

        Assert.Empty(Verify().Issues);

        using ECDsa other = KeyFiles.CreateKey();
        string file = SignedLog.FindFiles(_dir, "usage")[0];
        string[] lines = File.ReadAllLines(file);
        string lastAuth = lines[^1] + "\n";
        LogPayload last = ReadPayload(lines[^1]);
        string keyChanged =
            "{\"type\":\"key.changed\",\"ts\":"
            + Now.ToUnixTimeSeconds()
            + ",\"license\":"
            + Encoding.UTF8.GetString(LicenseFile(other))
            + ",\"key\":"
            + Encoding.UTF8.GetString(KeyFiles.ExportPublicJwk(other))
            + "}\n";
        var forged = new LogPayload
        {
            Ver = 1,
            Iat = Now.ToUnixTimeSeconds(),
            Jti = JwsWriter.NewTokenId(),
            Stream = "usage",
            N = last.N + 1,
            Prev = SignedLog.Hash(Encoding.UTF8.GetBytes(lastAuth)),
            Digest = SignedLog.Hash(Encoding.UTF8.GetBytes(keyChanged)),
            Lines = 1,
        };
        string sig = JwsWriter.Sign(other, TokenTypes.Log, forged, LicensingJsonContext.Default.LogPayload);
        File.AppendAllText(file, keyChanged + "{\"type\":\"auth\",\"ts\":0,\"sig\":\"" + sig + "\"}\n");

        VerifiedLog report = Verify();
        Assert.Contains(
            report.Issues,
            i => i.Severity == LogIssueSeverity.Error && i.Message.Contains("not made with the server's key", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// A signature of the server's key over the right lines is still an error when it does not link to the previous
    /// signature or skips a number, so each link of the chain is checked on its own.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A closed log of 1 event.
    ///
    /// Steps:
    /// - Append an event and a signature of the server's key over it, with the right digest and either a wrong prev and the
    ///   next number, or the right prev and a number one too high; verify.
    ///
    /// Expected:
    /// - An error that the signature does not follow the previous one; an error that signatures are missing.
    /// </remarks>
    [Theory]
    [InlineData("prev", "does not follow the previous signature")]
    [InlineData("n", "signatures are missing")]
    public void Verify_SignatureOutOfChain_IsAnError(string change, string message)
    {
        using (SignedLog log = Open())
        {
            Append(log, 1);
        }

        string file = SignedLog.FindFiles(_dir, "usage")[0];
        string[] lines = File.ReadAllLines(file);
        LogPayload last = ReadPayload(lines[^1]);
        string line = "{\"type\":\"lease.acquired\",\"ts\":" + Now.ToUnixTimeSeconds() + ",\"i\":5}\n";
        var payload = new LogPayload
        {
            Ver = 1,
            Iat = Now.ToUnixTimeSeconds(),
            Jti = JwsWriter.NewTokenId(),
            Stream = "usage",
            N = change == "n" ? last.N + 2 : last.N + 1,
            Prev =
                change == "prev" ? SignedLog.Hash(Encoding.UTF8.GetBytes(lines[0] + "\n")) : SignedLog.Hash(Encoding.UTF8.GetBytes(lines[^1] + "\n")),
            Digest = SignedLog.Hash(Encoding.UTF8.GetBytes(line)),
            Lines = 1,
        };
        string sig = JwsWriter.Sign(_server, TokenTypes.Log, payload, LicensingJsonContext.Default.LogPayload);
        File.AppendAllText(file, line + "{\"type\":\"auth\",\"ts\":0,\"sig\":\"" + sig + "\"}\n");

        Assert.Contains(Verify().Issues, i => i.Severity == LogIssueSeverity.Error && i.Message.Contains(message, StringComparison.Ordinal));
    }

    /// <summary>
    /// Lines after the last signature and a cut last line are warnings; their entries are reported as unsigned.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A log with 2 signed events, then 1 event written without a signature, then half a line.
    ///
    /// Steps:
    /// - Verify.
    ///
    /// Expected:
    /// - Intact with two warnings; 3 entries, the last unsigned.
    /// </remarks>
    [Fact]
    public void Verify_UnsignedTail_IsAWarning()
    {
        using (SignedLog log = Open(sealEvery: 2))
        {
            Append(log, 2);
            log.Seal();
        }

        string file = SignedLog.FindFiles(_dir, "usage")[0];
        File.AppendAllText(file, "{\"type\":\"lease.acquired\",\"ts\":1,\"i\":2}\n{\"type\":\"lea");

        VerifiedLog report = Verify();
        Assert.True(report.Intact);
        Assert.Equal(2, report.Issues.Count(i => i.Severity == LogIssueSeverity.Warning));
        Assert.Equal([true, true, false], report.Entries.Select(e => e.IsSigned));
    }

    /// <summary>
    /// When the previous file has no signature at its end, the next start writes chain.reset with the reason, which
    /// verifies with a warning.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A previous file of the log that holds only "{}".
    ///
    /// Steps:
    /// - Open the log, append, close; verify the new file.
    ///
    /// Expected:
    /// - ResetReason names the previous file; the new file is intact with a warning that the chain was reset.
    /// </remarks>
    [Fact]
    public void Open_PreviousWithoutSignature_ResetsTheChain()
    {
        File.WriteAllText(Path.Combine(_dir, "usage-20260930T000000000Z.jsonl"), "{}\n");
        string reason;
        using (SignedLog log = Open())
        {
            reason = log.ResetReason!;
            Append(log, 1);
        }

        Assert.Contains("usage-20260930T000000000Z.jsonl", reason, StringComparison.Ordinal);
        string newest = SignedLog.FindFiles(_dir, "usage")[^1];
        VerifiedLog report = Verify([newest]);
        Assert.True(report.Intact);
        Assert.Contains(report.Issues, i => i.Message.StartsWith("The chain was reset", StringComparison.Ordinal));
        Assert.Contains(
            "\"prev\":{\"file\":\"usage-20260930T000000000Z.jsonl\",\"first\":\"\"}",
            File.ReadLines(newest).First(),
            StringComparison.Ordinal
        );
    }

    /// <summary>
    /// At a start whose previous file has no signature in its last 1 MiB, the chain.reset names that file and the hash of its
    /// first signature line, read from the file's head; the two files verify intact (design §18.1, V1d23).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A closed file of the log, then 1 MiB and more of unsigned event lines appended to it.
    ///
    /// Steps:
    /// - A minute later, open the log, append, close; read the new file's first line; verify the folder.
    ///
    /// Expected:
    /// - The reason is "no signature was found at the end of the previous file (name)"; the line holds "prev":{"file":(name),
    ///   "first":(the SHA-256 of the previous file's second line, worked out here)}; the folder is intact, with warnings.
    /// </remarks>
    [Fact]
    public void Open_PreviousWithoutSignatureAtItsEnd_NamesItsFirstSignature()
    {
        using (SignedLog log = Open())
        {
            Append(log, 1);
        }

        string previous = SignedLog.FindFiles(_dir, "usage").Single();
        string unsigned = string.Concat(Enumerable.Repeat("{\"type\":\"lease.acquired\",\"ts\":1}\n", (1024 * 1024 / 32) + 1));
        File.AppendAllText(previous, unsigned);
        _time.Advance(TimeSpan.FromMinutes(1));
        string reason;
        using (SignedLog log = Open())
        {
            reason = log.ResetReason!;
            Append(log, 1);
        }

        string name = Path.GetFileName(previous);
        Assert.Equal($"no signature was found at the end of the previous file {name}", reason);
        string firstAuth = Base64UrlCodec.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadLines(previous).ElementAt(1) + "\n")));
        Assert.Contains(
            $"\"prev\":{{\"file\":\"{name}\",\"first\":\"{firstAuth}\"}}",
            File.ReadLines(SignedLog.FindFiles(_dir, "usage")[^1]).First(),
            StringComparison.Ordinal
        );
        Assert.True(Verify().Intact);
    }

    /// <summary>
    /// A start removes the files of the log left unfinished by a stop and reports each; other files stay, and the chain
    /// continues from the newest finished file (design §18.1, V1d16).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A closed file of the log; an unfinished file usage-20990101T000000000Z.jsonl.partial; files usage-x.partial and
    ///   other-20990101T000000000Z.jsonl.partial.
    ///
    /// Steps:
    /// - A minute later, open the log with a callback for removed files, and close it; list the folder.
    ///
    /// Expected:
    /// - The callback got usage-20990101T000000000Z.jsonl.partial only, which is gone; the other two stay; the new file
    ///   starts with chain.start that names the first file.
    /// </remarks>
    [Fact]
    public void Open_UnfinishedFile_IsRemovedAndReported()
    {
        using (SignedLog log = Open())
        {
            Append(log, 1);
        }

        string finished = Path.GetFileName(SignedLog.FindFiles(_dir, "usage")[0]);
        File.WriteAllText(Path.Combine(_dir, "usage-20990101T000000000Z.jsonl.partial"), "{}\n");
        File.WriteAllText(Path.Combine(_dir, "usage-x.partial"), "{}\n");
        File.WriteAllText(Path.Combine(_dir, "other-20990101T000000000Z.jsonl.partial"), "{}\n");
        _time.Advance(TimeSpan.FromMinutes(1));
        var removed = new List<string>();

        using (
            SignedLog.Open(
                _dir,
                "usage",
                "usage",
                Access(_server),
                LicenseFile(_server),
                KeyFiles.ExportPublicJwk(_server),
                _time,
                unfinishedRemoved: removed.Add
            )
        )
        {
            // Opened and closed.
        }

        Assert.Equal(["usage-20990101T000000000Z.jsonl.partial"], removed);
        Assert.False(File.Exists(Path.Combine(_dir, "usage-20990101T000000000Z.jsonl.partial")));
        Assert.True(File.Exists(Path.Combine(_dir, "usage-x.partial")));
        Assert.True(File.Exists(Path.Combine(_dir, "other-20990101T000000000Z.jsonl.partial")));
        string line = File.ReadLines(SignedLog.FindFiles(_dir, "usage")[^1]).First();
        Assert.Contains("\"type\":\"chain.start\"", line, StringComparison.Ordinal);
        Assert.Contains($"\"file\":\"{finished}\"", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The first signature of a file is its second line, so a chain.reset finds it in the file's first two lines of at most
    /// 1 MiB each, each with its line feed: 2 MiB and 2 bytes (design §18.1, V1d16).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A closed file of the log. A later file of two lines of exactly 1 MiB each: a line that is not a signature, and the
    ///   first file's signature line with a member added to make it 1 MiB; so its end holds no whole signature line.
    ///
    /// Steps:
    /// - A minute later, open the log and close it; read the new file's first line.
    ///
    /// Expected:
    /// - A chain.reset whose prev names the later file and, as first, the SHA-256 of its second line with its line feed.
    /// </remarks>
    [Fact]
    public void Open_PreviousWithItsFirstSignatureAtTheEndOfItsHead_NamesIt()
    {
        using (SignedLog log = Open())
        {
            Append(log, 1);
        }

        string auth = File.ReadLines(SignedLog.FindFiles(_dir, "usage")[0]).ElementAt(1);
        const string Pad = "\"pad\":\"";
        int at = auth.IndexOf("\"sig\"", StringComparison.Ordinal);
        string padded = auth[..at] + Pad + new string('p', LogVerifier.MaxLineLength - auth.Length - Pad.Length - 2) + "\"," + auth[at..];
        Assert.Equal(LogVerifier.MaxLineLength, Encoding.UTF8.GetByteCount(padded));
        const string Head = "{\"type\":\"lease.acquired\",\"ts\":1,\"x\":\"";
        string first = Head + new string('x', LogVerifier.MaxLineLength - Head.Length - 2) + "\"}";
        string later = Path.Combine(_dir, "usage-20990101T000000000Z.jsonl");
        File.WriteAllText(later, first + "\n" + padded + "\n");
        _time.Advance(TimeSpan.FromMinutes(1));

        using (SignedLog log = Open())
        {
            Assert.NotNull(log.ResetReason);
        }

        string hash = Base64UrlCodec.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(padded + "\n")));
        string line = File.ReadLines(SignedLog.FindFiles(_dir, "usage")[^1]).First();
        Assert.Contains("\"type\":\"chain.reset\"", line, StringComparison.Ordinal);
        Assert.Contains($"\"prev\":{{\"file\":\"usage-20990101T000000000Z.jsonl\",\"first\":\"{hash}\"}}", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The server license on the first line is judged as of the time the line was written: a log stays valid after its
    /// license expired, and a license from another vendor's root, the wrong product, or the wrong stream is an error.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A log written while the server license (30 days) was valid.
    ///
    /// Steps:
    /// - Advance 60 days and verify; verify with another root; with product acme-cam; as stream borrows.
    ///
    /// Expected:
    /// - No issue; then three errors.
    /// </remarks>
    [Fact]
    public void Verify_License_IsJudgedAtTheLineTime()
    {
        using (SignedLog log = Open())
        {
            Append(log, 1);
        }

        _time.Advance(TimeSpan.FromDays(60));
        Assert.Empty(Verify().Issues);

        using ECDsa otherRoot = KeyFiles.CreateKey();
        IReadOnlyList<string> files = SignedLog.FindFiles(_dir, "usage");
        Assert.False(VerifiedLog.Of(files, TrustStore.Create([KeyFiles.ExportPublic(otherRoot)], []), "acme-cad", "usage").Intact);
        Assert.False(VerifiedLog.Of(files, _trust, "acme-cam", "usage").Intact);
        Assert.False(VerifiedLog.Of(files, _trust, "acme-cad", "borrows").Intact);
    }

    /// <summary>
    /// A file with no whole line (empty, or cut inside its first line) is an error, not an intact file.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A log file of 0 bytes, or holding only {"type":"chain.st.
    ///
    /// Steps:
    /// - Verify it.
    ///
    /// Expected:
    /// - Not intact, with an error at line 1 that the file does not start with chain.start or chain.reset, and no file key.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("{\"type\":\"chain.st")]
    public void Verify_FileWithoutAWholeLine_IsAnError(string content)
    {
        string file = Path.Combine(_dir, "usage-20261001T000000000Z.jsonl");
        File.WriteAllText(file, content);

        VerifiedLog report = Verify([file]);
        Assert.False(report.Intact);
        Assert.Contains(report.Issues, i => i.Line == 1 && i.Message.Contains("does not start with chain.start", StringComparison.Ordinal));
        Assert.Empty(report.Files);
    }

    /// <summary>
    /// A line of exactly 1 MiB (before its line feed) is read, and a whole line one byte longer is an error; a last line of
    /// exactly 1 MiB cut before its line feed is only a warning.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A closed log, then an event line of exactly 1 MiB and its line feed appended without a signature.
    ///
    /// Steps:
    /// - Verify; append 1 MiB of text without a line feed and verify; write a file whose first line is 1 MiB + 1 byte with
    ///   its line feed and verify (the verifier reads a line at a time, so a line before it would be judged first).
    ///
    /// Expected:
    /// - Intact, the long line an unsigned entry; intact with a warning that the file ends in the middle of a line; an
    ///   error that the file cannot be read.
    /// </remarks>
    [Fact]
    public void Verify_LineOfExactly1MiB_IsRead()
    {
        using (SignedLog log = Open())
        {
            Append(log, 1);
        }

        string file = SignedLog.FindFiles(_dir, "usage")[0];
        const string Head = "{\"type\":\"lease.acquired\",\"ts\":1,\"x\":\"";
        string line = Head + new string('x', LogVerifier.MaxLineLength - Head.Length - 2) + "\"}";
        Assert.Equal(LogVerifier.MaxLineLength, Encoding.UTF8.GetByteCount(line));
        File.AppendAllText(file, line + "\n");

        VerifiedLog report = Verify();
        Assert.True(report.Intact);
        Assert.Contains(report.Entries, e => !e.IsSigned && e.Value.TryGetProperty("x", out _));

        File.AppendAllText(file, new string('y', LogVerifier.MaxLineLength));
        VerifiedLog cut = Verify();
        Assert.True(cut.Intact);
        Assert.Contains(cut.Issues, i => i.Message.Contains("middle of a line", StringComparison.Ordinal));

        string longer = Path.Combine(_dir, "usage-20990101T000000000Z.jsonl");
        File.WriteAllText(longer, new string('z', LogVerifier.MaxLineLength + 1) + "\n");
        Assert.Contains(
            Verify([longer]).Issues,
            i => i.Severity == LogIssueSeverity.Error && i.Message.StartsWith("The file cannot be read", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// A line longer than 1 MiB is an error, and the file is not read further.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A closed log, then a line of 1 MiB + 1 byte appended.
    ///
    /// Steps:
    /// - Verify.
    ///
    /// Expected:
    /// - An error that the file cannot be read.
    /// </remarks>
    [Fact]
    public void Verify_LongLine_IsAnError()
    {
        using (SignedLog log = Open())
        {
            Append(log, 1);
        }

        string file = SignedLog.FindFiles(_dir, "usage")[0];
        File.AppendAllText(file, new string('x', LogVerifier.MaxLineLength + 1));

        Assert.Contains(
            Verify().Issues,
            i => i.Severity == LogIssueSeverity.Error && i.Message.StartsWith("The file cannot be read", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// A file that continues the chain must be signed by the key that signed the end of the file before it: another key of
    /// the same server license (the server moved) is a warning, a key of another server license is an error. The report
    /// names the license and key of each file.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A file written by the server.
    ///
    /// Steps:
    /// - Continue the chain with another key and the same license ID (lic-srv), or with another key and lic-other; verify.
    ///
    /// Expected:
    /// - Intact with a warning that the server moved, and the second file reported with lic-srv and the new key; or not
    ///   intact with an error naming lic-other.
    /// </remarks>
    [Theory]
    [InlineData("lic-srv", true, "the server moved")]
    [InlineData("lic-other", false, "lic-other")]
    public void Verify_FileOfAnotherKey_IsReported(string licenseId, bool intact, string message)
    {
        using (SignedLog log = Open())
        {
            Append(log, 1);
        }

        _time.Advance(TimeSpan.FromMinutes(1));
        using ECDsa other = KeyFiles.CreateKey();
        using (
            SignedLog log = SignedLog.Open(
                _dir,
                "usage",
                "usage",
                Access(other),
                LicenseFile(other, licenseId),
                KeyFiles.ExportPublicJwk(other),
                _time
            )
        )
        {
            Append(log, 1);
        }

        VerifiedLog report = Verify();
        Assert.Equal(intact, report.Intact);
        Assert.Contains(report.Issues, i => i.Line == 1 && i.Message.Contains(message, StringComparison.Ordinal));
        Assert.Equal((licenseId, KeyFiles.KeyId(other)), (report.Files[^1].LicenseId, report.Files[^1].KeyId));
    }

    /// <summary>
    /// A write that fails closes the file; the next write starts a new file with chain.reset that names the failure, so the
    /// log verifies with warnings only. A signature reaches the disk before Append returns.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A log signed every line whose key fails once with an IOException.
    ///
    /// Steps:
    /// - Append one event (signed); make the key fail and append (throws); append again; read the first file while the log is
    ///   open; close and verify.
    ///
    /// Expected:
    /// - The second Append throws IOException; the first file holds the signed event on the disk; two files; intact, with
    ///   warnings for the unsigned line and for the reset, "The chain was reset: a write to {first file} failed": the reason
    ///   names the file and does not carry the exception's message, which can hold a path.
    /// </remarks>
    [Fact]
    public void Append_AfterAFailedWrite_StartsANewFile()
    {
        bool fail = false;
        string Access(Func<ECDsa, string> use) => fail ? throw new IOException("The disk is full.") : use(_server);
        using (
            SignedLog log = SignedLog.Open(
                _dir,
                "usage",
                "usage",
                Access,
                LicenseFile(_server),
                KeyFiles.ExportPublicJwk(_server),
                _time,
                sealEvery: 1
            )
        )
        {
            Append(log, 1);
            string first = SignedLog.FindFiles(_dir, "usage")[0];
            using (var reader = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var text = new StreamReader(reader))
            {
                Assert.Contains("\"i\":0", text.ReadToEnd(), StringComparison.Ordinal);
            }

            fail = true;
            Assert.Throws<IOException>(() => Append(log, 1));
            fail = false;
            _time.Advance(TimeSpan.FromSeconds(1));
            Append(log, 1);
        }

        Assert.Equal(2, SignedLog.FindFiles(_dir, "usage").Count);
        VerifiedLog report = Verify();
        Assert.True(report.Intact);
        Assert.Contains(report.Issues, i => i.Message.Contains("not signed", StringComparison.Ordinal));
        string failed = Path.GetFileName(SignedLog.FindFiles(_dir, "usage")[0]);
        Assert.Contains(report.Issues, i => i.Message == $"The chain was reset: a write to {failed} failed");
    }

    /// <summary>
    /// A signature that fails with an exception that is not an IOException leaves the digest and the count of unsigned lines
    /// as they were, so the next signature covers the lines before it, and the log stays intact (design §18.1, V1d23).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A log signed every line whose key fails once with a CryptographicException, which does not close the file.
    ///
    /// Steps:
    /// - Append one event (signed); make the key fail and append (throws); append again; close; verify.
    ///
    /// Expected:
    /// - The second Append throws CryptographicException; one file; no issue; three events, all signed (before V1d23 the
    ///   failed signature had reset the digest, and the next signature was reported as covering changed lines).
    /// </remarks>
    [Fact]
    public void Append_SignatureThatFails_IsCoveredByTheNextSignature()
    {
        bool fail = false;
        string Access(Func<ECDsa, string> use) => fail ? throw new CryptographicException("The key cannot be used.") : use(_server);
        using (
            SignedLog log = SignedLog.Open(
                _dir,
                "usage",
                "usage",
                Access,
                LicenseFile(_server),
                KeyFiles.ExportPublicJwk(_server),
                _time,
                sealEvery: 1
            )
        )
        {
            Append(log, 1);
            fail = true;
            Assert.Throws<CryptographicException>(() => Append(log, 1));
            fail = false;
            Append(log, 1);
        }

        Assert.Single(SignedLog.FindFiles(_dir, "usage"));
        VerifiedLog report = Verify();
        Assert.Empty(report.Issues);
        Assert.Equal([true, true, true], report.Entries.Select(e => e.IsSigned));
    }

    /// <summary>
    /// A file that starts with chain.reset names the file before it (prev.file, the newest of the folder), and the verifier,
    /// given the files before it, finds a removed file as for chain.start; a reset that names no file after another file is
    /// the same error (design §18.1, V1d23, finding T15-1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service log: a first file; a second file that continues it, whose second signature fails with an IOException;
    ///   the third file that the next write starts with chain.reset; and a file made by hand whose signed first line is a
    ///   chain.reset with no prev.
    ///
    /// Steps:
    /// - Read the third file's first line; verify the three files; the first and the third; the third and the hand-made one.
    ///   Verify the three files while the test holds the second file open for itself alone; cut the second file to its first
    ///   line, verify the three, and put it back. Remove the hand-made file and the second file, give the first file the
    ///   second's name, and verify the folder.
    ///
    /// Expected:
    /// - The first line holds "prev":{"file":"(second file)","first":(the SHA-256 of the second file's second line, its first
    ///   signature, worked out here)}; the three are intact; then "The file follows '(second)', not '(first)': a file is
    ///   missing." as the only error (before V1d23 the two passed with only the reset's warning); then "The file follows
    ///   'nothing', not '(third)': a file is missing." as the only error; then only "The file cannot be read: …" on line 0
    ///   of the second file (the reset is not judged against the first file); then "The file follows another file named
    ///   '(second)': that file was replaced or cut." as the only error, for the cut file and for the renamed one (with the
    ///   name alone, the renamed file passed).
    /// </remarks>
    [Fact]
    public void Verify_FileRemovedBeforeAReset_IsFoundMissing()
    {
        string certificate = Certificate(_signing, KeyUses.Log);
        using (SignedLog log = OpenService(certificate, _signing))
        {
            Append(log, 1);
        }

        _time.Advance(TimeSpan.FromMinutes(1));
        bool fail = false;
        string Access(Func<ECDsa, string> use) => fail ? throw new IOException("The disk is full.") : use(_signing);
        using (SignedLog log = SignedLog.Open(_dir, "usage", "usage", Access, LogKeyMaterial.Certificates([certificate], "n1"), _time, sealEvery: 1))
        {
            Append(log, 1);
            fail = true;
            Assert.Throws<IOException>(() => Append(log, 1));
            fail = false;
            _time.Advance(TimeSpan.FromSeconds(1));
            Append(log, 1);
        }

        IReadOnlyList<string> files = SignedLog.FindFiles(_dir, "usage");
        Assert.Equal(3, files.Count);
        string first = Path.GetFileName(files[0]);
        string second = Path.GetFileName(files[1]);
        string third = Path.GetFileName(files[2]);
        Assert.Contains("\"type\":\"chain.reset\",", File.ReadLines(files[2]).First(), StringComparison.Ordinal);
        string secondFirstAuth = Base64UrlCodec.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadLines(files[1]).ElementAt(1) + "\n")));
        Assert.Contains(
            $"\"prev\":{{\"file\":\"{second}\",\"first\":\"{secondFirstAuth}\"}}",
            File.ReadLines(files[2]).First(),
            StringComparison.Ordinal
        );
        Assert.True(Verify2().Intact);

        LogIssue missing = Assert.Single(
            VerifiedLog.Of([files[0], files[2]], _trust, null, "usage").Issues,
            i => i.Severity == LogIssueSeverity.Error
        );
        Assert.Equal($"The file follows '{second}', not '{first}': a file is missing.", missing.Message);

        string bare = Path.Combine(_dir, "usage-20991231T000000000Z.jsonl");
        WriteFirstFile(
            bare,
            $$"""{"type":"chain.reset","ts":{{Now.ToUnixTimeSeconds()}},"stream":"usage","reason":"x","node":"n1","certificates":["{{certificate}}"]}"""
        );
        LogIssue nothing = Assert.Single(VerifiedLog.Of([files[2], bare], _trust, null, "usage").Issues, i => i.Severity == LogIssueSeverity.Error);
        Assert.Equal($"The file follows 'nothing', not '{third}': a file is missing.", nothing.Message);

        // A second file that cannot be read is reported as such, and the reset after it is not judged against the first file.
        using (new FileStream(files[1], FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            LogIssue unreadable = Assert.Single(
                VerifiedLog.Of([files[0], files[1], files[2]], _trust, null, "usage").Issues,
                i => i.Severity == LogIssueSeverity.Error
            );
            Assert.Equal((second, 0), (unreadable.File, unreadable.Line));
            Assert.StartsWith("The file cannot be read: ", unreadable.Message, StringComparison.Ordinal);
        }

        // A second file cut to its first line, before its first signature, no longer matches the reset's prev.first.
        string whole = File.ReadAllText(files[1]);
        File.WriteAllText(files[1], whole[..(whole.IndexOf('\n', StringComparison.Ordinal) + 1)]);
        LogIssue cut = Assert.Single(
            VerifiedLog.Of([files[0], files[1], files[2]], _trust, null, "usage").Issues,
            i => i.Severity == LogIssueSeverity.Error
        );
        Assert.Equal($"The file follows another file named '{second}': that file was replaced or cut.", cut.Message);
        File.WriteAllText(files[1], whole);

        // The owner's answer K1: removing the second file and giving the first its name passes the name alone.
        File.Delete(bare);
        File.Delete(files[1]);
        File.Move(files[0], files[1]);
        LogIssue replaced = Assert.Single(Verify2().Issues, i => i.Severity == LogIssueSeverity.Error);
        Assert.Equal($"The file follows another file named '{second}': that file was replaced or cut.", replaced.Message);
    }

    /// <summary>
    /// The verifier hands each event line to its caller when a signature covers it, and the lines no signature covers when
    /// their file ends; without a caller it counts the same lines. A file whose rest is not read after an error, or whose
    /// read fails in the middle, hands out only the lines its signatures covered (design §18.1, V1d23, finding F28).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A log of four events signed every two, then an unsigned event line appended.
    ///
    /// Steps:
    /// - Verify with and without a caller; append a line that is not an object and verify both ways; replace that line with
    ///   one longer than 1 MiB and verify both ways.
    ///
    /// Expected:
    /// - Five lines, the last unsigned, the count 5 both ways with the same issues; then four signed lines and the count 4,
    ///   with the error on line 10; then four signed lines and the count 4, with the error that the file cannot be read.
    /// </remarks>
    [Fact]
    public void Verify_EventLines_AreHandedOutOrCounted()
    {
        using (SignedLog log = Open(sealEvery: 2))
        {
            Append(log, 4);
        }

        string file = SignedLog.FindFiles(_dir, "usage")[0];
        string signed = File.ReadAllText(file);
        File.AppendAllText(file, "{\"type\":\"lease.acquired\",\"ts\":1,\"i\":4}\n");
        void Check(int count, bool lastSigned, string message)
        {
            VerifiedLog handed = Verify();
            LogReport counted = LogVerifier.Verify([file], _trust, "acme-cad", "usage");
            Assert.Equal(count, handed.Entries.Count);
            Assert.Equal(count, counted.EntryCount);
            Assert.Equal(lastSigned, handed.Entries[^1].IsSigned);
            Assert.Equal(handed.Issues, counted.Issues);
            Assert.Contains(handed.Issues, i => i.Message.StartsWith(message, StringComparison.Ordinal));
        }

        Check(5, lastSigned: false, "1 line(s) after the last signature are not signed");
        File.AppendAllText(file, "[]\n");
        Check(4, lastSigned: true, "The line is not a JSON object.");
        Assert.Contains(Verify().Issues, i => i.Line == 10 && i.Message == "The line is not a JSON object.");
        File.WriteAllText(file, signed + new string('x', LogVerifier.MaxLineLength + 1) + "\n");
        Check(4, lastSigned: true, "The file cannot be read: ");
    }

    /// <summary>
    /// When a key change cannot be written, a log that takes it anyway (the usage log) starts its next file with chain.reset
    /// and the new license; a log that does not (the borrow record) goes on with the old license.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A log of lic-srv whose key fails while the change is signed.
    ///
    /// Steps:
    /// - Change to a license lic-new of the same key with takeOnFailure true or false (throws); append; close; verify.
    ///
    /// Expected:
    /// - IOException; intact; the last file is reported with lic-new when the change is taken, and with lic-srv when not.
    /// </remarks>
    [Theory]
    [InlineData(true, "lic-new")]
    [InlineData(false, "lic-srv")]
    public void ChangeKey_FailedWrite_TakesTheNewLicenseOnlyWhenAsked(bool takeOnFailure, string expected)
    {
        bool fail = false;
        string Access(Func<ECDsa, string> use) => fail ? throw new IOException("The disk is full.") : use(_server);
        using (
            SignedLog log = SignedLog.Open(
                _dir,
                "usage",
                "usage",
                Access,
                LicenseFile(_server),
                KeyFiles.ExportPublicJwk(_server),
                _time,
                sealEvery: 1
            )
        )
        {
            fail = true;
            byte[] newLicense = LicenseFile(_server, "lic-new");
            Assert.Throws<IOException>(() => log.ChangeKey(newLicense, KeyFiles.ExportPublicJwk(_server), Access, takeOnFailure));
            fail = false;
            _time.Advance(TimeSpan.FromSeconds(1));
            Append(log, 1);
        }

        VerifiedLog report = Verify();
        Assert.True(report.Intact);
        Assert.Equal(expected, report.Files[^1].LicenseId);
    }

    /// <summary>
    /// A clock set back does not name a new file before the newest one, so the newest file stays the one with the latest
    /// records, and the chain links on.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A file written now.
    ///
    /// Steps:
    /// - Set the clock back one hour; open the log, append, close.
    ///
    /// Expected:
    /// - The new file sorts after the first; the two files verify with no issue.
    /// </remarks>
    [Fact]
    public void Open_ClockSetBack_NamesTheFileAfterTheNewest()
    {
        byte[] license = LicenseFile(_server);
        byte[] jwk = KeyFiles.ExportPublicJwk(_server);
        _time.Advance(TimeSpan.FromHours(1));
        using (SignedLog log = SignedLog.Open(_dir, "usage", "usage", Access(_server), license, jwk, _time))
        {
            Append(log, 1);
        }

        string first = SignedLog.FindFiles(_dir, "usage")[0];
        var earlier = new FakeTimeProvider(Now);
        string second;
        using (SignedLog log = SignedLog.Open(_dir, "usage", "usage", Access(_server), license, jwk, earlier))
        {
            second = log.FileName;
            Append(log, 1);
        }

        IReadOnlyList<string> files = SignedLog.FindFiles(_dir, "usage");
        Assert.Equal([Path.GetFileName(first), second], files.Select(Path.GetFileName));
        Assert.Empty(Verify(files).Issues);
    }

    /// <summary>
    /// A log of <c>elm-service</c> carries the certificates of its signing key instead of a server license, and verifies
    /// across a restart without a product (design §18.1, decision 0.95).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A certificate of the signing key for license, lease, and log; a log signed every 3 lines with it.
    ///
    /// Steps:
    /// - Append 4 events; dispose; reopen and append 1; read the first line; verify with no product, then with a product.
    ///
    /// Expected:
    /// - The first line has "certificates" with the certificate and no "license" or "key"; no issue either way; 5 signed
    ///   entries; the files' key is the signing key with no license ID.
    /// </remarks>
    [Fact]
    public void ServiceLog_Certificates_VerifyWithoutProduct()
    {
        string certificate = Certificate(_signing, KeyUses.License | KeyUses.Lease | KeyUses.Log);
        using (SignedLog log = OpenService(certificate, _signing, sealEvery: 3))
        {
            Append(log, 4);
        }

        using (SignedLog log = OpenService(certificate, _signing))
        {
            Append(log, 1);
        }

        IReadOnlyList<string> files = SignedLog.FindFiles(_dir, "usage");
        using (var first = System.Text.Json.JsonDocument.Parse(File.ReadLines(files[0]).First()))
        {
            Assert.Equal(
                (certificate, false, false),
                (
                    first.RootElement.GetProperty("certificates").EnumerateArray().Single().GetString(),
                    first.RootElement.TryGetProperty("license", out _),
                    first.RootElement.TryGetProperty("key", out _)
                )
            );
        }

        foreach (string? product in new[] { null, "acme-cad" })
        {
            VerifiedLog report = VerifiedLog.Of(files, _trust, product, "usage");
            Assert.Empty(report.Issues);
            Assert.Equal(5, report.Entries.Count(e => e.IsSigned));
            Assert.All(report.Files, f => Assert.Equal((null, KeyFiles.KeyId(_signing)), (f.LicenseId, f.KeyId)));
        }
    }

    /// <summary>
    /// A service log is refused when no certificate on its first line is certified for log by the trust store at the line's
    /// time, or when a signature is made with a key the certificates do not name.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Certificates of the signing key: for license and lease only; for log from another root; for log from tomorrow; for
    ///   log (good). A log signed with another key under the good certificate.
    ///
    /// Steps:
    /// - Write a log with each certificate and verify it alone with no product.
    ///
    /// Expected:
    /// - "No certificate on the line is certified for log by the trust store at the line's time." three times; "The
    ///   signature is not made with a key certified for log." for the other key.
    /// </remarks>
    [Fact]
    public void ServiceLog_UncertifiedKey_IsRefused()
    {
        using ECDsa otherRoot = KeyFiles.CreateKey();
        const string NoLog = "No certificate on the line is certified for log by the trust store at the line's time.";
        (string Certificate, ECDsa Signer, string Message)[] cases =
        [
            (Certificate(_signing, KeyUses.License | KeyUses.Lease), _signing, NoLog),
            (CertificateIssuer.Issue(otherRoot, _signing, KeyUses.Log, Now.AddDays(-1), TimeSpan.FromDays(365), _time), _signing, NoLog),
            (Certificate(_signing, KeyUses.Log, Now.AddDays(1)), _signing, NoLog),
            (Certificate(_signing, KeyUses.Log), _server, "The signature is not made with a key certified for log."),
        ];
        foreach ((string certificate, ECDsa signer, string message) in cases)
        {
            foreach (string file in Directory.EnumerateFiles(_dir))
            {
                File.Delete(file);
            }

            using (SignedLog log = OpenService(certificate, signer))
            {
                Append(log, 2);
            }

            LogIssue error = Assert.Single(VerifiedLog.Of(SignedLog.FindFiles(_dir, "usage"), _trust, null, "usage").Issues);
            Assert.Equal((LogIssueSeverity.Error, message), (error.Severity, error.Message));
        }
    }

    /// <summary>
    /// A service log whose next file is signed by another key certified for log is a warning (a replaced signing key); a
    /// service file after a server's file is an error; a server's log verified without a product is an error.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two signing keys, each certified for log.
    ///
    /// Steps:
    /// - Write a file with the first key, restart with the second, verify with no product. In another folder write a
    ///   server's file, then a service file, and verify with the product. Verify the server's file alone with no product.
    ///
    /// Expected:
    /// - One warning, "The file is signed by another key certified for log than …: the signing key was replaced."; the error
    ///   "… a server's and the service's logs are mixed."; the error "The line carries a server license, and no product was
    ///   given to verify it.".
    /// </remarks>
    [Fact]
    public void ServiceLog_KeyReplacedOrMixed_IsReported()
    {
        using ECDsa replacement = KeyFiles.CreateKey();
        using (SignedLog log = OpenService(Certificate(_signing, KeyUses.Log), _signing))
        {
            Append(log, 1);
        }

        using (SignedLog log = OpenService(Certificate(replacement, KeyUses.Log), replacement))
        {
            Append(log, 1);
        }

        LogIssue replaced = Assert.Single(VerifiedLog.Of(SignedLog.FindFiles(_dir, "usage"), _trust, null, "usage").Issues);
        Assert.Equal(
            (
                LogIssueSeverity.Warning,
                $"The file is signed by another key certified for log than {KeyFiles.KeyId(_signing)}: the signing key was replaced."
            ),
            (replaced.Severity, replaced.Message)
        );

        foreach (string file in Directory.EnumerateFiles(_dir))
        {
            File.Delete(file);
        }

        using (SignedLog log = Open())
        {
            Append(log, 1);
        }

        string serverFile = SignedLog.FindFiles(_dir, "usage").Single();
        Assert.Equal(
            "The line carries a server license, and no product was given to verify it.",
            Assert.Single(VerifiedLog.Of([serverFile], _trust, null, "usage").Issues).Message
        );
        using (SignedLog log = OpenService(Certificate(_signing, KeyUses.Log), _signing))
        {
            Append(log, 1);
        }

        LogIssue mixed = Assert.Single(Verify().Issues);
        Assert.Equal(LogIssueSeverity.Error, mixed.Severity);
        Assert.EndsWith("a server's and the service's logs are mixed.", mixed.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The certificates of a service log are one to four, none empty, with a node that is not empty (design §18.1: every
    /// first line of elm-service names its node).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Make the material with no certificate, five, and an empty one; with an empty node and with no node; with four.
    ///
    /// Expected:
    /// - ArgumentException four times and ArgumentNullException once; the last is made.
    /// </remarks>
    [Fact]
    public void LogKeyMaterial_Certificates_AreChecked()
    {
        Assert.Throws<ArgumentException>(() => LogKeyMaterial.Certificates([], "n1"));
        Assert.Throws<ArgumentException>(() => LogKeyMaterial.Certificates(["a", "b", "c", "d", "e"], "n1"));
        Assert.Throws<ArgumentException>(() => LogKeyMaterial.Certificates(["a", string.Empty], "n1"));
        Assert.Throws<ArgumentException>(() => LogKeyMaterial.Certificates(["a"], string.Empty));
        Assert.Throws<ArgumentNullException>(() => LogKeyMaterial.Certificates(["a"], null!));
        Assert.NotNull(LogKeyMaterial.Certificates(["a", "b", "c", "d"], "n1"));
    }

    /// <summary>
    /// The first signature of a service's file fixes the key for the rest of it, and a file is compared with the previous
    /// one by the key that signed it, not by the keys its certificates name (decision 0.95).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two signing keys A and B, each certified for log.
    ///
    /// Steps:
    /// - Write a file whose certificates name A and B, signed every line, A for the first signature and B after; verify. In
    ///   a new folder write a file signed by A, then one whose certificates name A and B, signed by B; verify.
    ///
    /// Expected:
    /// - The error "The signature is made with another key than the earlier signatures of the file."; then one warning,
    ///   "The file is signed by another key certified for log than A: the signing key was replaced.".
    /// </remarks>
    [Fact]
    public void ServiceLog_KeysAreFixedPerFile()
    {
        using ECDsa other = KeyFiles.CreateKey();
        string a = Certificate(_signing, KeyUses.Log);
        string b = Certificate(other, KeyUses.Log);
        int signatures = 0;
        string Switching(Func<ECDsa, string> use) => use(signatures++ < 1 ? _signing : other);
        using (SignedLog log = SignedLog.Open(_dir, "usage", "usage", Switching, LogKeyMaterial.Certificates([a, b], "n1"), _time, sealEvery: 1))
        {
            Append(log, 2);
        }

        Assert.Contains(
            Verify2().Issues,
            i =>
                i.Severity == LogIssueSeverity.Error && i.Message == "The signature is made with another key than the earlier signatures of the file."
        );

        ClearFolder();
        using (SignedLog log = OpenService(a, _signing))
        {
            Append(log, 1);
        }

        using (SignedLog log = SignedLog.Open(_dir, "usage", "usage", Access(other), LogKeyMaterial.Certificates([a, b], "n1"), _time))
        {
            Append(log, 1);
        }

        LogIssue replaced = Assert.Single(Verify2().Issues);
        Assert.Equal(
            (
                LogIssueSeverity.Warning,
                $"The file is signed by another key certified for log than {KeyFiles.KeyId(_signing)}: the signing key was replaced."
            ),
            (replaced.Severity, replaced.Message)
        );
    }

    /// <summary>
    /// A service's file changes no key, and a server's file does not change into the service's form (design §18.1, 0.95).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service's file and a server's file, each with a signed key.changed line appended by hand: the first carrying
    ///   certificates, the second carrying certificates too.
    ///
    /// Steps:
    /// - Verify each alone (the server's with its product).
    ///
    /// Expected:
    /// - "A log of elm-service changes no key while it runs, and this file has a key change."; "A key change turns a
    ///   server's log into a log of elm-service.".
    /// </remarks>
    [Fact]
    public void ServiceLog_KeyChange_IsRefused()
    {
        string certificate = Certificate(_signing, KeyUses.Log);
        using (SignedLog log = OpenService(certificate, _signing))
        {
            Append(log, 1);
        }

        string file = SignedLog.FindFiles(_dir, "usage").Single();
        AppendSigned(file, _signing, $$"""{"type":"key.changed","ts":{{Now.ToUnixTimeSeconds()}},"certificates":["{{certificate}}"]}""");
        Assert.Equal(
            "A log of elm-service changes no key while it runs, and this file has a key change.",
            Assert.Single(VerifiedLog.Of([file], _trust, null, "usage").Issues).Message
        );

        ClearFolder();
        using (SignedLog log = Open())
        {
            Append(log, 1);
        }

        file = SignedLog.FindFiles(_dir, "usage").Single();
        AppendSigned(file, _server, $$"""{"type":"key.changed","ts":{{Now.ToUnixTimeSeconds()}},"certificates":["{{certificate}}"]}""");
        Assert.Equal("A key change turns a server's log into a log of elm-service.", Assert.Single(Verify([file]).Issues).Message);
    }

    // Writes a file whose first line is the given JSON, signed as SignedLog signs the first line of a log's first file: a
    // signature numbered 1 after no earlier one, over that line alone.
    private void WriteFirstFile(string path, string firstLine)
    {
        byte[] line = Encoding.UTF8.GetBytes(firstLine + "\n");
        var payload = new LogPayload
        {
            Ver = 1,
            Iat = Now.ToUnixTimeSeconds(),
            Jti = JwsWriter.NewTokenId(),
            Stream = "usage",
            N = 1,
            Prev = string.Empty,
            Digest = Base64UrlCodec.Encode(SHA256.HashData(line)),
            Lines = 1,
        };
        string signature = JwsWriter.Sign(_signing, TokenTypes.Log, payload, LicensingJsonContext.Default.LogPayload);
        File.WriteAllText(path, firstLine + "\n" + $$"""{"type":"auth","ts":{{Now.ToUnixTimeSeconds()}},"sig":"{{signature}}"}""" + "\n");
    }

    // Appends a line and a signature over it that continues the file's chain, as SignedLog would write them.
    private static void AppendSigned(string path, ECDsa key, string json)
    {
        string[] lines = File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string lastAuth = lines.Last(l => l.StartsWith("{\"type\":\"auth\"", StringComparison.Ordinal));
        byte[] line = Encoding.UTF8.GetBytes(json + "\n");
        var payload = new LogPayload
        {
            Ver = 1,
            Iat = Now.ToUnixTimeSeconds(),
            Jti = JwsWriter.NewTokenId(),
            Stream = "usage",
            N = ReadPayload(lastAuth).N + 1,
            Prev = SignedLog.Hash(Encoding.UTF8.GetBytes(lastAuth + "\n")),
            Digest = Base64UrlCodec.Encode(SHA256.HashData(line)),
            Lines = 1,
        };
        string signature = JwsWriter.Sign(key, TokenTypes.Log, payload, LicensingJsonContext.Default.LogPayload);
        File.AppendAllText(path, json + "\n" + $$"""{"type":"auth","ts":{{Now.ToUnixTimeSeconds()}},"sig":"{{signature}}"}""" + "\n");
    }

    private VerifiedLog Verify2() => VerifiedLog.Of(SignedLog.FindFiles(_dir, "usage"), _trust, null, "usage");

    private void ClearFolder()
    {
        foreach (string file in Directory.EnumerateFiles(_dir))
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// In a service's log, a missing file, a file cut after its last signature, and a removed line are errors, as in a
    /// server's log (design §18.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service's log of three files (two restarts), signed every 2 lines.
    ///
    /// Steps:
    /// - Verify the first and the third; remove the last line of the first file and verify all; restore it, remove an event
    ///   line from the second, and verify all.
    ///
    /// Expected:
    /// - An error that a file is missing; an error that the link does not match; an error that lines were changed, added, or
    ///   removed.
    /// </remarks>
    [Fact]
    public void ServiceLog_RemovedOrCut_IsFound()
    {
        string certificate = Certificate(_signing, KeyUses.Log);
        for (int file = 0; file < 3; file++)
        {
            using SignedLog log = OpenService(certificate, _signing, sealEvery: 2);
            Append(log, 4);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        IReadOnlyList<string> files = SignedLog.FindFiles(_dir, "usage");
        Assert.Contains(
            VerifiedLog.Of([files[0], files[2]], _trust, null, "usage").Issues,
            i => i.Message.Contains("a file is missing", StringComparison.Ordinal)
        );

        string first = File.ReadAllText(files[0]);
        string[] firstLines = first.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        File.WriteAllText(files[0], string.Join('\n', firstLines[..^1]) + "\n");
        Assert.Contains(Verify2().Issues, i => i.Message.Contains("does not match its last signature", StringComparison.Ordinal));

        File.WriteAllText(files[0], first);
        string[] secondLines = File.ReadAllText(files[1]).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        File.WriteAllText(files[1], string.Join('\n', secondLines.Where((_, i) => i != 2)) + "\n");
        Assert.Contains(Verify2().Issues, i => i.Message == "The lines before the signature were changed, added, or removed.");
    }

    /// <summary>
    /// A certificate counts for a service's file only when it is valid at the time of the file's first line, its bounds
    /// included, and its key is not revoked; a server's file after a service's file is an error (design §18.1, 0.95).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Certificates for log from now, ending now, from a second after now, ending a second before now.
    ///
    /// Steps:
    /// - Write a service's file with each and verify it; verify the good one with its key revoked; write a service's file and
    ///   then a server's file and verify both with the product.
    ///
    /// Expected:
    /// - The first two verify; the next two and the revoked key give "No certificate on the line is certified for log by the
    ///   trust store at the line's time."; the last ends with "a server's and the service's logs are mixed.".
    /// </remarks>
    [Fact]
    public void ServiceLog_CertificateBounds_AndRevocation()
    {
        const string NoLog = "No certificate on the line is certified for log by the trust store at the line's time.";
        (DateTimeOffset NotBefore, TimeSpan Validity, string? Problem)[] cases =
        [
            (Now, TimeSpan.FromDays(1), null),
            (Now.AddDays(-1), TimeSpan.FromDays(1), null),
            (Now.AddSeconds(1), TimeSpan.FromDays(1), NoLog),
            (Now.AddDays(-1).AddSeconds(-1), TimeSpan.FromDays(1), NoLog),
        ];
        foreach ((DateTimeOffset notBefore, TimeSpan validity, string? problem) in cases)
        {
            ClearFolder();
            string certificate = CertificateIssuer.Issue(_root, _signing, KeyUses.Log, notBefore, validity, _time);
            using (SignedLog log = OpenService(certificate, _signing))
            {
                Append(log, 1);
            }

            IReadOnlyList<LogIssue> issues = Verify2().Issues;
            if (problem is null)
            {
                Assert.Empty(issues);
            }
            else
            {
                Assert.Equal(problem, Assert.Single(issues).Message);
            }
        }

        ClearFolder();
        using (SignedLog log = OpenService(Certificate(_signing, KeyUses.Log), _signing))
        {
            Append(log, 1);
        }

        TrustStore revoked = TrustStore.Create([KeyFiles.ExportPublic(_root)], [KeyFiles.KeyId(_signing)]);
        Assert.Equal(NoLog, Assert.Single(VerifiedLog.Of(SignedLog.FindFiles(_dir, "usage"), revoked, null, "usage").Issues).Message);

        _time.Advance(TimeSpan.FromSeconds(1));
        using (SignedLog log = Open())
        {
            Append(log, 1);
        }

        LogIssue mixed = Assert.Single(Verify().Issues);
        Assert.EndsWith("a server's and the service's logs are mixed.", mixed.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A service's files name their node on the signed first line: a chain is of one node, the node asked for must be the
    /// files', and a first line with certificates and no node is an error whatever was asked (design §18.1, decision 0.95,
    /// V1d23).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A certificate for log; files written as node a, then as node b; a file made as SignedLog writes a first file, but
    ///   with no node on its signed first line.
    ///
    /// Steps:
    /// - Verify a's file as node a; a's file then b's; a's file as node b; a's file alone; the file with no node as node a,
    ///   alone with no node asked, and after a's file.
    ///
    /// Expected:
    /// - No issue; "The file is of node 'b', not of node 'a'."; "The file is of node 'a', not of node 'b'."; no issue; then
    ///   "The file of elm-service names no node on its first line." each time, with the missing-file error after a's file
    ///   (the hand-made file links to nothing).
    /// </remarks>
    [Fact]
    public void ServiceLog_Node_IsChecked()
    {
        string certificate = Certificate(_signing, KeyUses.Log);
        string Write(string node)
        {
            using (SignedLog log = SignedLog.Open(_dir, "usage", "usage", Access(_signing), LogKeyMaterial.Certificates([certificate], node), _time))
            {
                Append(log, 1);
            }

            _time.Advance(TimeSpan.FromSeconds(1));
            return SignedLog.FindFiles(_dir, "usage")[^1];
        }

        string a = Write("a");
        string b = Write("b");
        Assert.Empty(VerifiedLog.Of([a], _trust, null, "usage", "a").Issues);
        Assert.Equal("The file is of node 'b', not of node 'a'.", Assert.Single(VerifiedLog.Of([a, b], _trust, null, "usage").Issues).Message);
        Assert.Equal("The file is of node 'a', not of node 'b'.", Assert.Single(VerifiedLog.Of([a], _trust, null, "usage", "b").Issues).Message);
        Assert.Empty(VerifiedLog.Of([a], _trust, null, "usage").Issues);

        string none = Path.Combine(_dir, "usage-20261001T120005000Z.jsonl");
        WriteFirstFile(none, $$"""{"type":"chain.start","ts":{{Now.ToUnixTimeSeconds()}},"stream":"usage","certificates":["{{certificate}}"]}""");
        const string NoNode = "The file of elm-service names no node on its first line.";
        Assert.Equal(NoNode, Assert.Single(VerifiedLog.Of([none], _trust, null, "usage", "a").Issues).Message);
        Assert.Equal(NoNode, Assert.Single(VerifiedLog.Of([none], _trust, null, "usage").Issues).Message);
        Assert.Equal(
            [$"The file follows 'nothing', not '{Path.GetFileName(a)}': a file is missing.", NoNode],
            VerifiedLog.Of([a, none], _trust, null, "usage").Issues.Select(i => i.Message)
        );
    }

    /// <summary>
    /// A last line of the previous file that is JSON but not a signature the writer can read is passed over, and the chain
    /// goes on from the signature before it (design §18.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A closed file of the log with one line appended: [], "x", 123, null, true, an object whose type is a lone surrogate
    ///   escape, a signature line whose sig is one, an object with a member name that is one, an object whose type holds
    ///   bytes that are not UTF-8, or a signature line whose payload holds a lone surrogate escape.
    ///
    /// Steps:
    /// - A minute later, open the log and close it; read the first line and the first signature of the new file.
    ///
    /// Expected:
    /// - No exception and no ResetReason. The first line is chain.start, and its prev names the previous file and the hash
    ///   of that file's last signature; the first signature has the number after that signature's.
    /// </remarks>
    [Theory]
    [InlineData("array")]
    [InlineData("text")]
    [InlineData("number")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("type-surrogate")]
    [InlineData("sig-surrogate")]
    [InlineData("name-surrogate")]
    [InlineData("type-not-utf8")]
    [InlineData("payload-surrogate")]
    public void Open_LastLineIsNotASignature_ContinuesFromTheSignatureBefore(string shape)
    {
        using (SignedLog log = Open())
        {
            Append(log, 2);
        }

        string previous = SignedLog.FindFiles(_dir, "usage").Single();
        string lastAuth = File.ReadAllLines(previous).Last(l => l.StartsWith("{\"type\":\"auth\"", StringComparison.Ordinal));
        AppendLine(previous, NotASignature(shape));

        _time.Advance(TimeSpan.FromMinutes(1));
        string? reason;
        using (SignedLog log = Open())
        {
            reason = log.ResetReason;
        }

        Assert.Null(reason);
        string[] lines = File.ReadAllLines(SignedLog.FindFiles(_dir, "usage")[^1]);
        using var first = System.Text.Json.JsonDocument.Parse(lines[0]);
        Assert.Equal(
            ("chain.start", Path.GetFileName(previous), SignedLog.Hash(Encoding.UTF8.GetBytes(lastAuth + "\n"))),
            (
                first.RootElement.GetProperty("type").GetString(),
                first.RootElement.GetProperty("prev").GetProperty("file").GetString(),
                first.RootElement.GetProperty("prev").GetProperty("auth").GetString()
            )
        );
        Assert.Equal(ReadPayload(lastAuth).N + 1, ReadPayload(lines[1]).N);
    }

    /// <summary>
    /// When no line of the previous file is a signature, lines that are not objects or whose text cannot be read give the
    /// same chain.reset reason as any file without a signature (design §18.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A previous file of the log that holds only [], or only an object whose type is a lone surrogate escape.
    ///
    /// Steps:
    /// - Open the log.
    ///
    /// Expected:
    /// - ResetReason is "no signature was found at the end of the previous file usage-20260930T000000000Z.jsonl".
    /// </remarks>
    [Theory]
    [InlineData("array")]
    [InlineData("type-surrogate")]
    public void Open_PreviousHoldsOnlyLinesThatAreNotSignatures_ResetsTheChain(string shape)
    {
        string previous = Path.Combine(_dir, "usage-20260930T000000000Z.jsonl");
        File.WriteAllBytes(previous, []);
        AppendLine(previous, NotASignature(shape));

        using SignedLog log = Open();
        Assert.Equal("no signature was found at the end of the previous file usage-20260930T000000000Z.jsonl", log.ResetReason);
    }

    /// <summary>
    /// The reason of a chain.reset names the previous file by its name only: the usage log is read outside the machine, and
    /// the folder's path is not written into it (design §18.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A closed file of the log, held open by the test for itself alone, so that the log cannot read it.
    ///
    /// Steps:
    /// - A minute later, open the log; read the first line of the new file.
    ///
    /// Expected:
    /// - ResetReason is exactly "the previous file {name} cannot be read"; the first line is chain.reset with prev.file the
    ///   previous file's name and no prev.first, the file not being readable, and that reason (design §18.1, V1d23), and does
    ///   not hold the folder's name. Once the file is free, the two files verify intact: a reset without prev.first is
    ///   checked by the name alone.
    /// </remarks>
    [Fact]
    public void Open_PreviousFileCannotBeRead_ResetReasonNamesOnlyTheFile()
    {
        using (SignedLog log = Open())
        {
            Append(log, 1);
        }

        string previous = SignedLog.FindFiles(_dir, "usage").Single();
        _time.Advance(TimeSpan.FromMinutes(1));
        string? reason;
        using (new FileStream(previous, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (SignedLog log = Open())
        {
            reason = log.ResetReason;
        }

        Assert.Equal($"the previous file {Path.GetFileName(previous)} cannot be read", reason);
        string first = File.ReadLines(SignedLog.FindFiles(_dir, "usage")[^1]).First();
        Assert.Contains(
            $"\"type\":\"chain.reset\",\"ts\":{_time.GetUtcNow().ToUnixTimeSeconds()},\"stream\":\"usage\",\"prev\":{{\"file\":\"{Path.GetFileName(previous)}\"}},\"reason\":\"{reason}\"",
            first,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(Path.GetFileName(_dir), first, StringComparison.Ordinal);
        Assert.True(Verify().Intact);
    }

    /// <summary>
    /// A file whose name has the form of a log file's name but no real date and time is not a file of the log: it is not
    /// listed, it is reported when a file starts, and it decides neither the previous file nor the next name (design §18.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A closed file of the log written now, and an empty file named with month 13, February 31, hour 25, 99999999 as the
    ///   date, year 0000, full-width digits, or Arabic-Indic digits.
    ///
    /// Steps:
    /// - List the files; a minute later open the log with a callback for such names, append, and close; list and verify.
    ///
    /// Expected:
    /// - Only the real file is listed. The callback gets the name once; no ResetReason; the new file is named with the
    ///   clock's time; the two files verify with no issue.
    /// </remarks>
    [Theory]
    [InlineData("usage-20261301T000000000Z.jsonl")]
    [InlineData("usage-20270231T000000000Z.jsonl")]
    [InlineData("usage-20261001T250000000Z.jsonl")]
    [InlineData("usage-99999999T000000000Z.jsonl")]
    [InlineData("usage-00000101T000000000Z.jsonl")]
    [InlineData("usage-２０２６1002T000000000Z.jsonl")]
    [InlineData("usage-٢٠٢٦1002T000000000Z.jsonl")]
    public void FindFiles_NameWithoutARealTime_IsNotAFileOfTheLog(string name)
    {
        using (SignedLog log = Open())
        {
            Append(log, 1);
        }

        string real = SignedLog.FindFiles(_dir, "usage").Single();
        File.WriteAllBytes(Path.Combine(_dir, name), []);
        Assert.Equal([real], SignedLog.FindFiles(_dir, "usage"));

        _time.Advance(TimeSpan.FromMinutes(1));
        var reported = new List<string>();
        string? reason;
        string opened;
        using (SignedLog log = Open(notALogFile: reported.Add))
        {
            reason = log.ResetReason;
            opened = log.FileName;
            Append(log, 1);
        }

        Assert.Equal([name], reported);
        Assert.Null(reason);
        Assert.Equal("usage-20261001T120100000Z.jsonl", opened);
        IReadOnlyList<string> files = SignedLog.FindFiles(_dir, "usage");
        Assert.Equal([Path.GetFileName(real), opened], files.Select(Path.GetFileName));
        Assert.Empty(Verify(files).Issues);
    }

    /// <summary>
    /// A running log starts its file of the new UTC day while the folder holds a file whose name has no real date and time,
    /// and reports that file again (design §18.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An open log with one event; then an empty file usage-20261301T000000000Z.jsonl put into the folder.
    ///
    /// Steps:
    /// - Move the clock one day; append an event; close; list and verify.
    ///
    /// Expected:
    /// - Nothing is reported at the start, and the name once at the new day. Two files, the second named with the clock's
    ///   time, which verify with no issue.
    /// </remarks>
    [Fact]
    public void Append_NewDayBesideANameWithoutARealTime_StartsTheNextFile()
    {
        var reported = new List<string>();
        using (SignedLog log = Open(notALogFile: reported.Add))
        {
            Append(log, 1);
            File.WriteAllBytes(Path.Combine(_dir, "usage-20261301T000000000Z.jsonl"), []);
            Assert.Empty(reported);

            _time.Advance(TimeSpan.FromDays(1));
            Append(log, 1);
        }

        Assert.Equal(["usage-20261301T000000000Z.jsonl"], reported);
        IReadOnlyList<string> files = SignedLog.FindFiles(_dir, "usage");
        Assert.Equal(["usage-20261001T120000000Z.jsonl", "usage-20261002T120000000Z.jsonl"], files.Select(Path.GetFileName));
        Assert.Empty(Verify(files).Issues);
    }

    /// <summary>
    /// The last name a file can have is 99991231T235959999Z. Beside a newest file of one millisecond before it the log still
    /// opens; beside a newest file of that name it cannot be written, which is an IOException and not another exception.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A folder with a file usage-99991231T235959998Z.jsonl, or usage-99991231T235959999Z.jsonl, holding "{}".
    ///
    /// Steps:
    /// - Open the log.
    ///
    /// Expected:
    /// - Beside 998, the log opens a file usage-99991231T235959999Z.jsonl. Beside 999, IOException "No file name comes
    ///   after usage-99991231T235959999Z.jsonl, the newest file of the log.".
    /// </remarks>
    [Fact]
    public void Open_NewestFileHasTheLastName_ThrowsIOException()
    {
        File.WriteAllText(Path.Combine(_dir, "usage-99991231T235959998Z.jsonl"), "{}\n");
        using (SignedLog log = Open())
        {
            Assert.Equal("usage-99991231T235959999Z.jsonl", log.FileName);
        }

        IOException refused = Assert.Throws<IOException>(() => Open());
        Assert.Equal("No file name comes after usage-99991231T235959999Z.jsonl, the newest file of the log.", refused.Message);
    }

    /// <summary>
    /// A running log that reaches a new UTC day beside a file of the last name fails that write and the next ones as writes
    /// that cannot be made, with IOException.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An open log with one event; then a file usage-99991231T235959999Z.jsonl put into the folder.
    ///
    /// Steps:
    /// - Move the clock one day; append twice.
    ///
    /// Expected:
    /// - Each append throws IOException whose message ends with "No file name comes after
    ///   usage-99991231T235959999Z.jsonl, the newest file of the log.".
    /// </remarks>
    [Fact]
    public void Append_NewDayBesideTheLastName_ThrowsIOException()
    {
        using SignedLog log = Open();
        Append(log, 1);
        File.WriteAllText(Path.Combine(_dir, "usage-99991231T235959999Z.jsonl"), "{}\n");
        _time.Advance(TimeSpan.FromDays(1));

        for (int attempt = 0; attempt < 2; attempt++)
        {
            IOException refused = Assert.Throws<IOException>(() => Append(log, 1));
            Assert.EndsWith(
                "No file name comes after usage-99991231T235959999Z.jsonl, the newest file of the log.",
                refused.Message,
                StringComparison.Ordinal
            );
        }
    }

    /// <summary>
    /// A line that is JSON but not an object is an error of the verification, wherever it stands (design §18.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A closed log of one event (four lines), with [], "x", 123, null, or true appended as a fifth line; and a file
    ///   whose only line is that value.
    ///
    /// Steps:
    /// - Verify each file.
    ///
    /// Expected:
    /// - One error each, "The line is not a JSON object.", at line 5 and at line 1.
    /// </remarks>
    [Theory]
    [InlineData("array")]
    [InlineData("text")]
    [InlineData("number")]
    [InlineData("null")]
    [InlineData("true")]
    public void Verify_LineThatIsNotAnObject_IsAnError(string shape)
    {
        using (SignedLog log = Open())
        {
            Append(log, 1);
        }

        string file = SignedLog.FindFiles(_dir, "usage").Single();
        AppendLine(file, NotASignature(shape));
        LogIssue appended = Assert.Single(Verify([file]).Issues);
        Assert.Equal((5, LogIssueSeverity.Error, "The line is not a JSON object."), (appended.Line, appended.Severity, appended.Message));

        string alone = Path.Combine(_dir, "usage-20261002T000000000Z.jsonl");
        File.WriteAllBytes(alone, []);
        AppendLine(alone, NotASignature(shape));
        LogIssue first = Assert.Single(Verify([alone]).Issues);
        Assert.Equal((1, LogIssueSeverity.Error, "The line is not a JSON object."), (first.Line, first.Severity, first.Message));
    }

    /// <summary>
    /// A line whose text the verification reads and cannot read as text (a lone surrogate escape, bytes that are not UTF-8)
    /// is an error, not an exception: the type of any line, and every string and member name of a chain line, a signature
    /// line, and a key change (design §18.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A closed log of one event (four lines) with one line appended: an event whose type is a lone surrogate escape; an
    ///   event with such a member name after its type; an event whose type holds bytes that are not UTF-8; a signature line
    ///   whose sig is a lone surrogate escape; a key.changed line with such a member name before its type. Or a file whose
    ///   only line is a chain.start whose stream is a lone surrogate escape, or a chain.reset of stream usage whose reason
    ///   is one.
    ///
    /// Steps:
    /// - Verify the file.
    ///
    /// Expected:
    /// - One error, "The line holds a string that is not valid Unicode.", at the appended line (5) or at line 1.
    /// </remarks>
    [Theory]
    [InlineData("type-surrogate", 5)]
    [InlineData("name-after-type", 5)]
    [InlineData("type-not-utf8", 5)]
    [InlineData("sig-surrogate", 5)]
    [InlineData("key-changed-name", 5)]
    [InlineData("chain-start-stream", 1)]
    [InlineData("chain-reset-reason", 1)]
    public void Verify_TextThatIsNotUnicode_IsAnError(string shape, int line)
    {
        string file;
        if (line == 1)
        {
            file = Path.Combine(_dir, "usage-20261002T000000000Z.jsonl");
            File.WriteAllBytes(file, []);
        }
        else
        {
            using (SignedLog log = Open())
            {
                Append(log, 1);
            }

            file = SignedLog.FindFiles(_dir, "usage").Single();
        }

        AppendLine(file, NotASignature(shape));

        LogIssue issue = Assert.Single(Verify([file]).Issues);
        Assert.Equal(
            (line, LogIssueSeverity.Error, "The line holds a string that is not valid Unicode."),
            (issue.Line, issue.Severity, issue.Message)
        );
    }

    /// <summary>
    /// A signature of the file's own key whose payload holds a lone surrogate escape is an error of the verification, not an
    /// exception: whoever holds a server's key can write one.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A closed log of one event (four lines), with a signature line appended that the server's key signed over a payload
    ///   whose jti is a lone surrogate escape.
    ///
    /// Steps:
    /// - Verify.
    ///
    /// Expected:
    /// - One error at line 5, "The signature's payload is not valid.".
    /// </remarks>
    [Fact]
    public void Verify_SignaturePayloadThatIsNotUnicode_IsAnError()
    {
        using (SignedLog log = Open())
        {
            Append(log, 1);
        }

        string file = SignedLog.FindFiles(_dir, "usage").Single();
        AppendLine(file, NotASignature("payload-surrogate"));

        LogIssue issue = Assert.Single(Verify([file]).Issues);
        Assert.Equal((5, LogIssueSeverity.Error, "The signature's payload is not valid."), (issue.Line, issue.Severity, issue.Message));
    }

    /// <summary>
    /// A file whose name has no real date and time is reported at the start and once in a UTC day, not at each write that
    /// tries again after a failed write: the report would otherwise repeat with every request while the disk is full.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A folder with an empty file usage-20261301T000000000Z.jsonl; a log signed every line, opened with a callback for
    ///   such names, whose key can be made to fail with an IOException.
    ///
    /// Steps:
    /// - Make the key fail and append three times (the second and the third start a new file, which fails too). Move the
    ///   clock one day, let the key work, and append.
    ///
    /// Expected:
    /// - Each of the three appends throws IOException, and the name stays reported once, from the start. After the new day
    ///   it is reported a second time.
    /// </remarks>
    [Fact]
    public void Append_AgainAfterAFailedWrite_DoesNotReportTheNameAgain()
    {
        File.WriteAllBytes(Path.Combine(_dir, "usage-20261301T000000000Z.jsonl"), []);
        var reported = new List<string>();
        bool fail = false;
        string Access(Func<ECDsa, string> use) => fail ? throw new IOException("The disk is full.") : use(_server);
        using SignedLog log = SignedLog.Open(
            _dir,
            "usage",
            "usage",
            Access,
            LicenseFile(_server),
            KeyFiles.ExportPublicJwk(_server),
            _time,
            sealEvery: 1,
            notALogFile: reported.Add
        );
        Assert.Single(reported);

        fail = true;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            Assert.Throws<IOException>(() => Append(log, 1));
        }

        Assert.Single(reported);

        fail = false;
        _time.Advance(TimeSpan.FromDays(1));
        Append(log, 1);
        Assert.Equal(["usage-20261301T000000000Z.jsonl", "usage-20261301T000000000Z.jsonl"], reported);
    }

    // Appends bytes and a line feed to a file.
    private static void AppendLine(string path, byte[] line)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write);
        stream.Write(line);
        stream.WriteByte((byte)'\n');
    }

    // A line that is JSON and that the writer cannot continue from: not an object, or with text that cannot be read. The
    // lone surrogate is written as its escape, the six characters \ud800.
    private byte[] NotASignature(string shape) =>
        shape switch
        {
            "array" => "[]"u8.ToArray(),
            "text" => "\"x\""u8.ToArray(),
            "number" => "123"u8.ToArray(),
            "null" => "null"u8.ToArray(),
            "true" => "true"u8.ToArray(),
            "type-surrogate" => """{"type":"\ud800","ts":1,"sig":"x"}"""u8.ToArray(),
            "sig-surrogate" => """{"type":"auth","ts":1,"sig":"\ud800"}"""u8.ToArray(),
            "name-surrogate" => """{"sig":"x","\ud800":1}"""u8.ToArray(),
            "name-after-type" => """{"type":"lease.acquired","ts":1,"\ud800":1}"""u8.ToArray(),
            "key-changed-name" => """{"\ud800":1,"type":"key.changed","ts":1}"""u8.ToArray(),
            "chain-start-stream" => """{"type":"chain.start","ts":1,"stream":"\ud800"}"""u8.ToArray(),
            "chain-reset-reason" => """{"type":"chain.reset","ts":1,"stream":"usage","reason":"\ud800"}"""u8.ToArray(),
            "type-not-utf8" => [.. "{\"type\":\""u8, 0xC3, 0x28, .. "\",\"ts\":1}"u8],
            "payload-surrogate" => Encoding.UTF8.GetBytes(
                $$"""{"type":"auth","ts":1,"sig":"{{SignRaw(
                    _server,
                    """{"ver":1,"iat":1,"jti":"\ud800","stream":"usage","n":2,"prev":"","digest":"","lines":0}"""
                )}}"}"""
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

    // An elm-log+jws of the key over a payload given as JSON text, for a payload that JwsWriter does not write.
    private static string SignRaw(ECDsa key, string payloadJson)
    {
        string header = JwsWriter.Sign(key, TokenTypes.Log, new LogPayload(), LicensingJsonContext.Default.LogPayload).Split('.')[0];
        string signingInput = header + "." + Base64UrlCodec.Encode(Encoding.UTF8.GetBytes(payloadJson));
        byte[] signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );
        return signingInput + "." + Base64UrlCodec.Encode(signature);
    }

    private static void Append(SignedLog log, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int value = i;
            log.Append("lease.acquired", w => w.WriteNumber("i", value));
        }
    }

    private static LogPayload ReadPayload(string authLine)
    {
        int start = authLine.IndexOf("\"sig\":\"", StringComparison.Ordinal) + 7;
        string sig = authLine[start..authLine.LastIndexOf('"')];
        Assert.True(CompactJws.TryParse(sig, TokenTypes.Log, out CompactJws? jws, out _));
        return System.Text.Json.JsonSerializer.Deserialize(jws!.Payload, LicensingJsonContext.Default.LogPayload)!;
    }

    private static SigningKeyAccess Access(ECDsa key) => use => use(key);

    private SignedLog Open(int sealEvery = 100, Action<string>? notALogFile = null) =>
        SignedLog.Open(
            _dir,
            "usage",
            "usage",
            Access(_server),
            LicenseFile(_server),
            KeyFiles.ExportPublicJwk(_server),
            _time,
            sealEvery,
            notALogFile: notALogFile
        );

    private SignedLog OpenService(string certificate, ECDsa signer, int sealEvery = 100) =>
        SignedLog.Open(_dir, "usage", "usage", Access(signer), LogKeyMaterial.Certificates([certificate], "n1"), _time, sealEvery);

    private string Certificate(ECDsa key, KeyUses uses, DateTimeOffset? notBefore = null) =>
        CertificateIssuer.Issue(_root, key, uses, notBefore ?? Now.AddDays(-1), TimeSpan.FromDays(365), _time);

    private VerifiedLog Verify(IReadOnlyList<string>? files = null) =>
        VerifiedLog.Of(files ?? SignedLog.FindFiles(_dir, "usage"), _trust, "acme-cad", "usage");

    private byte[] LicenseFile(ECDsa server, string licenseId = "lic-srv")
    {
        string certificate = CertificateIssuer.Issue(_root, _signing, KeyUses.License, Now.AddDays(-1), TimeSpan.FromDays(365), _time);
        var spec = new LicenseSpec
        {
            LicenseId = licenseId,
            ProductId = "acme-cad",
            Kind = LicenseKind.Server,
            Features = [new FeatureSpec("pro", Seats: 5)],
            ExpiresAt = Now.AddDays(30),
            GracePeriod = TimeSpan.FromDays(7),
            InstallationKeyThumbprint = KeyFiles.KeyId(server),
            LeaseDuration = TimeSpan.FromMinutes(10),
            BorrowLimit = TimeSpan.FromDays(7),
            OveragePercent = 0,
            CloneStrategy = CloneStrategy.Deny,
            BorrowReturnLimit = 2,
            ServerBorrowReturnLimit = 10_000,
        };
        return LicenseIssuer.Issue(_signing, [certificate], spec, _time).File;
    }
}
