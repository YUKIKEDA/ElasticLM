using System.Net.Sockets;
using ElasticLM.Cli;
using ElasticLM.Client;
using ElasticLM.Client.Tests;

namespace ElasticLM.Server.Tests;

/// <summary>
/// What the <c>elm server</c> commands do with an answer of the administration socket that is larger than their limit, or
/// that stops (design §13.7, §16, the V1d6 Grill): the commands that call the administration API and print its answer
/// (<c>status</c>, <c>leases</c>), the server license commands that ask for the status or a reload, and the exit code of
/// a server license command whose answer from <c>elm-service</c> is too large. The listener on <c>admin.sock</c> is one
/// of the test that writes its answers byte by byte.
/// </summary>
public sealed class AdminAnswerLimitTests : IAsyncDisposable
{
    private const int Limit = ServerAdminApi.MaxAnswerLength;
    private const string Product = "acme-cad";
    private const string TooLarge = "error: ELM_MALFORMED: The server's administration API answered with a body larger than 67108864 bytes.";

    // What a listener may write beyond what elm read, into the buffers of the two sockets.
    private const long SocketBuffers = 16L * 1024 * 1024;

    // A bound on the real clock for a command that would otherwise never end. It is shorter than the commands' 30 seconds,
    // so a limit counted on the real clock cannot end a command within it; no passing test waits for it.
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(20);

    private readonly Deployment _deployment = new();

    public ValueTask DisposeAsync() => _deployment.DisposeAsync();

    /// <summary>
    /// An answer of the administration API of exactly 64 MiB is read, and one of a byte more ends the command with exit
    /// code 3 and ELM_MALFORMED, whether the answer declares its length or not.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The data folder of a stopped server, with its administrator token, and a listener of the test on admin.sock in it.
    /// - The answer has status 200 and a JSON body followed by spaces, with a Content-Length (declared) or ended by closing
    ///   the connection (until-close). For leases the body is {"leases":[]}; for deactivate-proof, which asks for the
    ///   status first, it is {"autoRefresh":false}.
    ///
    /// Steps:
    /// - Run elm server leases --json, and elm server deactivate-proof, against an answer of 67,108,864 bytes; then
    ///   against one of 67,108,865.
    ///
    /// Expected:
    /// - 67,108,864 bytes: leases exits 0 and prints the object {"leases":[]} (compared without its white space);
    ///   deactivate-proof reads the status and exits 1 with
    ///   "error: Stop elm-server before deactivating its license.".
    /// - 67,108,865 bytes: both exit 3 with the one line "error: ELM_MALFORMED: The server's administration API answered
    ///   with a body larger than 67108864 bytes."; deactivate-proof writes no file.
    /// </remarks>
    /// <param name="shape">How the answer delimits its body.</param>
    [Theory]
    [InlineData("declared")]
    [InlineData("until-close")]
    public async Task Commands_AnswerAtTheLimitAndOneByteOver_IsReadThenMalformed(string shape)
    {
        string folder = await StoppedServerFolderAsync();
        string made = Path.Combine(folder, "made.file");
        RawAnswer Shaped(string target, long length)
        {
            string body = target.EndsWith("/leases", StringComparison.Ordinal) ? """{"leases":[]}""" : """{"autoRefresh":false}""";
            return shape == "declared" ? RawAnswer.Whole(body, length) : RawAnswer.UntilClose(body, length);
        }

        (int Code, string Out, string Error) leases;
        (int Code, string Out, string Error) proof;
        await using (RawAnswerServer listener = RawAnswerServer.StartOnSocket(AdminSocket.PathIn(folder), t => Shaped(t, Limit)))
        {
            leases = await RunAsync(["server", "leases", "--json", .. Common(folder)]);
            proof = await RunAsync(["server", "deactivate-proof", .. Common(folder), "--out", made]);
        }

        Assert.Equal(
            (0, """{"leases":[]}""", string.Empty),
            (leases.Code, string.Concat(leases.Out.Where(c => !char.IsWhiteSpace(c))), leases.Error)
        );
        Assert.Equal((1, "error: Stop elm-server before deactivating its license."), (proof.Code, proof.Error.Trim()));

        await using (RawAnswerServer listener = RawAnswerServer.StartOnSocket(AdminSocket.PathIn(folder), t => Shaped(t, Limit + 1L)))
        {
            leases = await RunAsync(["server", "leases", "--json", .. Common(folder)]);
            proof = await RunAsync(["server", "deactivate-proof", .. Common(folder), "--out", made]);
        }

        Assert.Equal((3, string.Empty, TooLarge), (leases.Code, leases.Out, leases.Error.Trim()));
        Assert.Equal((3, TooLarge), (proof.Code, proof.Error.Trim()));
        Assert.False(File.Exists(made));
    }

