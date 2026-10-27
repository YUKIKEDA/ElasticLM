using System.Security.Cryptography;
using ElasticLM.Issuing;
using ElasticLM.Licensing;

namespace ElasticLM.Leasing;

/// <summary>
/// Signs a lease token with the server's key at a given issue time.
/// </summary>
/// <param name="spec">The lease.</param>
/// <param name="issuedAt">The issue time the engine computed the duration from.</param>
/// <returns>The lease token.</returns>
public delegate string LeaseSigner(LeaseSpec spec, DateTimeOffset issuedAt);

/// <summary>
/// A request for a new lease (design §12.7.1), after its proof has been verified.
/// </summary>
/// <param name="FeatureId">The feature to lease.</param>
/// <param name="Version">The feature version the client runs (<c>major.minor[.patch]</c>), or <see langword="null"/> to skip the check.</param>
/// <param name="Count">The number of seats, at least 1 and at most the feature's seats.</param>
/// <param name="KeyId">The installation key thumbprint from the proof.</param>
/// <param name="Key">The installation public key from the proof, kept to verify later proofs for the lease.</param>
/// <param name="ProofId">The proof's <c>jti</c>, kept with the token it produced (design §10.4).</param>
/// <param name="Borrow">The borrow period (design §12.7.4), or <see langword="null"/> for a lease that is renewed.</param>
/// <param name="Fingerprints">The machine fingerprints the client sent; a borrow needs at least one, because early returns are
/// counted per fingerprint as well as per key (design §12.7.4).</param>
public sealed record LeaseRequest(
    string FeatureId,
    string? Version,
    int Count,
    string KeyId,
    ECParameters Key,
    string ProofId,
    TimeSpan? Borrow = null,
    IReadOnlyList<string>? Fingerprints = null
);

/// <summary>
/// The answer to an acquire or renew request.
/// </summary>
public abstract record LeaseOutcome
{
    private LeaseOutcome() { }

    /// <summary>
    /// A lease token was issued, or the last one was returned again for a retried renewal.
    /// </summary>
    /// <param name="Token">The lease token.</param>
    /// <param name="LeaseId">The lease ID.</param>
    /// <param name="ExpiresAt">The lease expiry.</param>
    /// <param name="CloneFlagged">Whether the request rolled back the sequence number and the server license's clone strategy
    /// is <see cref="CloneStrategy.Flag"/> (design §10.4).</param>
    public sealed record Granted(string Token, string LeaseId, DateTimeOffset ExpiresAt, bool CloneFlagged) : LeaseOutcome
    {
        /// <summary>Gets the seats the feature's leases hold after a new lease was added, including it; 0 for a renewal.</summary>
        public long InUse { get; init; }

        /// <summary>Gets the feature's seats in the server license, for a new lease; 0 for a renewal.</summary>
        public int Seats { get; init; }
    }

    /// <summary>
    /// The request was refused.
    /// </summary>
    /// <param name="Error">The reason.</param>
    /// <param name="RetryAfter">When to try again, for <see cref="ErrorCode.ServerRecovering"/>; otherwise <see langword="null"/>.</param>
    public sealed record Refused(LicenseError Error, TimeSpan? RetryAfter = null) : LeaseOutcome;
}

/// <summary>
/// What a release did (design §12.7.1).
/// </summary>
public enum ReleaseOutcome
{
    /// <summary>The lease was released and its seats freed.</summary>
    Released,

    /// <summary>The server does not know the lease; a release is idempotent, so this is not an error.</summary>
    NotFound,

    /// <summary>The proof's key is not the key the lease is bound to; nothing changed.</summary>
    WrongKey,

    /// <summary>The lease was stopped as a clone and stays until it expires (design §10.4); nothing changed.</summary>
    Stopped,

    /// <summary>
    /// The borrow cannot be returned early (design §12.7.4): the key used up its early returns of 30 days, or the server
    /// license changed since the borrow. It stays until it expires.
    /// </summary>
    ReturnNotAllowed,
}

