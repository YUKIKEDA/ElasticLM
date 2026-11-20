using System.Text.RegularExpressions;

namespace ElasticLM.ActionPins;

/// <summary>
/// The rules on the value of a uses key and on an action's runs mapping that need no network (design §20.5, V1d22). The
/// script checks the rest against the action's repository: the tag's commit, and the definition at that commit.
/// </summary>
internal static partial class Pin
{
    /// <summary>
    /// Checks the form of a uses value and of the rest of its line.
    /// </summary>
    /// <param name="value">The value of the uses key.</param>
    /// <param name="rest">The text of the line after the value.</param>
    /// <param name="pinned">The parts of the value when it passes.</param>
    /// <returns>The reason it fails, or <see langword="null"/> when it passes.</returns>
    public static string? Check(string value, string rest, out Pinned pinned)
    {
        pinned = default;
        if (value.StartsWith("./", StringComparison.Ordinal) || value.StartsWith("docker://", StringComparison.OrdinalIgnoreCase))
        {
            return $"{value} is a local action or a docker:// image, which this check does not cover; change design §20.5 and this check first.";
        }

        Match match = PinnedPattern().Match(value);
        if (!match.Success)
        {
            return $"{value} is not pinned to a full commit SHA (40 lowercase hex digits).";
        }

        string folder = match.Groups["path"].Value;
        if (folder.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || folder.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            return $"{value} calls a reusable workflow, which this check does not cover; change design §20.5 and this check first.";
        }

        Match comment = CommentPattern().Match(rest);
        if (!comment.Success)
        {
            return $"{value} has no version comment of the form '# vX.Y.Z' after it on its line.";
        }

        pinned = new Pinned(match.Groups["repo"].Value, folder, match.Groups["sha"].Value, comment.Groups["version"].Value);
        return null;
    }

    /// <summary>
    /// Gets a value indicating whether the repository is actions/checkout, in any case and with or without .git.
    /// </summary>
    /// <param name="repo">The owner and repository of a pinned value.</param>
    /// <returns><see langword="true"/> when the step is a checkout.</returns>
    public static bool IsCheckout(string repo) => CheckoutPattern().IsMatch(repo);

    /// <summary>
    /// Checks the runs mapping of an action definition.
    /// </summary>
    /// <param name="usingValue">The value of runs.using.</param>
    /// <param name="image">The value of runs.image.</param>
    /// <param name="hasUses">Whether runs.steps holds a uses key.</param>
    /// <returns>The reason it fails, or <see langword="null"/> when it passes.</returns>
    public static string? CheckRuns(string usingValue, string image, bool hasUses)
    {
        if (usingValue.Length == 0)
        {
            return "the action has no runs.using.";
        }

        if (hasUses && !string.Equals(usingValue, "composite", StringComparison.OrdinalIgnoreCase))
        {
            return $"the action has steps with uses, but runs.using is {usingValue}, not composite.";
        }

        // The runner takes the docker:// prefix in any case (actions/runner ActionManager.cs and ContainerActionHandler.cs
        // compare it with OrdinalIgnoreCase); a tag can be moved to another image, a digest cannot.
        if (
            string.Equals(usingValue, "docker", StringComparison.OrdinalIgnoreCase)
            && image.StartsWith("docker://", StringComparison.OrdinalIgnoreCase)
            && !DigestPattern().IsMatch(image)
        )
        {
            return $"the action runs the image {image}, which is not pinned by a sha256 digest.";
        }

        return null;
    }

    // A path segment is never empty, "." or "..": actions/checkout/.@<sha> names the same action as actions/checkout@<sha>.
    [GeneratedRegex(
        @"^(?<repo>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)(?<path>(?:/(?!\.\.?(?:/|@))[A-Za-z0-9_.-]+)+)?@(?<sha>[0-9a-f]{40})$",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex PinnedPattern();

    [GeneratedRegex(@"^[ \t]+#[ \t]*(?<version>v[0-9]+\.[0-9]+\.[0-9]+)[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"^actions/checkout(?:\.git)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CheckoutPattern();

    [GeneratedRegex(@"@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();
}