    /// <summary>
    /// An answer of the administration API that goes on past the limit, or that declares such a body, ends the command with
    /// exit code 3 and ELM_MALFORMED, and elm takes no more of it than the limit.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The folder and the listener of <see cref="Commands_AnswerAtTheLimitAndOneByteOver_IsReadThenMalformed"/>. The
    ///   answer, with status 200 or 500: endless (128 MiB of spaces in chunks, without a last chunk), or declared (a
    ///   Content-Length of 4 GiB and no body).
    /// - The commands: status (it prints the answer) and deactivate-proof (it asks for the status first).
    ///
    /// Steps:
    /// - Run the command; wait until the listener has stopped writing.
    ///
    /// Expected:
    /// - Exit code 3 and the one line "error: ELM_MALFORMED: The server's administration API answered with a body larger
    ///   than 67108864 bytes."; deactivate-proof writes no file.
    /// - endless: the listener wrote more than 64 MiB and at most 64 MiB and 16 MiB; an elm without a limit takes all
    ///   128 MiB. declared: no body byte.
    /// </remarks>
    /// <param name="command">The command.</param>
    /// <param name="shape">The shape of the answer.</param>
    /// <param name="status">The status of the answer.</param>
    [Theory]
    [InlineData("status", "endless", 200)]
    [InlineData("status", "endless", 500)]
    [InlineData("status", "declared", 200)]
    [InlineData("deactivate-proof", "endless", 200)]
    [InlineData("deactivate-proof", "endless", 500)]
    [InlineData("deactivate-proof", "declared", 200)]
    public async Task Commands_AnswerFarOverTheLimit_ExitThreeAndTakeNoMore(string command, string shape, int status)
    {
        string folder = await StoppedServerFolderAsync();
        string made = Path.Combine(folder, "made.file");
        RawAnswer offered = shape == "endless" ? RawAnswer.Endless(2L * Limit, status) : RawAnswer.DeclaredOnly(4_294_967_296L, status);
        await using RawAnswerServer listener = RawAnswerServer.StartOnSocket(AdminSocket.PathIn(folder), _ => offered);

        (int Code, string Out, string Error) result = await RunAsync(
            command == "status" ? ["server", "status", .. Common(folder)] : ["server", "deactivate-proof", .. Common(folder), "--out", made]
        );
        long written = await listener.SettledBodyBytesAsync();

        Assert.Equal((3, string.Empty, TooLarge), (result.Code, result.Out, result.Error.Trim()));
        Assert.False(File.Exists(made));
        if (shape == "endless")
        {
            Assert.InRange(written, Limit + 1L, Limit + SocketBuffers);
        }
        else
        {
            Assert.Equal(0, written);
        }
    }

