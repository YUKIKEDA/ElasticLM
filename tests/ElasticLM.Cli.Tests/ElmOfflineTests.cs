using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ElasticLM.Service;
using ElasticLM.Service.Tests;

namespace ElasticLM.Cli.Tests;

/// <summary>
/// <c>elm offline process</c> (design §12.8.4, §16) against a real <c>elm-service</c> on loopback (SQLite: the command only
/// passes files over HTTP), and against a fake one for the answer's size limit.
/// </summary>
public sealed class ElmOfflineTests
{
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
    /// - 0; 0 with the answer written as received; 3 "The service's answer is larger than 262144 bytes.".
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

    private (int Code, string Out, string Error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var host = new CliHost
        {
            Out = output,
            Error = error,
            Time = TimeProvider.System,
            GetEnvironmentVariable = name => _environment.GetValueOrDefault(name),
            ReadSecret = null,
        };
        int code = ElmApp.Run(args, host);
        return (code, output.ToString(), error.ToString());
    }
}
