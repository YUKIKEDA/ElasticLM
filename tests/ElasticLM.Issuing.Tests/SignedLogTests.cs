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
            log.ChangeKey(LicenseFile(_server), KeyFiles.ExportPublicJwk(_server), Access(_server));
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
    /// When a key change cannot be written, the log still takes the new license: the next file starts with chain.reset and
    /// carries it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A log of lic-srv whose key fails while the change is signed.
    ///
    /// Steps:
    /// - Change to a license lic-new of the same key (throws); append; close; verify.
    ///
    /// Expected:
    /// - IOException; intact; the last file is reported with lic-new.
    /// </remarks>
    [Fact]
    public void ChangeKey_FailedWrite_TakesTheNewLicense()
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
            Assert.Throws<IOException>(() => log.ChangeKey(LicenseFile(_server, "lic-new"), KeyFiles.ExportPublicJwk(_server), Access));
            fail = false;
            _time.Advance(TimeSpan.FromSeconds(1));
            Append(log, 1);
        }

        LogReport report = Verify();
        Assert.True(report.Intact);
        Assert.Equal("lic-new", report.Files[^1].LicenseId);
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
