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
    ///   names of every prefix (D0, W0, F2, M1, M4c, M4c3, V1, V1c11, V1c4d2b, A-1), Issue numbers, and test names of two,
    ///   three, and four parts, one of them with a first part of capital letters and digits only.
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
    [InlineData("A DER signature included (Verify_JwsRuleBroken_IsRejected, der-signature).", "test-name", "Verify_JwsRuleBroken_IsRejected")]
    [InlineData("Sweep_RemovesEndedSessions covers it.", "test-name", "Sweep_RemovesEndedSessions")]
    [InlineData("Activate_Check_Deactivate_WithCallbacks", "test-name", "Activate_Check_Deactivate_WithCallbacks")]
    [InlineData("Rule_Broken and Rule_Kept.", "test-name", "Rule_Broken|Rule_Kept")]
    [InlineData("ES256_WrongCurve_IsRejected", "test-name", "ES256_WrongCurve_IsRejected")]
    public void Find_OneKindOfIdentifier_OnlyItsPatternMatches(string text, string pattern, string matched)
    {
        IReadOnlyList<(string Pattern, string Text)> found = InternalReferences.Find(text);

        Assert.Equal([.. matched.Split('|').Select(value => (pattern, value))], found);
    }

    /// <summary>
    /// Names of public standards, settings, addresses, routes, and algorithms, plain numbers, and names joined by '_' that are
    /// not test names are not internal identifiers.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - One text per case that is near a pattern and must pass: an RFC's section, PKCS#8, an environment variable, an IP
    ///   address, a route with a lower-case v1, algorithm and curve names, a format string, years, a threat-like ID of three
    ///   digits or inside a word, roadmap-like names outside the roadmap's ranges (M7, F3, V2, D1), lower-case forms, "design"
    ///   and "decision" not followed by a number, a '#' that no number follows or that follows a word character, and names
    ///   joined by '_' that are not test names: names with no lower-case letter (an error code, a registry key, an algorithm
    ///   name), names in lower case (a C function, a column), a name whose first or later part starts in lower case or with a
    ///   digit, a test-like name inside a longer word, and words with no second part.
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
    [InlineData("ELM_NO_SEATS and HKEY_LOCAL_MACHINE and SHA256_P256")]
    [InlineData("elm_activation_destroy and expires_at")]
    [InlineData("camel_Case")]
    [InlineData("Pascal_case")]
    [InlineData("xVerify_Jws_IsRejected")]
    [InlineData("Verify_Jws_isRejected")]
    [InlineData("2Verify_Parts")]
    [InlineData("Verify_2Parts_Works")]
    [InlineData("Verify_ and _Verify and Verify")]
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
    ///   not. This test fails when a row is named in a new way or written in another form (bold, a link), so that the pattern
    ///   is widened with it.
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
    /// The test-name pattern knows the name of every xUnit test method under tests/.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Every .cs file under tests/ of the repository, except those in a bin or obj folder.
    ///
    /// Steps:
    /// - Parse each file and take the name of every method that has an attribute whose name, a trailing Attribute removed,
    ///   ends in Fact or Theory.
    ///
    /// Expected:
    /// - No file has a syntax error; the message names the files that have.
    /// - No file has an #if directive; the message names the files that have. The methods of a branch that is not compiled
    ///   are not read, so the check would pass without their names.
    /// - Test methods were found.
    /// - The test-name pattern matches each name from its first character to its last; the message lists those it does not,
    ///   with their files. This test fails when a test is named another way, so that the pattern is widened with it or the
    ///   test is renamed.
    /// </remarks>
    [Fact]
    public void TestNamePattern_EveryTestMethodUnderTests_MatchesInFull()
    {
        var broken = new List<string>();
        var conditional = new List<string>();
        var tests = new List<(string File, string Name)>();
        foreach ((string relative, string full) in SourceFiles("tests"))
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(full), cancellationToken: TestContext.Current.CancellationToken);
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

            tests.AddRange(
                syntax.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(IsTest).Select(method => (relative, method.Identifier.ValueText))
            );
        }

        Assert.True(broken.Count == 0, "Files under tests/ with syntax errors: " + string.Join(", ", broken));
        Assert.True(conditional.Count == 0, "Files under tests/ with an #if directive: " + string.Join(", ", conditional));
        Assert.True(tests.Count > 0, "No test method was found under tests/.");
        string[] unknown = [.. tests.Where(test => !InternalReferences.IsTestName(test.Name)).Select(test => $"{test.File}: {test.Name}")];
        Assert.True(
            unknown.Length == 0,
            unknown.Length + " test names the test-name pattern does not match:" + Environment.NewLine + string.Join(Environment.NewLine, unknown)
        );
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
        var broken = new List<string>();
        var conditional = new List<string>();
        var found = new List<string>();
        int files = 0;
        int tokens = 0;
        foreach ((string relative, string full) in SourceFiles("src"))
        {
            files++;
            SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(full), cancellationToken: TestContext.Current.CancellationToken);
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

    // The .cs files under a folder of the repository in ordinal order, each with its path from the repository root. Files in
    // the build's bin and obj folders are left out.
    private static IEnumerable<(string Relative, string Full)> SourceFiles(string folder)
    {
        string root = ReferencePages.RepositoryFile(folder);
        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(file => (Relative: folder + "/" + Path.GetRelativePath(root, file).Replace('\\', '/'), Full: file))
            .Where(file => !file.Relative.Split('/').Any(part => part is "bin" or "obj"));
    }

    // Whether a method is a test: it has an attribute whose name, a trailing Attribute removed, ends in Fact or Theory.
    private static bool IsTest(MethodDeclarationSyntax method) =>
        method
            .AttributeLists.SelectMany(list => list.Attributes)
            .Select(attribute => attribute.Name.ToString())
            .Select(name => name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name)
            .Any(name => name.EndsWith("Fact", StringComparison.Ordinal) || name.EndsWith("Theory", StringComparison.Ordinal));

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
