using System.Text.Json;
using ElasticLM.Cli;
using ElasticLM.Client;
using ElasticLM.Issuing;

namespace ElasticLM.Server.Tests;

/// <summary>
/// <c>elm server</c> and <c>elm log verify</c> against a server in the same process (design §13.7, §16, §18.1).
/// </summary>
public sealed class ElmCommandsTests : IAsyncDisposable
{
    private readonly Deployment _deployment = new();

    public ValueTask DisposeAsync() => _deployment.DisposeAsync();

    /// <summary>
    /// elm server reads the administrator token from the data folder and calls each operation; the answers print as text or
    /// JSON; a refusal exits with 1, input errors with 2, and an unreachable server with 3.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server past its window with one lease.
    ///
    /// Steps:
    /// - status, status --json, leases, seal, reload, release of an unknown ID, release of the lease; status without --data
    ///   and --product; status with a data folder that holds no token.
    ///
    /// Expected:
    /// - Exit 0 with the license, the lease ID, "reloaded"; exit 1 with ELM_UNKNOWN_LEASE; exit 0 "released" and no seat in
    ///   use; exit 2 twice.
    /// </remarks>
    [Fact]
    public async Task ServerCommands_CallTheAdministrationApi()
    {
        ElmServer server = await _deployment.StartServerAsync();
        _deployment.Time.Advance(TimeSpan.FromMinutes(10));
        Lease lease = Assert
            .IsType<LeaseResult.Acquired>(
                await _deployment.Client(server.PublicUrls[0]).AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken)
            )
            .Lease;
        string[] target = ["--data", server.DataDirectory];

        (int code, string output, _) = Run(["server", "status", .. target]);
        Assert.Equal(0, code);
        Assert.Contains("license: lic-srv", output, StringComparison.Ordinal);
        Assert.Contains("pro: 1 of 2 seats in use", output, StringComparison.Ordinal);
        using (JsonDocument json = JsonDocument.Parse(Run(["server", "status", "--json", .. target]).Out))
        {
            Assert.Equal("lic-srv", json.RootElement.GetProperty("license").GetString());
        }

        Assert.Contains(lease.Id, Run(["server", "leases", .. target]).Out, StringComparison.Ordinal);
        Assert.Equal(0, Run(["server", "seal", .. target]).Code);
        Assert.Contains("reloaded: license lic-srv", Run(["server", "reload", .. target]).Out, StringComparison.Ordinal);

        (int unknown, _, string unknownError) = Run(["server", "release", "nope", .. target]);
        Assert.Equal(1, unknown);
        Assert.Contains("ELM_UNKNOWN_LEASE", unknownError, StringComparison.Ordinal);
        (int released, string releasedOutput, _) = Run(["server", "release", lease.Id, .. target]);
        Assert.Equal(0, released);
        Assert.Contains("released", releasedOutput, StringComparison.Ordinal);
        Assert.Equal(0, server.Engine.SeatsInUse("pro"));

