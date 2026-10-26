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

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
