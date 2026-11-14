using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ElasticLM.Client.Tests;
using ElasticLM.Service;
using ElasticLM.Service.Tests;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Cli.Tests;

/// <summary>
/// <c>elm offline process</c> (design §12.8.4, §16) against a real <c>elm-service</c> on loopback (SQLite: the command only
/// passes files over HTTP), and against a fake one for the answer's size limit and its time limit.
/// </summary>
public sealed class ElmOfflineTests
{
    private const int MaxAnswer = 256 * 1024;

    // The login file is pointed at a file that does not exist, so the tests never read the person's own login (M5a).
    private readonly Dictionary<string, string> _environment = new()
    {
        ["ELM_ADMIN_LOGIN"] = Path.Combine(
            Path.GetTempPath(),
            "elm-offline-tests-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)),
            "login"
        ),
    };

    /// <summary>
    /// A request file is passed and its answer written; an answer file is not overwritten without --force; a deactivation
    /// proof gets no answer file; with ELM_API_KEY the administration entry is used; a refusal is told with its code.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with product app (feature core), a policy, and a license; a device; an API key with activation.offline.
    ///
    /// Steps:
    /// - Process an activation file to resp; again to resp; again with --force and --json; process an expired file;
    ///   process a deactivation proof with --out, then without; set ELM_API_KEY and process an activation file of a second
    ///   license; read the audit log.
    ///
    /// Expected:
    /// - 0 with "activation <ID>, license <ID>, seq 1: resp", and resp holds {activationId, license}; 2 "… exists. Pass
    ///   --force …"; 0 with the JSON output, the same answer; 1 with "error: ELM_REQUEST_EXPIRED: The request has expired; make
    ///   a new one."; 2 "… leave out --out."; 0 "deactivated"; 0, and an activation.offline row by the API key; no folder
    ///   where the login would be.
    /// </remarks>
    [Fact]
    public async Task Process_PassesFilesAndWritesAnswers()
    {
        await using var test = new TestService();
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        (string licenseId, string key, string secondKey) = await LicensesAsync(admin);
        using var device = TestDevice.Create(test, service);
        string[] prints = TestDevice.Fingerprints();
        string url = service.Url.ToString();
        string request = Write(test, "a.elmreq", device.ActivationFile(key, prints));
        string answer = test.FilePath("a.elmresp");

        (int code, string output, string error) = Run("offline", "process", "--url", url, "--in", request, "--out", answer);
        Assert.True(code == 0, error);
        using (JsonDocument issued = JsonDocument.Parse(File.ReadAllBytes(answer)))
        {
            string activationId = issued.RootElement.GetProperty("activationId").GetString()!;
            Assert.Equal($"activation {activationId}, license {licenseId}, seq 1: {answer}", output.Trim());
            Assert.Equal(JsonValueKind.Object, issued.RootElement.GetProperty("license").ValueKind);
        }

        byte[] first = File.ReadAllBytes(answer);
        Assert.Equal(
            (2, $"error: The file '{answer}' exists. Pass --force to overwrite it."),
            Trimmed(Run("offline", "process", "--url", url, "--in", request, "--out", answer))
        );
        (code, output, _) = Run("offline", "process", "--url", url, "--in", request, "--out", answer, "--force", "--json");
        Assert.Equal(0, code);
        Assert.Equal(first, File.ReadAllBytes(answer));
        using (JsonDocument shown = JsonDocument.Parse(output))
        {
            Assert.Equal(
                (false, 1L, answer),
                (
                    shown.RootElement.GetProperty("deactivated").GetBoolean(),
                    shown.RootElement.GetProperty("seq").GetInt64(),
                    shown.RootElement.GetProperty("file").GetString()
                )
            );
        }

        string expired = Write(test, "old.elmreq", device.ActivationFile(key, prints, iat: -604_800));
        Assert.Equal(
            (1, "error: ELM_REQUEST_EXPIRED: The request has expired; make a new one."),
            Trimmed(Run("offline", "process", "--url", url, "--in", expired, "--out", test.FilePath("old.elmresp")))
        );

        using JsonDocument kept = JsonDocument.Parse(first);
        string proof = Write(test, "a.elmdeact", device.DeactivationFile(kept.RootElement.GetProperty("activationId").GetString()!, 1));
        Assert.Equal(
            (2, "error: A deactivation proof gets no answer file; leave out --out."),
            Trimmed(Run("offline", "process", "--url", url, "--in", proof, "--out", test.FilePath("x")))
        );
        Assert.Equal((0, "deactivated"), Trimmed(Run("offline", "process", "--url", url, "--in", proof), output: true));

        _environment["ELM_API_KEY"] = await TestService.CreateKeyAsync(admin, "\"activation.offline\"");
        string second = Write(test, "b.elmreq", device.ActivationFile(secondKey, prints));
        Assert.Equal(0, Run("offline", "process", "--url", url, "--in", second, "--out", test.FilePath("b.elmresp")).Code);
        Assert.Contains(
            await TestService.AuditRowsAsync(test, service, owner),
            r => r.GetProperty("action").GetString() == "activation.offline" && r.GetProperty("actorType").GetString() == "apikey"
        );

        // design §16 (M5a): without a login, nothing is created where the login would be.
        Assert.False(Directory.Exists(Path.GetDirectoryName(_environment["ELM_ADMIN_LOGIN"])));
    }

