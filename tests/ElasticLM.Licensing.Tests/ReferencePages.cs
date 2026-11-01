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
    /// Returns the routes that do not appear in their program's section of one reference page, in either language. A program's
    /// section starts at a level-2 heading (<c>## </c>) that holds the program's name as a whole code span, such as
    /// <c>## `elm-server` local administration API</c>, and runs to the next level-2 heading. A route appears when a whole
    /// inline code span is the route: the method, one space, and the path, such as <c>GET /v1/nonce</c>.
    /// </summary>
    /// <param name="page">The page's file name under <c>reference/</c>, such as <c>device-protocol.md</c>.</param>
    /// <param name="routes">The routes to find, by the name of the program whose section must hold them.</param>
    /// <returns>One line per missing route, prefixed with the page's path and the program; empty when every route appears.</returns>
    public static IReadOnlyList<string> FindMissingRoutes(string page, IReadOnlyDictionary<string, IReadOnlyList<string>> routes)
    {
        var missing = new List<string>();
        foreach (string folder in (string[])["docs/guide/reference", "docs/guide.ja/reference"])
        {
            string relative = folder + "/" + page;
            string path = Path.Combine(RepositoryRoot.Value, relative);
            if (!File.Exists(path))
            {
                missing.Add(relative + ": the page does not exist");
                missing.AddRange(routes.SelectMany(program => program.Value.Select(route => relative + ": " + program.Key + ": " + route)));
                continue;
            }

            string[] lines = File.ReadAllLines(path);
            foreach ((string program, IReadOnlyList<string> wanted) in routes)
            {
                HashSet<string> spans = SectionSpans(lines, program);
                if (spans.Count == 0)
                {
                    missing.Add(relative + ": no level-2 heading holds `" + program + "`, or its section has no code");
                }

                missing.AddRange(wanted.Where(route => !spans.Contains(route)).Select(route => relative + ": " + program + ": " + route));
            }
        }

        return missing;
    }

    /// <summary>
    /// Returns the name and HTTP status pairs that one reference page does not hold, in either language. A page holds a pair when
    /// a table row has the name in a code span as a whole cell, and the row's cell in the column headed "HTTP status" (English)
    /// or "HTTP の状態" (Japanese) has the status as a whole number: 401 is found in "401; 400 for an offline request file".
    /// </summary>
    /// <param name="page">The page's file name under <c>reference/</c>, such as <c>error-codes.md</c>.</param>
    /// <param name="pairs">The names and the statuses that belong to them.</param>
    /// <returns>One line per pair the page does not hold, prefixed with the page's path; empty when the page holds every pair.</returns>
    public static IReadOnlyList<string> FindMissingStatuses(string page, IEnumerable<(string Name, int Status)> pairs)
    {
        (string Name, int Status)[] wanted = [.. pairs];
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

            // Each table's rows, with the index of its status column, or -1 when no column has the heading.
            var rows = new List<(string[] Cells, int Status)>();
            int status = -1;
            bool header = true;
            foreach (string line in File.ReadAllLines(path))
            {
                if (!line.TrimStart().StartsWith('|'))
                {
                    header = true;
                    continue;
                }

                string[] cells = [.. line.Split('|').Select(cell => cell.Trim())];
                if (header)
                {
                    status = Array.FindIndex(cells, cell => cell is "HTTP status" or "HTTP の状態");
                    header = false;
                    continue;
                }

                rows.Add((cells, status));
            }

            string Number(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            missing.AddRange(
                wanted
                    .Where(pair =>
                        !rows.Any(row =>
                            row.Status >= 0
                            && row.Status < row.Cells.Length
                            && row.Cells.Contains("`" + pair.Name + "`", StringComparer.Ordinal)
                            && WholeNumber().Matches(row.Cells[row.Status]).Any(match => match.Value == Number(pair.Status))
                        )
                    )
                    .Select(pair => relative + ": " + pair.Name + " " + Number(pair.Status))
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

    // The inline code spans of every section whose level-2 heading holds the program's name as a whole code span. Lines in fenced
    // code blocks are neither headings nor spans.
    private static HashSet<string> SectionSpans(string[] lines, string program)
    {
        var spans = new HashSet<string>(StringComparer.Ordinal);
        bool inSection = false;
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

                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = trimmed[..3];
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inSection = CodeSpan().Matches(line).Any(match => match.Groups["code"].Value == program);
                continue;
            }

            if (inSection)
            {
                spans.UnionWith(CodeSpan().Matches(line).Select(match => match.Groups["code"].Value));
            }
        }

        return spans;
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

    [GeneratedRegex("[0-9]+")]
    private static partial Regex WholeNumber();
}
