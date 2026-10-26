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
public sealed record LeaseRequest(string FeatureId, string? Version, int Count, string KeyId, ECParameters Key, string ProofId);

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
    public sealed record Granted(string Token, string LeaseId, DateTimeOffset ExpiresAt, bool CloneFlagged) : LeaseOutcome;

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
}

/// <summary>
/// The result for one token of a reclaim request (design §13.4).
/// </summary>
/// <param name="LeaseId">The lease ID, or <see langword="null"/> when the token could not be read.</param>
/// <param name="Error">Why the lease was not reclaimed, or <see langword="null"/> when it is held again.</param>
/// <param name="CloneFlagged">Whether the token rolled back the sequence number under <see cref="CloneStrategy.Flag"/>.</param>
public sealed record ReclaimResult(string? LeaseId, LicenseError? Error, bool CloneFlagged = false);
