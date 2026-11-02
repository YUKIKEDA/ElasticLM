using System.Text.RegularExpressions;

namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Checks that the code of the user documentation is taken from the samples that the gate builds (design §20.7, V1c3): a code
/// block with a <c>&lt;!-- sample: path#name --&gt;</c> comment equals that region of the sample (the whole file less its snippet
/// markers without <c>#name</c>), and the C# blocks of the tutorials and the .NET API page carry such a comment.
/// </summary>
public sealed partial class GuideSamplesTests
{
    private static readonly string[] GuideFolders = ["docs/guide", "docs/guide.ja"];

    /// <summary>
    /// Every code block with a sample comment equals the sample file, or the region of it that the comment names.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The pages of docs/guide and docs/guide.ja under the repository root, and the samples they name.
    ///
    /// Steps:
    /// - Find each <c>&lt;!-- sample: &lt;path&gt;#&lt;name&gt; --&gt;</c> comment among the comment lines right above a code
    ///   block's opening fence, with no blank line between them.
    /// - Read the sample: without <c>#name</c>, the whole file less every line that is only a snippet marker
    ///   (<c>// &lt;snippet:name&gt;</c> or <c>// &lt;/snippet:name&gt;</c>, with any indentation); with <c>#name</c>, the lines
    ///   between <c>// &lt;snippet:name&gt;</c> and <c>// &lt;/snippet:name&gt;</c>.
    /// - Remove the leading and trailing blank lines and the common indentation of both, and compare them.
    ///
    /// Expected:
    /// - At least one block is compared, and every block equals its sample character for character, so a block that pulls a
    ///   whole file shows no snippet marker.
    /// - A missing sample file, a missing or repeated region, a nested region, and a sample comment that is not right above a
    ///   code block are each reported with the page and its line.
    /// </remarks>
    [Fact]
    public void SampleComment_EveryCommentedBlock_EqualsItsSample()
    {
        var problems = new List<string>();
        int compared = 0;
        foreach (string folder in GuideFolders)
        {
            foreach (string page in PagesUnder(folder))
            {
                string[] lines = ReadLines(ReferencePages.RepositoryFile(page));
                var used = new HashSet<int>();
                foreach (Block block in Blocks(lines))
                {
                    foreach ((int line, Match comment) in block.Samples)
                    {
                        used.Add(line);
                        compared++;
                        string where = $"{page}:{line + 1}";
                        string? sample = SampleText(comment.Groups["path"].Value, comment.Groups["name"].Value, where, problems);
                        if (sample is not null)
                        {
                            CompareBlock(where, Normalize(block.Lines), sample, problems);
                        }
                    }
                }

                for (int i = 0; i < lines.Length; i++)
                {
                    if (!used.Contains(i) && lines[i].Contains("<!-- sample:", StringComparison.Ordinal))
                    {
                        problems.Add(
                            $"{page}:{i + 1}: the sample comment is not right above a code block, or it is not <!-- sample: <path>#<name> -->."
                        );
                    }
                }
            }
        }

        Assert.True(compared > 0, "No code block of the user documentation has a sample comment.");
        Assert.True(
            problems.Count == 0,
            problems.Count + " code blocks differ from their samples:" + Environment.NewLine + string.Join(Environment.NewLine, problems)
        );
    }

    /// <summary>
    /// Every C# code block of a tutorial and of the .NET API page, in both languages, carries a sample comment.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The pages of docs/guide/tutorials and, once they exist, docs/guide.ja/tutorials; docs/guide/reference/dotnet-api.md and
    ///   docs/guide.ja/reference/dotnet-api.md.
    ///
    /// Steps:
    /// - Find each code block whose fence names <c>csharp</c>, <c>cs</c>, or <c>c#</c>.
    /// - Look for a <c>&lt;!-- sample: ... --&gt;</c> comment among the comment lines right above its opening fence.
    ///
    /// Expected:
    /// - At least one English tutorial page exists, both .NET API pages exist, and at least one C# block is found.
    /// - Every C# block has a sample comment; the message names the page and line of each block without one.
    /// </remarks>
    [Fact]
    public void CSharpBlock_OnTutorialOrDotnetApiPage_HasSampleComment()
    {
        string[] tutorials = [.. PagesUnder("docs/guide/tutorials"), .. PagesUnder("docs/guide.ja/tutorials")];
        string[] apiPages = ["docs/guide/reference/dotnet-api.md", "docs/guide.ja/reference/dotnet-api.md"];
        Assert.Contains(tutorials, page => page.StartsWith("docs/guide/", StringComparison.Ordinal));
        Assert.All(apiPages, page => Assert.True(File.Exists(ReferencePages.RepositoryFile(page)), page + " does not exist."));

        var problems = new List<string>();
        int found = 0;
        foreach (string page in tutorials.Concat(apiPages))
        {
            foreach (Block block in Blocks(ReadLines(ReferencePages.RepositoryFile(page))))
            {
                if (block.Language is "csharp" or "cs" or "c#")
                {
                    found++;
                    if (block.Samples.Count == 0)
                    {
                        problems.Add($"{page}:{block.Fence + 1}: the C# block has no <!-- sample: <path>#<name> --> comment above it.");
                    }
                }
            }
        }

        Assert.True(found > 0, "No C# code block was found on the tutorials or the .NET API pages.");
        Assert.True(
            problems.Count == 0,
            problems.Count + " C# blocks are not taken from a sample:" + Environment.NewLine + string.Join(Environment.NewLine, problems)
        );
    }