    /// <summary>
    /// An answer of the administration API whose body stops ends the command after 30 seconds of its clock, with exit code
    /// 3: the time limit covers the body.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The folder and the listener of <see cref="Commands_AnswerAtTheLimitAndOneByteOver_IsReadThenMalformed"/>. The
    ///   answer is a head with status 200 and the first byte of a body in chunks, and then nothing.
    /// - elm on the test's fake clock.
    ///
    /// Steps:
    /// - Run elm server status, and elm server deactivate-proof; once the listener waits, move the clock 30 seconds.
    ///
    /// Expected:
    /// - status: exit code 3 and "error: The server cannot be reached: No whole answer came within 30 seconds.".
    /// - deactivate-proof: exit code 3 and "error: The server's administration API did not answer in time."; no file.
    /// - Both within 20 seconds of the real clock: a command whose limit runs on the real clock would not return in that time.
    /// </remarks>
    /// <param name="command">The command.</param>
    /// <param name="line">The line expected on standard error.</param>
    [Theory]
    [InlineData("status", "error: The server cannot be reached: No whole answer came within 30 seconds.")]
    [InlineData("deactivate-proof", "error: The server's administration API did not answer in time.")]
    public async Task Commands_BodyThatStops_ExitThreeAfterThirtySeconds(string command, string line)
    {
        string folder = await StoppedServerFolderAsync();
        string made = Path.Combine(folder, "made.file");
        await using RawAnswerServer listener = RawAnswerServer.StartOnSocket(AdminSocket.PathIn(folder), _ => RawAnswer.Stalled("{", 1));

        Task<(int Code, string Out, string Error)> running = RunAsync(
            command == "status" ? ["server", "status", .. Common(folder)] : ["server", "deactivate-proof", .. Common(folder), "--out", made]
        );
        await listener.Held.WaitAsync(Hang, TestContext.Current.CancellationToken);
        _deployment.Time.Advance(TimeSpan.FromSeconds(30));
        (int Code, string Out, string Error) result = await running;

        Assert.Equal((3, string.Empty, line), (result.Code, result.Out, result.Error.Trim()));
        Assert.False(File.Exists(made));
    }

    /// <summary>
    /// A server license command whose answer from the service is larger than 256 KiB exits with code 3, as a failed
    /// exchange; another ELM_MALFORMED, and a refusal, exit with code 1 (design §16).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a server's installation key and no server. A service of the test on loopback whose answer to GET /v1/nonce is: too-large
    ///   (status 200, 262,145 bytes); too-large-refusal (status 503, 262,145 bytes); at-limit (status 409 and the problem
    ///   ELM_NO_SEATS with the detail "none", padded with spaces to 262,144 bytes); no-nonce (status 200 and {}).
    ///
    /// Steps:
    /// - Run elm server activate with a license key against the service.
    ///
    /// Expected:
    /// - too-large and too-large-refusal: exit code 3 and "error: ELM_MALFORMED: The service's answer is larger than the
    ///   limit.".
    /// - at-limit: exit code 1 and "error: ELM_NO_SEATS: none".
    /// - no-nonce: exit code 1 and "error: ELM_MALFORMED: The service's nonce answer is not valid.".
    /// - No license file is written.
    /// </remarks>
    /// <param name="answer">The case.</param>
    /// <param name="code">The exit code expected.</param>
    /// <param name="line">The line expected on standard error.</param>
    [Theory]
    [InlineData("too-large", 3, "error: ELM_MALFORMED: The service's answer is larger than the limit.")]
    [InlineData("too-large-refusal", 3, "error: ELM_MALFORMED: The service's answer is larger than the limit.")]
    [InlineData("at-limit", 1, "error: ELM_NO_SEATS: none")]
    [InlineData("no-nonce", 1, "error: ELM_MALFORMED: The service's nonce answer is not valid.")]
    public async Task ServerActivate_ServiceAnswerOverTheLimit_ExitsThree(string answer, int code, string line)
    {
        const int ServiceLimit = 256 * 1024;
        const string NoSeats = """{"code":"ELM_NO_SEATS","detail":"none"}""";
        string folder = _deployment.NewFolder();
        _ = Deployment.ServerKeyId(folder);
        string license = Path.Combine(folder, "server.elmlic");
        RawAnswer shaped = answer switch
        {
            "too-large" => RawAnswer.Whole(string.Empty, ServiceLimit + 1),
            "too-large-refusal" => RawAnswer.Whole(string.Empty, ServiceLimit + 1, status: 503),
            "at-limit" => RawAnswer.Whole(NoSeats, ServiceLimit, status: 409),
            _ => RawAnswer.Whole("{}"),
        };
        await using RawAnswerServer service = RawAnswerServer.Start(_ => shaped);

        (int Code, string Out, string Error) result = await RunAsync([
            "server",
            "activate",
            "ELM-AAAA-AAAA-AAAA-AAAA",
            .. Common(folder),
            "--service",
            service.Url.ToString(),
            "--license",
            license,
        ]);

        Assert.Equal((code, line), (result.Code, result.Error.Trim()));
        Assert.False(File.Exists(license));
    }

