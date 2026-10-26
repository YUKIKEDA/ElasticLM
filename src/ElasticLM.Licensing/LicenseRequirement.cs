namespace ElasticLM.Licensing;

/// <summary>
/// What the app requires from a license (design §11.1).
/// </summary>
/// <param name="ProductId">The product ID the license must be for.</param>
/// <param name="Kind">The license kind. <see cref="LicenseKind.Standard"/> also accepts trials unless <see cref="LicenseVerifierOptions.AcceptTrialAsStandard"/> is off.</param>
/// <param name="FeatureId">The feature to check, or <see langword="null"/> to check none.</param>
/// <param name="FeatureVersion">The feature version the app runs as (<c>major.minor[.patch]</c>), or <see langword="null"/> to skip the version check.</param>
/// <param name="Installation">This installation, or <see langword="null"/> when the app uses unbound licenses only.</param>
public readonly record struct LicenseRequirement(
    string ProductId,
    LicenseKind Kind,
    string? FeatureId,
    string? FeatureVersion,
    InstallationIdentity? Installation
);