    private static IEnumerable<string> PagesUnder(string folder)
    {
        string path = ReferencePages.RepositoryFile(folder);
        return Directory.Exists(path)
            ? Directory
                .EnumerateFiles(path, "*.md", SearchOption.AllDirectories)
                .Select(file => folder + "/" + Path.GetRelativePath(path, file).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
            : [];
    }

    private static string[] ReadLines(string path) => File.ReadAllText(path).ReplaceLineEndings("\n").Split('\n');

    // The fenced code blocks of a page. A block's comments are the lines right above its opening fence that are each one HTML
    // comment, with no blank line between them. A sample comment may be any of them, so that a block can carry a steps comment
    // on the line right before the fence (which eng/docs/Test-GuideSteps.ps1 requires) and a sample comment above it.
    private static List<Block> Blocks(string[] lines)
    {
        var blocks = new List<Block>();
        for (int i = 0; i < lines.Length; i++)
        {
            Match open = Fence().Match(lines[i]);
            if (!open.Success)
            {
                continue;
            }

            string fence = open.Groups["fence"].Value;
            var samples = new List<(int, Match)>();
            for (int above = i - 1; above >= 0 && HtmlComment().IsMatch(lines[above]); above--)
            {
                Match sample = SampleComment().Match(lines[above]);
                if (sample.Success)
                {
                    samples.Add((above, sample));
                }
            }

            int close = i + 1;
            while (close < lines.Length && !IsClosingFence(lines[close], fence))
            {
                close++;
            }

            blocks.Add(new Block(i, open.Groups["info"].Value.ToLowerInvariant(), samples, lines[(i + 1)..Math.Min(close, lines.Length)]));
            i = close;
        }

        return blocks;
    }

    private static bool IsClosingFence(string line, string fence)
    {
        string trimmed = line.Trim();
        return trimmed.Length >= fence.Length && trimmed.All(c => c == fence[0]);
    }

    // The sample's text, normalized, or null after adding a problem. The path is relative to the repository root and must stay
    // under it.
    private static string? SampleText(string path, string name, string where, List<string> problems)
    {
        string root = Path.GetFullPath(ReferencePages.RepositoryFile(".")) + Path.DirectorySeparatorChar;
        string file = Path.GetFullPath(Path.Combine(root, path));
        if (!file.StartsWith(root, StringComparison.Ordinal) || !File.Exists(file))
        {
            problems.Add($"{where}: the sample {path} does not exist.");
            return null;
        }

        string[] lines = ReadLines(file);
        if (name.Length == 0)
        {
            return Normalize([.. lines.Where(line => !SnippetMarker().IsMatch(line))]);
        }

        int[] starts = [.. Enumerable.Range(0, lines.Length).Where(i => lines[i].Trim() == $"// <snippet:{name}>")];
        int[] ends = [.. Enumerable.Range(0, lines.Length).Where(i => lines[i].Trim() == $"// </snippet:{name}>")];
        if (starts.Length != 1 || ends.Length != 1 || ends[0] < starts[0])
        {
            problems.Add($"{where}: {path} does not have exactly one region {name}, opened before it is closed.");
            return null;
        }

        string[] region = lines[(starts[0] + 1)..ends[0]];
        if (region.Any(line => SnippetMarker().IsMatch(line)))
        {
            problems.Add($"{where}: the region {name} of {path} holds another region; regions do not nest.");
            return null;
        }

        return Normalize(region);
    }

    private static void CompareBlock(string where, string block, string sample, List<string> problems)
    {
        if (block == sample)
        {
            return;
        }

        string[] blockLines = block.Split('\n');
        string[] sampleLines = sample.Split('\n');
        int line = 0;
        while (line < blockLines.Length && line < sampleLines.Length && blockLines[line] == sampleLines[line])
        {
            line++;
        }

        string Show(string[] lines) => line < lines.Length ? "'" + lines[line] + "'" : "(the end)";
        problems.Add($"{where}: line {line + 1} of the block is {Show(blockLines)}, but the sample has {Show(sampleLines)}.");
    }

    // Removes the leading and trailing blank lines and the indentation that every other line shares, and joins the lines with \n.
    private static string Normalize(string[] lines)
    {
        int first = Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        if (first < 0)
        {
            return string.Empty;
        }

        int last = Array.FindLastIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        string[] kept = lines[first..(last + 1)];
        int indent = kept.Where(line => !string.IsNullOrWhiteSpace(line)).Min(line => line.Length - line.TrimStart(' ', '\t').Length);
        return string.Join('\n', kept.Select(line => string.IsNullOrWhiteSpace(line) ? string.Empty : line[indent..]));
    }

    [GeneratedRegex("^\\s*(?<fence>`{3,}|~{3,})\\s*(?<info>[^\\s`]*)")]
    private static partial Regex Fence();

    [GeneratedRegex("^\\s*<!--.*-->\\s*$")]
    private static partial Regex HtmlComment();

    [GeneratedRegex("^\\s*<!-- sample: (?<path>[^#\\s]+)(#(?<name>[A-Za-z0-9_-]+))? -->\\s*$")]
    private static partial Regex SampleComment();

    // A line that is only a snippet marker, with any indentation.
    [GeneratedRegex("^\\s*// </?snippet:[A-Za-z0-9_-]+>\\s*$")]
    private static partial Regex SnippetMarker();

    private sealed record Block(int Fence, string Language, IReadOnlyList<(int Line, Match Comment)> Samples, string[] Lines);
}
