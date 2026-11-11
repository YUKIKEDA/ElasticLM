using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Finds the identifiers that only this repository explains (design section numbers, decision numbers, threat and attacker IDs,
/// roadmap row names, Issue and PR numbers, and test names) in text that users read: the string literals under <c>src/</c>, the
/// XML documentation files of the packages, and the C header (design §20.7, V1c6). Each test project that checks such text links
/// this file.
/// </summary>
internal static partial class InternalReferences
{
    /// <summary>
    /// Returns every internal identifier in a text.
    /// </summary>
    /// <param name="text">The text a user reads.</param>
    /// <returns>
    /// One item per match, with the name of the pattern and the text it matched, in the order section, design-number,
    /// decision-number, threat, roadmap, issue, test-name; empty when the text holds none.
    /// </returns>
    public static IReadOnlyList<(string Pattern, string Text)> Find(string text)
    {
        (string Name, Regex Pattern)[] patterns =
        [
            ("section", Section()),
            ("design-number", DesignNumber()),
            ("decision-number", DecisionNumber()),
            ("threat", Threat()),
            ("roadmap", Roadmap()),
            ("issue", Issue()),
            ("test-name", TestName()),
        ];
        return [.. patterns.SelectMany(pattern => pattern.Pattern.Matches(text).Select(match => (pattern.Name, match.Value)))];
    }

    /// <summary>
    /// Returns whether a whole text is a roadmap row name as the roadmap pattern knows them.
    /// </summary>
    /// <param name="id">The ID of a row of <c>docs/roadmap.md</c>.</param>
    /// <returns><see langword="true"/> when the pattern matches the ID from its first character to its last.</returns>
    public static bool IsRoadmapRow(string id)
    {
        Match match = Roadmap().Match(id);
        return match.Success && match.Length == id.Length;
    }

    /// <summary>
    /// Returns whether a whole text is a test name as the test-name pattern knows them.
    /// </summary>
    /// <param name="name">The name of a test method.</param>
    /// <returns><see langword="true"/> when the pattern matches the name from its first character to its last.</returns>
    public static bool IsTestName(string name)
    {
        Match match = TestName().Match(name);
        return match.Success && match.Length == name.Length;
    }

    /// <summary>
    /// Reads the XML documentation file that the compiler wrote for a referenced project, from the test's output folder. It is
    /// the file that goes into the project's package.
    /// </summary>
    /// <param name="assembly">The assembly's name, such as <c>ElasticLM.Licensing</c>.</param>
    /// <returns>Each <c>member</c> element's name and its text, with every run of white space as one space.</returns>
    /// <exception cref="FileNotFoundException">The output folder has no such file.</exception>
    public static IReadOnlyList<(string Name, string Text)> DocumentedMembers(string assembly)
    {
        string path = Path.Combine(AppContext.BaseDirectory, assembly + ".xml");
        return
        [
            .. XDocument
                .Load(path)
                .Descendants("member")
                .Select(member => ((string?)member.Attribute("name") ?? string.Empty, WhiteSpace().Replace(member.Value, " ").Trim())),
        ];
    }

    /// <summary>
    /// Returns the internal identifiers in the XML documentation file of a referenced project.
    /// </summary>
    /// <param name="members">The members from <see cref="DocumentedMembers"/>.</param>
    /// <returns>One line per match, with the member's name, the pattern's name, and the matched text.</returns>
    public static IReadOnlyList<string> FindInMembers(IReadOnlyList<(string Name, string Text)> members) =>
        [.. members.SelectMany(member => Find(member.Text).Select(hit => $"{member.Name}: {hit.Pattern} '{hit.Text}'"))];

    // The section sign, except after the number of an RFC: "RFC 7515 §2" names a public standard.
    [GeneratedRegex(@"(?<!\bRFC \d+ )§", RegexOptions.CultureInvariant)]
    private static partial Regex Section();

    // "(design 5.4)".
    [GeneratedRegex(@"\bdesign\s+\d", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DesignNumber();

    // "decision 0.94". A bare "0.94" cannot be told from any other number.
    [GeneratedRegex(@"\bdecisions?\s+\d", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DecisionNumber();

    // Threats (T12) and attackers (A3).
    [GeneratedRegex(@"(?<!\w)[TA]\d{1,2}(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex Threat();

    // The row names of docs/roadmap.md as they are given today (D0, W0, F1, M4c3, V1c11, A-1). A test compares the pattern with
    // the roadmap's rows and fails when a row is named another way.
    [GeneratedRegex(@"(?<!\w)(?:D0|W0|F[0-2]|M[1-6](?:[a-z]\d?)?|V1(?:[a-z][0-9a-z]*)?|A-\d+)(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex Roadmap();

    // Issue and PR numbers. In "PKCS#8" a word character stands before the '#'.
    [GeneratedRegex(@"(?<!\w)#\d+(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex Issue();

    // The names of this repository's tests (Verify_JwsRuleBroken_IsRejected): two or more parts joined by '_', each starting
    // with a capital letter. A name with no lower-case letter, such as ELM_NO_SEATS, is not one. A test compares the pattern
    // with the names of the test methods under tests/ and fails when a test is named another way.
    [GeneratedRegex(@"(?<!\w)(?=[A-Za-z0-9_]*[a-z])[A-Z][A-Za-z0-9]*(?:_[A-Z][A-Za-z0-9]*)+(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex TestName();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
