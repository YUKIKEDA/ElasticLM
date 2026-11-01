using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Reads the reference pages of the user documentation, in English and in Japanese, from the repository (design §20.7, V1c2),
/// so that a test can check that every name the code defines appears on them, and takes the names of a package's public types and
/// members by reflection. Each test project that checks a page links this file.
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
    /// Returns the names that do not appear in their section of one reference page, in either language. A section starts at a
    /// heading of the given level that holds the section's name as a whole code span, such as
    /// <c>## `elm-server` local administration API</c> or <c>### `LicenseClient`</c>, and runs to the next heading of the same or a
    /// higher level. A name appears when a whole inline code span is the name, such as <c>GET /v1/nonce</c> or
    /// <c>TrustStore</c>; lines in fenced code blocks do not count.
    /// </summary>
    /// <param name="page">The page's file name under <c>reference/</c>, such as <c>device-protocol.md</c>.</param>
    /// <param name="level">The level of the sections' headings: 2 for <c>## </c>, 3 for <c>### </c>.</param>
    /// <param name="names">The names to find, by the name of the section that must hold them.</param>
    /// <returns>One line per missing name, prefixed with the page's path and the section; empty when every name appears.</returns>
    public static IReadOnlyList<string> FindMissingInSections(string page, int level, IReadOnlyDictionary<string, IReadOnlyList<string>> names) =>
        FindMissingSpans(page, level, [.. names.Select(section => ((string?)section.Key, section.Value))]);

    /// <summary>
    /// Returns the names that do not appear anywhere on one reference page, in either language. A name appears when a whole
    /// inline code span is the name, such as <c>elm_verifier_create</c>; lines in fenced code blocks do not count.
    /// </summary>
    /// <param name="page">The page's file name under <c>reference/</c>, such as <c>c-api.md</c>.</param>
    /// <param name="names">The names to find.</param>
    /// <returns>One line per missing name, prefixed with the page's path; empty when every name appears.</returns>
    public static IReadOnlyList<string> FindMissingOnPage(string page, IEnumerable<string> names) =>
        FindMissingSpans(page, 0, [(null, [.. names.Distinct(StringComparer.Ordinal)])]);

    /// <summary>
    /// Returns the name and HTTP status pairs that one reference page does not hold, in either language. A page holds a pair when
    /// a table row has the name in a code span as a whole cell, and the row's cell in the column headed "HTTP status" (English)
    /// or "HTTP の状態" (Japanese) starts with the status as its first whole number: 401 is found in "401; 400 for an offline
    /// request file" and in "401（オフラインのリクエストファイルでは 400）", but 400 is not. Only the first number counts, so a
    /// later number for another route (a license reload's 409) cannot hide a wrong status in the code's table.
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
                            && WholeNumber().Match(row.Cells[row.Status]) is { Success: true } first
                            && first.Value == Number(pair.Status)
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

    /// <summary>
    /// Returns the full path of a file of the repository.
    /// </summary>
    /// <param name="relative">The file's path under the repository root, such as <c>include/elasticlm.h</c>.</param>
    /// <returns>The full path.</returns>
    public static string RepositoryFile(string relative) => Path.Combine(RepositoryRoot.Value, relative);

    /// <summary>
    /// Returns the names of an assembly's public top-level types, without the generic arity suffix (<c>`1</c>). Nested types
    /// are left out: the page lists them in their parent type's row (design §20.7, V1c2c).
    /// </summary>
    /// <param name="assembly">The assembly of a package.</param>
    /// <returns>The names, in order.</returns>
    public static IReadOnlyList<string> TypeNames(Assembly assembly) =>
        [
            .. assembly
                .GetExportedTypes()
                .Where(type => type.DeclaringType is null)
                .Select(type => type.Name.Split('`')[0])
                .Distinct()
                .Order(StringComparer.Ordinal),
        ];

    /// <summary>
    /// Returns the names of the public methods a type declares, static ones included. Left out are the methods the compiler
    /// makes or names specially (property accessors, event add and remove, operators, and a record's members, which carry
    /// <see cref="CompilerGeneratedAttribute"/>) and the overrides of <see cref="object"/>'s methods (<c>Equals</c>,
    /// <c>GetHashCode</c>, <c>ToString</c>). Overloads give one name.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns>The names, in order.</returns>
    public static IReadOnlyList<string> MethodNames(Type type) =>
        [
            .. type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method =>
                    !method.IsSpecialName
                    && !method.IsDefined(typeof(CompilerGeneratedAttribute))
                    && method.GetBaseDefinition().DeclaringType != typeof(object)
                )
                .Select(method => method.Name)
                .Distinct()
                .Order(StringComparer.Ordinal),
        ];

    /// <summary>
    /// Returns the names an entry type's section must hold: the public methods the type declares (<see cref="MethodNames"/>) and
    /// the public properties of its options type, static ones included.
    /// </summary>
    /// <param name="entry">The entry type, such as <c>LicenseVerifier</c>.</param>
    /// <param name="options">Its options type, or <see langword="null"/> when it has none.</param>
    /// <returns>The names.</returns>
    public static IReadOnlyList<string> EntryNames(Type entry, Type? options) =>
        [
            .. MethodNames(entry),
            .. options?.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(property => property.Name) ?? [],
        ];

    private static List<string> FindMissingSpans(string page, int level, (string? Section, IReadOnlyList<string> Names)[] wanted)
    {
        string Line(string relative, string? section, string name) => relative + ": " + (section is null ? string.Empty : section + ": ") + name;
        var missing = new List<string>();
        foreach (string folder in (string[])["docs/guide/reference", "docs/guide.ja/reference"])
        {
            string relative = folder + "/" + page;
            string path = Path.Combine(RepositoryRoot.Value, relative);
            if (!File.Exists(path))
            {
                missing.Add(relative + ": the page does not exist");
                missing.AddRange(wanted.SelectMany(section => section.Names.Select(name => Line(relative, section.Section, name))));
                continue;
            }

            string[] lines = File.ReadAllLines(path);
            foreach ((string? section, IReadOnlyList<string> names) in wanted)
            {
                HashSet<string> spans = SectionSpans(lines, level, section);
                if (spans.Count == 0)
                {
                    missing.Add(
                        relative
                            + (
                                section is null
                                    ? ": the page has no code"
                                    : $": no level-{level} heading holds `{section}`, or its section has no code"
                            )
                    );
                }

                missing.AddRange(names.Where(name => !spans.Contains(name)).Select(name => Line(relative, section, name)));
            }
        }

        return missing;
    }

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

    // The inline code spans of every section whose heading of the level holds the section's name as a whole code span, to the next
    // heading of the same or a higher level; of the whole page when the section is null. Deeper headings are part of the section,
    // as the route headings (### `GET /v1/nonce`) are. Lines in fenced code blocks are neither headings nor spans.
    private static HashSet<string> SectionSpans(string[] lines, int level, string? section)
    {
        var spans = new HashSet<string>(StringComparer.Ordinal);
        bool inSection = section is null;
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

            int heading = line.TakeWhile(c => c == '#').Count();
            if (section is not null && heading > 0 && heading <= level && line.Length > heading && line[heading] == ' ')
            {
                inSection = heading == level && CodeSpan().Matches(line).Any(match => match.Groups["code"].Value == section);
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
