using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ElasticLM.Client.Tests;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Cli.Tests;

/// <summary>
/// What elm writes from the answers of elm-service and of an identity provider (design §16, V1d27): control characters
/// replaced in text, the answer limits and the clock of the OIDC reader, and the time without a byte that ends the body of
/// elm admin request. The other side is a listener of the test that writes its answers byte by byte.
/// </summary>
public sealed class ElmRemoteTextTests : IDisposable
{
    private const int OidcLimit = 256 * 1024;
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("elm-v1d27-");
    private readonly Dictionary<string, string> _environment = [];

    public void Dispose() => _folder.Delete(recursive: true);

    /// <summary>
    /// elm offline process replaces the control characters of the service's problem answer and of the IDs it reads from an
    /// issued answer, in text; its JSON output escapes them; a code that is not a string is "?" (design §16, V1d27, F21).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A request file whose header has typ elm-actreq+jws (elm reads only the typ before sending).
    /// - A listener that answers 400 with: code "ELM_X" ESC "[31m" and detail "a" CSI "2Jb" CR LF "c" as JSON escapes;
    ///   code "ELM_Y" and a detail of the raw UTF-8 bytes of U+009B, "x", and 0x7F; code 1 and detail "d"; code "ELM_L" and
    ///   a detail of 30,000 escaped ESC.
    /// - A listener that answers 200 with activationId "act" ESC "]0;t" BEL and a license token whose payload has lic "L"
    ///   U+0085 and seq 7.
    ///
    /// Steps:
    /// - Process the file against each answer; the issued one as text and with --json.
    ///
    /// Expected:
    /// - 400: exit 1 and the one line "error: ELM_X\u001B[31m: a\u009B2Jb\u000D\u000Ac", "error: ELM_Y: \u009Bx\u007F",
    ///   "error: ?: d", and "error: ELM_L: " followed by 30,000 "\u001B".
    /// - 200: exit 0 and "activation act\u001B]0;t\u0007, license L\u0085, seq 7: (file)"; with --json the members hold the
    ///   characters as JSON escapes, which read back as the characters sent.
    /// </remarks>
    [Fact]
    public async Task Offline_ProblemAndIssuedAnswer_ReplaceControlCharacters()
    {
        string request = Path.Combine(_folder.FullName, "a.elmreq");
        File.WriteAllText(request, Base64Url("""{"alg":"ES256","typ":"elm-actreq+jws"}""") + ".e30.c2ln");
        string answer = Path.Combine(_folder.FullName, "a.elmresp");
        byte[] raw = [.. "{\"code\":\"ELM_Y\",\"detail\":\""u8, 0xC2, 0x9B, (byte)'x', 0x7F, .. "\"}"u8];
        (byte[] Body, string Line)[] problems =
        [
            ("{\"code\":\"ELM_X\\u001b[31m\",\"detail\":\"a\\u009b2Jb\\r\\nc\"}"u8.ToArray(), "error: ELM_X\\u001B[31m: a\\u009B2Jb\\u000D\\u000Ac"),
            (raw, "error: ELM_Y: \\u009Bx\\u007F"),
            ("{\"code\":1,\"detail\":\"d\"}"u8.ToArray(), "error: ?: d"),
            (
                Encoding.UTF8.GetBytes("{\"code\":\"ELM_L\",\"detail\":\"" + string.Concat(Enumerable.Repeat("\\u001b", 30_000)) + "\"}"),
                "error: ELM_L: " + string.Concat(Enumerable.Repeat("\\u001B", 30_000))
            ),
        ];
        foreach ((byte[] body, string line) in problems)
        {
            await using RawAnswerServer service = RawAnswerServer.Start(_ => RawAnswer.Bytes(body, 400));
            (int code, string output, string error) = await RunAsync(
                TimeProvider.System,
                "offline",
                "process",
                "--url",
                service.Url.ToString(),
                "--in",
                request,
                "--out",
                answer
            );
            Assert.Equal((1, string.Empty, line), (code, output, error.TrimEnd()));
        }

        string token = "h." + Base64Url("{\"lic\":\"L\\u0085\",\"seq\":7}") + ".s";
        byte[] issued = Encoding.UTF8.GetBytes($$$"""{"activationId":"act\u001b]0;t\u0007","license":{"license":"{{{token}}}"}}""");
        await using (RawAnswerServer service = RawAnswerServer.Start(_ => RawAnswer.Bytes(issued)))
        {
            string[] args = ["offline", "process", "--url", service.Url.ToString(), "--in", request, "--out", answer, "--force"];
            (int code, string output, string error) = await RunAsync(TimeProvider.System, args);
            Assert.Equal((0, $"activation act\\u001B]0;t\\u0007, license L\\u0085, seq 7: {answer}", string.Empty), (code, output.Trim(), error));

            (code, output, error) = await RunAsync(TimeProvider.System, [.. args, "--json"]);
            Assert.Equal((0, string.Empty), (code, error));
            Assert.Contains("\"act\\u001B]0;t\\u0007\"", output, StringComparison.Ordinal);
            Assert.Contains("\"L\\u0085\"", output, StringComparison.Ordinal);
            using JsonDocument json = JsonDocument.Parse(output);
            Assert.Equal("act\u001b]0;t\u0007", json.RootElement.GetProperty("activationId").GetString());
            Assert.Equal("L\u0085", json.RootElement.GetProperty("licenseId").GetString());
        }
    }

