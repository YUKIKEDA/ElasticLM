namespace ElasticLM.ActionPins.Tests;

/// <summary>
/// The rules of <see cref="Pin"/> that need no network (design §20.5, V1d22): the form of a uses value and its version
/// comment, which repositories count as actions/checkout, and the runs mapping of an action definition.
/// </summary>
public sealed class PinTests
{
    private const string Sha = "3d3c42e5aac5ba805825da76410c181273ba90b1";

    /// <summary>
    /// A value pinned to a full commit with a version comment passes and gives its parts.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Values with and without a folder, and comments with one or more spaces or tabs around the hash.
    ///
    /// Steps:
    /// - Check each value with its rest of the line.
    ///
    /// Expected:
    /// - No refusal, and the repository, the folder (with its leading slash, or empty), the commit, and the version
    ///   are the parts written.
    /// </remarks>
    [Theory]
    [InlineData("actions/checkout@" + Sha, " # v7.0.1", "actions/checkout", "", "v7.0.1")]
    [InlineData("actions/checkout@" + Sha, "\t#v7.0.1  ", "actions/checkout", "", "v7.0.1")]
    [InlineData("owner/repo/sub/dir@" + Sha, "  #  v10.20.30", "owner/repo", "/sub/dir", "v10.20.30")]
    [InlineData("owner/repo/.github@" + Sha, " # v1.0.0", "owner/repo", "/.github", "v1.0.0")]
    public void Check_PinnedValue_GivesParts(string value, string rest, string repo, string folder, string version)
    {
        string? refusal = Pin.Check(value, rest, out Pinned pinned);

        Assert.Null(refusal);
        Assert.Equal(new Pinned(repo, folder, Sha, version), pinned);
    }

    /// <summary>
    /// A value that is not a full commit pin of an action is refused with the reason for its form.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Each value breaks one rule and has a valid comment: a tag, a short or uppercase SHA, an empty, ".", or ".." path
    ///   segment, a trailing slash, a local action, a docker image in either case, and a reusable workflow.
    ///
    /// Steps:
    /// - Check each value.
    ///
    /// Expected:
    /// - The message that names the value and the rule.
    /// </remarks>
    [Theory]
    [InlineData("actions/checkout@v7", "is not pinned to a full commit SHA (40 lowercase hex digits).")]
    [InlineData("actions/checkout@3d3c42e", "is not pinned to a full commit SHA (40 lowercase hex digits).")]
    [InlineData("actions/checkout@3D3C42E5AAC5BA805825DA76410C181273BA90B1", "is not pinned to a full commit SHA (40 lowercase hex digits).")]
    [InlineData("actions/checkout/.@" + Sha, "is not pinned to a full commit SHA (40 lowercase hex digits).")]
    [InlineData("actions/checkout/x/..@" + Sha, "is not pinned to a full commit SHA (40 lowercase hex digits).")]
    [InlineData("actions/checkout//x@" + Sha, "is not pinned to a full commit SHA (40 lowercase hex digits).")]
    [InlineData("actions/checkout/@" + Sha, "is not pinned to a full commit SHA (40 lowercase hex digits).")]
    [InlineData("actions/checkout@" + Sha + "x", "is not pinned to a full commit SHA (40 lowercase hex digits).")]
    [InlineData(
        "./.github/actions/x",
        "is a local action or a docker:// image, which this check does not cover; change design §20.5 and this check first."
    )]
    [InlineData(
        "docker://alpine:3.20",
        "is a local action or a docker:// image, which this check does not cover; change design §20.5 and this check first."
    )]
    [InlineData(
        "DOCKER://alpine:latest",
        "is a local action or a docker:// image, which this check does not cover; change design §20.5 and this check first."
    )]
    [InlineData(
        "owner/repo/.github/workflows/x.yml@" + Sha,
        "calls a reusable workflow, which this check does not cover; change design §20.5 and this check first."
    )]
    [InlineData(
        "owner/repo/.github/workflows/x.YAML@" + Sha,
        "calls a reusable workflow, which this check does not cover; change design §20.5 and this check first."
    )]
    public void Check_ValueNotPinned_IsRefused(string value, string reason)
    {
        string? refusal = Pin.Check(value, " # v7.0.1", out _);

        Assert.Equal($"{value} {reason}", refusal);
    }

