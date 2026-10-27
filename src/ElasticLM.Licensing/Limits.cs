namespace ElasticLM.Licensing;

/// <summary>
/// Input limits (design §8.6).
/// </summary>
internal static class Limits
{
    /// <summary>The largest early-return count a server license may allow in 30 days (design §8.6, <c>brl</c>).</summary>
    public const int MaxBorrowReturns = 1_000;

    public const int MaxTokenLength = 16 * 1024;
    public const int MaxLicenseFileLength = 64 * 1024;
    public const int MaxCertificates = 4;
    public const int MaxFeatures = 256;
    public const int MaxFingerprints = 8;
    public const int MaxMetaEntries = 32;
    public const int MaxMetaKeyBytes = 64;
    public const int MaxMetaValueBytes = 1024;
    public const int MaxJsonDepth = 8;

    /// <summary>The largest time accepted in a claim: 9999-12-31T23:59:59Z, the last second DateTimeOffset can hold.</summary>
    public const long MaxUnixSeconds = 253_402_300_799;

    /// <summary>The largest duration accepted in a claim (grace, refresh, lease, borrow): 100 years.</summary>
    public const long MaxDurationSeconds = 100L * 366 * 86400;

    public static bool IsTime(long value) => value is >= 0 and <= MaxUnixSeconds;

    public static bool IsTime(long? value) => value is null || IsTime(value.Value);

    public static bool IsDuration(long? value) => value is null or (>= 0 and <= MaxDurationSeconds);
}
