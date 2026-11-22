using System.CommandLine;
using System.Text.Json;
using ElasticLM.Issuing;
using ElasticLM.Licensing;

namespace ElasticLM.Cli;

/// <summary>
/// <c>elm log</c>: verifying the signed usage log and borrow record of a server, and the usage log of <c>elm-service</c>
/// (design §16, §18.1, decision 0.95).
/// </summary>
internal static class LogCommands
{
    private const string NotVerified = "The file's name has the form of a log file's name but no real date and time; the file was not verified.";

    public static Command Create(CliHost host)
    {
        var filesArgument = new Argument<string[]>("files")
        {
            Description = "The log files, or a folder of them. They are verified in the order of their names (oldest first).",
            Arity = ArgumentArity.OneOrMore,
        }.AsPath();
        var productOption = new Option<string>("--product")
        {
            Description = "The product ID of the server license; needed for a server's log, not for elm-service's.",
        };
        var nodeOption = new Option<string>("--node")
        {
            Description = "For a folder of elm-service's usage logs: the node whose files to verify (files usage-<node>-*.jsonl).",
        };
        var streamOption = new Option<string>("--stream") { Description = "usage or borrows. Default: usage.", DefaultValueFactory = _ => "usage" };
        var trustOption = new Option<string[]>("--trust")
        {
            Description = $"A root public key file to trust. Repeat for several. Default: the PEM keys in {TrustKeys.EnvironmentVariable}.",
        }.AsPath();
        var revokedOption = new Option<string[]>("--revoked") { Description = "A revoked key ID. Repeat for several." };
        var licenseOption = new Option<string?>("--license")
        {
            Description = "The server license ID that must have signed each file; a file signed under another one is an error.",
        };
        Option<bool> jsonOption = CommonOptions.Json();

        var verify = new Command("verify", "Verify the signatures and the chain of a server's or elm-service's log files.")
        {
            filesArgument,
            productOption,
            nodeOption,
            streamOption,
            trustOption,
            revokedOption,
            licenseOption,
            jsonOption,
        };
        verify.SetAction(parse =>
            CommonOptions.Run(
                host,
                () =>
                {
                    string stream = parse.GetValue(streamOption)!;
                    if (stream is not ("usage" or "borrows"))
                    {
                        throw new CliException("--stream must be usage or borrows.");
                    }

                    TrustStore trust = TrustKeys.Store(host, parse.GetValue(trustOption), parse.GetValue(revokedOption));
                    string? node = parse.GetValue(nodeOption);
                    if (node is not null && (stream != "usage" || !IsNodeName(node)))
                    {
                        throw new CliException("--node must be 1 to 64 characters of A-Z, a-z, 0-9, and '-', for the usage stream.");
                    }

                    string[] paths = parse.GetValue(filesArgument)!;
                    if (node is not null && paths.Any(File.Exists))
                    {
                        throw new CliException("--node chooses a node's files in a folder; give folders, or files without --node.");
                    }

                    string? expected = parse.GetValue(licenseOption) is string given ? CliParse.NotEmpty(given, "--license") : null;
                    string[] files = ExpandFiles(host, paths, node is null ? stream : $"{stream}-{node}", out IReadOnlyList<string> notLogFiles);
                    LogReport report = LogVerifier.Verify(files, trust, parse.GetValue(productOption), stream, node);

                    // design §18.1: a file of a folder whose name has no real date and time is not verified. It is named in a
                    // warning, so that a log file renamed to such a name does not leave the result without a trace. With
                    // --license, a file signed under another server license is an error (V1d27, T15-3).
                    LogIssue[] issues =
                    [
                        .. notLogFiles.Select(NotVerifiedIssue),
                        .. report.Issues,
                        .. expected is null
                            ? Enumerable.Empty<LogIssue>()
                            : report.Files.Where(f => f.LicenseId != expected).Select(f => OtherLicense(f, expected)),
                    ];
                    bool intact = !issues.Any(i => i.Severity == LogIssueSeverity.Error);
                    if (parse.GetValue(jsonOption))
                    {
                        var output = new LogVerifyOutput(
                            intact,
                            files.Length,
                            report.EntryCount,
                            [
                                .. issues.Select(i => new LogIssueOutput(
                                    i.File,
                                    i.Line,
                                    i.Severity == LogIssueSeverity.Error ? "error" : "warning",
                                    i.Message
                                )),
                            ],
                            [.. report.Files.Select(f => new LogLicenseOutput(f.File, f.LicenseId))]
                        );
                        host.Out.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.LogVerifyOutput));
                    }
                    else
                    {
                        foreach (LogIssue issue in issues)
                        {
                            host.Out.WriteLine(IssueLine(issue));
                        }

                        // design §18.1 (V1d27): the server license whose key signed the end of each file.
                        foreach (LogFileKey file in report.Files)
                        {
                            string signer = file.LicenseId is string id ? "server license " + CliText.Printable(id) : "elm-service";
                            host.Out.WriteLine($"file: {CliText.Printable(file.File)}: {signer}");
                        }

                        host.Out.WriteLine((intact ? "intact" : "NOT INTACT") + $": {files.Length} file(s), {report.EntryCount} event line(s)");
                    }

                    return intact ? ExitCodes.Success : ExitCodes.VerificationFailed;
                }
            )
        );

        return new Command("log", "Verify the signed logs of a license server or of elm-service.") { verify };
    }

    // design §14.8: a node name of elm-service, which is part of its usage log's file names.
    private static bool IsNodeName(string node) =>
        node.Length is >= 1 and <= 64 && node.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    private static LogIssue NotVerifiedIssue(string name) => new(name, 0, LogIssueSeverity.Warning, NotVerified);

    // design §18.1 (V1d27): the file's name and the message come from the files, so their control characters are replaced.
    private static string IssueLine(LogIssue issue) =>
        $"{(issue.Severity == LogIssueSeverity.Error ? "error" : "warning")}: {CliText.Printable(issue.File)}:{issue.Line}: {CliText.Printable(issue.Message)}";

    // design §18.1 (V1d27, T15-3): a file whose end was signed under another server license than --license, or by elm-service.
    private static LogIssue OtherLicense(LogFileKey file, string expected)
    {
        string message = file.LicenseId is string id
            ? $"The file is signed by the key of server license {id}, not of {expected} (--license)."
            : $"The file is a log of elm-service, not of server license {expected} (--license).";
        return new LogIssue(file.File, 0, LogIssueSeverity.Error, message);
    }

    // A folder stands for the files of the stream (or of one node's usage log) in it; files are taken in name order, which
    // is time order.
    private static string[] ExpandFiles(CliHost host, string[] paths, string stream, out IReadOnlyList<string> notLogFiles)
    {
        var files = new List<string>();
        var others = new List<string>();
        notLogFiles = others;
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                files.AddRange(SignedLog.FindFiles(path, stream, out IReadOnlyList<string> named));
                others.AddRange(named);
            }
            else if (File.Exists(path))
            {
                files.Add(Path.GetFullPath(path));
            }
            else
            {
                throw new CliException($"'{path}' is not a file or a folder.");
            }
        }

        if (files.Count == 0)
        {
            // design §18.1 (V1d27, Issue #404): the files that were not verified are named before the command stops. No
            // result follows, so the warnings go where the error goes, before it.
            foreach (string name in others)
            {
                host.Error.WriteLine(IssueLine(NotVerifiedIssue(name)));
            }

            // design §16: a folder of elm-service's usage logs holds usage-<node>-*.jsonl, which need --node.
            bool nodes = stream == "usage" && paths.Any(p => Directory.Exists(p) && Directory.EnumerateFiles(p, "usage-*-*.jsonl").Any());
            throw new CliException(
                nodes ? "No usage log file was found; for elm-service's files, add --node <name>." : $"No {stream} log file was found."
            );
        }

        return [.. files.Distinct(StringComparer.Ordinal).OrderBy(Path.GetFileName, StringComparer.Ordinal)];
    }
}
