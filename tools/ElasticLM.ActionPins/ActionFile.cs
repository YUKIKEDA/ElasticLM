using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace ElasticLM.ActionPins;

/// <summary>
/// Reads one workflow file or action definition and writes the lines that eng/ci/Test-ActionPins.ps1 checks.
/// </summary>
/// <remarks>
/// Lines, tab separated:
/// <c>pin name line repo folder sha version</c> for each uses value of the form the check reads;
/// <c>runs name using</c> for an action definition;
/// <c>error name line message</c> for each refusal.
/// </remarks>
internal static class ActionFile
{
    // The keys whose place the check relies on. In the mappings the tool reads, a key that matches one of them in another
    // case is refused, so that a key the tool does not read can never be one GitHub reads.
    private static readonly string[] Watched = ["jobs", "steps", "uses", "with", "persist-credentials", "runs", "using", "image"];

    /// <summary>
    /// Reads the file at <paramref name="path"/>.
    /// </summary>
    /// <param name="isAction">Whether the file is an action definition rather than a workflow.</param>
    /// <param name="path">The file, which is also the name in the lines.</param>
    /// <param name="output">The lines are appended here.</param>
    public static void Read(bool isAction, string path, StringBuilder output)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error(output, path, 0, "the file cannot be read");
            return;
        }

        ReadBytes(isAction, path, bytes, output);
    }

    /// <summary>
    /// Reads the bytes of a file.
    /// </summary>
    /// <param name="isAction">Whether the file is an action definition rather than a workflow.</param>
    /// <param name="name">The name in the lines.</param>
    /// <param name="bytes">The file's bytes.</param>
    /// <param name="output">The lines are appended here.</param>
    public static void ReadBytes(bool isAction, string name, ReadOnlySpan<byte> bytes, StringBuilder output)
    {
        // Only UTF-8: the decoder detects no byte order mark, so a UTF-16 or UTF-32 file is refused as not UTF-8. A UTF-8
        // byte order mark decodes to U+FEFF at the start of the first line, which the parser skips (the BOM case of
        // ActionFileTests passes with it), and no uses value stands on that line.
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Error(output, name, 0, "the file is not UTF-8");
            return;
        }

        // A YAML parser takes U+0085, U+2028, U+2029, and a lone CR as line breaks in some places and not in others, and
        // the lines below are split at LF only; a file that holds one is refused before it is parsed.
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
                Error(output, name, LineOf(text, i), "the file holds U+0085, U+2028, U+2029, or a carriage return without a line feed");
                return;
            }
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (!CheckEvents(text, name, output))
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
            Error(output, name, (int)e.Start.Line, "the file is not valid YAML: " + Clean(e.Message));
            return;
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            Error(output, name, 0, "the file is not one YAML document whose root is a mapping");
            return;
        }

        var reader = new Reader(name, lines, output);
        if (isAction)
        {
            reader.ReadAction(root);
        }
        else
        {
            reader.ReadWorkflow(root);
        }

        reader.CheckKeys(root, strayUses: !isAction);
    }

    // Anchors, aliases, tags, and more than one document change what a node means away from where it is written; none of
    // the workflows uses them, so a file that does is refused.
    private static bool CheckEvents(string text, string name, StringBuilder output)
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
                        Error(output, name, line, "the file uses an alias (*name)");
                        return false;
                    case NodeEvent node when !node.Anchor.IsEmpty:
                        Error(output, name, line, "the file uses an anchor (&name)");
                        return false;
                    case NodeEvent node when !node.Tag.IsEmpty:
                        Error(output, name, line, "the file uses a tag (!name)");
                        return false;
                    case DocumentStart:
                        documents++;
                        break;
                }
            }
        }
        catch (YamlException e)
        {
            Error(output, name, (int)e.Start.Line, "the file is not valid YAML: " + Clean(e.Message));
            return false;
        }

        if (documents != 1)
        {
            Error(output, name, 0, "the file does not hold exactly one YAML document");
            return false;
        }

        return true;
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static string Clean(string value) => value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static void Error(StringBuilder output, string name, int line, string message) =>
        output.Append("error\t").Append(name).Append('\t').Append(line).Append('\t').Append(Clean(message)).Append('\n');

    private sealed class Reader(string name, string[] lines, StringBuilder output)
    {
        private readonly HashSet<YamlNode> _expected = new(ReferenceEqualityComparer.Instance);

        public void ReadWorkflow(YamlMappingNode root)
        {
            CheckCase(root);
            YamlNode? jobs = Get(root, "jobs");
            if (jobs is null)
            {
                return;
            }

            if (jobs is not YamlMappingNode jobMap)
            {
                Error(output, name, Line(jobs), "jobs is not a mapping");
                return;
            }

            CheckCase(jobMap);
            foreach (KeyValuePair<YamlNode, YamlNode> job in jobMap.Children)
            {
                if (job.Value is not YamlMappingNode jobBody)
                {
                    Error(output, name, Line(job.Value), "a job is not a mapping");
                    continue;
                }

                CheckCase(jobBody);
                if (Key(jobBody, "uses") is YamlNode jobUsesKey)
                {
                    _expected.Add(jobUsesKey);
                    Error(
                        output,
                        name,
                        Line(jobUsesKey),
                        "the job calls a reusable workflow, which this check does not cover; change design §20.5 and this check first."
                    );
                }

                ReadSteps(Get(jobBody, "steps"));
            }
        }

        public void ReadAction(YamlMappingNode root)
        {
            CheckCase(root);
            if (Get(root, "runs") is not YamlMappingNode runs)
            {
                Error(output, name, 0, "the action has no runs mapping");
                return;
            }

            CheckCase(runs);
            string usingValue = Get(runs, "using") is YamlScalarNode u ? u.Value ?? string.Empty : string.Empty;
            string image = Get(runs, "image") is YamlScalarNode i ? i.Value ?? string.Empty : string.Empty;
            int before = _expected.Count;
            ReadSteps(Get(runs, "steps"));
            if (Pin.CheckRuns(usingValue, image, _expected.Count > before) is string refusal)
            {
                Error(output, name, Line(runs), refusal);
            }

            output.Append("runs\t").Append(name).Append('\t').Append(Clean(usingValue)).Append('\n');
        }

        // Refuses a merge key anywhere and, in a workflow, a uses key outside jobs.<id> and jobs.<id>.steps.
        public void CheckKeys(YamlNode node, bool strayUses)
        {
            switch (node)
            {
                case YamlMappingNode map:
                    foreach (KeyValuePair<YamlNode, YamlNode> entry in map.Children)
                    {
                        if (entry.Key is not YamlScalarNode key)
                        {
                            Error(output, name, Line(entry.Key), "a key is not a scalar");
                            continue;
                        }

                        if (key.Value == "<<")
                        {
                            Error(output, name, Line(key), "the file uses a merge key (<<)");
                        }
                        else if (strayUses && key.Value == "uses" && !_expected.Contains(key))
                        {
                            Error(output, name, Line(key), "a uses key stands outside jobs.<id> and jobs.<id>.steps");
                        }

                        CheckKeys(entry.Value, strayUses);
                    }

                    break;
                case YamlSequenceNode sequence:
                    foreach (YamlNode child in sequence.Children)
                    {
                        CheckKeys(child, strayUses);
                    }

                    break;
            }
        }

        private static YamlNode? Key(YamlMappingNode map, string key) =>
            map.Children.Keys.FirstOrDefault(k => k is YamlScalarNode s && s.Value == key);

        private static YamlNode? Get(YamlMappingNode map, string key) => Key(map, key) is YamlNode found ? map[found] : null;

        private static int Line(YamlNode node) => (int)node.Start.Line;

        private void ReadSteps(YamlNode? steps)
        {
            if (steps is null)
            {
                return;
            }

            if (steps is not YamlSequenceNode sequence)
            {
                Error(output, name, Line(steps), "steps is not a sequence");
                return;
            }

            foreach (YamlNode step in sequence.Children)
            {
                if (step is not YamlMappingNode stepMap)
                {
                    Error(output, name, Line(step), "a step is not a mapping");
                    continue;
                }

                CheckCase(stepMap);
                YamlMappingNode? with = Get(stepMap, "with") as YamlMappingNode;
                if (with is not null)
                {
                    CheckCase(with);
                }

                if (Key(stepMap, "uses") is YamlNode usesKey)
                {
                    _expected.Add(usesKey);
                    ReadUses(usesKey, stepMap[usesKey], with);
                }
            }
        }

        private void ReadUses(YamlNode usesKey, YamlNode usesNode, YamlMappingNode? with)
        {
            int line = Line(usesKey);
            if (usesNode is not YamlScalarNode scalar || scalar.Value is null)
            {
                Error(output, name, line, "the value of uses is not a scalar");
                return;
            }

            if (scalar.Start.Line != scalar.End.Line || scalar.Style is ScalarStyle.Literal or ScalarStyle.Folded)
            {
                Error(output, name, line, "the value of uses is not written on one line");
                return;
            }

            if (scalar.Value.Any(char.IsControl))
            {
                Error(output, name, line, "the value of uses holds a control character");
                return;
            }

            // Comments are not part of the YAML model: the version comment is read from the rest of the line the value
            // ends on, which is the line it starts on.
            int endLine = (int)scalar.End.Line;
            int endColumn = (int)scalar.End.Column;
            string source = endLine >= 1 && endLine <= lines.Length ? lines[endLine - 1] : string.Empty;
            string rest = endColumn - 1 <= source.Length ? source[(endColumn - 1)..] : string.Empty;
            if (Pin.Check(scalar.Value, rest, out Pinned pinned) is string refusal)
            {
                Error(output, name, line, refusal);
                return;
            }

            bool persistFalse = with is not null && Get(with, "persist-credentials") is YamlScalarNode flag && flag.Value == "false";
            if (Pin.IsCheckout(pinned.Repo) && !persistFalse)
            {
                Error(output, name, line, "actions/checkout does not set with.persist-credentials to false.");
            }

            output
                .Append("pin\t")
                .Append(name)
                .Append('\t')
                .Append(line)
                .Append('\t')
                .Append(pinned.Repo)
                .Append('\t')
                .Append(pinned.Folder)
                .Append('\t')
                .Append(pinned.Sha)
                .Append('\t')
                .Append(pinned.Version)
                .Append('\n');
        }

        // Refuses, in a mapping the tool reads, a key that differs from a watched key only in case.
        private void CheckCase(YamlMappingNode map)
        {
            foreach (YamlNode keyNode in map.Children.Keys)
            {
                if (keyNode is not YamlScalarNode key || key.Value is not string value)
                {
                    continue;
                }

                if (Array.Exists(Watched, w => string.Equals(w, value, StringComparison.OrdinalIgnoreCase) && w != value))
                {
                    Error(output, name, Line(key), $"the key {Clean(value)} differs in case from a key the check reads");
                }
            }
        }
    }
}
