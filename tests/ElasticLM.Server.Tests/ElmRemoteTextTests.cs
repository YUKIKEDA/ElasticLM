using System.Text;
using System.Text.Json;
using ElasticLM.Cli;
using ElasticLM.Client;
using ElasticLM.Client.Tests;
using ElasticLM.Issuing;

namespace ElasticLM.Server.Tests;

/// <summary>
/// What the elm server commands and elm log verify write from text they did not make (design §16, §18.1, V1d27): the
/// answers of whatever listens on the administration socket, and log files and their names. Control characters are
/// replaced; answers of the wrong shape end with an error, not an exception (F21); and elm log verify names the server
/// license of each file and checks it against --license (T15-3).
/// </summary>
public sealed class ElmRemoteTextTests : IAsyncDisposable
{
    private const string NotTheAnswer = "error: The server answered 200 with a body that is not the answer asked for.";
    private readonly Deployment _deployment = new();

    public ValueTask DisposeAsync() => _deployment.DisposeAsync();

    /// <summary>
    /// An error answer of the administration API is one line with its code's and detail's control characters replaced; a
    /// code that is not a string gives the status, and a detail that is not a string gives nothing (design §13.7, §16, V1d27,
    /// F21).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The folder of a stopped server with its administrator token; a listener of the test on admin.sock that answers 409
    ///   with: code "ELM_X" ESC "[2J" and detail "D", the raw UTF-8 bytes of U+0085, and CR (escaped); code 1 and detail
    ///   "d"; code "ELM_X" and detail {"a":1}.
    ///
    /// Steps:
    /// - Run elm server status, leases, release, reload, and seal against each answer.
    ///
    /// Expected:
    /// - Each exits 1 with nothing on standard output and the one line "error: ELM_X\u001B[2J: D\u0085\u000D", "error: The
    ///   server's administration API answered 409.", and "error: ELM_X: ".
    /// </remarks>
    [Fact]
    public async Task ServerCommands_ErrorAnswer_IsOneLineWithControlCharactersReplaced()
    {
        string folder = await StoppedServerFolderAsync();
        (byte[] Body, string Line)[] answers =
        [
            ([.. "{\"code\":\"ELM_X\\u001b[2J\",\"detail\":\"D"u8, 0xC2, 0x85, .. "\\r\"}"u8], "error: ELM_X\\u001B[2J: D\\u0085\\u000D"),
            ("{\"code\":1,\"detail\":\"d\"}"u8.ToArray(), "error: The server's administration API answered 409."),
            ("{\"code\":\"ELM_X\",\"detail\":{\"a\":1}}"u8.ToArray(), "error: ELM_X:"),
        ];
        string[][] commands =
        [
            ["status"],
            ["leases"],
            ["release", "some-lease"],
            ["reload"],
            ["seal"],
        ];
        foreach ((byte[] body, string line) in answers)
        {
            await using RawAnswerServer listener = RawAnswerServer.StartOnSocket(AdminSocket.PathIn(folder), _ => RawAnswer.Bytes(body, 409));
            foreach (string[] command in commands)
            {
                (int code, string output, string error) = await RunAsync(["server", .. command, "--data", folder]);
                Assert.True((1, string.Empty, line) == (code, output, error.Trim()), $"elm server {command[0]}: {code} {output} {error}");
            }
        }
    }