    /// <summary>
    /// Arguments and files are checked before anything is sent; an unreachable service is exit code 3.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A request file of a made-up device key; a file of 64 KiB and one of 64 KiB and a byte.
    ///
    /// Steps:
    /// - Process: with an http URL that is not loopback; an ftp URL; a missing file; a text that is no request; an
    ///   activation request without --out; the file one byte too large; the request to a closed loopback port.
    ///
    /// Expected:
    /// - 2 with "--url must be …" twice; 2 "… does not exist."; 2 "… is not an offline request …"; 2 "… needs --out …"; 2
    ///   "… larger than 65536 bytes."; 3 "The service cannot be reached: …".
    /// </remarks>
    [Fact]
    public async Task Process_ChecksItsInput()
    {
        await using var test = new TestService();
        using var device = TestDevice.Create(test, (await StartedAsync(test)).Service);
        string request = Write(test, "a.elmreq", device.ActivationFile("ELM-AAAA-AAAA-AAAA-AAAA", TestDevice.Fingerprints()));
        string answer = test.FilePath("a.elmresp");
        const string BadUrl = "error: --url must be an https URL, or http on a loopback address: ";

        Assert.Equal(
            (2, BadUrl + "'http://example.com/'."),
            Trimmed(Run("offline", "process", "--url", "http://example.com/", "--in", request, "--out", answer))
        );
        Assert.Equal(
            (2, BadUrl + "'ftp://127.0.0.1/'."),
            Trimmed(Run("offline", "process", "--url", "ftp://127.0.0.1/", "--in", request, "--out", answer))
        );
        string missing = test.FilePath("missing.elmreq");
        Assert.Equal(
            (2, $"error: The file '{missing}' does not exist."),
            Trimmed(Run("offline", "process", "--url", "https://127.0.0.1/", "--in", missing, "--out", answer))
        );
        string text = Write(test, "text.elmreq", "hello");
        Assert.Equal(
            (2, $"error: The file '{text}' is not an offline request (elm-actreq+jws or elm-deact+jws)."),
            Trimmed(Run("offline", "process", "--url", "https://127.0.0.1/", "--in", text, "--out", answer))
        );
        Assert.Equal(
            (2, "error: An activation or refresh request needs --out for its answer."),
            Trimmed(Run("offline", "process", "--url", "https://127.0.0.1/", "--in", request))
        );
        string large = Write(test, "large.elmreq", File.ReadAllText(request).PadRight((64 * 1024) + 1));
        Assert.Equal(
            (2, $"error: The file '{large}' is larger than 65536 bytes."),
            Trimmed(Run("offline", "process", "--url", "https://127.0.0.1/", "--in", large, "--out", answer))
        );

        using var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        int port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        (int code, _, string error) = Run("offline", "process", "--url", $"http://127.0.0.1:{port}/", "--in", request, "--out", answer);
        Assert.Equal(3, code);
        Assert.StartsWith("error: The service cannot be reached: ", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The request file is read up to 64 KiB and the service's answer up to 256 KiB, each at the limit and one byte past it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A real service with a license, and a fake loopback service that answers 200 with a body of a chosen size.
    ///
    /// Steps:
    /// - Process a request padded with spaces to 64 KiB against the real service; against the fake, an answer of an issued
    ///   form padded to 256 KiB, then one of 256 KiB and a byte.
    ///
    /// Expected:
    /// - 0; 0 with the answer written as received; 3 "ELM_MALFORMED: The service's answer is larger than 262144 bytes.".
    /// </remarks>
    [Fact]
    public async Task Process_ReadsUpToItsLimits()
    {
        await using var test = new TestService();
        (ElmService service, HttpClient admin) = await StartedAsync(test);
        (_, string key, _) = await LicensesAsync(admin);
        using var device = TestDevice.Create(test, service);
        string padded = Write(test, "padded.elmreq", device.ActivationFile(key, TestDevice.Fingerprints()).PadRight(64 * 1024));
        Assert.Equal(0, Run("offline", "process", "--url", service.Url.ToString(), "--in", padded, "--out", test.FilePath("padded.elmresp")).Code);

        string request = Write(test, "a.elmreq", device.ActivationFile(key, TestDevice.Fingerprints()));
        string token = "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"lic\":\"L\",\"seq\":4}")).TrimEnd('=') + ".c2ln";
        string issued = $$$"""{"activationId":"A","license":{"license":"{{{token}}}"}}""";
        string atLimit = issued.PadRight(256 * 1024);
        string answer = test.FilePath("fake.elmresp");
        Assert.Equal(0, await RunAgainstAsync(atLimit, request, answer));
        Assert.Equal(atLimit, await File.ReadAllTextAsync(answer, TestContext.Current.CancellationToken));
        Assert.Equal(3, await RunAgainstAsync(atLimit + " ", request, test.FilePath("over.elmresp")));
    }