    /// <summary>
    /// A pinned value whose line does not end with a version comment is refused.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pinned value and, as the rest of its line: nothing, a comment with no space before the hash, a major version
    ///   only, a version without the v, more text after the version, a second comment, and a version with Arabic-Indic
    ///   digits.
    ///
    /// Steps:
    /// - Check the value with each rest.
    ///
    /// Expected:
    /// - The message that no version comment follows the value.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("#v7.0.1")]
    [InlineData(" # v7")]
    [InlineData(" # 7.0.1")]
    [InlineData(" # v7.0.1 latest")]
    [InlineData(" # v7.0.1 # v7.0.2")]
    [InlineData(" # v٧.0.1")]
    public void Check_NoVersionComment_IsRefused(string rest)
    {
        string value = "actions/checkout@" + Sha;

        string? refusal = Pin.Check(value, rest, out _);

        Assert.Equal($"{value} has no version comment of the form '# vX.Y.Z' after it on its line.", refusal);
    }

    /// <summary>
    /// actions/checkout counts in any case and with .git, and nothing else does.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Repository names.
    ///
    /// Steps:
    /// - Ask whether each is a checkout.
    ///
    /// Expected:
    /// - The written answer.
    /// </remarks>
    [Theory]
    [InlineData("actions/checkout", true)]
    [InlineData("Actions/Checkout", true)]
    [InlineData("actions/checkout.git", true)]
    [InlineData("actions/checkout2", false)]
    [InlineData("other/checkout", false)]
    public void IsCheckout_RepositoryName_GivesAnswer(string repo, bool expected)
    {
        Assert.Equal(expected, Pin.IsCheckout(repo));
    }

    /// <summary>
    /// The runs mapping of an action passes when it has a type and, for a docker image, a digest.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - runs.using and runs.image values, and whether the steps hold a uses key.
    ///
    /// Steps:
    /// - Check each.
    ///
    /// Expected:
    /// - No refusal for: a composite with uses, a node action, a docker action built from a Dockerfile, and a docker image
    ///   with a sha256 digest in either case of the prefix.
    /// </remarks>
    [Theory]
    [InlineData("composite", "", true)]
    [InlineData("node24", "", false)]
    [InlineData("docker", "Dockerfile", false)]
    [InlineData("docker", "docker://alpine@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("Docker", "DOCKER://alpine@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    public void CheckRuns_Allowed_Passes(string usingValue, string image, bool hasUses)
    {
        Assert.Null(Pin.CheckRuns(usingValue, image, hasUses));
    }

    /// <summary>
    /// The runs mapping of an action is refused without a type, with uses outside a composite, or with a docker image
    /// that has no full sha256 digest.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - runs.using and runs.image values, and whether the steps hold a uses key.
    ///
    /// Steps:
    /// - Check each.
    ///
    /// Expected:
    /// - The message for each: no runs.using; uses under node24; a tag in lowercase or uppercase prefix; a short digest; a
    ///   digest followed by more text.
    /// </remarks>
    [Theory]
    [InlineData("", "", false, "the action has no runs.using.")]
    [InlineData("node24", "", true, "the action has steps with uses, but runs.using is node24, not composite.")]
    [InlineData("docker", "docker://alpine:3.20", false, "the action runs the image docker://alpine:3.20, which is not pinned by a sha256 digest.")]
    [InlineData(
        "docker",
        "DOCKER://alpine:latest",
        false,
        "the action runs the image DOCKER://alpine:latest, which is not pinned by a sha256 digest."
    )]
    [InlineData(
        "docker",
        "docker://alpine@sha256:0123",
        false,
        "the action runs the image docker://alpine@sha256:0123, which is not pinned by a sha256 digest."
    )]
    [InlineData(
        "docker",
        "docker://alpine@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef:x",
        false,
        "the action runs the image docker://alpine@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef:x, which is not pinned by a sha256 digest."
    )]
    public void CheckRuns_NotAllowed_IsRefused(string usingValue, string image, bool hasUses, string expected)
    {
        Assert.Equal(expected, Pin.CheckRuns(usingValue, image, hasUses));
    }
}
