using System.Text;

namespace ElasticLM.ActionPins.Tests;

/// <summary>
/// <see cref="ActionFile"/> reads workflows and action definitions as YAML (design §20.5, V1d22): it finds every uses value
/// the runner reads, and refuses what it does not read, without the network.
/// </summary>
public sealed class ActionFileTests
{
    private const string Sha = "3d3c42e5aac5ba805825da76410c181273ba90b1";
    private const string Fork = "44c2bc1922b103805732b83abe0aac1658d22d96";
    private const string Head = "on: push\njobs:\n  j:\n    runs-on: ubuntu-latest\n    steps:\n";
    private const string Good = "      - uses: actions/checkout@" + Sha + " # v7.0.1\n        with:\n          persist-credentials: false\n";
    private const string HiddenPin = "actions/checkout@" + Fork + " # v7.0.1";

    /// <summary>Gets the workflows that are refused, with the line and the message of the refusal.</summary>
    public static TheoryData<string, string, int, string> Refused =>
        new()
        {
            { "anchor", Head + "      - &c\n        uses: actions/checkout@" + Sha + " # v7.0.1\n", 6, "the file uses an anchor (&name)" },
            { "alias", Head + Good + "      - *c\n", 9, "the file uses an alias (*name)" },
            { "tag", Head + "      - uses: !!str actions/checkout@" + Sha + " # v7.0.1\n", 6, "the file uses a tag (!name)" },
            { "merge key", Head + Good + "      - <<: {uses: x}\n", 9, "the file uses a merge key (<<)" },
            { "duplicate key", Head + Good + "        uses: actions/checkout@" + Fork + "\n", 9, "the file is not valid YAML: Duplicate key uses" },
            {
                "uses under env",
                Head + Good + "      - run: echo\n        env:\n          uses: " + HiddenPin + "\n",
                11,
                "a uses key stands outside jobs.<id> and jobs.<id>.steps"
            },
            {
                "With in another case",
                Head + Good + "        With:\n          persist-credentials: true\n",
                9,
                "the key With differs in case from a key the check reads"
            },
            {
                "Uses in another case",
                Head + Good + "      - Uses: " + HiddenPin + "\n",
                9,
                "the key Uses differs in case from a key the check reads"
            },
            { "Steps in another case", "on: push\njobs:\n  j:\n    Steps:\n" + Good, 4, "the key Steps differs in case from a key the check reads" },
            {
                "two-line value",
                Head + "      - uses: \"actions/checkout@3d3c42e5aac5ba80\\\n          5825da76410c181273ba90b1\" # v7.0.1\n",
                6,
                "the value of uses is not written on one line"
            },
            { "block value", Head + Good + "      - uses: |\n          " + HiddenPin + "\n", 9, "the value of uses is not written on one line" },
            { "value not a scalar", Head + Good + "      - uses: [a]\n", 9, "the value of uses is not a scalar" },
            { "control character", Head + Good + "      - uses: \"a\\x01b\"\n", 9, "the value of uses holds a control character" },
            {
                "U+2028",
                Head + Good + "      - run: echo # x" + (char)0x2028 + "      - uses: " + HiddenPin + "\n",
                9,
                "the file holds U+0085, U+2028, U+2029, or a carriage return without a line feed"
            },
            {
                "U+2029",
                Head + Good + "      # x" + (char)0x2029 + "\n",
                9,
                "the file holds U+0085, U+2028, U+2029, or a carriage return without a line feed"
            },
            {
                "U+0085",
                Head + Good + "      # x" + (char)0x0085 + "      - uses: " + HiddenPin + "\n",
                9,
                "the file holds U+0085, U+2028, U+2029, or a carriage return without a line feed"
            },
            {
                "lone CR",
                Head + Good + "      - run: echo\r      - uses: " + HiddenPin + "\n",
                9,
                "the file holds U+0085, U+2028, U+2029, or a carriage return without a line feed"
            },
            { "two documents", "on: push\n---\njobs: {}\n", 0, "the file does not hold exactly one YAML document" },
            { "root not a mapping", "- a\n", 0, "the file is not one YAML document whose root is a mapping" },
            { "jobs not a mapping", "on: push\njobs: [a]\n", 2, "jobs is not a mapping" },
            { "steps not a sequence", "on: push\njobs:\n  j:\n    steps: a\n", 4, "steps is not a sequence" },
            {
                "B4 nested sequence",
                Head + Good + "      - - name: |\n            x\n          uses: " + HiddenPin + "\n",
                9,
                "a step is not a mapping"
            },
            {
                "reusable workflow",
                "on: push\njobs:\n  j:\n    uses: owner/repo/.github/workflows/x.yml@" + Sha + "\n",
                4,
                "the job calls a reusable workflow, which this check does not cover; change design §20.5 and this check first."
            },
            {
                "checkout without persist-credentials",
                Head + "      - uses: actions/checkout@" + Sha + " # v7.0.1\n",
                6,
                "actions/checkout does not set with.persist-credentials to false."
            },
            {
                "persist-credentials true",
                Head + "      - uses: actions/checkout@" + Sha + " # v7.0.1\n        with:\n          persist-credentials: true\n",
                6,
                "actions/checkout does not set with.persist-credentials to false."
            },
            {
                "persist-credentials False",
                Head + "      - uses: actions/checkout@" + Sha + " # v7.0.1\n        with:\n          persist-credentials: False\n",
                6,
                "actions/checkout does not set with.persist-credentials to false."
            },
            {
                "persist-credentials under env",
                Head + "      - uses: actions/checkout@" + Sha + " # v7.0.1\n        env:\n          persist-credentials: false\n",
                6,
                "actions/checkout does not set with.persist-credentials to false."
            },
        };

