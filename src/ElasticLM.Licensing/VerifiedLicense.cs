namespace ElasticLM.Licensing;

/// <summary>
/// A feature granted by a verified license (design §8.4.2).
/// </summary>
/// <param name="Id">The feature ID.</param>
/// <param name="VersionRange">The allowed version range (design §9.2), or <see langword="null"/> for every version.</param>
/// <param name="Seats">The seat count for server licenses, otherwise <see langword="null"/>.</param>
/// <param name="ExpiresAt">The feature's own expiry, or <see langword="null"/>.</param>
public sealed record LicensedFeature(string Id, string? VersionRange, int? Seats, DateTimeOffset? ExpiresAt);

/// <summary>
/// A license that passed signature verification and every check of design §11.2. It can be created only by
/// <see cref="LicenseVerifier"/>.
/// </summary>
public sealed class VerifiedLicense
{
    internal VerifiedLicense(LicensePayload payload, LicenseKind kind, IReadOnlyList<LicensedFeature> features)
    {
        LicenseId = payload.Lic;
        ProductId = payload.Prd;
        Kind = kind;
        Features = features;
        TokenId = payload.Jti;
        SigningKeyId = payload.Iss;
        IssuedAt = DateTimeOffset.FromUnixTimeSeconds(payload.Iat);
        ExpiresAt = payload.Exp is long exp ? DateTimeOffset.FromUnixTimeSeconds(exp) : null;
        GracePeriod = TimeSpan.FromSeconds(payload.Grace ?? 0);
        RefreshWindow = payload.Refresh is long refresh ? TimeSpan.FromSeconds(refresh) : null;
        InstallationKeyThumbprint = payload.Cnf?.Jkt;
        Fingerprints = payload.Fp ?? [];
        Sequence = payload.Seq;
        OfflineRequestNonce = payload.Areq;
        LeaseDuration = payload.Lease is long lease ? TimeSpan.FromSeconds(lease) : null;
        BorrowLimit = payload.Borrow is long borrow ? TimeSpan.FromSeconds(borrow) : null;
        OveragePercent = payload.Overage;
        BorrowReturnLimit = payload.Brl;
        CloneStrategy = payload.Clone switch
        {
            "deny" => Licensing.CloneStrategy.Deny,
            "flag" => Licensing.CloneStrategy.Flag,
            _ => null,
        };
        Metadata = payload.Meta ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Gets the license ID.</summary>
    public string LicenseId { get; }

    /// <summary>Gets the product ID.</summary>
    public string ProductId { get; }

    /// <summary>Gets the license kind.</summary>
    public LicenseKind Kind { get; }

    /// <summary>Gets the granted features.</summary>
    public IReadOnlyList<LicensedFeature> Features { get; }

    /// <summary>Gets the token ID (<c>jti</c>).</summary>
    public string TokenId { get; }

    /// <summary>Gets the key ID of the signing key that issued the token.</summary>
    public string SigningKeyId { get; }

    /// <summary>Gets the time the token was issued.</summary>
    public DateTimeOffset IssuedAt { get; }

    /// <summary>Gets the token expiry, or <see langword="null"/> for a token without expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>Gets the period after <see cref="ExpiresAt"/> during which the license is still usable.</summary>
    public TimeSpan GracePeriod { get; }

    /// <summary>Gets how long before <see cref="ExpiresAt"/> the client starts to refresh, or <see langword="null"/>.</summary>
    public TimeSpan? RefreshWindow { get; }

    /// <summary>Gets the installation key thumbprint the license is bound to, or <see langword="null"/> when unbound.</summary>
    public string? InstallationKeyThumbprint { get; }

    /// <summary>Gets the machine fingerprints in the license.</summary>
    public IReadOnlyList<string> Fingerprints { get; }

    /// <summary>Gets the activation sequence number (design §10.4), or <see langword="null"/>.</summary>
    public long? Sequence { get; }

    /// <summary>Gets the nonce of the offline request this license answers (design §12.8), or <see langword="null"/>.</summary>
    public string? OfflineRequestNonce { get; }

    /// <summary>Gets the lease duration for server licenses, or <see langword="null"/>.</summary>
    public TimeSpan? LeaseDuration { get; }

    /// <summary>Gets the borrow limit for server licenses, or <see langword="null"/>.</summary>
    public TimeSpan? BorrowLimit { get; }

    /// <summary>Gets the overage percentage for server licenses, or <see langword="null"/>.</summary>
    public int? OveragePercent { get; }

    /// <summary>
    /// Gets how many borrowed leases one installation key may return early in 30 days, for server licenses, or
    /// <see langword="null"/> (design §9.3 <c>BorrowReturnLimit</c>, §12.7.4).
    /// </summary>
    public int? BorrowReturnLimit { get; }

    /// <summary>Gets what the server does with a detected clone, for server licenses, or <see langword="null"/>.</summary>
    public CloneStrategy? CloneStrategy { get; }

    /// <summary>Gets the vendor metadata. The library does not interpret it.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }
}
