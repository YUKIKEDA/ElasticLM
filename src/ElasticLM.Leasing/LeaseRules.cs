using ElasticLM.Licensing;

namespace ElasticLM.Leasing;

/// <summary>How a renewal's sequence number stands to the lease's (design §10.4 step 3).</summary>
public enum LeaseSequence
{
    /// <summary>The sequence number of the last token: the next token is issued.</summary>
    Current,

    /// <summary>One behind, naming the request that got the last token: its answer was lost, so the last token goes again.</summary>
    Retry,

    /// <summary>Anything else: another copy of the installation went ahead.</summary>
    Clone,
}

/// <summary>
/// The decisions of the lease rules (design §12.7.2 to §12.7.4, §10.4, decision 0.64), as functions of the values they decide
/// on. The on-premises <c>LeaseEngine</c> applies them to its memory, and <c>elm-service</c> to the rows it reads under the
/// license's lock, so both answer the same request the same way.
/// </summary>
public static class LeaseRules
{
    /// <summary>The window of early returns that count against the limit (design §12.7.4).</summary>
    public static readonly TimeSpan ReturnWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// Checks that a feature is granted, has not expired, and covers the version asked for.
    /// </summary>
    /// <param name="granted">Whether the license grants the feature.</param>
    /// <param name="expiresAt">The feature's own expiry, or <see langword="null"/>.</param>
    /// <param name="versionRange">The licensed version range, or <see langword="null"/> for any.</param>
    /// <param name="version">The version asked for, or <see langword="null"/>.</param>
    /// <param name="nowSeconds">The time now, in seconds since 1970.</param>
    /// <returns><see langword="null"/> when the feature may be leased, otherwise why not.</returns>
    public static LicenseError? CheckFeature(bool granted, DateTimeOffset? expiresAt, string? versionRange, string? version, long nowSeconds)
    {
        if (!granted)
        {
            return new LicenseError(ErrorCode.FeatureNotLicensed, "The license does not grant the feature.");
        }

        if (expiresAt is DateTimeOffset featureExpires && nowSeconds > featureExpires.ToUnixTimeSeconds())
        {
            return new LicenseError(ErrorCode.FeatureExpired, "The feature has expired.");
        }

        if (version is null)
        {
            return null;
        }

        if (!FeatureVersion.TryParse(version, out FeatureVersion parsed))
        {
            return new LicenseError(ErrorCode.InvalidArgument, "The version must be major.minor[.patch].");
        }

        // A range that does not parse grants no version; the license's range was checked when the license was made.
        return versionRange is not null && (!FeatureVersionRange.TryParse(versionRange, out FeatureVersionRange? range) || !range!.Contains(parsed))
            ? new LicenseError(ErrorCode.FeatureVersionNotLicensed, "The feature version is outside the licensed range.")
            : null;
    }

    /// <summary>Returns the seats that may be held of a feature: floor(seats × (100 + overage) / 100) (design §9.3).</summary>
    /// <param name="seats">The feature's seats.</param>
    /// <param name="overagePercent">The overage in percent.</param>
    /// <returns>The seats that may be held.</returns>
    public static long Allowed(int seats, int overagePercent) => (long)seats * (100 + overagePercent) / 100;

    /// <summary>Returns whether a lease of some seats fits: the seats held and asked for stay within the allowed less those
    /// kept back for leases when it is a borrow (design §12.7.2, §12.7.4).</summary>
    /// <param name="used">The seats held now.</param>
    /// <param name="count">The seats asked for.</param>
    /// <param name="allowed">The seats that may be held.</param>
    /// <param name="reserve">The seats a borrow leaves free (on premises <c>borrow.reserve</c>; 0 in the cloud).</param>
    /// <param name="borrow">Whether the lease is a borrow.</param>
    /// <returns>Whether the lease fits.</returns>
    public static bool Fits(long used, int count, long allowed, long reserve, bool borrow) => used + count <= allowed - (borrow ? reserve : 0);