    /// <summary>Gets the workflows whose hidden uses value the parser reads (the forms that hid it from a text reader).</summary>
    public static TheoryData<string, string, int> Hidden =>
        new()
        {
            { "double-quoted key", Head + Good + "      - \"uses\": " + HiddenPin + "\n", 9 },
            { "single-quoted key", Head + Good + "      - 'uses': " + HiddenPin + "\n", 9 },
            { "escaped key", Head + Good + "      - \"u\\x73es\": " + HiddenPin + "\n", 9 },
            { "explicit key", Head + Good + "      - ? uses\n        : " + HiddenPin + "\n", 9 },
            { "B1 dash spaces", Head + Good + "      -   name: |\n            x\n          uses: " + HiddenPin + "\n", 11 },
            { "B1 folded if", Head + Good + "      -   if: >-\n            true\n          uses: " + HiddenPin + "\n", 11 },
            { "B1 empty block", Head + Good + "      -   name: |\n          uses: " + HiddenPin + "\n", 10 },
            { "B3 multi-line quoted", Head + Good + "      - name: 'a\n          x: |'\n        uses: " + HiddenPin + "\n", 11 },
        };

    /// <summary>
    /// A file the tool does not read in full is refused with the line and the reason.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Workflows that each hold one form: an anchor, an alias, a tag, a merge key, a duplicate key, a uses key under env,
    ///   a watched key in another case (With, Uses, Steps), a uses value on two lines, as a block, as a sequence, or with
    ///   a control character, U+2028, U+2029, U+0085, a lone CR, two documents, a root that is not a mapping, jobs that
    ///   are not a mapping, steps that are not a sequence, a nested sequence as a step (B4), a reusable workflow, and a
    ///   checkout without persist-credentials, with true, with False, or with it under env.
    ///
    /// Steps:
    /// - Read each as a workflow.
    ///
    /// Expected:
    /// - An error line with the written line number and message.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Refused))]
    public void ReadBytes_FormNotRead_IsRefused(string form, string text, int line, string message)
    {
        string[][] records = Read(isAction: false, text);

        Assert.Contains(["error", "w.yml", line.ToString(System.Globalization.CultureInfo.InvariantCulture), message], records);
        Assert.NotEmpty(form);
    }

    /// <summary>
    /// A uses value that a text reader missed is read by the parser, so its pin is checked.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Workflows with a valid checkout and a second step whose uses is a fork-only commit, written as a quoted key, an
    ///   escaped key, an explicit key, after a dash with three spaces and a block or folded scalar (B1, also with an
    ///   empty block), and after a multi-line single-quoted scalar that holds "x: |" (B3).
    ///
    /// Steps:
    /// - Read each as a workflow.
    ///
    /// Expected:
    /// - A pin line for the hidden value at its line, with the fork-only commit, and the checkout's persist-credentials
    ///   refusal for it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Hidden))]
    public void ReadBytes_HiddenUses_IsRead(string form, string text, int line)
    {
        string[][] records = Read(isAction: false, text);
        string at = line.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Contains(["pin", "w.yml", at, "actions/checkout", string.Empty, Fork, "v7.0.1"], records);
        Assert.Contains(["error", "w.yml", at, "actions/checkout does not set with.persist-credentials to false."], records);
        Assert.NotEmpty(form);
    }

