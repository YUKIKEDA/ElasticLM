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
    public static Command Create(CliHost host)
    {
        var filesArgument = new Argument<string[]>("files")
        {
            Description = "The log files, or a folder of them. They are verified in the order of their names (oldest first).",
            Arity = ArgumentArity.OneOrMore,
        };
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
        };
        var revokedOption = new Option<string[]>("--revoked") { Description = "A revoked key ID. Repeat for several." };
        Option<bool> jsonOption = CommonOptions.Json();

        var verify = new Command("verify", "Verify the signatures and the chain of a server's or elm-service's log files.")
        {
            filesArgument,
            productOption,
            nodeOption,
            streamOption,
            trustOption,
            revokedOption,
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

                    TrustStore trust = TrustStore.Create(
                        TrustKeys.Read(host, parse.GetValue(trustOption) ?? []),
                        parse.GetValue(revokedOption) ?? []
                    );
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

                    string[] files = ExpandFiles(paths, node is null ? stream : $"{stream}-{node}");
                    LogReport report = LogVerifier.Verify(files, trust, parse.GetValue(productOption), stream);
                    if (parse.GetValue(jsonOption))
                    {
                        var output = new LogVerifyOutput(
                            report.Intact,
                            files.Length,
                            report.Entries.Count,
                            [
                                .. report.Issues.Select(i => new LogIssueOutput(
                                    i.File,
                                    i.Line,
                                    i.Severity == LogIssueSeverity.Error ? "error" : "warning",
                                    i.Message
                                )),
                            ]
                        );
                        host.Out.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.LogVerifyOutput));
                    }
                    else
                    {
                        foreach (LogIssue issue in report.Issues)
                        {
                            string severity = issue.Severity == LogIssueSeverity.Error ? "error" : "warning";
                            host.Out.WriteLine($"{severity}: {issue.File}:{issue.Line}: {issue.Message}");
                        }

                        host.Out.WriteLine(
                            (report.Intact ? "intact" : "NOT INTACT") + $": {files.Length} file(s), {report.Entries.Count} event line(s)"
                        );
                    }

                    return report.Intact ? ExitCodes.Success : ExitCodes.VerificationFailed;
                }
            )
        );

        return new Command("log", "Verify the signed logs of a license server or of elm-service.") { verify };
    }

    // design §14.8: a node name of elm-service, which is part of its usage log's file names.
    private static bool IsNodeName(string node) =>
        node.Length is >= 1 and <= 64 && node.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    // A folder stands for the files of the stream (or of one node's usage log) in it; files are taken in name order, which
    // is time order.
    private static string[] ExpandFiles(string[] paths, string stream)
    {
        var files = new List<string>();
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                files.AddRange(SignedLog.FindFiles(path, stream));
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
            // design §16: a folder of elm-service's usage logs holds usage-<node>-*.jsonl, which need --node.
            bool nodes = stream == "usage" && paths.Any(p => Directory.Exists(p) && Directory.EnumerateFiles(p, "usage-*-*.jsonl").Any());
            throw new CliException(
                nodes ? "No usage log file was found; for elm-service's files, add --node <name>." : $"No {stream} log file was found."
            );
        }

        return [.. files.Distinct(StringComparer.Ordinal).OrderBy(Path.GetFileName, StringComparer.Ordinal)];
    }
}