    /// <summary>
    /// A success of the administration API whose body is not the answer asked for ends status, leases, and reload with exit
    /// code 3 and one line, not with an exception (design §13.7, V1d6, F21).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The folder and the listener of the test above, which answers 200 with: {} (no member), a status whose product is
    ///   1, a list whose lease has id 1, a reload whose license is null, and a status without features.
    ///
    /// Steps:
    /// - Run the command each body is for.
    ///
    /// Expected:
    /// - Each exits 3 with the one line "error: The server answered 200 with a body that is not the answer asked for." and
    ///   no standard output; for the status without features, what it printed before it read them.
    /// </remarks>
    [Fact]
    public async Task ServerCommands_SuccessOfAnotherShape_ExitsThree()
    {
        string folder = await StoppedServerFolderAsync();
        const string Status = "\"license\":\"l\",\"licenseToken\":\"t\",\"key\":\"k\",\"recovering\":false,\"recoveryEnds\":0,\"leases\":0";
        (string Command, string Body)[] cases =
        [
            ("status", "{}"),
            ("leases", "{}"),
            ("reload", "{}"),
            ("status", "{\"product\":1," + Status + ",\"features\":[]}"),
            ("leases", "{\"leases\":[{\"id\":1,\"feature\":\"f\",\"seats\":1,\"key\":\"k\",\"expires\":0,\"borrow\":false,\"stopped\":false}]}"),
            ("reload", "{\"license\":null,\"licenseToken\":\"t\"}"),
            ("status", "{\"product\":\"p\"," + Status + "}"),
        ];
        foreach ((string command, string body) in cases)
        {
            await using RawAnswerServer listener = RawAnswerServer.StartOnSocket(
                AdminSocket.PathIn(folder),
                _ => RawAnswer.Bytes(Encoding.UTF8.GetBytes(body))
            );
            (int code, string output, string error) = await RunAsync(["server", command, "--data", folder]);
            Assert.True((3, NotTheAnswer) == (code, error.Trim()), $"elm server {command} {body}: {code} {error}");
            bool printedFirst = command == "status" && body.StartsWith("{\"product\":\"p\"", StringComparison.Ordinal);
            Assert.True(printedFirst ? output.StartsWith("product: p", StringComparison.Ordinal) : output.Length == 0, output);
        }
    }

    /// <summary>
    /// elm server status, leases, and reload replace the control characters of the strings they print from the answer, and
    /// their --json output escapes them (design §16, V1d27).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The folder and the listener of the tests above. The status names product "p" ESC "[2J", license "l" U+0085, token
    ///   "t" CSI, key "k" CR, and feature "f" BEL; the list has a lease with id "i" ESC, feature "f" U+0085, and key "k" DEL;
    ///   the reload has license "l" ESC and token "t" U+0085. Each control character is sent as a JSON escape.
    ///
    /// Steps:
    /// - Run each command as text and with --json.
    ///
    /// Expected:
    /// - Text: "product: p\u001B[2J", "license: l\u0085 (token t\u009B)", "key: k\u000D", "  f\u0007: 0 of 1 seats in use
    ///   (1 with overage, 0 borrowed)"; "i\u001B lease f\u0085 x1 key k\u007F until 1970-01-01 00:00:00Z"; "reloaded:
    ///   license l\u001B, token t\u0085". No output holds a control character other than the line breaks.
    /// - --json: the same characters as upper-case JSON escapes, and no control character other than the line breaks.
    /// </remarks>
    [Fact]
    public async Task ServerCommands_TextOfTheAnswer_ReplacesControlCharacters()
    {
        string folder = await StoppedServerFolderAsync();
        const string StatusBody =
            "{\"product\":\"p\\u001b[2J\",\"license\":\"l\\u0085\",\"licenseToken\":\"t\\u009b\",\"key\":\"k\\r\",\"recovering\":false,"
            + "\"recoveryEnds\":0,\"leases\":0,\"features\":[{\"feature\":\"f\\u0007\",\"seats\":1,\"allowed\":1,\"inUse\":0,\"borrowed\":0}]}";
        const string LeasesBody =
            "{\"leases\":[{\"id\":\"i\\u001b\",\"feature\":\"f\\u0085\",\"seats\":1,\"key\":\"k\\u007f\",\"expires\":0,\"borrow\":false,\"stopped\":false}]}";
        const string ReloadBody = "{\"license\":\"l\\u001b\",\"licenseToken\":\"t\\u0085\"}";
        (string Command, string Body, string[] Lines, string[] Escapes)[] cases =
        [
            (
                "status",
                StatusBody,
                [
                    "product: p\\u001B[2J",
                    "license: l\\u0085 (token t\\u009B)",
                    "key: k\\u000D",
                    "  f\\u0007: 0 of 1 seats in use (1 with overage, 0 borrowed)",
                ],
                ["\"p\\u001B[2J\"", "\"l\\u0085\"", "\"t\\u009B\"", "\"k\\r\"", "\"f\\u0007\""]
            ),
            (
                "leases",
                LeasesBody,
                ["i\\u001B lease f\\u0085 x1 key k\\u007F until 1970-01-01 00:00:00Z"],
                ["\"i\\u001B\"", "\"f\\u0085\"", "\"k\\u007F\""]
            ),
            ("reload", ReloadBody, ["reloaded: license l\\u001B, token t\\u0085"], ["\"l\\u001B\"", "\"t\\u0085\""]),
        ];
        foreach ((string command, string body, string[] lines, string[] escapes) in cases)
        {
            await using RawAnswerServer listener = RawAnswerServer.StartOnSocket(
                AdminSocket.PathIn(folder),
                _ => RawAnswer.Bytes(Encoding.UTF8.GetBytes(body))
            );
            (int code, string output, string error) = await RunAsync(["server", command, "--data", folder]);
            Assert.True(code == 0, error);
            string[] printed = output.TrimEnd().Split(Environment.NewLine);
            foreach (string line in lines)
            {
                Assert.Contains(line, printed);
            }

            Assert.DoesNotContain(output, c => char.IsControl(c) && c is not '\r' and not '\n');
            Assert.DoesNotContain(output.Replace(Environment.NewLine, string.Empty, StringComparison.Ordinal), c => c is '\r' or '\n');

            (code, output, error) = await RunAsync(["server", command, "--data", folder, "--json"]);
            Assert.True(code == 0, error);
            foreach (string escape in escapes)
            {
                Assert.Contains(escape, output, StringComparison.Ordinal);
            }

            Assert.DoesNotContain(output, c => char.IsControl(c) && c is not '\r' and not '\n');
        }
    }

