namespace ElasticLM.Licensing;

/// <summary>
/// The outcome of <see cref="LicenseVerifier.Verify"/>: either <see cref="Valid"/> or <see cref="Invalid"/>.
/// </summary>
public abstract record LicenseVerificationResult
{
    private LicenseVerificationResult() { }

    /// <summary>
    /// The license passed every check.
    /// </summary>
    /// <param name="License">The verified license.</param>
    /// <param name="InGracePeriod"><see langword="true"/> when the token is past its expiry but inside the grace period.</param>
    public sealed record Valid(VerifiedLicense License, bool InGracePeriod) : LicenseVerificationResult;

    /// <summary>
    /// The license failed a check. The error names the first check that failed (design §11.2).
    /// </summary>
    /// <param name="Error">The error.</param>
    public sealed record Invalid(LicenseError Error) : LicenseVerificationResult;
}