    /// <summary>
    /// An answer of the administration API whose JSON holds text that cannot be read is taken as an answer that is not JSON,
    /// once, where the body is checked: elm prints nothing of it and exits with 3, without an exception (design §8.3, §13.7,
    /// V1d23, Issue #402).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The folder of a stopped server and a listener of the test on admin.sock that answers 200 with an otherwise valid
    ///   body ({"leases":[]} or {"autoRefresh":false}) that holds the value "\ud800", the member "\ud800" at its end, the
    ///   value "x" and the byte 0xFF, or the value "\udc00\ud800".
    ///
    /// Steps:
    /// - Run elm server leases --json (it prints the body); run elm server deactivate-proof (it reads autoRefresh from the
    ///   status first).
    ///
    /// Expected:
    /// - leases: exit 3, no output, "error: The server answered 200 with a body that is not JSON."; deactivate-proof: exit
    ///   3, "error: The server's administration API answered with a body that is not JSON.", no file (before V1d23 each
    ///   threw InvalidOperationException, or printed the body).
    /// </remarks>
    /// <param name="shape">Where the text that cannot be read is.</param>
    [Theory]
    [InlineData("value")]
    [InlineData("name")]
    [InlineData("not-utf8")]
    [InlineData("reversed")]
    public async Task Commands_AnswerWithTextThatIsNotUnicode_IsNotJson(string shape)
    {
        string folder = await StoppedServerFolderAsync();
        string made = Path.Combine(folder, "made.file");
        RawAnswer Shaped(string target)
        {
            string start = target.EndsWith("/leases", StringComparison.Ordinal) ? """{"leases":[]""" : """{"autoRefresh":false""";
            string text = shape switch
            {
                "value" => start + ""","x":"\ud800"}""",
                "name" => start + ""","\ud800":1}""",
                "reversed" => start + ""","x":"\udc00\ud800"}""",
                _ => start + ",\"x\":\"xÿ\"}",
            };
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            int at = Array.IndexOf(bytes, (byte)0xC3);
            return RawAnswer.Bytes(at < 0 ? bytes : [.. bytes[..at], 0xFF, .. bytes[(at + 2)..]]);
        }

        (int Code, string Out, string Error) leases;
        (int Code, string Out, string Error) proof;
        await using (RawAnswerServer listener = RawAnswerServer.StartOnSocket(AdminSocket.PathIn(folder), Shaped))
        {
            leases = await RunAsync(["server", "leases", "--json", .. Common(folder)]);
            proof = await RunAsync(["server", "deactivate-proof", .. Common(folder), "--out", made]);
        }

        Assert.Equal(
            (3, string.Empty, "error: The server answered 200 with a body that is not JSON."),
            (leases.Code, leases.Out, leases.Error.Trim())
        );
        Assert.Equal((3, "error: The server's administration API answered with a body that is not JSON."), (proof.Code, proof.Error.Trim()));
        Assert.False(File.Exists(made));
    }

    private static string[] Common(string folder) => ["--product", Product, "--data", folder];

    // A data folder in which a server ran with its license and stopped: it holds a key, a license, and a token, and no
    // socket file.
    private async Task<string> StoppedServerFolderAsync()
    {
        ElmServer server = await _deployment.StartServerAsync();
        await server.DisposeAsync();
        Assert.False(Path.Exists(AdminSocket.PathIn(server.DataDirectory)));
        return server.DataDirectory;
    }

    // Runs elm on a thread of its own, on the test's fake clock: the commands block until their answer.
    private Task<(int Code, string Out, string Error)> RunAsync(string[] args)
    {
        string pem = File.ReadAllText(_deployment.RootKeyFile);
        return Task.Run(
                () =>
                {
                    var output = new StringWriter();
                    var error = new StringWriter();
                    var host = new CliHost
                    {
                        Out = output,
                        Error = error,
                        Time = _deployment.Time,
                        GetEnvironmentVariable = name => name == "ELM_TRUST_ROOT_KEYS" ? pem : null,
                    };
                    int code = ElmApp.Run(args, host);
                    return (code, output.ToString(), error.ToString());
                },
                TestContext.Current.CancellationToken
            )
            .WaitAsync(Hang, TestContext.Current.CancellationToken);
    }
}
