using System.CommandLine;
using System.CommandLine.Help;
using ElasticLM.Licensing.Tests;

namespace ElasticLM.Cli.Tests;

/// <summary>
/// Tests of the <c>elm</c> command tree (design §16).
/// </summary>
public sealed class ElmAppTests
{
    /// <summary>
    /// The command reference lists every command and option of <c>elm</c> (design §20.7, V1c2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - docs/guide/reference/elm-cli.md and docs/guide.ja/reference/elm-cli.md under the repository root.
    ///
    /// Steps:
    /// - Walk the tree of <see cref="ElmApp.CreateRoot"/>: every command as its full path from elm (elm license issue), every
    ///   option's name (--signing-key) and alias, and every argument's name (license-file), except the built-in --help, -h,
    ///   -?, and --version.
    /// - Look for each in code on both pages.
    ///
    /// Expected:
    /// - Every command path, option name, alias, and argument name appears as a whole word in a code span or a code block on
    ///   both pages; the message lists every one that does not.
    /// </remarks>
    [Fact]
    public void CreateRoot_AllCommandsAndOptions_AppearOnReferencePage()
    {
        using var output = new StringWriter();
        var host = new CliHost
        {
            Out = output,
            Error = output,
            Time = TimeProvider.System,
            GetEnvironmentVariable = _ => null,
        };
        var names = new List<string>();
        Walk(ElmApp.CreateRoot(host), "elm", names);

        IReadOnlyList<string> missing = ReferencePages.FindMissing("elm-cli.md", names);

        Assert.True(missing.Count == 0, ReferencePages.Message(missing));
    }

    private static void Walk(Command command, string path, List<string> names)
    {
        foreach (Option option in command.Options.Where(option => option is not (HelpOption or VersionOption)))
        {
            names.Add(option.Name);
            names.AddRange(option.Aliases);
        }

        names.AddRange(command.Arguments.Select(argument => argument.Name));
        foreach (Command subcommand in command.Subcommands)
        {
            string subpath = path + " " + subcommand.Name;
            names.Add(subpath);
            Walk(subcommand, subpath, names);
        }
    }
}
