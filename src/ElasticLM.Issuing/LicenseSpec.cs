using ElasticLM.Licensing;

namespace ElasticLM.Issuing;

/// <summary>
/// How a license compares machine fingerprints (design §9.3 <c>FingerprintMatching</c>).
/// </summary>
public enum FingerprintMatching
{
    /// <summary>One of the license fingerprints must match.</summary>
    Any,

    /// <summary>Every license fingerprint must match.</summary>
    All,

    /// <summary>Fingerprints are not compared.</summary>
    Ignore,
}

/// <summary>
/// A feature to grant (design §8.4.2).
/// </summary>
/// <param name="Id">The feature ID.</param>
/// <param name="VersionRange">The allowed version range (design §9.2), or <see langword="null"/> for every version.</param>
/// <param name="Seats">The seat count; required for server licenses and not allowed otherwise.</param>
/// <param name="ExpiresAt">The feature's own expiry, or <see langword="null"/>.</param>
public sealed record FeatureSpec(string Id, string? VersionRange = null, int? Seats = null, DateTimeOffset? ExpiresAt = null);

/// <summary>
/// What a license token says (design §8.4.2). <see cref="LicenseIssuer"/> checks it against the same rules the verifier
/// applies, so a token the verifier rejects is never issued.
/// </summary>
public sealed record LicenseSpec
{
    /// <summary>Gets the license ID.</summary>
    public required string LicenseId { get; init; }

    /// <summary>Gets the product ID.</summary>
    public required string ProductId { get; init; }

    /// <summary>Gets the license kind.</summary>
    public LicenseKind Kind { get; init; } = LicenseKind.Standard;

    /// <summary>Gets the granted features.</summary>
    public IReadOnlyList<FeatureSpec> Features { get; init; } = [];

    /// <summary>Gets the token expiry, or <see langword="null"/> for a token without expiry, which cannot be revoked (design §5.4).</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Gets the grace period after <see cref="ExpiresAt"/>. Trials must have none.</summary>
    public TimeSpan GracePeriod { get; init; }

    /// <summary>Gets how long before the expiry clients start refreshing, or <see langword="null"/> for tokens not refreshed online.</summary>
    public TimeSpan? RefreshWindow { get; init; }

    /// <summary>Gets the installation key thumbprint to bind to, or <see langword="null"/> for an unbound license.</summary>
    public string? InstallationKeyThumbprint { get; init; }

    /// <summary>Gets the machine fingerprints to bind to.</summary>
    public IReadOnlyList<string> Fingerprints { get; init; } = [];

    /// <summary>Gets how fingerprints are compared. Used only when <see cref="Fingerprints"/> is not empty.</summary>
    public FingerprintMatching FingerprintMatching { get; init; } = FingerprintMatching.Any;

    /// <summary>Gets the activation sequence number (design §10.4), or <see langword="null"/> for manual issuance.</summary>
    public long? Sequence { get; init; }

    /// <summary>Gets the nonce of the offline request answered (design §12.8), or <see langword="null"/>.</summary>
    public string? OfflineRequestNonce { get; init; }

    /// <summary>Gets the lease duration; required for server licenses.</summary>
    public TimeSpan? LeaseDuration { get; init; }

    /// <summary>Gets the borrow limit; required for server licenses (zero disallows borrowing).</summary>
    public TimeSpan? BorrowLimit { get; init; }

    /// <summary>Gets the overage percentage; required for server licenses.</summary>
    public int? OveragePercent { get; init; }

    /// <summary>Gets the vendor metadata.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