    /// <summary>
    /// elm log verify names the server license that signed the end of each file, in text and in --json, and with --license
    /// a file of another server license is an error (design §18.1, V1d27, T15-3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two stopped servers of the same product: one with server license lic-srv, one with lic-other; each wrote one usage
    ///   log file.
    ///
    /// Steps:
    /// - Verify the first folder; with --license lic-srv; the second folder with --license lic-srv, as text and with
    ///   --json; with --license "".
    ///
    /// Expected:
    /// - The first: exit 0, the lines "file: (name): server license lic-srv" and "intact: 1 file(s), …", in that order; the
    ///   same with --license lic-srv.
    /// - The second with --license lic-srv: exit 1, the lines "error: (name):0: The file is signed by the key of server
    ///   license lic-other, not of lic-srv (--license).", "file: (name): server license lic-other", and "NOT INTACT: 1
    ///   file(s), …"; --json has intact false, licenses [{file (name), licenseIds [lic-other]}], and that issue as an error.
    /// - --license "": exit 2 and "error: --license must not be empty.".
    /// </remarks>
    [Fact]
    public async Task LogVerify_LicenseOfEachFile_IsPrintedAndChecked()
    {
        string first = await StoppedServerFolderAsync();
        string second = _deployment.NewFolder();
        _deployment.WriteServerLicense(second, s => s with { LicenseId = "lic-other" });
        await (await _deployment.StartServerAsync(second)).DisposeAsync();
        string firstName = Path.GetFileName(SignedLog.FindFiles(first, "usage").Single());
        string secondName = Path.GetFileName(SignedLog.FindFiles(second, "usage").Single());

        foreach (string[] extra in new[] { Array.Empty<string>(), ["--license", "lic-srv"] })
        {
            (int code, string output, string error) = await RunAsync(["log", "verify", first, "--product", "acme-cad", .. extra]);
            string[] lines = output.TrimEnd().Split(Environment.NewLine);
            Assert.True(code == 0, output + error);
            Assert.Equal(2, lines.Length);
            Assert.Equal($"file: {firstName}: server license lic-srv", lines[0]);
            Assert.StartsWith("intact: 1 file(s), ", lines[1], StringComparison.Ordinal);
        }

        string[] other = ["log", "verify", second, "--product", "acme-cad", "--license", "lic-srv"];
        string message = "The file is signed by the key of server license lic-other, not of lic-srv (--license).";
        (int otherCode, string otherOutput, _) = await RunAsync(other);
        string[] otherLines = otherOutput.TrimEnd().Split(Environment.NewLine);
        Assert.Equal(1, otherCode);
        Assert.Equal(3, otherLines.Length);
        Assert.Equal($"error: {secondName}:0: {message}", otherLines[0]);
        Assert.Equal($"file: {secondName}: server license lic-other", otherLines[1]);
        Assert.StartsWith("NOT INTACT: 1 file(s), ", otherLines[2], StringComparison.Ordinal);

        (otherCode, otherOutput, _) = await RunAsync([.. other, "--json"]);
        Assert.Equal(1, otherCode);
        using (JsonDocument json = JsonDocument.Parse(otherOutput))
        {
            JsonElement root = json.RootElement;
            Assert.False(root.GetProperty("intact").GetBoolean());
            JsonElement license = Assert.Single(root.GetProperty("licenses").EnumerateArray());
            Assert.Equal(
                (secondName, "lic-other"),
                (license.GetProperty("file").GetString(), Assert.Single(license.GetProperty("licenseIds").EnumerateArray()).GetString())
            );
            Assert.Contains(
                root.GetProperty("issues").EnumerateArray(),
                i =>
                    i.GetProperty("file").GetString() == secondName
                    && i.GetProperty("line").GetInt32() == 0
                    && i.GetProperty("severity").GetString() == "error"
                    && i.GetProperty("message").GetString() == message
            );
        }

        (int emptyCode, _, string emptyError) = await RunAsync(["log", "verify", first, "--product", "acme-cad", "--license", string.Empty]);
        Assert.Equal((2, "error: --license must not be empty."), (emptyCode, emptyError.Trim()));
    }