        Assert.Equal(2, Run(["server", "status"]).Code);
        Assert.Equal(2, Run(["server", "status", "--data", _deployment.NewFolder()]).Code);
    }

    /// <summary>
    /// elm server status prints "never" for a server license without expiry, whose status answer leaves out licenseExpires
    /// (design §13.7, Issue #203).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server whose license has no expiry.
    ///
    /// Steps:
    /// - status, and status --json.
    ///
    /// Expected:
    /// - Exit 0 with "expires: never"; the JSON answer has no licenseExpires member.
    /// </remarks>
    [Fact]
    public async Task ServerStatus_LicenseWithoutExpiry_PrintsNever()
    {
        string data = _deployment.NewFolder();
        _deployment.WriteServerLicense(data, spec => spec with { ExpiresAt = null });
        ElmServer server = await _deployment.StartServerAsync(data);
        string[] target = ["--data", server.DataDirectory];

        (int code, string output, _) = Run(["server", "status", .. target]);

        Assert.Equal(0, code);
        Assert.Contains("expires: never", output, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(Run(["server", "status", "--json", .. target]).Out);
        Assert.False(json.RootElement.TryGetProperty("licenseExpires", out _));
    }

    /// <summary>
    /// elm server reload prints a refused license's error code once, before the server's sentence (design §13.7, V1c6 Q5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A running server; a license bound to another installation key written over its license file.
    ///
    /// Steps:
    /// - elm server reload.
    ///
    /// Expected:
    /// - Exit 1, nothing on standard output, and the one line "error: ELM_WRONG_INSTALLATION: The server license does not
    ///   verify: The license is bound to another installation key. This server's installation key is {thumbprint}.".
    /// </remarks>
    [Fact]
    public async Task ServerReload_LicenseOfAnotherKey_PrintsTheCodeOnce()
    {
        string data = _deployment.NewFolder();
        ElmServer server = await _deployment.StartServerAsync(data);
        string otherKey = Deployment.ServerKeyId(_deployment.NewFolder());
        _deployment.WriteServerLicense(data, spec => spec with { InstallationKeyThumbprint = otherKey });

        (int code, string output, string error) = Run(["server", "reload", "--data", data]);

        Assert.Equal(1, code);
        Assert.Equal(string.Empty, output);
        Assert.Equal(
            "error: ELM_WRONG_INSTALLATION: The server license does not verify: The license is bound to another installation key. "
                + $"This server's installation key is {Deployment.ServerKeyId(data)}.{Environment.NewLine}",
            error
        );
    }

    /// <summary>
    /// elm server status, leases, release, reload, and seal say which of two things keeps them from the server: no socket
    /// file, or a socket file that refuses the connection; both exit with 3 (design §13.7, V1d2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder in which a server ran and stopped: it holds the administrator token and no admin.sock.
    ///
    /// Steps:
    /// - Run the five commands with --data. Leave a socket file at admin.sock on which nobody listens, and run them again.
    ///
    /// Expected:
    /// - First, each exits 3 with the one line "error: The server cannot be reached: the administration socket '{path}' does
    ///   not exist.".
    /// - Then each exits 3 with one line that begins "error: The server cannot be reached: " and does not say that the
    ///   socket does not exist.
    /// </remarks>
    [Fact]
    public async Task ServerCommands_NoSocketFileOrARefusedOne_ExitWith3AndSayWhich()
    {
        ElmServer server = await _deployment.StartServerAsync();
        string data = server.DataDirectory;
        await server.DisposeAsync();
        string[][] commands =
        [
            ["status"],
            ["leases"],
            ["release", "some-lease"],
            ["reload"],
            ["seal"],
        ];

        foreach (string[] command in commands)
        {
            (int code, _, string error) = Run(["server", .. command, "--data", data]);

            Assert.Equal(
                (3, $"error: The server cannot be reached: the administration socket '{Path.Combine(data, "admin.sock")}' does not exist."),
                (code, error.Trim())
            );
        }

        FakeAdminSocket.LeaveFile(data);
        foreach (string[] command in commands)
        {
            (int code, _, string error) = Run(["server", .. command, "--data", data]);

            Assert.Equal(3, code);
            Assert.StartsWith("error: The server cannot be reached: ", error, StringComparison.Ordinal);
            Assert.DoesNotContain("does not exist", error, StringComparison.Ordinal);
            Assert.Single(error.Trim().Split('\n'));
        }
    }

    /// <summary>
    /// elm server status, leases, release, reload, and seal print an error answer of the administration API in one line and
    /// exit with 1: its code and detail when it has a code, and otherwise its status code, as the server license commands do
    /// (design §13.7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder in which a server ran and stopped, so it holds the administrator token; a listener of the test on
    ///   admin.sock that answers each request with a given answer.
    ///
    /// Steps:
    /// - Run the five commands against a listener that answers 409 with the problem body of code ELM_RETURN_NOT_ALLOWED and
    ///   detail "Why."; then against one that answers 404 without a body.
    ///
    /// Expected:
    /// - First, each exits 1 with the one line "error: ELM_RETURN_NOT_ALLOWED: Why.".
    /// - Then each exits 1 with the one line "error: The server's administration API answered 404.".
    /// - Nothing goes to standard output.
    /// </remarks>
    [Fact]
    public async Task ServerCommands_ErrorAnswerWithAndWithoutACode_PrintOneLineAndExitWith1()
    {
        ElmServer server = await _deployment.StartServerAsync();
        string data = server.DataDirectory;
        await server.DisposeAsync();
        const string Body = "{\"code\":\"ELM_RETURN_NOT_ALLOWED\",\"detail\":\"Why.\"}";
        string withCode =
            $"HTTP/1.1 409 Conflict\r\nContent-Type: application/problem+json\r\nContent-Length: {Body.Length}\r\nConnection: close\r\n\r\n{Body}";
        const string WithoutCode = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        string[][] commands =
        [
            ["status"],
            ["leases"],
            ["release", "some-lease"],
            ["reload"],
            ["seal"],
        ];

        foreach (
            (string answer, string line) in new[]
            {
                (withCode, "error: ELM_RETURN_NOT_ALLOWED: Why."),
                (WithoutCode, "error: The server's administration API answered 404."),
            }
        )
        {
            using var listener = new FakeAdminSocket(data, _ => answer);
            foreach (string[] command in commands)
            {
                (int code, string output, string error) = Run(["server", .. command, "--data", data]);

                Assert.True((1, line) == (code, error.Trim()), $"elm server {command[0]}: {code} {error}");
                Assert.Equal(string.Empty, output);
            }
        }
    }

    /// <summary>
    /// Every elm server command checks the length of the socket's path before it reads the administrator token: a data folder
    /// whose socket path is too long stops the command with the line of the length and exit code 2, and is never taken for a
    /// folder whose server does not run (design §13.7, V1d2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The path of a data folder that does not exist, so it holds no token, padded so that admin.sock in it is one byte
    ///   over this system's limit.
    ///
    /// Steps:
    /// - Run each of the thirteen elm server commands with --data, and the other options each needs.
    ///
    /// Expected:
    /// - Each exits 2 with the one line "error: The path of the administration socket '{path}' is {limit + 1} bytes in UTF-8,
    ///   and this system allows {limit}. Use a data folder with a shorter path."; nothing is written.
    /// </remarks>
    [Fact]
    public void ServerCommands_SocketPathTooLong_StopBeforeTheToken()
    {
        int limit = AdminSocket.MaxPathBytes;
        int name = limit + 1 - System.Text.Encoding.UTF8.GetByteCount(_deployment.Directory) - 1 - "/admin.sock".Length;
        string data = Path.Combine(_deployment.Directory, new string('a', name));
        string socket = Path.Combine(data, "admin.sock");
        string line =
            $"error: The path of the administration socket '{socket}' is {limit + 1} bytes in UTF-8, and this system allows {limit}. "
            + "Use a data folder with a shorter path.";
        string made = _deployment.FilePath("made.file");

        foreach (string[] command in AllServerCommands(data, made))
        {
            (int code, string output, string error) = Run(command);

            Assert.True((2, line) == (code, error.Trim()), $"elm server {command[1]}: {code} {error}");
            Assert.Equal(string.Empty, output);
        }

        Assert.False(Path.Exists(made));
        Assert.False(Path.Exists(data));
    }

    /// <summary>
    /// --url is gone from every elm server command: passing it is the parse error of any unknown option (design §13.7, V1d2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The path of a data folder that does not exist.
    ///
    /// Steps:
    /// - Run each of the thirteen elm server commands with its options and "--url http://127.0.0.1:7071/".
    ///
    /// Expected:
    /// - Each exits 2, and standard error names '--url' as an unrecognized argument.
    /// </remarks>
    [Fact]
    public void ServerCommands_UrlOption_IsAnUnknownOption()
    {
        string data = _deployment.NextFolderPath();

        foreach (string[] command in AllServerCommands(data, _deployment.FilePath("made.file")))
        {
            (int code, _, string error) = Run([.. command, "--url", "http://127.0.0.1:7071/"]);

            Assert.True(code == 2, $"elm server {command[1]}: {code} {error}");
            Assert.Contains("Unrecognized command or argument '--url'", error, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The HTTP handler of the elm server commands uses no proxy and follows no redirect, and the address of its requests
    /// leads nowhere without the handler's own connection to the socket file (design §13.7, V1d2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The handler that elm server makes for the socket file of a data folder; an HTTP client without that handler, and
    ///   without a proxy.
    ///
    /// Steps:
    /// - Read the handler's settings. Send GET admin/v1/status under the commands' base address with the other client.
    ///
    /// Expected:
    /// - UseProxy and AllowAutoRedirect are false, and the handler has a connect callback. The base address is
    ///   http://elm-server.invalid/. The request of the other client fails with HttpRequestException whose error is
    ///   NameResolutionError: the name does not resolve (RFC 6761 §6.4), so nothing is sent to any address.
    /// </remarks>
    [Fact]
    public async Task ServerAdminApi_Handler_UsesNoProxyAndTheAddressLeadsNowhereWithoutIt()
    {
        var host = new CliHost
        {
            Out = TextWriter.Null,
            Error = TextWriter.Null,
            Time = _deployment.Time,
            GetEnvironmentVariable = _ => null,
        };
        (_, System.Net.Sockets.UnixDomainSocketEndPoint endPoint) = ServerAdminApi.SocketOf(_deployment.NextFolderPath());

        using SocketsHttpHandler handler = ServerAdminApi.CreateHandler(host, endPoint);

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
        Assert.Equal(new Uri("http://elm-server.invalid/"), ServerAdminApi.BaseAddress);
        using var plain = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var unresolved = await Assert.ThrowsAsync<HttpRequestException>(() =>
            plain.GetAsync(new Uri(ServerAdminApi.BaseAddress, "admin/v1/status"), TestContext.Current.CancellationToken)
        );
        Assert.Equal(HttpRequestError.NameResolutionError, unresolved.HttpRequestError);
    }

    /// <summary>
    /// elm log verify checks a folder of usage logs and of the borrow record, and fails on a changed line.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server that lent a lease and a borrow and stopped.
    ///
    /// Steps:
    /// - Verify the data folder as usage and as borrows; verify with --json; change a line of the usage log and verify;
    ///   verify with --stream other.
    ///
    /// Expected:
    /// - Exit 0 "intact" twice and an intact JSON report; exit 1 "NOT INTACT"; exit 2.
    /// </remarks>
    [Fact]
    public async Task LogVerify_ChecksTheServerLogs()
    {
        ElmServer server = await _deployment.StartServerAsync();
        _deployment.Time.Advance(TimeSpan.FromMinutes(10));
        LicenseClient client = _deployment.Client(server.PublicUrls[0]);
        Assert.IsType<LeaseResult.Acquired>(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
        Assert.IsType<LeaseResult.Acquired>(
            await client.BorrowLeaseAsync("pro", TimeSpan.FromDays(1), cancellationToken: TestContext.Current.CancellationToken)
        );
        await server.DisposeAsync();
        string data = server.DataDirectory;

        (int code, string output, _) = Run(["log", "verify", data, "--product", "acme-cad"]);
        Assert.Equal(0, code);
        Assert.StartsWith("intact", output.Trim(), StringComparison.Ordinal);
        Assert.Equal(0, Run(["log", "verify", data, "--product", "acme-cad", "--stream", "borrows"]).Code);
        using (JsonDocument json = JsonDocument.Parse(Run(["log", "verify", data, "--product", "acme-cad", "--json"]).Out))
        {
            Assert.True(json.RootElement.GetProperty("intact").GetBoolean());
        }

        string usage = SignedLog.FindFiles(data, "usage")[0];
        string[] lines = File.ReadAllLines(usage);
        int acquired = Array.FindIndex(lines, l => l.Contains("lease.acquired", StringComparison.Ordinal));
        lines[acquired] = lines[acquired].Replace("\"n\":1", "\"n\":2", StringComparison.Ordinal);
        File.WriteAllText(usage, string.Join('\n', lines) + "\n");
        (int broken, string brokenOutput, _) = Run(["log", "verify", data, "--product", "acme-cad"]);
        Assert.Equal(1, broken);
        Assert.Contains("NOT INTACT", brokenOutput, StringComparison.Ordinal);

        Assert.Equal(2, Run(["log", "verify", data, "--product", "acme-cad", "--stream", "other"]).Code);
    }

    /// <summary>
    /// elm log verify reports a line that is JSON but not an object, and a line whose type is not text, as an error with
    /// exit code 1, not as an unhandled exception (design §16, §18.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server that ran and stopped.
    ///
    /// Steps:
    /// - Append [] to the usage log file and verify the data folder; replace that line with an object whose type is a lone
    ///   surrogate escape and verify.
    ///
    /// Expected:
    /// - Exit 1 each time, with the line "error: {file}:{line}: The line is not a JSON object." or "… The line holds a
    ///   string that is not valid Unicode.", and "NOT INTACT".
    /// </remarks>
    [Fact]
    public async Task LogVerify_LinesThatAreNotObjectsOrNotText_AreErrors()
    {
        ElmServer server = await _deployment.StartServerAsync();
        await server.DisposeAsync();
        string data = server.DataDirectory;
        string usage = SignedLog.FindFiles(data, "usage").Single();
        string written = File.ReadAllText(usage);
        int line = written.Count(c => c == '\n') + 1;
        (string Line, string Message)[] cases =
        [
            ("[]", "The line is not a JSON object."),
            ("{\"type\":\"\\ud800\"}", "The line holds a string that is not valid Unicode."),
        ];
        foreach ((string appended, string message) in cases)
        {
            File.WriteAllText(usage, written + appended + "\n");
            (int broken, string brokenOutput, string fault) = Run(["log", "verify", data, "--product", "acme-cad"]);
            Assert.Equal((1, string.Empty), (broken, fault));
            Assert.Contains($"error: {Path.GetFileName(usage)}:{line}: {message}", brokenOutput, StringComparison.Ordinal);
            Assert.Contains("NOT INTACT", brokenOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// elm log verify on a folder names, in a warning, each file whose name has the form of a log file's name but no real
    /// date and time, which it does not verify: a log file renamed to such a name does not vanish from the result without a
    /// trace. A folder without such a file gets no such warning (design §16, §18.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server that ran over three UTC days and stopped (three usage log files).
    ///
    /// Steps:
    /// - Verify the data folder. Rename the third file to usage-20261301T000000000Z.jsonl (month 13); start the server again
    ///   and stop it, so that its new file links to the second file. Verify the folder as text and with --json.
    ///
    /// Expected:
    /// - First exit 0, "intact: 3 file(s)", and no warning that names a date and time. After the rename exit 0, the line
    ///   "warning: usage-20261301T000000000Z.jsonl:0: The file's name has the form of a log file's name but no real date and
    ///   time; the file was not verified.", and "intact: 3 file(s)"; the JSON report is intact and holds the same issue.
    /// </remarks>
    [Fact]
    public async Task LogVerify_FolderWithAFileNamedWithoutARealTime_WarnsAndNamesIt()
    {
        const string Renamed = "usage-20261301T000000000Z.jsonl";
        const string Message = "The file's name has the form of a log file's name but no real date and time; the file was not verified.";
        ElmServer server = await _deployment.StartServerAsync();
        string data = server.DataDirectory;
        for (int files = 2; files <= 3; files++)
        {
            _deployment.Time.Advance(TimeSpan.FromDays(1));
            int expected = files;
            await Deployment.WaitUntilAsync(() => SignedLog.FindFiles(data, "usage").Count == expected);
        }

        await server.DisposeAsync();
        (int code, string output, _) = Run(["log", "verify", data, "--product", "acme-cad"]);
        Assert.Equal(0, code);
        Assert.Contains("intact: 3 file(s)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("no real date and time", output, StringComparison.Ordinal);

        File.Move(SignedLog.FindFiles(data, "usage")[2], Path.Combine(data, Renamed));
        _deployment.Time.Advance(TimeSpan.FromMinutes(1));
        await (await _deployment.StartServerAsync(data)).DisposeAsync();

        (int after, string afterOutput, _) = Run(["log", "verify", data, "--product", "acme-cad"]);
        Assert.Equal(0, after);
        Assert.Contains($"warning: {Renamed}:0: {Message}", afterOutput, StringComparison.Ordinal);
        Assert.Contains("intact: 3 file(s)", afterOutput, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(Run(["log", "verify", data, "--product", "acme-cad", "--json"]).Out);
        Assert.True(json.RootElement.GetProperty("intact").GetBoolean());
        Assert.Contains(
            json.RootElement.GetProperty("issues").EnumerateArray(),
            i =>
                i.GetProperty("file").GetString() == Renamed
                && i.GetProperty("severity").GetString() == "warning"
                && i.GetProperty("message").GetString() == Message
        );
    }

    // Each of the thirteen elm server commands, with a data folder and the other options it needs; the commands that write
    // a file write the one given.
    private static string[][] AllServerCommands(string data, string made)
    {
        string[] folder = ["--data", data];
        string[] license = ["--product", "acme-cad", .. folder];
        const string Service = "https://service.example/";
        return
        [
            ["server", "status", .. folder],
            ["server", "leases", .. folder],
            ["server", "release", "some-lease", .. folder],
            ["server", "reload", .. folder],
            ["server", "seal", .. folder],
            ["server", "activate", "some-key", .. license, "--service", Service, "--license", made],
            ["server", "refresh", .. license, "--service", Service, "--license", made],
            ["server", "deactivate", .. license, "--service", Service, "--license", made],
            ["server", "activate-request", "--key", "some-key", .. license, "--out", made],
            ["server", "refresh-request", .. license, "--out", made],
            ["server", "import", made, .. license, "--license", made],
            ["server", "deactivate-proof", .. license, "--out", made],
            ["server", "forget-proof", .. license],
        ];
    }

    private (int Code, string Out, string Error) Run(string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        string pem = File.ReadAllText(_deployment.RootKeyFile);
        var host = new CliHost
        {
            Out = output,
            Error = error,
            Time = _deployment.Time,
            GetEnvironmentVariable = name => name == "ELM_TRUST_ROOT_KEYS" ? pem : null,
        };
        int code = ElmApp.Run(args, host);
        return (code, output.ToString(), error.ToString());
    }
}
