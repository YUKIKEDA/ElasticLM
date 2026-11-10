using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Checks that text users read holds no identifier that only this repository explains (design §20.7, V1c6): the string literals
/// under <c>src/</c> and the XML documentation file of <c>ElasticLM.Licensing</c>. The tests of <c>ElasticLM.Client</c> and of
/// <c>libelasticlm</c> check that package's XML documentation file and the C header with the same patterns.
/// </summary>
public sealed partial class InternalReferencesTests
{
    /// <summary>
    /// Each pattern finds its own kind of internal identifier, and no other pattern finds it too.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - One text per case with one kind of identifier: section signs with and without "design", a design number without the
    ///   sign in both letter cases, decision numbers in the singular and the plural, a threat and an attacker ID, roadmap row
    ///   names of every prefix (D0, W0, F2, M1, M4c, M4c3, V1, V1c11, V1c4d2b, A-1), and Issue numbers.
    ///
    /// Steps:
    /// - Find the internal identifiers of the text.
    ///
    /// Expected:
    /// - The matches are exactly those of the named pattern, with the given matched text, once for each time it occurs.
    /// </remarks>
    /// <param name="text">The text.</param>
    /// <param name="pattern">The name of the pattern that matches.</param>
    /// <param name="matched">The matched texts, separated by '|'.</param>
    [Theory]
    [InlineData("The trust store (design §13.7).", "section", "§")]
    [InlineData("See §8.6 and §8.7.", "section", "§|§")]
    [InlineData("RFC 7515, §2", "section", "§")]
    [InlineData("It cannot be revoked (design 5.4).", "design-number", "design 5")]
    [InlineData("Design 9.6 says so.", "design-number", "Design 9")]
    [InlineData("By decision 0.94.", "decision-number", "decision 0")]
    [InlineData("Decisions 0.94 and 0.95.", "decision-number", "Decisions 0")]
    [InlineData("The threat T12.", "threat", "T12")]
    [InlineData("The attacker (A3) holds the file.", "threat", "A3")]
    [InlineData("Since D0.", "roadmap", "D0")]
    [InlineData("Since W0.", "roadmap", "W0")]
    [InlineData("Since F2.", "roadmap", "F2")]
    [InlineData("Since M1.", "roadmap", "M1")]
    [InlineData("Since M4c.", "roadmap", "M4c")]
    [InlineData("The server's kind (M4c3).", "roadmap", "M4c3")]
    [InlineData("After V1.", "roadmap", "V1")]
    [InlineData("A header of 130 characters, V1c11.", "roadmap", "V1c11")]
    [InlineData("V1c4d2b", "roadmap", "V1c4d2b")]
    [InlineData("The row A-1.", "roadmap", "A-1")]
    [InlineData("Issue #198.", "issue", "#198")]
    [InlineData("(#7)", "issue", "#7")]
    public void Find_OneKindOfIdentifier_OnlyItsPatternMatches(string text, string pattern, string matched)
    {
        IReadOnlyList<(string Pattern, string Text)> found = InternalReferences.Find(text);

        Assert.Equal([.. matched.Split('|').Select(value => (pattern, value))], found);
    }

    /// <summary>
    /// Names of public standards, settings, addresses, routes, algorithms, and plain numbers are not internal identifiers.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - One text per case that is near a pattern and must pass: an RFC's section, PKCS#8, an environment variable, an IP
    ///   address, a route with a lower-case v1, algorithm and curve names, a format string, years, a threat-like ID of three
    ///   digits or inside a word, roadmap-like names outside the roadmap's ranges (M7, F3, V2, D1), lower-case forms, "design"
    ///   and "decision" not followed by a number, and a '#' that no number follows or that follows a word character.
    ///
    /// Steps:
    /// - Find the internal identifiers of the text.
    ///
    /// Expected:
    /// - None.
    /// </remarks>
    /// <param name="text">The text.</param>
    [Theory]
    [InlineData("RFC 7515 §2")]
    [InlineData("The JWS of RFC 7518 §3.4.")]
    [InlineData("A PKCS#8 file.")]
    [InlineData("ELM_PRODUCT")]
    [InlineData("0.0.0.0")]
    [InlineData("/v1/nonce")]
    [InlineData("ES256 on P-256 with SHA256")]
    [InlineData("X509 and UTF8")]
    [InlineData("The validity period must lie between 1970 and 9999.")]
    [InlineData("HTTP 409")]
    [InlineData("T123")]
    [InlineData("AT12")]
    [InlineData("T12x")]
    [InlineData("t12 and a3")]
    [InlineData("M7, F3, V2, D1, W1")]
    [InlineData("m4c3 and v1c11")]
    [InlineData("XM4c3")]
    [InlineData("M4c3x")]
    [InlineData("The design allows 3 retries; it was designed 3 times.")]
    [InlineData("The decision is 3.")]
    [InlineData("C# and #include and a#1")]
    [InlineData("#12a")]
    [InlineData("")]
    public void Find_NoInternalIdentifier_ReturnsNone(string text)
    {
        Assert.Empty(InternalReferences.Find(text));
    }

