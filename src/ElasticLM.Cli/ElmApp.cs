using System.CommandLine;

namespace ElasticLM.Cli;

/// <summary>
/// The <c>elm</c> command line (design §16).
/// </summary>
internal static class ElmApp
{
    public static int Run(string[] args, CliHost host)
    {
        var root = new RootCommand("elm: keys, license files, and ElasticLM servers.");
        root.Subcommands.Add(KeyCommands.Create(host));
        root.Subcommands.Add(LicenseCommands.Create(host));
        root.Subcommands.Add(LogCommands.Create(host));
        root.Subcommands.Add(ServerCommands.Create(host));

        ParseResult parse = root.Parse(args);
        if (parse.Errors.Count > 0)
        {
            foreach (System.CommandLine.Parsing.ParseError error in parse.Errors)
            {
                host.Error.WriteLine("error: " + error.Message);
            }

            host.Error.WriteLine("Run 'elm --help' for usage.");
            return ExitCodes.InvalidInput;
        }

        return parse.Invoke(new InvocationConfiguration { Output = host.Out, Error = host.Error });
    }
}