    /// <summary>
    /// A file whose server license changed in the middle (a reload of a server license with another ID, written as
    /// key.changed) names both licenses, and passes --license with neither of them: a file passes only when every license
    /// that signed it is the one given (design §18.1, V1d27, T15-3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server of lic-srv that, while it runs, reloads a server license lic-other bound to the same installation key,
    ///   and stops: its one usage log file has a key.changed line from lic-srv to lic-other.
    ///
    /// Steps:
    /// - Verify the folder; with --license lic-srv, with --license lic-other, and with --license lic-x; with --json.
    ///
    /// Expected:
    /// - Without --license: exit 0 and "file: (name): server license lic-srv, lic-other".
    /// - Each --license: exit 1 and "error: (name):0: The file is signed by the keys of server licenses lic-srv,
    ///   lic-other, not only of (ID) (--license).", then "NOT INTACT: 1 file(s), …".
    /// - --json: licenses has the file with licenseIds [lic-srv, lic-other].
    /// </remarks>
    [Fact]
    public async Task LogVerify_LicenseChangedInTheFile_PassesNoSingleLicense()
    {
        ElmServer server = await _deployment.StartServerAsync();
        string data = server.DataDirectory;
        _deployment.WriteServerLicense(data, s => s with { LicenseId = "lic-other" });
        Assert.Null(server.ReloadLicense());
        await server.DisposeAsync();
        string name = Path.GetFileName(SignedLog.FindFiles(data, "usage").Single());

        (int code, string output, string error) = await RunAsync(["log", "verify", data, "--product", "acme-cad"]);
        Assert.True(code == 0, output + error);
        Assert.Contains($"file: {name}: server license lic-srv, lic-other", output.TrimEnd().Split(Environment.NewLine));

        foreach (string expected in new[] { "lic-srv", "lic-other", "lic-x" })
        {
            (code, output, _) = await RunAsync(["log", "verify", data, "--product", "acme-cad", "--license", expected]);
            string[] lines = output.TrimEnd().Split(Environment.NewLine);
            Assert.Equal(1, code);
            Assert.Equal(
                $"error: {name}:0: The file is signed by the keys of server licenses lic-srv, lic-other, not only of {expected} (--license).",
                lines[0]
            );
            Assert.StartsWith("NOT INTACT: 1 file(s), ", lines[^1], StringComparison.Ordinal);
        }

        (_, output, _) = await RunAsync(["log", "verify", data, "--product", "acme-cad", "--json"]);
        using JsonDocument json = JsonDocument.Parse(output);
        JsonElement license = Assert.Single(json.RootElement.GetProperty("licenses").EnumerateArray());
        Assert.Equal(["lic-srv", "lic-other"], license.GetProperty("licenseIds").EnumerateArray().Select(e => e.GetString()!));
    }