    /// <summary>
    /// Checks a seat count and a borrow period (design §12.7.1, §12.7.4).
    /// </summary>
    /// <param name="count">The seats asked for.</param>
    /// <param name="seats">The feature's seats.</param>
    /// <param name="borrow">The borrow period, or <see langword="null"/> for a lease.</param>
    /// <param name="borrowLimit">The longest borrow allowed; zero when borrows are not lent.</param>
    /// <param name="hasFingerprints">Whether the request carries machine fingerprints.</param>
    /// <returns><see langword="null"/> when the request may go on, otherwise why not.</returns>
    public static LicenseError? CheckRequest(int count, int seats, TimeSpan? borrow, TimeSpan borrowLimit, bool hasFingerprints)
    {
        if (count < 1 || count > seats)
        {
            return new LicenseError(ErrorCode.InvalidArgument, "The seat count must be at least 1 and at most the feature's seats.");
        }

        if (borrow is not TimeSpan wanted)
        {
            return null;
        }

        if (wanted < TimeSpan.FromSeconds(1))
        {
            return new LicenseError(ErrorCode.InvalidArgument, "A borrow period is at least one second.");
        }

        if (wanted > borrowLimit)
        {
            return new LicenseError(
                ErrorCode.BorrowNotAllowed,
                borrowLimit == TimeSpan.Zero ? "Borrows are not lent." : "The borrow period is longer than allowed."
            );
        }

        return hasFingerprints ? null : new LicenseError(ErrorCode.FingerprintUnavailable, "A borrow needs the machine fingerprints of the client.");
    }

    /// <summary>Judges a renewal's sequence number against the lease's (design §10.4 step 3).</summary>
    /// <param name="sequence">The sequence number sent.</param>
    /// <param name="last">The lease's sequence number.</param>
    /// <param name="issuedBy">The jti of the request that got the last token, or <see langword="null"/>.</param>
    /// <param name="retry">The jti of the requests the client got no answer to.</param>
    /// <returns>The judgement.</returns>
    public static LeaseSequence Judge(long sequence, long last, string? issuedBy, IReadOnlyCollection<string> retry)
    {
        ArgumentNullException.ThrowIfNull(retry);
        if (sequence == last)
        {
            return LeaseSequence.Current;
        }

        return sequence == last - 1 && issuedBy is not null && retry.Contains(issuedBy) ? LeaseSequence.Retry : LeaseSequence.Clone;
    }

    /// <summary>
    /// Returns whether a borrow may be returned early (design §12.7.4): the early returns of the window, the most among the
    /// installation key and the fingerprints, are under the limit, and the license has not changed for the worse since the
    /// borrow (the same license, no fewer seats of the feature, no shorter borrow limit).
    /// </summary>
    /// <param name="returns">The most early returns in the window among the counters.</param>
    /// <param name="returnLimit">The license's limit.</param>
    /// <param name="sameLicense">Whether the license is the one the borrow was lent under.</param>
    /// <param name="seats">The feature's seats now.</param>
    /// <param name="borrowedSeats">The feature's seats when it was borrowed.</param>
    /// <param name="borrowLimit">The borrow limit now.</param>
    /// <param name="borrowedLimit">The borrow limit when it was borrowed.</param>
    /// <returns>Whether the borrow may be returned.</returns>
    public static bool MayReturnEarly(
        int returns,
        int returnLimit,
        bool sameLicense,
        int seats,
        int borrowedSeats,
        TimeSpan borrowLimit,
        TimeSpan borrowedLimit
    ) => returns < returnLimit && sameLicense && seats >= borrowedSeats && borrowLimit >= borrowedLimit;

    /// <summary>Returns the counters an early return counts against: the installation key and each machine fingerprint
    /// (design §12.7.4), so a new key on the same machine does not start a new count.</summary>
    /// <param name="keyId">The installation key's thumbprint.</param>
    /// <param name="fingerprints">The machine fingerprints of the borrow.</param>
    /// <returns>The counters.</returns>
    public static IEnumerable<string> ReturnCounters(string keyId, IReadOnlyList<string> fingerprints)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);
        return fingerprints.Select(f => "fp:" + f).Prepend("key:" + keyId);
    }

    /// <summary>
    /// Returns a token's expiry: now plus the period, cut at the license's limit (its <c>exp + grace</c>), so the client's
    /// check of design §12.7.5 step 5 holds; <see langword="null"/> when less than a second would be left.
    /// </summary>
    /// <param name="nowSeconds">The time now, in seconds since 1970.</param>
    /// <param name="periodSeconds">The lease or borrow period.</param>
    /// <param name="licenseLimit">The license's <c>exp + grace</c>, or <see langword="null"/> without an expiry.</param>
    /// <returns>The expiry, or <see langword="null"/>.</returns>
    public static long? Expiry(long nowSeconds, long periodSeconds, long? licenseLimit)
    {
        long expires = licenseLimit is long limit ? Math.Min(nowSeconds + periodSeconds, limit) : nowSeconds + periodSeconds;
        return expires - nowSeconds < 1 ? null : expires;
    }
}
