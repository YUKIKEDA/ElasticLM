namespace ElasticLM.Licensing;

/// <summary>
/// Options for <see cref="LicenseVerifier"/>.
/// </summary>
public sealed class LicenseVerifierOptions
{
    /// <summary>The largest allowed clock skew.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets the allowed clock skew between this machine and the issuer (design §11.3). The default is 5 minutes. It
    /// must be between zero and <see cref="MaxClockSkew"/>; <see cref="LicenseVerifier"/> checks it when it is created.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets a value indicating whether a requirement for <see cref="LicenseKind.Standard"/> also accepts trial
    /// licenses. The default is <see langword="true"/>.
    /// </summary>
    public bool AcceptTrialAsStandard { get; set; } = true;
}
