using System.Net;
using System.Text;
using ElasticLM.Licensing;

namespace ElasticLM.Client.Tests;

/// <summary>
/// The client's readers of what a server sends and of its pin files, which are untrusted input (design §8.6, §13.5).
/// </summary>
public sealed class ServerAnswerTests : IDisposable
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
    /// A problem answer gives its §19 code; an unknown or missing code, or a body that is not JSON, gives ELM_INTERNAL_ERROR;
    /// a detail longer than 512 characters is not passed on.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Problem bodies of each kind.
    ///
    /// Steps:
    /// - Read each with status 409.
    ///
    /// Expected:
    /// - ELM_NO_SEATS with the detail; ELM_INTERNAL_ERROR for the rest; the long detail replaced by the status.
    /// </remarks>
    [Fact]
    public void Problem_CodeIsRead()
    {
        LicenseError known = LicenseClient.ProblemOf(HttpStatusCode.Conflict, Bytes("""{"code":"ELM_NO_SEATS","detail":"No seats."}"""));
        Assert.Equal(ErrorCode.NoSeats, known.Code);
        Assert.Equal("No seats.", known.Message);

        foreach (string body in new[] { """{"code":"ELM_SOMETHING"}""", "{}", "<html>", """{"code":7}""" })
        {
            Assert.Equal(ErrorCode.InternalError, LicenseClient.ProblemOf(HttpStatusCode.Conflict, Bytes(body)).Code);
        }

        string longDetail = new('x', 513);
        LicenseError shortened = LicenseClient.ProblemOf(HttpStatusCode.Conflict, Bytes($$"""{"code":"ELM_NO_SEATS","detail":"{{longDetail}}"}"""));
        Assert.Equal("The server answered 409.", shortened.Message);
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
}
