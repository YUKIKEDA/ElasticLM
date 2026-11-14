namespace ElasticLM.Licensing.Tests;

/// <summary>
/// The public members of the verified license's types (design §9.2, §12.7.5 step 7).
/// </summary>
public sealed class VerifiedLicenseTests
{
    /// <summary>
    /// <see cref="LicensedFeature.CoversVersion"/> reads versions and ranges as the verifier does: inside the range at both of
    /// its ends, outside it past them, every version without a range, and never a version that is not major.minor[.patch].
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A feature with the range &gt;=2.0 &lt;3.0, and one without a range.
    ///
    /// Steps:
    /// - Ask each feature about the version of the case.
    ///
    /// Expected:
    /// - With the range: 2.0, 2.5, 2.999.999, and "02.0" (a leading zero is read as 2.0) are covered; 1.999.999, 3.0, and
    ///   3.0.1 are not; "two", "2", "", "2.0.0.0", " 2.0", "2.0 ", "2.0-beta", and "1234567890.0" (a part of 10 digits)
    ///   are not.
    /// - Without a range: 3.0 and 0.0 are covered; "two" and "" are not.
    /// </remarks>
    /// <param name="range">The feature's range, or <see langword="null"/>.</param>
    /// <param name="version">The version asked about.</param>
    /// <param name="covered">The answer expected.</param>
    [Theory]
    [InlineData(">=2.0 <3.0", "2.0", true)]
    [InlineData(">=2.0 <3.0", "2.5", true)]
    [InlineData(">=2.0 <3.0", "2.999.999", true)]
    [InlineData(">=2.0 <3.0", "1.999.999", false)]
    [InlineData(">=2.0 <3.0", "3.0", false)]
    [InlineData(">=2.0 <3.0", "3.0.1", false)]
    [InlineData(">=2.0 <3.0", "two", false)]
    [InlineData(">=2.0 <3.0", "2", false)]
    [InlineData(">=2.0 <3.0", "", false)]
    [InlineData(">=2.0 <3.0", "2.0.0.0", false)]
    [InlineData(">=2.0 <3.0", "02.0", true)]
    [InlineData(">=2.0 <3.0", " 2.0", false)]
    [InlineData(">=2.0 <3.0", "2.0 ", false)]
    [InlineData(">=2.0 <3.0", "2.0-beta", false)]
    [InlineData(">=2.0 <3.0", "1234567890.0", false)]
    [InlineData(null, "3.0", true)]
    [InlineData(null, "0.0", true)]
    [InlineData(null, "two", false)]
    [InlineData(null, "", false)]
    public void CoversVersion_Version_IsJudgedAsTheVerifierDoes(string? range, string version, bool covered)
    {
        var feature = new LicensedFeature("pro", range, 5, null);

        Assert.Equal(covered, feature.CoversVersion(version));
    }

    /// <summary>
    /// <see cref="LicensedFeature.CoversVersion"/> refuses a null version as an argument error.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A feature with the range &gt;=2.0 &lt;3.0.
    ///
    /// Steps:
    /// - Ask about a null version.
    ///
    /// Expected:
    /// - ArgumentNullException naming version.
    /// </remarks>
    [Fact]
    public void CoversVersion_NullVersion_Throws()
    {
        var feature = new LicensedFeature("pro", ">=2.0 <3.0", 5, null);

        Assert.Equal("version", Assert.Throws<ArgumentNullException>(() => feature.CoversVersion(null!)).ParamName);
    }
}
