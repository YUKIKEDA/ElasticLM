using System.Buffers;
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
    public static bool IsFeatureId(string? id) =>
        id is { Length: > 0 and <= MaxFeatureIdBytes } && Encoding.UTF8.GetByteCount(id) <= MaxFeatureIdBytes;

    // design §12.2, §8.4.3 (V1d5).

    /// <summary>The most characters a proof's <c>jti</c> has.</summary>
    public const int MaxProofIdLength = 64;

    // design §12.2 (V1d26): the characters of base64url (RFC 4648 §5) only, which ElasticLM.Client sends; a database may not
    // store others (PostgreSQL refuses U+0000 in text), and a proof ID goes into tokens, records, and logs.
    private static readonly SearchValues<char> ProofIdCharacters = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
    );

    /// <summary>
    /// Returns whether a proof's <c>jti</c> has its form: 1 to 64 characters of base64url (<c>A-Z</c>, <c>a-z</c>,
    /// <c>0-9</c>, <c>-</c>, and <c>_</c>). A server accepts no other in a proof, so a lease token that names the proof it
    /// answers holds no other.
    /// </summary>
    public static bool IsProofId(string? id) => id is { Length: > 0 and <= MaxProofIdLength } && !id.AsSpan().ContainsAnyExcept(ProofIdCharacters);

    // design §12.6.1, §12.7.1 (V1d26): the servers refuse a request's client host, user, and app version with one, so none
    // is stored (PostgreSQL refuses U+0000 in text) or reaches the logs and the admin UI.

    /// <summary>
    /// Returns whether a text holds a C0 control character (U+0000 to U+001F).
    /// </summary>
    public static bool HasControlCharacter(string text) => text.AsSpan().ContainsAnyInRange('\u0000', '\u001F');

    // design §8.4.2 (V1d5a).

    /// <summary>
    /// Returns whether an activation ID has its form: a UUID of 36 characters with hyphens, as the hosted activation service
    /// writes it.
    /// </summary>
    public static bool IsActivationId(string? id) => Guid.TryParseExact(id, "D", out _);
}
