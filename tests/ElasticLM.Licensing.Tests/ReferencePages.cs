using System.Text.RegularExpressions;

namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Reads the reference pages of the user documentation, in English and in Japanese, from the repository (design §20.7, V1c2),
/// so that a test can check that every name the code defines appears on them. Each test project that checks a page links this
/// file.
/// </summary>
internal static partial class ReferencePages
{
    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    /// <summary>
    /// Returns the names that do not appear in code on one reference page, in either language. A name appears when an inline
    /// code span or a line of a fenced code block holds it as a whole word: not as part of a longer name.
    /// </summary>
    /// <param name="page">The page's file name under <c>reference/</c>, such as <c>error-codes.md</c>.</param>
    /// <param name="names">The names to find.</param>
    /// <returns>One line per missing name, prefixed with the page's path; empty when every name appears.</returns>
    public static IReadOnlyList<string> FindMissing(string page, IEnumerable<string> names)
    {
        string[] wanted = [.. names.Distinct(StringComparer.Ordinal)];
        var missing = new List<string>();
        foreach (string folder in (string[])["docs/guide/reference", "docs/guide.ja/reference"])
        {
            string relative = folder + "/" + page;
            string path = Path.Combine(RepositoryRoot.Value, relative);
            if (!File.Exists(path))
            {
                missing.Add(relative + ": the page does not exist");
                missing.AddRange(wanted.Select(name => relative + ": " + name));
                continue;
            }

            IReadOnlyList<string> code = CodeOf(File.ReadAllLines(path));
            missing.AddRange(wanted.Where(name => !code.Any(text => HoldsWord(text, name))).Select(name => relative + ": " + name));
        }

        return missing;
    }

    /// <summary>
    /// Returns the name and number pairs that no table row of one reference page holds together, in either language. A row holds
    /// a pair when one of its cells is the name in a code span and another cell is the number, alone or in a code span.
    /// </summary>
    /// <param name="page">The page's file name under <c>reference/</c>, such as <c>error-codes.md</c>.</param>
    /// <param name="pairs">The names and the numbers that belong to them.</param>
    /// <returns>One line per pair no row holds, prefixed with the page's path; empty when every pair is in one row.</returns>
    public static IReadOnlyList<string> FindMissingRows(string page, IEnumerable<(string Name, int Number)> pairs)
    {
        (string Name, int Number)[] wanted = [.. pairs];
        var missing = new List<string>();
        foreach (string folder in (string[])["docs/guide/reference", "docs/guide.ja/reference"])
        {
            string relative = folder + "/" + page;
            string path = Path.Combine(RepositoryRoot.Value, relative);
            if (!File.Exists(path))
            {
                missing.Add(relative + ": the page does not exist");
                continue;
            }

            string[][] rows =
            [
                .. File.ReadAllLines(path)
                    .Where(line => line.TrimStart().StartsWith('|'))
                    .Select(line => line.Split('|').Select(cell => cell.Trim().Trim('`')).ToArray()),
            ];
            string Number(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            missing.AddRange(
                wanted
                    .Where(pair =>
                        !rows.Any(cells =>
                            cells.Contains(pair.Name, StringComparer.Ordinal) && cells.Contains(Number(pair.Number), StringComparer.Ordinal)
                        )
                    )
                    .Select(pair => relative + ": " + pair.Name + " " + Number(pair.Number))
            );
        }

        return missing;
    }

    /// <summary>
    /// Formats the missing names for an assertion message: one per line.
    /// </summary>
    /// <param name="missing">The lines from <see cref="FindMissing"/>.</param>
    /// <returns>The message.</returns>
    public static string Message(IReadOnlyList<string> missing) =>
        missing.Count + " names are missing from the reference pages:" + Environment.NewLine + string.Join(Environment.NewLine, missing);

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "ElasticLM.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException($"No folder above '{AppContext.BaseDirectory}' holds ElasticLM.slnx.");
    }

    private static List<string> CodeOf(string[] lines)
    {
        var code = new List<string>();
        string? fence = null;
        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();
            if (fence is not null)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal))
                {
                    fence = null;
                }
                else
                {
                    code.Add(line);
                }

                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = trimmed[..3];
                continue;
            }

            code.AddRange(CodeSpan().Matches(line).Select(match => match.Groups["code"].Value));
        }

        return code;
    }

    private static bool HoldsWord(string text, string name)
    {
        for (int at = text.IndexOf(name, StringComparison.Ordinal); at >= 0; at = text.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            int end = at + name.Length;
            if ((at == 0 || !IsNameCharacter(text[at - 1])) && (end == text.Length || !IsNameCharacter(text[end])))
            {
                return true;
            }
        }

        return false;
    }

    // A name runs on through letters, digits, '_', '-', and '.': ELM_OK is not found in ELM_OK_X, --key not in --key-file, and
    // listen not in admin.listen.
    private static bool IsNameCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.';

    [GeneratedRegex("(?<!`)(?<ticks>`+)(?!`)(?<code>.+?)(?<!`)\\k<ticks>(?!`)")]
    private static partial Regex CodeSpan();
}
