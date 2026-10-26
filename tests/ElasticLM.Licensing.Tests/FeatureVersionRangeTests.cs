namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Tests of <see cref="FeatureVersionRange"/> and <see cref="FeatureVersion"/> (design §9.2).
/// </summary>
public sealed class FeatureVersionRangeTests
{
    /// <summary>
    /// Each comparator holds exactly at its boundary as defined.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Ranges with each comparator.
    ///
    /// Steps:
    /// - Test versions on both sides of and at the bound.
    ///
    /// Expected:
    /// - The listed result, where a missing patch counts as 0.
    /// </remarks>
    [Theory]
    [InlineData(">=2.0 <3.0", "2.0", true)]
    [InlineData(">=2.0 <3.0", "1.99.99", false)]
    [InlineData(">=2.0 <3.0", "3.0.0", false)]
    [InlineData(">2.0", "2.0.0", false)]
    [InlineData(">2.0", "2.0.1", true)]
    [InlineData("<=1.5", "1.5", true)]
    [InlineData("<=1.5", "1.5.1", false)]
    [InlineData("=1.2.3", "1.2.3", true)]
    [InlineData("=1.2.3", "1.2.4", false)]
    [InlineData(">=10.0", "9.99", false)]
    public void Contains_Comparators_HoldAtBounds(string range, string version, bool expected)
    {
        Assert.True(FeatureVersionRange.TryParse(range, out FeatureVersionRange? parsed));
        Assert.True(FeatureVersion.TryParse(version, out FeatureVersion v));

        Assert.Equal(expected, parsed!.Contains(v));
    }

    /// <summary>
    /// Ranges and versions outside the grammar are rejected.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Texts that break the grammar: no comparator, a tilde, one part, four parts, a sign, letters, empty.
    ///
    /// Steps:
    /// - Parse each as a range.
    ///
    /// Expected:
    /// - False.
    /// </remarks>
    [Theory]
    [InlineData("2.0")]
    [InlineData("~2.0")]
    [InlineData(">=2")]
    [InlineData(">=1.2.3.4")]
    [InlineData(">=-1.0")]
    [InlineData(">=1.x")]
    [InlineData("")]
    [InlineData(">=1.0 <2.0 <3.0 <4.0 <5.0")]
    public void TryParse_OutsideGrammar_ReturnsFalse(string text)
    {
        Assert.False(FeatureVersionRange.TryParse(text, out _));
    }
}