    /// <summary>
    /// elm admin request writes the body of an answer with its line feeds and tabs, and with every other control character
    /// replaced, for a success on standard output and for an error on standard error (design §16, V1d27).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - ELM_API_KEY set; a listener that answers 200 with the raw bytes {"a":"x"} LF ESC "[2J" TAB CR, U+009B as UTF-8, and
    ///   LF; then one that answers 400 with {"code":"E" ESC "]0;t" BEL "\u001b"} (raw ESC and BEL, and one escape).
    ///
    /// Steps:
    /// - GET /v1/admin/licenses against each.
    ///
    /// Expected:
    /// - 200: exit 0 and the standard output {"a":"x"} LF "\u001B[2J" TAB "\u000D\u009B" LF, with no line break added.
    /// - 400: exit 1, standard error "error: the service answered 400." and then {"code":"E\u001B]0;t\u0007\u001b"}: the JSON
    ///   escape stays as it came.
    /// </remarks>
    [Fact]
    public async Task Request_AnswerBodies_KeepLinesAndReplaceOtherControlCharacters()
    {
        _environment["ELM_API_KEY"] = "elm_test";
        byte[] success = [.. "{\"a\":\"x\"}\n\u001b[2J\t\r"u8, 0xC2, 0x9B, (byte)'\n'];
        await using (RawAnswerServer service = RawAnswerServer.Start(_ => RawAnswer.Bytes(success)))
        {
            (int code, string output, string error) = await RunAsync(
                TimeProvider.System,
                "admin",
                "request",
                "GET",
                "/v1/admin/licenses",
                "--url",
                service.Url.ToString()
            );
            Assert.Equal((0, "{\"a\":\"x\"}\n\\u001B[2J\t\\u000D\\u009B\n", string.Empty), (code, output, error));
        }

        byte[] failure = "{\"code\":\"E\u001b]0;t\u0007\\u001b\"}"u8.ToArray();
        await using (RawAnswerServer service = RawAnswerServer.Start(_ => RawAnswer.Bytes(failure, 400)))
        {
            (int code, string output, string error) = await RunAsync(
                TimeProvider.System,
                "admin",
                "request",
                "GET",
                "/v1/admin/licenses",
                "--url",
                service.Url.ToString()
            );
            Assert.Equal(
                (1, string.Empty, "error: the service answered 400." + Environment.NewLine + "{\"code\":\"E\\u001B]0;t\\u0007\\u001b\"}"),
                (code, output, error.TrimEnd())
            );
        }
    }

