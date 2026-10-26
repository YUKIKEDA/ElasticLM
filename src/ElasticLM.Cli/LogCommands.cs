using System.CommandLine;
using System.Text.Json;
using ElasticLM.Issuing;
using ElasticLM.Licensing;

namespace ElasticLM.Cli;

/// <summary>
/// <c>elm log</c>: verifying the signed usage log and borrow record of a server (design §16, §18.1).
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
        var productOption = new Option<string>("--product") { Description = "The product ID of the server license.", Required = true };
        var streamOption = new Option<string>("--stream") { Description = "usage or borrows. Default: usage.", DefaultValueFactory = _ => "usage" };
        var trustOption = new Option<string[]>("--trust")
        {
            Description = $"A root public key file to trust. Repeat for several. Default: the PEM keys in {TrustKeys.EnvironmentVariable}.",
        };
        var revokedOption = new Option<string[]>("--revoked") { Description = "A revoked key ID. Repeat for several." };
        Option<bool> jsonOption = CommonOptions.Json();

        var verify = new Command("verify", "Verify the signatures and the chain of a server's log files.")
        {
            filesArgument,
            productOption,
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
                    string[] files = ExpandFiles(parse.GetValue(filesArgument)!, stream);
                    LogReport report = LogVerifier.Verify(files, trust, parse.GetValue(productOption)!, stream);
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

        return new Command("log", "Verify the signed logs of a license server.") { verify };
    }

    // A folder stands for the files of the stream in it; files are taken in name order, which is time order.
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
            throw new CliException($"No {stream} log file was found.");
        }

        return [.. files.Distinct(StringComparer.Ordinal).OrderBy(Path.GetFileName, StringComparer.Ordinal)];
    }
}