    /// <summary>
    /// An answer of the service that goes on past 256 KiB, or that declares such a body, ends elm offline process with exit
    /// code 3 and ELM_MALFORMED, and elm takes no more of it than the limit (design §12.8.4, §16, the V1d6 Grill).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service on loopback that writes its answer byte by byte. The answer to the POST, by shape: endless (64 MiB of
    ///   spaces in chunks, without a last chunk), until-close (64 MiB without a length), declared (a Content-Length of 4 GiB
    ///   and no body), one-over (262,145 bytes without a length). Each with status 200 and with status 503.
    /// - An activation request file.
    ///
    /// Steps:
    /// - Process the request against the service; wait until the service has stopped writing.
    ///
    /// Expected:
    /// - Exit code 3 and the one line "error: ELM_MALFORMED: The service's answer is larger than 262144 bytes."; no answer
    ///   file.
    /// - endless and until-close: the service wrote more than 256 KiB and at most 256 KiB and 16 MiB, which is what the
    ///   buffers of the two sockets may hold; an elm that reads the whole answer first takes all 64 MiB. declared: no body
    ///   byte. one-over: all 262,145 bytes.
    /// </remarks>
    /// <param name="shape">The shape of the answer.</param>
    /// <param name="status">The status of the answer.</param>
    [Theory]
    [InlineData("endless", 200)]
    [InlineData("endless", 503)]
    [InlineData("until-close", 200)]
    [InlineData("until-close", 503)]
    [InlineData("declared", 200)]
    [InlineData("declared", 503)]
    [InlineData("one-over", 200)]
    [InlineData("one-over", 503)]
    public async Task Process_AnswerOverTheLimit_ExitsThreeAndIsNotTaken(string shape, int status)
    {
        const long Offered = 64L * 1024 * 1024;
        const long SocketBuffers = 16L * 1024 * 1024;
        await using var test = new TestService();
        using var device = TestDevice.Create(test, (await StartedAsync(test)).Service);
        string request = Write(test, "a.elmreq", device.ActivationFile("ELM-AAAA-AAAA-AAAA-AAAA", TestDevice.Fingerprints()));
        string answer = test.FilePath("a.elmresp");
        RawAnswer offered = shape switch
        {
            "endless" => RawAnswer.Endless(Offered, status),
            "until-close" => RawAnswer.UntilClose(string.Empty, Offered, status),
            "declared" => RawAnswer.DeclaredOnly(4_294_967_296L, status),
            _ => RawAnswer.UntilClose(string.Empty, MaxAnswer + 1, status),
        };
        await using RawAnswerServer service = RawAnswerServer.Start(_ => offered);

        (int Code, string Out, string Error) result = await Task.Run(
            () => Run("offline", "process", "--url", service.Url.ToString(), "--in", request, "--out", answer),
            TestContext.Current.CancellationToken
        );
        long written = await service.SettledBodyBytesAsync();

        Assert.Equal((3, "error: ELM_MALFORMED: The service's answer is larger than 262144 bytes."), Trimmed(result));
        Assert.False(File.Exists(answer));
        (long least, long most) = shape switch
        {
            "declared" => (0L, 0L),
            "one-over" => (MaxAnswer + 1, MaxAnswer + 1),
            _ => (MaxAnswer + 1, MaxAnswer + SocketBuffers),
        };
        Assert.InRange(written, least, most);
    }

