using System.Text;

namespace ElasticLM.Licensing;

// design §8.6.

/// <summary>
/// Input limits.
/// </summary>
internal static class Limits
{
    // design §8.6.

    /// <summary>The largest early-return count a server license may allow in 30 days (<c>brl</c>).</summary>
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

    // design §8.4.3, §14.2.

    /// <summary>The length of a lease ID: 128 random bits in base64url without padding.</summary>
    public const int LeaseIdLength = 22;

    // design §8.4.3.

    /// <summary>
    /// Returns whether a lease ID has its form: 22 characters of <c>[A-Za-z0-9_-]</c>. A client names a
    /// borrow's file after it, so a path (<c>..\x</c>, a rooted path) must never pass.
    /// </summary>
    public static bool IsLeaseId(string? id) =>
        id is { Length: LeaseIdLength } && id.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    // design §8.4.2, §8.6, §12.7.1 (V1d4).

    /// <summary>The most UTF-8 bytes of a feature ID, in a license and in a lease request to an on-premises server.</summary>
    public const int MaxFeatureIdBytes = 64;

    // design §8.4.2, §12.7.1 (V1d4): the issuer, the verifier, and the on-premises server's lease request share it.

    /// <summary>
    /// Returns whether a feature ID has its form: 1 to 64 bytes in UTF-8, of any characters.
    /// </summary>
    public static bool IsFeatureId(string? id) => id is { Length: > 0 } && Encoding.UTF8.GetByteCount(id) <= MaxFeatureIdBytes;
}
