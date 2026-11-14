using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace ElasticLM.ActionPins;

/// <summary>
/// Reads one workflow file or action definition and writes the lines that eng/ci/Test-ActionPins.ps1 checks.
/// </summary>
internal static class ActionFile
{
    // The keys whose place the check relies on. A key that matches one of them in another case is refused, so that a key
    // the check does not read can never be one GitHub reads.
    private static readonly string[] Watched = ["jobs", "steps", "uses", "with", "persist-credentials", "runs", "using", "image"];

    public static void Read(bool isAction, string path, StringBuilder output)
    {
        string text;
        try
        {
            text = File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        }
        catch (Exception e) when (e is IOException or DecoderFallbackException or UnauthorizedAccessException)
        {
            Error(output, path, 0, "the file cannot be read as UTF-8");
            return;
        }

        // A YAML parser takes U+0085, U+2028, U+2029, and a lone CR as line breaks in some places and not in others, and
        // a text reader may not take them at all; a file that holds one is refused before it is parsed.
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (
                c == (char)0x0085
                || c == (char)0x2028
                || c == (char)0x2029
                || (c == (char)0x0D && (i + 1 == text.Length || text[i + 1] != (char)0x0A))
            )
            {
                Error(output, path, LineOf(text, i), "the file holds U+0085, U+2028, U+2029, or a carriage return without a line feed");
                return;
            }
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (!CheckEvents(text, path, output))
        {
            return;
        }

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException e)
        {
            Error(output, path, (int)e.Start.Line, "the file is not valid YAML: " + OneLine(e.Message));
            return;
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            Error(output, path, 0, "the file is not one YAML document whose root is a mapping");
            return;
        }

        var expected = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
        if (isAction)
        {
            ReadAction(root, path, lines, output, expected);
        }
        else
        {
            ReadWorkflow(root, path, lines, output, expected);
        }