    /// <summary>
    /// The body of an answer to elm admin request that stops ends the command once no byte has come for 100 seconds of the
    /// command's clock, with exit code 3 (design §16, V1d27, Issue #406).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - ELM_API_KEY set; a listener that answers 200 in chunks with the one byte "{" and then nothing; elm on a fake clock
    ///   that tells the test when elm makes its first timer once "{" is on standard output: the wait of the read that
    ///   follows the byte.
    ///
    /// Steps:
    /// - GET /v1/admin/licenses; once that timer is made, move the clock 99 seconds, then 1 more.
    ///
    /// Expected:
    /// - The command has not ended after 99 seconds of the clock (checked after half a second of the real clock).
    /// - After 100: exit code 3 and the one line "error: The service cannot be reached: No byte of the answer came within
    ///   100 seconds.", within 20 seconds of the real clock; standard output holds "{". A wait of 101 seconds would not end
    ///   the command in that time. Before Issue #521 the test moved the clock 1 second every 50 milliseconds of the real
    ///   clock until the command ended and asserted how far it had moved, which a slow runner took past 101 seconds.
    /// </remarks>
    [Fact]
    public async Task Request_BodyThatStops_EndsAfter100SecondsWithoutAByte()
    {
        _environment["ELM_API_KEY"] = "elm_test";
        await using RawAnswerServer service = RawAnswerServer.Start(_ => RawAnswer.Stalled("{", 1));
        var output = new SyncWriter();
        var time = new TimerAfterTime(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), () => output.Text == "{");
        Task<(int Code, string Out, string Error)> running = RunAsync(
            time,
            output,
            ["admin", "request", "GET", "/v1/admin/licenses", "--url", service.Url.ToString()]
        );
        await time.Made.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        // design §16 (V1d27): 99 seconds without a byte do not end the command.
        time.Advance(TimeSpan.FromSeconds(99));
        await Task.WhenAny(running, Task.Delay(500, TestContext.Current.CancellationToken));
        Assert.False(running.IsCompleted, "The command ended before 100 seconds without a byte.");
        time.Advance(TimeSpan.FromSeconds(1));

