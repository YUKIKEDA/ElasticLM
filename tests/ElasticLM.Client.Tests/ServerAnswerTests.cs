using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using ElasticLM.Licensing.Tests;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Client.Tests;

/// <summary>
/// The client's readers of what a server sends and of its pin files, which are untrusted input (design §8.6, §12.1, §13.5).
/// </summary>
public sealed partial class ServerAnswerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "elm-pins-" + Guid.CreateVersion7().ToString("N"));

    public ServerAnswerTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// The server license answer must be an object with an object license and an object key; anything else is refused.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Answers: valid; not JSON; an array; license as a string; key missing; key null; nested deeper than 8.
    ///
    /// Steps:
    /// - Read each.
    ///
    /// Expected:
    /// - The valid one gives the raw license and key JSON; the others are refused without an exception.
    /// </remarks>
    [Fact]
    public void ServerLicenseAnswer_ShapeIsChecked()
    {
        Assert.True(LicenseClient.TryReadServerLicense(Bytes("""{"license":{"a":1},"key":{"kty":"EC"}}"""), out byte[] license, out byte[] key));
        Assert.Equal("""{"a":1}""", Encoding.UTF8.GetString(license));
        Assert.Equal("""{"kty":"EC"}""", Encoding.UTF8.GetString(key));

        string deep = """{"license":""" + new string('[', 10) + new string(']', 10) + ""","key":{}}""";
        foreach (
            string answer in new[] { "not json", "[]", """{"license":"x","key":{}}""", """{"license":{}}""", """{"license":{},"key":null}""", deep }
        )
        {
            Assert.False(LicenseClient.TryReadServerLicense(Bytes(answer), out _, out _));
        }
    }

    /// <summary>
    /// A nonce from a server, from an answer's body or its <c>ELM-Nonce</c> header alike, is kept only when it is 1 to 128
    /// characters long (design §12.1, V1c11).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Nonces of 0, 1, 128, and 129 characters, and none; nonce answers whose nonce is 0, 1, 128, and 129 characters.
    ///
    /// Steps:
    /// - Check each nonce with ValidNonce, and read each answer with ReadNonce.
    ///
    /// Expected:
    /// - The ones of 1 and 128 characters are kept as they are; the empty one, the one of 129 characters, and none give
    ///   null, from the header check and the body reader alike.
    /// </remarks>
    [Fact]
    public void Nonce_LengthOutsideOneTo128_IsNoNonce()
    {
        foreach (int length in new[] { 1, 128 })
        {
            string nonce = new('n', length);
            Assert.Equal(nonce, LicenseClient.ValidNonce(nonce));
            Assert.Equal(nonce, LicenseClient.ReadNonce(Bytes($$"""{"nonce":"{{nonce}}"}""")));
        }

        foreach (int length in new[] { 0, 129 })
        {
            string nonce = new('n', length);
            Assert.Null(LicenseClient.ValidNonce(nonce));
            Assert.Null(LicenseClient.ReadNonce(Bytes($$"""{"nonce":"{{nonce}}"}""")));
        }

        Assert.Null(LicenseClient.ValidNonce(null));
    }

    /// <summary>
    /// A problem answer gives its §19 code; an unknown or missing code, or a body that is not JSON, gives ELM_INTERNAL_ERROR
    /// without anything of the body; a detail of 1 to 512 characters is the message, and only when the caller keeps it
    /// (design §12.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Problem bodies: ELM_NO_SEATS with a detail; an unknown name with a detail, no code with a detail, a body that is
    ///   not JSON, a code that is a number; ELM_NO_SEATS with a detail of 512 and of 513 characters, with an empty detail,
    ///   and with none.
    ///
    /// Steps:
    /// - Read each with status 409, keeping the detail; read the first one again without keeping it.
    ///
    /// Expected:
    /// - ELM_NO_SEATS with the detail; ELM_INTERNAL_ERROR with exactly "The server answered 409 without a known error code."
    ///   for the four bodies without a known code; the detail of 512 characters is kept; the one of 513, the empty one, and
    ///   the missing one give "The server answered 409."; so does the first body when the detail is not kept.
    /// </remarks>
    [Fact]
    public void Problem_CodeIsRead()
    {
        const string NoSeats = """{"code":"ELM_NO_SEATS","detail":"No seats."}""";
        LicenseError known = LicenseClient.ProblemOf(HttpStatusCode.Conflict, Bytes(NoSeats), keepDetail: true);
        Assert.Equal((ErrorCode.NoSeats, "No seats."), (known.Code, known.Message));

        foreach (
            string body in new[] { """{"code":"ELM_SOMETHING","detail":"Forged."}""", """{"detail":"Forged."}""", "<html>Forged.", """{"code":7}""" }
        )
        {
            LicenseError unknown = LicenseClient.ProblemOf(HttpStatusCode.Conflict, Bytes(body), keepDetail: true);
            Assert.Equal((ErrorCode.InternalError, "The server answered 409 without a known error code."), (unknown.Code, unknown.Message));
        }

        string longest = new('x', 512);
        Assert.Equal(longest, LicenseClient.ProblemOf(HttpStatusCode.Conflict, NoSeatsWith($"\"{longest}\""), keepDetail: true).Message);
        foreach (string? detail in new[] { $"\"{longest}x\"", "\"\"", null })
        {
            LicenseError shortened = LicenseClient.ProblemOf(HttpStatusCode.Conflict, NoSeatsWith(detail), keepDetail: true);
            Assert.Equal((ErrorCode.NoSeats, "The server answered 409."), (shortened.Code, shortened.Message));
        }

        LicenseError unkept = LicenseClient.ProblemOf(HttpStatusCode.Conflict, Bytes(NoSeats), keepDetail: false);
        Assert.Equal((ErrorCode.NoSeats, "The server answered 409."), (unkept.Code, unkept.Message));

        static byte[] NoSeatsWith(string? detail) =>
            Bytes("{\"code\":\"ELM_NO_SEATS\"" + (detail is null ? string.Empty : ",\"detail\":" + detail) + "}");
    }

    /// <summary>
    /// A client takes from a problem answer only the 34 codes that a server sends with a 4xx or 5xx status. The success
    /// code, the codes only a client or the library makes, and the codes a server sends only when it refuses a license
    /// reload are read as a code the client does not know, and the answer's detail is dropped (design §12.1, the V1d1
    /// Grill, Q1 and Q2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Each of the 49 values of ErrorCode, by its wire name, as the code of a problem body whose detail is "Forged detail.".
    /// - The 34 numbers design §12.1 lists: 1, 2, 7, 10 to 14, 20 to 22, 24, 30 to 37, 39 to 44, 50 to 56, and 99.
    ///
    /// Steps:
    /// - Read each body with status 400, keeping the detail.
    ///
    /// Expected:
    /// - There are 49 codes, 34 of them listed.
    /// - A listed code comes back as itself with the detail.
    /// - Each of the other 15, ELM_OK among them, is ELM_INTERNAL_ERROR with exactly "The server answered 400 without a known
    ///   error code.".
    /// </remarks>
    [Fact]
    public void Problem_EachOfThe49Codes_IsTakenOnlyWhenAServerSendsItInAProblem()
    {
        int[] listed =
        [
            1,
            2,
            7,
            10,
            11,
            12,
            13,
            14,
            20,
            21,
            22,
            24,
            30,
            31,
            32,
            33,
            34,
            35,
            36,
            37,
            39,
            40,
            41,
            42,
            43,
            44,
            50,
            51,
            52,
            53,
            54,
            55,
            56,
            99,
        ];
        ErrorCode[] all = Enum.GetValues<ErrorCode>();
        Assert.Equal((49, 34), (all.Length, listed.Length));

        foreach (ErrorCode code in all)
        {
            string name = ErrorCodes.ToWireName(code);
            LicenseError error = LicenseClient.ProblemOf(
                HttpStatusCode.BadRequest,
                Bytes($$"""{"type":"about:blank","title":"{{name}}","status":400,"detail":"Forged detail.","code":"{{name}}"}"""),
                keepDetail: true
            );

            (ErrorCode Code, string Message) expected = listed.Contains((int)code)
                ? (code, "Forged detail.")
                : (ErrorCode.InternalError, "The server answered 400 without a known error code.");
            Assert.True(expected == (error.Code, error.Message), $"{name} was read as {ErrorCodes.ToWireName(error.Code)}: {error.Message}");
        }
    }

    /// <summary>
    /// The two lists of codes a client takes are the ones design §19 states: from a problem answer, the codes whose HTTP
    /// cell starts with a 4xx or 5xx status; from a lease's code in a reclaim answer, ELM_OK and the ten numbers the section
    /// names (the V1d1 Grill, Q1 and Q6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - docs/design.ja.md under the repository root. The rows of the table of its section "## 19. エラーコード" have the form
    ///   | number | `ELM_…` | meaning | HTTP |, and the section's sentence on the reclaim answer names the numbers between
    ///   "各リースの `code` に " and " を入れうる".
    ///
    /// Steps:
    /// - Read the number, the name, and the last cell of each row; read the numbers of the sentence.
    /// - Ask ProblemCodes about each of the 49 values of ErrorCode.
    ///
    /// Expected:
    /// - The table has 49 rows, and each row's number and name are those of one ErrorCode value.
    /// - InProblemAnswer is true exactly for the codes whose HTTP cell starts with 4 or 5 and two more digits: not for
    ///   "2xx" (ELM_OK) and not for a cell that starts with "—". The message names each code that differs.
    /// - The sentence names ten numbers, and InReclaimAnswer is true exactly for them and for ELM_OK (0).
    /// </remarks>
    [Fact]
    public void ProblemCodes_BothLists_AreTheOnesOfTheDesign()
    {
        string[] lines = File.ReadAllLines(ReferencePages.RepositoryFile("docs/design.ja.md"));
        int start = Array.FindIndex(lines, line => line.StartsWith("## 19. エラーコード", StringComparison.Ordinal));
        Assert.True(start >= 0, "docs/design.ja.md has no section 19.");
        int end = Array.FindIndex(lines, start + 1, line => line.StartsWith("## ", StringComparison.Ordinal));
        string[] section = lines[(start + 1)..(end < 0 ? lines.Length : end)];

        var statusOf = new Dictionary<ErrorCode, string>();
        foreach (string line in section)
        {
            if (TableRow().Match(line) is { Success: true } row)
            {
                var code = (ErrorCode)int.Parse(row.Groups["number"].Value, CultureInfo.InvariantCulture);
                Assert.Equal(ErrorCodes.ToWireName(code), row.Groups["name"].Value);
                statusOf.Add(code, row.Groups["http"].Value.Trim());
            }
        }

        ErrorCode[] all = Enum.GetValues<ErrorCode>();
        Assert.Equal(49, statusOf.Count);
        Assert.Equal(all.Order(), statusOf.Keys.Order());

        string[] problemDiffers =
        [
            .. all.Where(code => ProblemCodes.InProblemAnswer(code) != ErrorStatus().IsMatch(statusOf[code]))
                .Select(code => $"{ErrorCodes.ToWireName(code)} (HTTP cell: {statusOf[code]})"),
        ];
        Assert.True(problemDiffers.Length == 0, "The problem list differs from the table for: " + string.Join(", ", problemDiffers));

        string sentence = Assert.Single(section, line => ReclaimSentence().IsMatch(line));
        int[] reclaim =
        [
            .. ReclaimSentence().Match(sentence).Groups["numbers"].Value.Split(", ").Select(n => int.Parse(n, CultureInfo.InvariantCulture)),
        ];
        Assert.Equal(10, reclaim.Length);
        string[] reclaimDiffers =
        [
            .. all.Where(code => ProblemCodes.InReclaimAnswer(code) != (code == ErrorCode.Ok || reclaim.Contains((int)code)))
                .Select(ErrorCodes.ToWireName),
        ];
        Assert.True(reclaimDiffers.Length == 0, "The reclaim list differs from the design's sentence for: " + string.Join(", ", reclaimDiffers));
    }

    /// <summary>
    /// A redirect is ELM_INTERNAL_ERROR whatever its body says: a 3xx answer cannot hand the app a code, even one a server
    /// sends in a problem answer (design §12.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A problem body with ELM_NO_SEATS, which is taken from a 4xx answer, and a detail.
    ///
    /// Steps:
    /// - Read it with status 300, 307, 308, and 399, the ends of the 3xx range included; then with 400.
    ///
    /// Expected:
    /// - For each 3xx status, ELM_INTERNAL_ERROR with exactly "The server answered {status} without a known error code.".
    /// - For 400, ELM_NO_SEATS with the detail.
    /// </remarks>
    [Fact]
    public void Problem_RedirectWithAListedCode_IsAnInternalError()
    {
        byte[] body = Bytes("""{"code":"ELM_NO_SEATS","detail":"No seats."}""");
        foreach (int status in new[] { 300, 307, 308, 399 })
        {
            LicenseError error = LicenseClient.ProblemOf((HttpStatusCode)status, body, keepDetail: true);
            Assert.Equal((ErrorCode.InternalError, $"The server answered {status} without a known error code."), (error.Code, error.Message));
        }

        LicenseError taken = LicenseClient.ProblemOf(HttpStatusCode.BadRequest, body, keepDetail: true);
        Assert.Equal((ErrorCode.NoSeats, "No seats."), (taken.Code, taken.Message));
    }

    /// <summary>
    /// A problem answer to the request for the server license, which comes before the server is verified over a connection
    /// that takes any certificate, gives its code by the same list and never its detail: the party that answers does not
    /// choose the app's message (design §12.1, §13.5, the V1d1 Grill, Q3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two servers on loopback TLS with a self-signed certificate made in the test, which answer every request with a
    ///   problem: one with 403, ELM_FORBIDDEN, and the detail "Call this number to renew."; one with 400, ELM_OK, and the
    ///   same detail.
    /// - An on-premises client of each (no license key), with a new data folder and a fake clock.
    ///
    /// Steps:
    /// - Acquire a lease of pro from each; read what each server was asked.
    ///
    /// Expected:
    /// - From the first, Failed with ELM_FORBIDDEN and exactly "The server answered 403.".
    /// - From the second, Failed with ELM_INTERNAL_ERROR and exactly "The server answered 400 without a known error code.".
    /// - Each server was asked only for GET /v1/server/license, once.
    /// </remarks>
    [Fact]
    public async Task ServerLicenseRequest_ProblemAnswer_GivesTheListedCodeAndNeverTheDetail()
    {
        static string Problem(int status, string code) =>
            $$"""{"type":"about:blank","title":"{{code}}","status":{{status}},"detail":"Call this number to renew.","code":"{{code}}"}""";

        await using ProblemServer forbidden = ProblemServer.Start(tls: true, HttpStatusCode.Forbidden, Problem(403, "ELM_FORBIDDEN"));
        await using ProblemServer forged = ProblemServer.Start(tls: true, HttpStatusCode.BadRequest, Problem(400, "ELM_OK"));
        using ECDsa root = KeyFiles.CreateKey();
        await using LicenseClient refused = OnPremisesClient(forbidden.Url, root, "refused");
        await using LicenseClient deceived = OnPremisesClient(forged.Url, root, "deceived");

        LicenseError listed = Assert
            .IsType<LeaseResult.Failed>(await refused.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken))
            .Error;
        LicenseError unlisted = Assert
            .IsType<LeaseResult.Failed>(await deceived.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken))
            .Error;

        Assert.Equal((ErrorCode.Forbidden, "The server answered 403."), (listed.Code, listed.Message));
        Assert.Equal((ErrorCode.InternalError, "The server answered 400 without a known error code."), (unlisted.Code, unlisted.Message));
        Assert.Equal(["GET /v1/server/license"], forbidden.Requests);
        Assert.Equal(["GET /v1/server/license"], forged.Requests);
    }

    /// <summary>
    /// A nonce answer must carry a nonce of 1 to 128 characters.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Answers with a nonce of 75 and 129 characters, an empty nonce, a null, and no JSON.
    ///
    /// Steps:
    /// - Read each.
    ///
    /// Expected:
    /// - Only the 75-character nonce is read.
    /// </remarks>
    [Fact]
    public void Nonce_IsLimited()
    {
        Assert.Equal(new string('n', 75), LicenseClient.ReadNonce(Bytes($$"""{"nonce":"{{new string('n', 75)}}"}""")));
        Assert.Null(LicenseClient.ReadNonce(Bytes($$"""{"nonce":"{{new string('n', 129)}}"}""")));
        Assert.Null(LicenseClient.ReadNonce(Bytes("""{"nonce":""}""")));
        Assert.Null(LicenseClient.ReadNonce(Bytes("""{"nonce":null}""")));
        Assert.Null(LicenseClient.ReadNonce(Bytes("nonce")));
    }

    /// <summary>
    /// A pin is written and read back per server URL; a damaged pin, a pin of another URL, and a pin larger than 4 KiB are
    /// refused rather than ignored; delete removes it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty folder; two server URLs that differ only in case and the default port.
    ///
    /// Steps:
    /// - Read before writing; write and read through the other spelling; damage the file; write a pin of another URL into it;
    ///   pad it past 4 KiB; delete.
    ///
    /// Expected:
    /// - No pin; the pin; refused three times; deleted, and a second delete reports nothing deleted.
    /// </remarks>
    [Fact]
    public void Pins_AreReadWrittenAndChecked()
    {
        var pins = new ServerPins(_directory);
        var url = new Uri("https://Licenses.Example:443/");

        Assert.True(pins.TryRead(url, out PinFile? none));
        Assert.Null(none);

        Assert.True(pins.TryWrite(url, "lic-srv", "key-1"));
        Assert.True(pins.TryRead(new Uri("https://licenses.example/"), out PinFile? pin));
        Assert.Equal(("lic-srv", "key-1"), (pin!.Lic, pin.Jkt));

        string path = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "servers")));
        File.WriteAllText(path, "{not json");
        Assert.False(pins.TryRead(url, out _));
        File.WriteAllText(path, """{"url":"https://other.example/","lic":"lic-srv","jkt":"key-1"}""");
        Assert.False(pins.TryRead(url, out _));
        File.WriteAllText(path, """{"url":"https://licenses.example/","lic":"lic-srv","jkt":"key-1"}""" + new string(' ', 4096));
        Assert.False(pins.TryRead(url, out _));

        Assert.True(pins.Delete(url));
        Assert.False(pins.Delete(url));
    }

    /// <summary>
    /// A saved borrow file must be an object with version 1, a server, a lease token, a license object, and a key object;
    /// other members are ignored.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Files: valid with an unknown member; not JSON; no version; version 2; version as text; the lease as a number; the
    ///   license as a string; no key; nested deeper than 8.
    ///
    /// Steps:
    /// - Read each.
    ///
    /// Expected:
    /// - The valid one gives its parts; the others are refused without an exception.
    /// </remarks>
    [Fact]
    public void BorrowFile_ShapeIsChecked()
    {
        Assert.True(
            LicenseClient.TryReadBorrowFile(
                Bytes("""{"ver":1,"server":"https://s/","lease":"a.b.c","license":{"x":1},"key":{"kty":"EC"},"later":true}"""),
                out string? server,
                out string? token,
                out byte[] license,
                out byte[] key
            )
        );
        Assert.Equal(
            ("https://s/", "a.b.c", """{"x":1}""", """{"kty":"EC"}"""),
            (server, token, Encoding.UTF8.GetString(license), Encoding.UTF8.GetString(key))
        );

        string deep = """{"ver":1,"server":"s","lease":"t","license":""" + new string('[', 10) + new string(']', 10) + ""","key":{}}""";
        foreach (
            string file in new[]
            {
                "not json",
                """{"server":"s","lease":"t","license":{},"key":{}}""",
                """{"ver":2,"server":"s","lease":"t","license":{},"key":{}}""",
                """{"ver":"1","server":"s","lease":"t","license":{},"key":{}}""",
                """{"ver":1,"server":"s","lease":1,"license":{},"key":{}}""",
                """{"ver":1,"server":"s","lease":"t","license":"x","key":{}}""",
                """{"ver":1,"server":"s","lease":"t","license":{}}""",
                deep,
            }
        )
        {
            Assert.False(LicenseClient.TryReadBorrowFile(Bytes(file), out _, out _, out _, out _));
        }
    }

    /// <summary>
    /// A borrow saved from <c>elm-service</c> has the common members and one to four certificate strings of at most 16 KiB
    /// (design §12.7.4, §8.6); the two kinds of file are read only by their own reader.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Files with 1 and with 4 certificates, the 4th of 16,384 characters; with none, 5, an empty list, a number, a
    ///   certificate of 16,385 characters, and no list; an on-premises file.
    ///
    /// Steps:
    /// - Read each with the hosted reader; read the hosted file with the on-premises reader.
    ///
    /// Expected:
    /// - The first two give their parts; the others are refused; the on-premises reader refuses the hosted file.
    /// </remarks>
    [Fact]
    public void HostedBorrowFile_ShapeIsChecked()
    {
        static string File(string certificates) => """{"ver":1,"server":"https://s/","lease":"a.b.c","certificates":""" + certificates + "}";
        string longest = new('c', 16 * 1024);
        Assert.True(
            LicenseClient.TryReadHostedBorrowFile(Bytes(File("""["x"]""")), out string? server, out string? token, out IReadOnlyList<string>? one)
        );
        Assert.Equal(("https://s/", "a.b.c"), (server, token));
        Assert.Equal(["x"], one!);
        Assert.True(
            LicenseClient.TryReadHostedBorrowFile(Bytes(File($$"""["a","b","c","{{longest}}"]""")), out _, out _, out IReadOnlyList<string>? four)
        );
        Assert.Equal(4, four!.Count);

        foreach (
            string file in new[]
            {
                File("[]"),
                File("""["a","b","c","d","e"]"""),
                File("""["a",1]"""),
                File($$"""["{{longest}}c"]"""),
                File("null"),
                """{"ver":1,"server":"https://s/","lease":"a.b.c"}""",
                """{"ver":1,"server":"https://s/","lease":"a.b.c","license":{},"key":{}}""",
            }
        )
        {
            Assert.False(LicenseClient.TryReadHostedBorrowFile(Bytes(file), out _, out _, out _));
        }

        Assert.False(LicenseClient.TryReadBorrowFile(Bytes(File("""["x"]""")), out _, out _, out _, out _));
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // A row of the table of design §19: | number | `ELM_…` | meaning | HTTP |.
    [GeneratedRegex(@"^\| (?<number>\d+) \| `(?<name>ELM_[A-Z_]+)` \| .* \| (?<http>[^|]*) \|$")]
    private static partial Regex TableRow();

    // An HTTP cell that starts with the status of an error answer.
    [GeneratedRegex(@"^[45]\d\d")]
    private static partial Regex ErrorStatus();

    // The sentence of design §19 that names the codes of a lease in a reclaim answer.
    [GeneratedRegex(@"各リースの `code` に (?<numbers>\d+(?:, \d+)*) を入れうる")]
    private static partial Regex ReclaimSentence();

    // A client of an on-premises server, which asks for the server license before its first request: its own installation key
    // in a new folder under the test's folder, and a fake clock.
    private LicenseClient OnPremisesClient(Uri server, ECDsa root, string folder)
    {
        var options = new LicenseClientOptions
        {
            ProductId = "acme-cad",
            ServerUrl = server,
            Trust = TrustStore.Create([KeyFiles.ExportPublic(root)], []),
            DataDirectory = Path.Combine(_directory, folder),
            ClockGuard = StatelessClockGuard.Instance,
            TimeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)),
        };
        Assert.True(LicenseClient.TryCreate(options, out LicenseClient? client, out LicenseError? error), error?.Message);
        return client!;
    }
}