    /// <summary>
    /// An answer of the service that stops ends elm offline process after 30 seconds of the command's clock, with exit code
    /// 3, whether the answer stops before its head or inside its body (design §12.8.4, the V1d6 Grill).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service on loopback that reads the POST and then sends nothing (no-head), or sends a head with status 200 and
    ///   the first byte of a body in chunks, and then nothing (body).
    /// - elm on a fake clock.
    ///
    /// Steps:
    /// - Process an activation request; once the service waits, move the clock 30 seconds.
    ///
    /// Expected:
    /// - Exit code 3 and the one line "error: The service cannot be reached: No whole answer came within 30 seconds."; no
    ///   answer file, within 20 seconds of the real clock. An elm whose limit runs on the real clock would not return in
    ///   that time.
    /// </remarks>
    /// <param name="stop">Where the answer stops.</param>
    [Theory]
    [InlineData("no-head")]
    [InlineData("body")]
    public async Task Process_AnswerThatStops_ExitsThreeAfterThirtySeconds(string stop)
    {
        await using var test = new TestService();
        using var device = TestDevice.Create(test, (await StartedAsync(test)).Service);
        string request = Write(test, "a.elmreq", device.ActivationFile("ELM-AAAA-AAAA-AAAA-AAAA", TestDevice.Fingerprints()));
        string answer = test.FilePath("a.elmresp");
        await using RawAnswerServer service = RawAnswerServer.Start(_ => stop == "no-head" ? RawAnswer.Silent : RawAnswer.Stalled("{", 1));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

        Task<(int Code, string Out, string Error)> running = Task.Run(
            () => Run(time, "offline", "process", "--url", service.Url.ToString(), "--in", request, "--out", answer),
            TestContext.Current.CancellationToken
        );
        await service.Held.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(30));
        (int Code, string Out, string Error) result = await running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.Equal((3, "error: The service cannot be reached: No whole answer came within 30 seconds."), Trimmed(result));
        Assert.False(File.Exists(answer));
    }

    /// <summary>
    /// Text that cannot be read, in the header of the request file or in the service's problem answer, and a problem answer
    /// that is not a JSON object, are reported as such, without an exception (design §8.3, V1d23, Issues #342 and #402).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A request file whose header holds typ elm-actreq+jws and: the value "\ud800" in typ; the member "\ud800" and 16 x
    ///   after typ (a name is looked up from the last member); the value "x" and the byte 0xFF in typ; the value
    ///   "\udc00\ud800" in typ.
    /// - A real request file, and a fake loopback service that answers 400 with a problem body holding the same four shapes
    ///   in code, and with the body [].
    ///
    /// Steps:
    /// - Process each request file; process the real one against each answer.
    ///
    /// Expected:
    /// - Each file: exit 2, "error: The file '…' is not an offline request (elm-actreq+jws or elm-deact+jws)."; each
    ///   answer: exit 1, "error: ?:" (before V1d23 each threw InvalidOperationException).
    /// </remarks>
    [Fact]
    public async Task Process_TextThatIsNotUnicode_IsReportedWithoutAnException()
    {
        await using var test = new TestService();
        using var device = TestDevice.Create(test, (await StartedAsync(test)).Service);
        string request = Write(test, "a.elmreq", device.ActivationFile("ELM-AAAA-AAAA-AAAA-AAAA", TestDevice.Fingerprints()));
        string answer = test.FilePath("a.elmresp");
        static byte[] Shaped(string shape, string name, string value)
        {
            string text = shape switch
            {
                "value" => $$"""{"{{name}}":"\ud800"}""",
                "name" => $$"""{"{{name}}":"{{value}}","\ud800xxxxxxxxxxxxxxxx":1}""",
                "reversed" => $$"""{"{{name}}":"\udc00\ud800"}""",
                _ => $$"""{"{{name}}":"x{{'ÿ'}}"}""",
            };
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            int at = Array.IndexOf(bytes, (byte)0xC3);
            return at < 0 ? bytes : [.. bytes[..at], 0xFF, .. bytes[(at + 2)..]];
        }

        foreach (string shape in new[] { "value", "name", "not-utf8", "reversed" })
        {
            string file = Write(
                test,
                shape + ".elmreq",
                Convert.ToBase64String(Shaped(shape, "typ", "elm-actreq+jws")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".e30.c2ln"
            );
            Assert.Equal(
                (2, $"error: The file '{file}' is not an offline request (elm-actreq+jws or elm-deact+jws)."),
                Trimmed(Run("offline", "process", "--url", "http://127.0.0.1:1/", "--in", file, "--out", answer))
            );
        }

        foreach (
            byte[] body in new[]
            {
                Shaped("value", "code", "ELM_X"),
                Shaped("name", "code", "ELM_X"),
                Shaped("not-utf8", "code", "ELM_X"),
                Shaped("reversed", "code", "ELM_X"),
                "[]"u8.ToArray(),
            }
        )
        {
            await using RawAnswerServer service = RawAnswerServer.Start(_ => RawAnswer.Bytes(body, 400));
            (int Code, string Out, string Error) result = await Task.Run(
                () => Run("offline", "process", "--url", service.Url.ToString(), "--in", request, "--out", answer),
                TestContext.Current.CancellationToken
            );
            Assert.Equal((1, "error: ?:"), Trimmed(result));
            Assert.False(File.Exists(answer));
        }
    }

    /// <summary>
    /// A redirect is not followed (design §12.1): the request file holds the license key, and a 307 would send it on.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A fake loopback service that answers its one request with 307 and a Location of /moved.
    ///
    /// Steps:
    /// - Process an activation request against it.
    ///
    /// Expected:
    /// - Exit code 1 (an answer that is not a success), and no answer file; a followed redirect would wait on the fake
    ///   service, which answers once, and end with exit code 3.
    /// </remarks>
    [Fact]
    public async Task Process_Redirect_IsNotFollowed()
    {
        await using var test = new TestService();
        using var device = TestDevice.Create(test, (await StartedAsync(test)).Service);
        string request = Write(test, "a.elmreq", device.ActivationFile("ELM-AAAA-AAAA-AAAA-AAAA", TestDevice.Fingerprints()));
        string answer = test.FilePath("a.elmresp");

        Assert.Equal(1, await RunAgainstAsync(string.Empty, request, answer, "307 Temporary Redirect", "Location: /moved\r\n"));
        Assert.False(File.Exists(answer));
    }

    private static async Task<(ElmService Service, HttpClient Admin)> StartedAsync(TestService test)
    {
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        return (service, test.Http(service, owner));
    }

    private static async Task<(string LicenseId, string Key, string SecondKey)> LicensesAsync(HttpClient admin)
    {
        string product = (await TestService.CreatedAsync(admin, "v1/admin/products", """{"code":"app","name":"app"}"""))
            .GetProperty("id")
            .GetString()!;
        await TestService.CreatedAsync(admin, $"v1/admin/products/{product}/features", """{"code":"core","name":"core"}""");
        string policy = await TestService.PolicyAsync(admin, product, string.Empty);
        string body = $$"""{"policyId":"{{policy}}","name":"n","features":[{"code":"core"}]}""";
        JsonElement first = await TestService.CreatedAsync(admin, "v1/admin/licenses", body);
        JsonElement second = await TestService.CreatedAsync(admin, "v1/admin/licenses", body);
        return (first.GetProperty("id").GetString()!, first.GetProperty("key").GetString()!, second.GetProperty("key").GetString()!);
    }

    private static string Write(TestService test, string name, string text)
    {
        string path = test.FilePath(name);
        File.WriteAllText(path, text);
        return path;
    }

    private static (int Code, string Text) Trimmed((int Code, string Out, string Error) result, bool output = false) =>
        (result.Code, (output ? result.Out : result.Error).Trim());

    // A fake service on loopback that answers one request with a status (200 by default) and a body, read after the whole
    // request arrived.
    private async Task<int> RunAgainstAsync(string body, string request, string answer, string status = "200 OK", string headers = "")
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task serve = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            NetworkStream stream = client.GetStream();
            var received = new StringBuilder();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);
                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                string text = received.ToString();
                int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (read == 0 || (end >= 0 && text.Length - end - 4 >= ContentLength(text)))
                {
                    break;
                }
            }

            byte[] bytes = Encoding.UTF8.GetBytes(body);
            byte[] head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\n{headers}Content-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"
            );
            await stream.WriteAsync(head, TestContext.Current.CancellationToken);
            await stream.WriteAsync(bytes, TestContext.Current.CancellationToken);
        });
        int code = await Task.Run(() => Run("offline", "process", "--url", $"http://127.0.0.1:{port}/", "--in", request, "--out", answer).Code);
        await serve;
        return code;
    }

    private static int ContentLength(string head)
    {
        foreach (string line in head.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                return int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return 0;
    }

    private (int Code, string Out, string Error) Run(params string[] args) => Run(TimeProvider.System, args);

    private (int Code, string Out, string Error) Run(TimeProvider time, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var host = new CliHost
        {
            Out = output,
            Error = error,
            Time = time,
            GetEnvironmentVariable = name => _environment.GetValueOrDefault(name),
            ReadSecret = null,
        };
        int code = ElmApp.Run(args, host);
        return (code, output.ToString(), error.ToString());
    }
}