    /// <summary>
    /// elm log verify replaces the control characters of file names and of messages that quote the files, in its issue and
    /// license lines (design §16, §18.1, V1d27).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server that ran over two UTC days and stopped (two usage log files, the second linked to the first). A copy of
    ///   the first file named "a" U+0085 ".jsonl", and a copy of the second with the line [] added, named "b" U+0085
    ///   ".jsonl".
    ///
    /// Steps:
    /// - Verify the two copies, given as files.
    ///
    /// Expected:
    /// - Exit 1; the lines "error: b\u0085.jsonl:1: The file follows '(first name)', not 'a\u0085.jsonl': a file is
    ///   missing." and "error: b\u0085.jsonl:(last line): The line is not a JSON object.", and "file: a\u0085.jsonl: server
    ///   license lic-srv"; no control character in the output other than the line breaks.
    /// </remarks>
    [Fact]
    public async Task LogVerify_NamesAndMessagesOfTheFiles_ReplaceControlCharacters()
    {
        ElmServer server = await _deployment.StartServerAsync();
        string data = server.DataDirectory;
        _deployment.Time.Advance(TimeSpan.FromDays(1));
        await Deployment.WaitUntilAsync(() => SignedLog.FindFiles(data, "usage").Count == 2);
        await server.DisposeAsync();
        IReadOnlyList<string> files = SignedLog.FindFiles(data, "usage");
        string copies = _deployment.NewFolder();
        string a = Path.Combine(copies, "a\u0085.jsonl");
        string b = Path.Combine(copies, "b\u0085.jsonl");
        File.Copy(files[0], a);
        string second = File.ReadAllText(files[1]);
        File.WriteAllText(b, second + "[]\n");
        int last = second.Count(c => c == '\n') + 1;

        (int code, string output, string error) = await RunAsync(["log", "verify", a, b, "--product", "acme-cad"]);

        string[] lines = output.TrimEnd().Split(Environment.NewLine);
        Assert.True(code == 1, output + error);
        Assert.Contains($"error: b\\u0085.jsonl:1: The file follows '{Path.GetFileName(files[0])}', not 'a\\u0085.jsonl': a file is missing.", lines);
        Assert.Contains($"error: b\\u0085.jsonl:{last}: The line is not a JSON object.", lines);
        Assert.Contains("file: a\\u0085.jsonl: server license lic-srv", lines);
        Assert.DoesNotContain(output, c => char.IsControl(c) && c is not '\r' and not '\n');
    }

    /// <summary>
    /// A folder whose only file has the form of a log file's name but no real date and time names it in a warning before
    /// "No usage log file was found." and exit code 2, as text and with --json, and writes nothing to standard output
    /// (design §18.1, V1d27, Issue #404).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A folder that holds only usage-20271301T000000000Z.jsonl (month 13).
    ///
    /// Steps:
    /// - Verify the folder, as text and with --json.
    ///
    /// Expected:
    /// - Exit 2; standard error is the two lines "warning: usage-20271301T000000000Z.jsonl:0: The file's name has the form of
    ///   a log file's name but no real date and time; the file was not verified." and "error: No usage log file was
    ///   found."; standard output is empty.
    /// </remarks>
    [Fact]
    public async Task LogVerify_FolderOfOnlyFilesWithoutARealTime_WarnsBeforeTheError()
    {
        string folder = _deployment.NewFolder();
        File.WriteAllText(Path.Combine(folder, "usage-20271301T000000000Z.jsonl"), "{}\n");
        string[] expected =
        [
            "warning: usage-20271301T000000000Z.jsonl:0: The file's name has the form of a log file's name but no real date and time; the file was not verified.",
            "error: No usage log file was found.",
        ];

        foreach (string[] extra in new[] { Array.Empty<string>(), ["--json"] })
        {
            (int code, string output, string error) = await RunAsync(["log", "verify", folder, "--product", "acme-cad", .. extra]);

            Assert.Equal((2, string.Empty), (code, output));
            Assert.Equal(expected, error.TrimEnd().Split(Environment.NewLine));
        }
    }

    private async Task<string> StoppedServerFolderAsync()
    {
        ElmServer server = await _deployment.StartServerAsync();
        await server.DisposeAsync();
        return server.DataDirectory;
    }

    // Runs elm on a thread of its own, on the test's fake clock, with the vendor's root key as the trust store.
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
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }
}