        (int code, string text, string error) = await running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal((3, "{", "error: The service cannot be reached: No byte of the answer came within 100 seconds."), (code, text, error.Trim()));
    }

    /// <summary>
    /// The 100 seconds without a byte start again with each byte, and the body as a whole has no time limit (design §16,
    /// V1d27, Issue #406).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - ELM_API_KEY set; a listener of the test that answers 200 in chunks: "a", then "b" when the test says so, then the
    ///   last chunk when the test says so; elm on a fake clock.
    ///
    /// Steps:
    /// - GET /v1/admin/licenses; once "a" is written, move the clock 60 seconds and send "b"; once "b" is written, move the
    ///   clock 60 seconds and end the body.
    ///
    /// Expected:
    /// - Exit code 0 and the output "ab" and a line break, after 120 seconds of the clock in all. A wait counted from the
    ///   start of the body would have ended the command at 100 seconds.
    /// </remarks>
    [Fact]
    public async Task Request_BodyThatKeepsComing_IsReadBeyond100Seconds()
    {
        _environment["ELM_API_KEY"] = "elm_test";
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var next = new SemaphoreSlim(0);
        Task serving = Task.Run(
            async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
                NetworkStream stream = client.GetStream();
                await ReadHeadAsync(stream);
                await stream.WriteAsync(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n1\r\na\r\n"u8.ToArray(),
                    TestContext.Current.CancellationToken
                );
                await next.WaitAsync(TestContext.Current.CancellationToken);
                await stream.WriteAsync("1\r\nb\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
                await next.WaitAsync(TestContext.Current.CancellationToken);
                await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
            },
            TestContext.Current.CancellationToken
        );
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var output = new SyncWriter();
        string url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        Task<(int Code, string Out, string Error)> running = RunAsync(time, output, ["admin", "request", "GET", "/v1/admin/licenses", "--url", url]);

        await WaitForAsync(() => output.Text == "a");
        time.Advance(TimeSpan.FromSeconds(60));
        next.Release();
        await WaitForAsync(() => output.Text == "ab");
        time.Advance(TimeSpan.FromSeconds(60));
        next.Release();

        (int code, string text, string error) = await running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await serving;
        Assert.Equal((0, "ab" + Environment.NewLine, string.Empty), (code, text, error));
    }

    /// <summary>
    /// The OIDC reader reads an answer of exactly 256 KiB, and ends one of a byte more, or one that goes on or declares more,
    /// with ELM_MALFORMED and exit code 3, whatever its status (design §16, V1d27, Issue #406).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A listener that answers with status 200 or 400 and: the JSON {"a":1} padded with spaces to 262,144 bytes, with a
    ///   Content-Length (at); the same to 262,145 bytes with a Content-Length (declared) and ended by the close (until-close);
    ///   a body in chunks that goes on for 64 MiB (endless).
    ///
    /// Steps:
    /// - Send a GET through OidcHttp.Send for each.
    ///
    /// Expected:
    /// - at: the status and a document whose a is 1.
    /// - Each other: CliException with exit code 3 and "ELM_MALFORMED: The answer of (URL) is larger than 262144 bytes.";
    ///   endless: the listener wrote at most 256 KiB and 16 MiB (the buffers of the two sockets). Before V1d27 the answers of
    ///   64 KiB to 256 KiB were refused, and those over were "cannot be reached".
    /// </remarks>
    /// <param name="shape">The shape of the answer.</param>
    /// <param name="status">Its status.</param>
    [Theory]
    [InlineData("at", 200)]
    [InlineData("at", 400)]
    [InlineData("declared", 200)]
    [InlineData("declared", 400)]
    [InlineData("until-close", 200)]
    [InlineData("endless", 200)]
    [InlineData("endless", 400)]
    public async Task OidcAnswer_AtTheLimitAndOver_IsReadThenMalformed(string shape, int status)
    {
        RawAnswer offered = shape switch
        {
            "at" => RawAnswer.Whole("{\"a\":1}", OidcLimit, status),
            "declared" => RawAnswer.Whole("{\"a\":1}", OidcLimit + 1, status),
            "until-close" => RawAnswer.UntilClose("{\"a\":1}", OidcLimit + 1, status),
            _ => RawAnswer.Endless(64L * 1024 * 1024, status),
        };
        await using RawAnswerServer listener = RawAnswerServer.Start(_ => offered);
        using HttpClient http = OidcHttp.Create();
        using var request = new HttpRequestMessage(HttpMethod.Get, listener.Url);

        if (shape == "at")
        {
            (HttpStatusCode answered, JsonDocument? body) = await Task.Run(() => OidcHttp.Send(http, request, TimeProvider.System));
            using (body)
            {
                Assert.Equal(((HttpStatusCode)status, 1), (answered, body!.RootElement.GetProperty("a").GetInt32()));
            }

            return;
        }

        CliException e = await Assert.ThrowsAsync<CliException>(() => Task.Run(() => OidcHttp.Send(http, request, TimeProvider.System)));
        Assert.Equal((3, $"ELM_MALFORMED: The answer of {listener.Url} is larger than 262144 bytes."), (e.ExitCode, e.Message));
        if (shape == "endless")
        {
            Assert.InRange(await listener.SettledBodyBytesAsync(), OidcLimit + 1, OidcLimit + (16L * 1024 * 1024));
        }
    }

    /// <summary>
    /// An answer of the identity provider that stops ends the OIDC reader after 30 seconds of the clock it is given, the body
    /// included (design §16, V1d27).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A listener that reads the request and sends nothing (no-head), or a head and the first byte of a body in chunks
    ///   and then nothing (body); a fake clock.
    ///
    /// Steps:
    /// - Send a GET through OidcHttp.Send; once the listener waits, move the clock 29 seconds, then 1 more.
    ///
    /// Expected:
    /// - After 29 seconds the reader still waits. After 30: CliException with exit code 3 and "(URL) cannot be reached: No
    ///   whole answer came within 30 seconds.", within 20
    ///   seconds of the real clock; a limit on the real clock would not end in that time.
    /// </remarks>
    /// <param name="stop">Where the answer stops.</param>
    [Theory]
    [InlineData("no-head")]
    [InlineData("body")]
    public async Task OidcAnswer_ThatStops_EndsAfterThirtySecondsOfTheClock(string stop)
    {
        await using RawAnswerServer listener = RawAnswerServer.Start(_ => stop == "no-head" ? RawAnswer.Silent : RawAnswer.Stalled("{", 1));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        using HttpClient http = OidcHttp.Create();
        using var request = new HttpRequestMessage(HttpMethod.Get, listener.Url);

        Task<(HttpStatusCode, JsonDocument?)> sending = Task.Run(() => OidcHttp.Send(http, request, time), TestContext.Current.CancellationToken);
        await listener.Held.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(29));
        await Task.WhenAny(sending, Task.Delay(500, TestContext.Current.CancellationToken));
        Assert.False(sending.IsCompleted, "The reader gave up before 30 seconds.");
        time.Advance(TimeSpan.FromSeconds(1));

        CliException e = await Assert.ThrowsAsync<CliException>(() =>
            sending.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)
        );
        Assert.Equal((3, $"{listener.Url} cannot be reached: No whole answer came within 30 seconds."), (e.ExitCode, e.Message));
    }

    /// <summary>
    /// elm admin login stops with ELM_MALFORMED and exit code 3 when the service's OIDC settings are over the limit (design
    /// §16, V1d27): the command's path to the reader.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A listener that answers GET /v1/admin/oidc with 200 and 262,145 bytes.
    ///
    /// Steps:
    /// - Run elm admin login --url against it.
    ///
    /// Expected:
    /// - Exit code 3 and the one line "error: ELM_MALFORMED: The answer of (URL)v1/admin/oidc is larger than 262144 bytes.".
    /// </remarks>
    [Fact]
    public async Task Login_SettingsOverTheLimit_ExitsThreeWithMalformed()
    {
        await using RawAnswerServer listener = RawAnswerServer.Start(_ => RawAnswer.Whole("{}", OidcLimit + 1));

        (int code, _, string error) = await RunAsync(TimeProvider.System, "admin", "login", "--url", listener.Url.ToString());

        Assert.Equal((3, $"error: ELM_MALFORMED: The answer of {listener.Url}v1/admin/oidc is larger than 262144 bytes."), (code, error.Trim()));
    }

    /// <summary>
    /// elm admin login replaces the control characters of what it writes from the service and the identity provider: the
    /// issuer, the sign-in addresses and code, and the provider's error (design §16, V1d27).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A listener that plays the service and the identity provider: the OIDC settings name the issuer (URL) "/i" U+0085;
    ///   the discovery document names the same issuer; the device authorization has user_code "U" ESC "[2J",
    ///   verification_uri "http://x/" CSI "31m", verification_uri_complete "http://x/" CR U+0085, and interval 1; the token
    ///   endpoint answers 400 with error "access_denied" ESC "[0m".
    ///
    /// Steps:
    /// - Run elm admin login --url (URL), with waits that return at once.
    ///
    /// Expected:
    /// - Exit code 1; standard error is the four lines "Signing in to (URL) with the identity provider (URL)/i\u0085.", "Open
    ///   http://x/\u009B31m and enter the code U\u001B[2J.", "Or open http://x/\u000D\u0085", and "error: The identity
    ///   provider ended the sign-in: access_denied\u001B[0m.".
    /// </remarks>
    [Fact]
    public async Task Login_TextOfTheProviderAndTheService_ReplacesControlCharacters()
    {
        _environment["ELM_ADMIN_LOGIN"] = Path.Combine(_folder.FullName, "login");
        string issuer = string.Empty;
        await using RawAnswerServer listener = RawAnswerServer.Start(target =>
            target switch
            {
                "GET /v1/admin/oidc" => Json($$"""{"issuer":"{{issuer}}/i\u0085","clientId":"elm","scopes":["openid"]}"""),
                "POST /device" => Json(
                    """{"device_code":"d","user_code":"U\u001b[2J","verification_uri":"http://x/\u009b31m","verification_uri_complete":"http://x/\r\u0085","expires_in":600,"interval":1}"""
                ),
                "POST /token" => Json("""{"error":"access_denied\u001b[0m"}""", 400),
                _ => Json(
                    $$"""{"issuer":"{{issuer}}/i\u0085","device_authorization_endpoint":"{{issuer}}/device","token_endpoint":"{{issuer}}/token"}"""
                ),
            }
        );
        issuer = listener.Url.ToString().TrimEnd('/');

        (int code, _, string error) = await RunAsync(TimeProvider.System, "admin", "login", "--url", listener.Url.ToString());

        Assert.Equal(
            [
                $"Signing in to {listener.Url} with the identity provider {issuer}/i\\u0085.",
                "Open http://x/\\u009B31m and enter the code U\\u001B[2J.",
                "Or open http://x/\\u000D\\u0085",
                "error: The identity provider ended the sign-in: access_denied\\u001B[0m.",
            ],
            error.TrimEnd().Split(Environment.NewLine)
        );
        Assert.Equal(1, code);
    }

    /// <summary>
    /// elm license inspect replaces the control characters that a header holds as raw bytes, which GetRawText hands on as
    /// they are; the payloads and the JSON output escape them already (design §16, V1d27).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license file whose license token and one certificate have the header {"alg":"ES256","kid":"k" DEL U+0085}, the
    ///   two characters as raw UTF-8 bytes, and the payload {"lic":"L" ESC} with ESC as a JSON escape. No signature is
    ///   valid: inspect does not verify.
    ///
    /// Steps:
    /// - Inspect the file as text and with --json.
    ///
    /// Expected:
    /// - Text: exit 0, the lines "license header: {"alg":"ES256","kid":"k\u007F\u0085"}" and "certificate 1 header: " with
    ///   the same, and the payloads with "L\u001B"; --json: the same escapes. Neither output holds a control character
    ///   other than the line breaks.
    /// </remarks>
    [Fact]
    public async Task Inspect_HeaderWithRawControlCharacters_ReplacesThem()
    {
        byte[] header = [.. "{\"alg\":\"ES256\",\"kid\":\"k"u8, 0x7F, 0xC2, 0x85, .. "\"}"u8];
        string token =
            Convert.ToBase64String(header).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "." + Base64Url("{\"lic\":\"L\\u001b\"}") + ".c2ln";
        string file = Path.Combine(_folder.FullName, "a.elmlic");
        File.WriteAllText(file, $$"""{"license":"{{token}}","certificates":["{{token}}"]}""");
        const string Header = "{\"alg\":\"ES256\",\"kid\":\"k\\u007F\\u0085\"}";

        (int code, string output, string error) = await RunAsync(TimeProvider.System, "license", "inspect", file);
        Assert.True(code == 0, error);
        string[] lines = output.TrimEnd().Split(Environment.NewLine);
        Assert.Contains("license header: " + Header, lines);
        Assert.Contains("certificate 1 header: " + Header, lines);
        Assert.Contains("\"L\\u001B\"", output, StringComparison.Ordinal);
        Assert.DoesNotContain(output, c => char.IsControl(c) && c is not '\r' and not '\n');

        (code, output, error) = await RunAsync(TimeProvider.System, "license", "inspect", file, "--json");
        Assert.True(code == 0, error);
        Assert.Contains("\"k\\u007F\\u0085\"", output, StringComparison.Ordinal);
        Assert.Contains("\"L\\u001B\"", output, StringComparison.Ordinal);
        Assert.DoesNotContain(output, c => char.IsControl(c) && c is not '\r' and not '\n');
    }

    /// <summary>
    /// A success of the identity provider whose body of one byte is not JSON is "not JSON", not an answer without a body
    /// (design §16): only an empty body counts as none.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A listener that answers 200 with the one byte "x", and one that answers 200 with no body.
    ///
    /// Steps:
    /// - Send a GET through OidcHttp.Send to each.
    ///
    /// Expected:
    /// - "x": CliException with exit code 1 and "The answer of (URL) is not JSON."; no body: status 200 and no document.
    /// </remarks>
    [Fact]
    public async Task OidcAnswer_OneByteThatIsNotJson_IsNotJson()
    {
        using HttpClient http = OidcHttp.Create();
        await using (RawAnswerServer listener = RawAnswerServer.Start(_ => RawAnswer.Bytes("x"u8.ToArray())))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, listener.Url);
            CliException e = await Assert.ThrowsAsync<CliException>(() => Task.Run(() => OidcHttp.Send(http, request, TimeProvider.System)));
            Assert.Equal((1, $"The answer of {listener.Url} is not JSON."), (e.ExitCode, e.Message));
        }

        await using (RawAnswerServer listener = RawAnswerServer.Start(_ => RawAnswer.Bytes([])))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, listener.Url);
            (HttpStatusCode status, JsonDocument? body) = await Task.Run(() => OidcHttp.Send(http, request, TimeProvider.System));
            Assert.Equal((HttpStatusCode.OK, null), (status, body));
        }
    }

    private static RawAnswer Json(string text, int status = 200) => RawAnswer.Bytes(Encoding.UTF8.GetBytes(text), status);

    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task ReadHeadAsync(NetworkStream stream)
    {
        var received = new StringBuilder();
        byte[] buffer = new byte[4096];
        while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);
            if (read == 0)
            {
                return;
            }

            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
    }

    // Waits on the real clock, with a bound, for elm to have written what the test waits for.
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 2_000 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "elm did not write what was expected.");
    }

    private Task<(int Code, string Out, string Error)> RunAsync(TimeProvider time, params string[] args) => RunAsync(time, new SyncWriter(), args);

    private Task<(int Code, string Out, string Error)> RunAsync(TimeProvider time, SyncWriter output, string[] args) =>
        Task.Run(
            () =>
            {
                var error = new SyncWriter();
                var host = new CliHost
                {
                    Out = output,
                    Error = error,
                    Time = time,
                    GetEnvironmentVariable = name => _environment.GetValueOrDefault(name),
                    ReadSecret = null,
                    Wait = _ => { },
                };
                int code = ElmApp.Run(args, host);
                return (code, output.Text, error.Text);
            },
            TestContext.Current.CancellationToken
        );

    // A fake clock that completes Made once elm makes a timer while the condition holds. A move of the clock made before
    // that does not count toward the timer's wait, so the test moves the clock only once the timer is there (Issue #521).
    // The timer's length is not matched: a shorter wait in elm then ends the command before the full move, and the test
    // says so.
    private sealed class TimerAfterTime(DateTimeOffset start, Func<bool> condition) : FakeTimeProvider(start)
    {
        private readonly TaskCompletionSource _made = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Made => _made.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = base.CreateTimer(callback, state, dueTime, period);
            if (condition())
            {
                _made.TrySetResult();
            }

            return timer;
        }
    }

    // A writer that the test may read while elm writes to it on another thread.
    private sealed class SyncWriter : TextWriter
    {
        private readonly StringBuilder _text = new();

        public override Encoding Encoding => Encoding.UTF8;

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public override void Write(char value)
        {
            lock (_text)
            {
                _text.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_text)
            {
                _text.Append(value);
            }
        }
    }
}