        CheckKeys(root, path, output, expected);
    }

    // Anchors, aliases, tags, and more than one document change what a node means away from where it is written; none of
    // the workflows uses them, so a file that does is refused.
    private static bool CheckEvents(string text, string path, StringBuilder output)
    {
        var parser = new Parser(new StringReader(text));
        int documents = 0;
        try
        {
            while (parser.MoveNext())
            {
                ParsingEvent current = parser.Current!;
                int line = (int)current.Start.Line;
                switch (current)
                {
                    case AnchorAlias:
                        Error(output, path, line, "the file uses an alias (*name)");
                        return false;
                    case NodeEvent node when !node.Anchor.IsEmpty:
                        Error(output, path, line, "the file uses an anchor (&name)");
                        return false;
                    case NodeEvent node when !node.Tag.IsEmpty:
                        Error(output, path, line, "the file uses a tag (!name)");
                        return false;
                    case DocumentStart:
                        documents++;
                        break;
                }
            }
        }
        catch (YamlException e)
        {
            Error(output, path, (int)e.Start.Line, "the file is not valid YAML: " + OneLine(e.Message));
            return false;
        }

        if (documents != 1)
        {
            Error(output, path, 0, "the file does not hold exactly one YAML document");
            return false;
        }

        return true;
    }

    private static void ReadWorkflow(YamlMappingNode root, string path, string[] lines, StringBuilder output, HashSet<YamlNode> expected)
    {
        YamlNode? jobs = Get(root, "jobs");
        if (jobs is null)
        {
            return;
        }

        if (jobs is not YamlMappingNode jobMap)
        {
            Error(output, path, Line(jobs), "jobs is not a mapping");
            return;
        }

        foreach (KeyValuePair<YamlNode, YamlNode> job in jobMap.Children)
        {
            if (job.Value is not YamlMappingNode jobBody)
            {
                Error(output, path, Line(job.Value), "a job is not a mapping");
                continue;
            }

            if (Key(jobBody, "uses") is YamlNode jobUsesKey)
            {
                expected.Add(jobUsesKey);
                if (Value(jobBody[jobUsesKey], path, output) is string value)
                {
                    output.Append("job-uses\t").Append(path).Append('\t').Append(Line(jobUsesKey)).Append('\t').Append(value).Append('\n');
                }
            }

            ReadSteps(Get(jobBody, "steps"), path, lines, output, expected);
        }
    }

    private static void ReadAction(YamlMappingNode root, string path, string[] lines, StringBuilder output, HashSet<YamlNode> expected)
    {
        if (Get(root, "runs") is not YamlMappingNode runs)
        {
            Error(output, path, 0, "the action has no runs mapping");
            return;
        }

        string usingValue = Get(runs, "using") is YamlScalarNode u ? u.Value ?? string.Empty : string.Empty;
        string image = Get(runs, "image") is YamlScalarNode i ? i.Value ?? string.Empty : string.Empty;
        output.Append("runs\t").Append(path).Append('\t').Append(Clean(usingValue)).Append('\t').Append(Clean(image)).Append('\n');
        ReadSteps(Get(runs, "steps"), path, lines, output, expected);
    }

    private static void ReadSteps(YamlNode? steps, string path, string[] lines, StringBuilder output, HashSet<YamlNode> expected)
    {
        if (steps is null)
        {
            return;
        }

        if (steps is not YamlSequenceNode sequence)
        {
            Error(output, path, Line(steps), "steps is not a sequence");
            return;
        }

        foreach (YamlNode step in sequence.Children)
        {
            if (step is not YamlMappingNode stepMap)
            {
                Error(output, path, Line(step), "a step is not a mapping");
                continue;
            }

            if (Key(stepMap, "uses") is not YamlNode usesKey)
            {
                continue;
            }

            expected.Add(usesKey);
            YamlNode usesNode = stepMap[usesKey];
            if (Value(usesNode, path, output) is not string value)
            {
                continue;
            }

            // Comments are not part of the YAML model: the version comment is read from the rest of the line the value
            // ends on. Value() has made sure that the value is on one line.
            int endLine = (int)usesNode.End.Line;
            int endColumn = (int)usesNode.End.Column;
            string source = endLine >= 1 && endLine <= lines.Length ? lines[endLine - 1] : string.Empty;
            string rest = endColumn - 1 <= source.Length ? source[(endColumn - 1)..] : string.Empty;
            string persist =
                Get(stepMap, "with") is YamlMappingNode with && Get(with, "persist-credentials") is YamlScalarNode flag && flag.Value == "false"
                    ? "false"
                    : "other";
            output
                .Append("uses\t")
                .Append(path)
                .Append('\t')
                .Append(Line(usesKey))
                .Append('\t')
                .Append(value)
                .Append('\t')
                .Append(Clean(rest))
                .Append('\t')
                .Append(persist)
                .Append('\n');
        }
    }

    // Refuses a key outside the places the check reads that could still carry a pin or its options: every uses key, and
    // any watched key written in another case, anywhere in the file.
    private static void CheckKeys(YamlNode node, string path, StringBuilder output, HashSet<YamlNode> expected)
    {
        switch (node)
        {
            case YamlMappingNode map:
                foreach (KeyValuePair<YamlNode, YamlNode> entry in map.Children)
                {
                    if (entry.Key is not YamlScalarNode key)
                    {
                        Error(output, path, Line(entry.Key), "a key is not a scalar");
                        continue;
                    }

                    string name = key.Value ?? string.Empty;
                    if (name == "<<")
                    {
                        Error(output, path, Line(key), "the file uses a merge key (<<)");
                    }
                    else if (name == "uses" && !expected.Contains(key))
                    {
                        Error(output, path, Line(key), "a uses key stands outside jobs.<id>, jobs.<id>.steps, and runs.steps");
                    }
                    else if (name != "uses" && Array.Exists(Watched, w => string.Equals(w, name, StringComparison.OrdinalIgnoreCase) && w != name))
                    {
                        Error(output, path, Line(key), $"the key {Clean(name)} differs in case from a key the check reads");
                    }

                    CheckKeys(entry.Value, path, output, expected);
                }

                break;
            case YamlSequenceNode sequence:
                foreach (YamlNode child in sequence.Children)
                {
                    CheckKeys(child, path, output, expected);
                }

                break;
        }
    }

    private static YamlNode? Key(YamlMappingNode map, string name) => map.Children.Keys.FirstOrDefault(k => k is YamlScalarNode s && s.Value == name);

    private static YamlNode? Get(YamlMappingNode map, string name) => Key(map, name) is YamlNode key ? map[key] : null;

    private static string? Value(YamlNode node, string path, StringBuilder output)
    {
        if (node is not YamlScalarNode scalar || scalar.Value is null)
        {
            Error(output, path, Line(node), "the value of uses is not a scalar");
            return null;
        }

        if (scalar.Start.Line != scalar.End.Line || scalar.Style is ScalarStyle.Literal or ScalarStyle.Folded)
        {
            Error(output, path, Line(node), "the value of uses is not written on one line");
            return null;
        }

        if (scalar.Value.Any(char.IsControl))
        {
            Error(output, path, Line(node), "the value of uses holds a control character");
            return null;
        }

        return scalar.Value;
    }

    private static int Line(YamlNode node) => (int)node.Start.Line;

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static string Clean(string value) => value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static string OneLine(string value) => Clean(value);

    private static void Error(StringBuilder output, string path, int line, string message) =>
        output.Append("error\t").Append(path).Append('\t').Append(line).Append('\t').Append(message).Append('\n');
}
