using System.Security.Cryptography;
using System.Text;
using ElasticLM.Licensing;
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

        LogReport report = Verify();
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

        LogReport report = Verify();
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

        LogReport report = Verify();
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
        LogReport report = Verify([newest]);
        Assert.True(report.Intact);
        Assert.Contains(report.Issues, i => i.Message.StartsWith("The chain was reset", StringComparison.Ordinal));
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
        Assert.False(LogVerifier.Verify(files, TrustStore.Create([KeyFiles.ExportPublic(otherRoot)], []), "acme-cad", "usage").Intact);
        Assert.False(LogVerifier.Verify(files, _trust, "acme-cam", "usage").Intact);
        Assert.False(LogVerifier.Verify(files, _trust, "acme-cad", "borrows").Intact);
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

        LogReport report = Verify([file]);
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
    /// - Verify; append 1 MiB of text without a line feed and verify; write a file whose second line is 1 MiB + 1 byte with
    ///   its line feed and verify.
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

        LogReport report = Verify();
        Assert.True(report.Intact);
        Assert.Contains(report.Entries, e => !e.IsSigned && e.Value.TryGetProperty("x", out _));

        File.AppendAllText(file, new string('y', LogVerifier.MaxLineLength));
        LogReport cut = Verify();
        Assert.True(cut.Intact);
        Assert.Contains(cut.Issues, i => i.Message.Contains("middle of a line", StringComparison.Ordinal));

        string longer = Path.Combine(_dir, "usage-20990101T000000000Z.jsonl");
        File.WriteAllText(longer, "{}\n" + new string('z', LogVerifier.MaxLineLength + 1) + "\n");
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

        LogReport report = Verify();
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
    ///   warnings for the unsigned line and for the reset that names the failed write.
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
        LogReport report = Verify();
        Assert.True(report.Intact);
        Assert.Contains(report.Issues, i => i.Message.Contains("not signed", StringComparison.Ordinal));
        Assert.Contains(report.Issues, i => i.Message.StartsWith("The chain was reset: a write to", StringComparison.Ordinal));
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

        LogReport report = Verify();
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
            LogReport report = LogVerifier.Verify(files, _trust, product, "usage");
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

            LogIssue error = Assert.Single(LogVerifier.Verify(SignedLog.FindFiles(_dir, "usage"), _trust, null, "usage").Issues);
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

        LogIssue replaced = Assert.Single(LogVerifier.Verify(SignedLog.FindFiles(_dir, "usage"), _trust, null, "usage").Issues);
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
            Assert.Single(LogVerifier.Verify([serverFile], _trust, null, "usage").Issues).Message
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
    /// The certificates of a service log are one to four, none empty.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Make the material with no certificate, five, and an empty one; with four.
    ///
    /// Expected:
    /// - ArgumentException three times; the last is made.
    /// </remarks>
    [Fact]
    public void LogKeyMaterial_Certificates_AreChecked()
    {
        Assert.Throws<ArgumentException>(() => LogKeyMaterial.Certificates([]));
        Assert.Throws<ArgumentException>(() => LogKeyMaterial.Certificates(["a", "b", "c", "d", "e"]));
        Assert.Throws<ArgumentException>(() => LogKeyMaterial.Certificates(["a", string.Empty]));
        Assert.NotNull(LogKeyMaterial.Certificates(["a", "b", "c", "d"]));
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
        using (SignedLog log = SignedLog.Open(_dir, "usage", "usage", Switching, LogKeyMaterial.Certificates([a, b]), _time, sealEvery: 1))
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

        using (SignedLog log = SignedLog.Open(_dir, "usage", "usage", Access(other), LogKeyMaterial.Certificates([a, b]), _time))
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
            Assert.Single(LogVerifier.Verify([file], _trust, null, "usage").Issues).Message
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

    private LogReport Verify2() => LogVerifier.Verify(SignedLog.FindFiles(_dir, "usage"), _trust, null, "usage");

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
            LogVerifier.Verify([files[0], files[2]], _trust, null, "usage").Issues,
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
        Assert.Equal(NoLog, Assert.Single(LogVerifier.Verify(SignedLog.FindFiles(_dir, "usage"), revoked, null, "usage").Issues).Message);

        _time.Advance(TimeSpan.FromSeconds(1));
        using (SignedLog log = Open())
        {
            Append(log, 1);
        }

        LogIssue mixed = Assert.Single(Verify().Issues);
        Assert.EndsWith("a server's and the service's logs are mixed.", mixed.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A service's files name their node on the signed first line: a chain is of one node, and the node asked for must be
    /// the files' (decision 0.95).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A certificate for log; files written as node a, then as node b, and one with no node.
    ///
    /// Steps:
    /// - Verify a's file as node a; a's file then b's; a's file as node b; the file with no node as node a; a's file alone.
    ///
    /// Expected:
    /// - No issue; "The file is of node 'b', not of node 'a'."; "The file is of node 'a', not of node 'b'."; "The file names no
    ///   node, and the log is of node 'a'."; no issue.
    /// </remarks>
    [Fact]
    public void ServiceLog_Node_IsChecked()
    {
        string certificate = Certificate(_signing, KeyUses.Log);
        string Write(string? node)
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
        Assert.Empty(LogVerifier.Verify([a], _trust, null, "usage", "a").Issues);
        Assert.Equal("The file is of node 'b', not of node 'a'.", Assert.Single(LogVerifier.Verify([a, b], _trust, null, "usage").Issues).Message);
        Assert.Equal("The file is of node 'a', not of node 'b'.", Assert.Single(LogVerifier.Verify([a], _trust, null, "usage", "b").Issues).Message);
        string none = Write(null);
        Assert.Equal(
            "The file names no node, and the log is of node 'a'.",
            Assert.Single(LogVerifier.Verify([none], _trust, null, "usage", "a").Issues).Message
        );
        Assert.Empty(LogVerifier.Verify([a], _trust, null, "usage").Issues);
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

    private SignedLog Open(int sealEvery = 100) =>
        SignedLog.Open(_dir, "usage", "usage", Access(_server), LicenseFile(_server), KeyFiles.ExportPublicJwk(_server), _time, sealEvery);

    private SignedLog OpenService(string certificate, ECDsa signer, int sealEvery = 100) =>
        SignedLog.Open(_dir, "usage", "usage", Access(signer), LogKeyMaterial.Certificates([certificate]), _time, sealEvery);

    private string Certificate(ECDsa key, KeyUses uses, DateTimeOffset? notBefore = null) =>
        CertificateIssuer.Issue(_root, key, uses, notBefore ?? Now.AddDays(-1), TimeSpan.FromDays(365), _time);

    private LogReport Verify(IReadOnlyList<string>? files = null) =>
        LogVerifier.Verify(files ?? SignedLog.FindFiles(_dir, "usage"), _trust, "acme-cad", "usage");

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
        };
        return LicenseIssuer.Issue(_signing, [certificate], spec, _time).File;
    }
}