/// <summary>
/// The result for one token of a reclaim request (design §13.4).
/// </summary>
/// <param name="LeaseId">The lease ID, or <see langword="null"/> when the token could not be read.</param>
/// <param name="Error">Why the lease was not reclaimed, or <see langword="null"/> when it is held again.</param>
/// <param name="CloneFlagged">Whether the token rolled back the sequence number under <see cref="CloneStrategy.Flag"/>.</param>
public sealed record ReclaimResult(string? LeaseId, LicenseError? Error, bool CloneFlagged = false);

/// <summary>
/// Settings of a <see cref="LeaseEngine"/> beyond the server license (design §12.7.4, §13.8).
/// </summary>
public sealed class LeaseEngineOptions
{
    /// <summary>Gets the operator's borrow limit (<c>borrow.maxDuration</c>), or <see langword="null"/> for the license's.</summary>
    public TimeSpan? MaxBorrow { get; init; }

    /// <summary>Gets the seats of each feature kept from borrowing (<c>borrow.reserve</c>).</summary>
    public int BorrowReserve { get; init; }

    /// <summary>Gets the record of borrows and early returns, written before a borrow or return takes effect.</summary>
    public IBorrowJournal? Journal { get; init; }

    /// <summary>Gets a callback for each lease removed because it expired.</summary>
    public Action<LeaseInfo>? Expired { get; init; }
}

/// <summary>
/// The durable record of borrows and early returns (design §13.4). A write that throws refuses the borrow or the return.
/// </summary>
public interface IBorrowJournal
{
    /// <summary>
    /// Records a borrow before it is granted.
    /// </summary>
    /// <param name="borrow">The borrow and the server license's values for its feature at that time.</param>
    void Borrowed(BorrowRecord borrow);

    /// <summary>
    /// Records an early return before it takes effect.
    /// </summary>
    /// <param name="returned">The return.</param>
    void Returned(BorrowReturn returned);
}

/// <summary>
/// A borrow as the record keeps it (design §12.7.4, §13.4).
/// </summary>
/// <param name="Token">The lease token.</param>
/// <param name="LicenseId">The server license ID at the borrow.</param>
/// <param name="FeatureSeats">The feature's seats in the server license at the borrow.</param>
/// <param name="FeatureBorrowLimit">The server license's borrow limit at the borrow.</param>
/// <param name="Fingerprints">The machine fingerprints of the borrowing client, against which early returns also count.</param>
public sealed record BorrowRecord(string Token, string LicenseId, int FeatureSeats, TimeSpan FeatureBorrowLimit, IReadOnlyList<string> Fingerprints);

/// <summary>
/// An early return as the record keeps it (design §12.7.4, §13.4). It counts against the key and each fingerprint for 30 days.
/// </summary>
/// <param name="LeaseId">The lease ID.</param>
/// <param name="KeyId">The installation key that borrowed it.</param>
/// <param name="Fingerprints">The machine fingerprints of the borrowing client.</param>
/// <param name="At">The time of the return.</param>
public sealed record BorrowReturn(string LeaseId, string KeyId, IReadOnlyList<string> Fingerprints, DateTimeOffset At);

/// <summary>
/// A lease as the administrator sees it.
/// </summary>
/// <param name="LeaseId">The lease ID.</param>
/// <param name="FeatureId">The feature.</param>
/// <param name="Seats">The seats.</param>
/// <param name="KeyId">The installation key thumbprint.</param>
/// <param name="ExpiresAt">The expiry.</param>
/// <param name="IsBorrow">Whether it is a borrow.</param>
/// <param name="IsStopped">Whether it was stopped as a clone.</param>
public sealed record LeaseInfo(string LeaseId, string FeatureId, int Seats, string KeyId, DateTimeOffset ExpiresAt, bool IsBorrow, bool IsStopped);

/// <summary>
/// The use of one feature.
/// </summary>
/// <param name="FeatureId">The feature.</param>
/// <param name="Seats">The seats of the server license; 0 for a feature the current license no longer grants.</param>
/// <param name="Allowed">The seats with overage.</param>
/// <param name="InUse">The seats held by unexpired leases.</param>
/// <param name="Borrowed">The seats held by borrows.</param>
public sealed record FeatureUsage(string FeatureId, int Seats, long Allowed, long InUse, long Borrowed);