    /// <summary>
    /// A workflow in a form the tool reads passes with its pins.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The same workflow with LF, with CRLF, with a UTF-8 byte order mark, with the value in single and in double
    ///   quotes, with persist-credentials as 'false', with flow collections outside the steps, and with uses text inside a
    ///   run block.
    ///
    /// Steps:
    /// - Read each as a workflow.
    ///
    /// Expected:
    /// - Exactly one line: the pin of the checkout at line 6 (line 7 for the flow form, whose on takes a line).
    /// </remarks>
    [Theory]
    [InlineData("lf", false)]
    [InlineData("crlf", false)]
    [InlineData("bom", true)]
    [InlineData("single quotes", false)]
    [InlineData("double quotes", false)]
    [InlineData("quoted false", false)]
    [InlineData("flow", false)]
    [InlineData("run block", false)]
    public void ReadBytes_FormRead_GivesPin(string form, bool bom)
    {
        string text = form switch
        {
            "crlf" => (Head + Good).Replace("\n", "\r\n", StringComparison.Ordinal),
            "single quotes" => Head + Good.Replace("actions/checkout@" + Sha, "'actions/checkout@" + Sha + "'", StringComparison.Ordinal),
            "double quotes" => Head + Good.Replace("actions/checkout@" + Sha, "\"actions/checkout@" + Sha + "\"", StringComparison.Ordinal),
            "quoted false" => Head + Good.Replace(": false", ": 'false'", StringComparison.Ordinal),
            "flow" => "on: {push: {branches: [main]}}\nenv: {A: \"x\\ty\"}\njobs:\n  j:\n    runs-on: [ubuntu-latest]\n    steps:\n" + Good,
            "run block" => Head + Good + "      - run: |\n          echo \"uses: evil/x@main\"\n          - { uses: evil/x@main }\n",
            _ => Head + Good,
        };
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (bom)
        {
            bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        }

        var output = new StringBuilder();
        ActionFile.ReadBytes(isAction: false, "w.yml", bytes, output);

        string expected = form == "flow" ? "7" : "6";
        Assert.Equal($"pin\tw.yml\t{expected}\tactions/checkout\t\t{Sha}\tv7.0.1\n", output.ToString());
    }

    /// <summary>
    /// A file that is not UTF-8 is refused, including UTF-16 and UTF-32 with their byte order marks.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The passing workflow encoded as UTF-16 LE with a byte order mark, UTF-32 LE with one, and UTF-8 with a stray
    ///   0xFF byte.
    ///
    /// Steps:
    /// - Read each as a workflow.
    ///
    /// Expected:
    /// - Exactly the line that the file is not UTF-8.
    /// </remarks>
    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-32")]
    [InlineData("stray byte")]
    public void ReadBytes_NotUtf8_IsRefused(string encoding)
    {
        byte[] bytes = encoding switch
        {
            "utf-16" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Head + Good)],
            "utf-32" => [.. Encoding.UTF32.GetPreamble(), .. Encoding.UTF32.GetBytes(Head + Good)],
            _ => [.. Encoding.UTF8.GetBytes(Head + Good), 0xFF],
        };

        var output = new StringBuilder();
        ActionFile.ReadBytes(isAction: false, "w.yml", bytes, output);

        Assert.Equal("error\tw.yml\t0\tthe file is not UTF-8\n", output.ToString());
    }

    /// <summary>
    /// An action definition gives its runs line and the pins of a composite's steps, and its own inputs do not count.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A composite action whose inputs are named uses and Image and whose second step uses a pinned action.
    ///
    /// Steps:
    /// - Read it as an action.
    ///
    /// Expected:
    /// - The pin of the step and the runs line with composite, and no error.
    /// </remarks>
    [Fact]
    public void ReadBytes_CompositeAction_GivesPinsAndRuns()
    {
        string text =
            "inputs:\n  uses:\n    default: x\n  Image:\n    default: y\nruns:\n  using: composite\n  steps:\n"
            + "    - run: echo\n      shell: bash\n    - uses: actions/upload-artifact@"
            + Sha
            + " # v7.0.0\n";

        string[][] records = Read(isAction: true, text);

        Assert.Equal(
            [
                ["pin", "w.yml", "11", "actions/upload-artifact", string.Empty, Sha, "v7.0.0"],
                ["runs", "w.yml", "composite"],
            ],
            records
        );
    }

    /// <summary>
    /// An action definition whose runs mapping breaks a rule is refused.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A docker action with an image by tag in an uppercase prefix, a node action with a uses step, and an action with
    ///   no runs mapping.
    ///
    /// Steps:
    /// - Read each as an action.
    ///
    /// Expected:
    /// - The error line for each.
    /// </remarks>
    [Theory]
    [InlineData(
        "runs:\n  using: docker\n  image: DOCKER://alpine:latest\n",
        "error\tw.yml\t2\tthe action runs the image DOCKER://alpine:latest, which is not pinned by a sha256 digest."
    )]
    [InlineData(
        "runs:\n  using: node24\n  steps:\n    - uses: actions/cache@" + Sha + " # v6.1.0\n",
        "error\tw.yml\t2\tthe action has steps with uses, but runs.using is node24, not composite."
    )]
    [InlineData("name: x\n", "error\tw.yml\t0\tthe action has no runs mapping")]
    public void ReadBytes_ActionRunsNotAllowed_IsRefused(string text, string line)
    {
        var output = new StringBuilder();
        ActionFile.ReadBytes(isAction: true, "w.yml", Encoding.UTF8.GetBytes(text), output);

        Assert.Contains(line + "\n", output.ToString(), StringComparison.Ordinal);
    }

    private static string[][] Read(bool isAction, string text)
    {
        var output = new StringBuilder();
        ActionFile.ReadBytes(isAction, "w.yml", Encoding.UTF8.GetBytes(text), output);
        return [.. output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t'))];
    }
}