    /// <summary>
    /// The roadmap pattern knows every row name of the roadmap.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - docs/roadmap.md under the repository root.
    ///
    /// Steps:
    /// - Take the first cell of every table row (a line that starts with '|'), except the header rows, whose first cell is
    ///   "ID", and the rows of dashes below them. A row whose first cell is empty is taken too.
    ///
    /// Expected:
    /// - The roadmap has such rows.
    /// - The roadmap pattern matches each first cell from its first character to its last; the message lists those it does
    ///   not. A row named in a new way, or written in another form (bold, a link), fails here, so that the pattern is widened
    ///   with it.
    /// </remarks>
    [Fact]
    public void RoadmapPattern_EveryRowOfTheRoadmap_MatchesInFull()
    {
        string[] ids =
        [
            .. File.ReadAllLines(ReferencePages.RepositoryFile("docs/roadmap.md"))
                .Select(line => RoadmapRow().Match(line))
                .Where(match => match.Success)
                .Select(match => match.Groups[1].Value.Trim())
                .Where(cell => cell != "ID" && !(cell.Length > 0 && cell.Trim('-', ':').Length == 0)),
        ];

        Assert.NotEmpty(ids);
        string[] unknown = [.. ids.Where(id => !InternalReferences.IsRoadmapRow(id))];
        Assert.True(unknown.Length == 0, "docs/roadmap.md: row names the roadmap pattern does not match: '" + string.Join("', '", unknown) + "'");
    }

    /// <summary>
    /// No string literal under src/ holds an internal identifier: program output, error messages, and log messages come from
    /// them.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Every .cs file under src/ of the repository, except those in a bin or obj folder.
    ///
    /// Steps:
    /// - Parse each file and take the value of every string token: string literals, UTF-8 string literals, raw string literals
    ///   of each kind, and the text parts of interpolated strings. The format clause of an interpolation ({x:F2}) is not read.
    /// - Find the internal identifiers of each value.
    ///
    /// Expected:
    /// - No file has a syntax error; the message names the files that have.
    /// - No file has an #if directive; the message names the files that have. The strings of a branch that is not compiled
    ///   are not tokens, so the check would pass without reading them.
    /// - Files and string tokens were read.
    /// - No value holds an internal identifier; the message names the file, the line, the pattern, and the matched text of each.
    /// </remarks>
    [Fact]
    public void SourceStrings_EveryStringLiteralUnderSrc_HoldsNoInternalIdentifier()
    {
        string root = ReferencePages.RepositoryFile("src");
        var broken = new List<string>();
        var conditional = new List<string>();
        var found = new List<string>();
        int files = 0;
        int tokens = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = "src/" + Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Split('/').Any(part => part is "bin" or "obj"))
            {
                continue;
            }

            files++;
            SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), cancellationToken: TestContext.Current.CancellationToken);
            if (tree.GetDiagnostics(TestContext.Current.CancellationToken).Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                broken.Add(relative);
                continue;
            }

            SyntaxNode syntax = tree.GetRoot(TestContext.Current.CancellationToken);
            if (syntax.GetFirstDirective(directive => directive is IfDirectiveTriviaSyntax) is not null)
            {
                conditional.Add(relative);
            }

            foreach (SyntaxToken token in syntax.DescendantTokens())
            {
                if (!IsStringText(token) || token.Parent is InterpolationFormatClauseSyntax)
                {
                    continue;
                }

                tokens++;
                int line = token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                found.AddRange(InternalReferences.Find(token.ValueText).Select(hit => $"{relative}:{line}: {hit.Pattern} '{hit.Text}'"));
            }
        }

        Assert.True(broken.Count == 0, "Files under src/ with syntax errors: " + string.Join(", ", broken));
        Assert.True(conditional.Count == 0, "Files under src/ with an #if directive: " + string.Join(", ", conditional));
        Assert.True(files > 0, "No .cs file was found under src/.");
        Assert.True(tokens > 0, "No string token was found under src/.");
        Assert.True(
            found.Count == 0,
            found.Count + " internal identifiers in string literals under src/:" + Environment.NewLine + string.Join(Environment.NewLine, found)
        );
    }

    /// <summary>
    /// The XML documentation file of ElasticLM.Licensing, which ships in the package, holds no internal identifier.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - ElasticLM.Licensing.xml in the test's output folder, as the compiler wrote it for the referenced project. It holds the
    ///   types and members that are not public too.
    ///
    /// Steps:
    /// - Take the text of every member element, with every run of white space as one space.
    /// - Find the internal identifiers of each text.
    ///
    /// Expected:
    /// - The file exists and has members.
    /// - No member's text holds an internal identifier; the message names the member, the pattern, and the matched text of each.
    /// </remarks>
    [Fact]
    public void XmlDocumentation_EveryMemberOfLicensing_HoldsNoInternalIdentifier()
    {
        IReadOnlyList<(string Name, string Text)> members = InternalReferences.DocumentedMembers("ElasticLM.Licensing");

        Assert.NotEmpty(members);
        IReadOnlyList<string> found = InternalReferences.FindInMembers(members);
        Assert.True(
            found.Count == 0,
            found.Count + " internal identifiers in ElasticLM.Licensing.xml:" + Environment.NewLine + string.Join(Environment.NewLine, found)
        );
    }

    private static bool IsStringText(SyntaxToken token) =>
        token.Kind()
            is SyntaxKind.StringLiteralToken
                or SyntaxKind.Utf8StringLiteralToken
                or SyntaxKind.SingleLineRawStringLiteralToken
                or SyntaxKind.MultiLineRawStringLiteralToken
                or SyntaxKind.Utf8SingleLineRawStringLiteralToken
                or SyntaxKind.Utf8MultiLineRawStringLiteralToken
                or SyntaxKind.InterpolatedStringTextToken;

    [GeneratedRegex(@"^\|([^|]*)\|")]
    private static partial Regex RoadmapRow();
}
