using ElasticLM.Licensing;

namespace ElasticLM.Client;

// design §12.1, §19 (the V1d1 Grill, Q1 and Q6): a problem answer and a reclaim answer carry no signature, so a relay or a
// false server can write any code into them. Each list is the design's; a test compares both with the table of design §19.

/// <summary>
/// Lists the error codes a client takes from what a server answers. A code outside the list is read as a code the client
/// does not know, so an answer cannot hand the app the success code or a code that only the client itself makes.
/// </summary>
internal static class ProblemCodes
{
    /// <summary>
    /// Returns whether a client takes <paramref name="code"/> from the <c>code</c> of a problem answer: the codes that a
    /// server sends with a 4xx or 5xx status.
    /// </summary>
    /// <param name="code">The code the answer names.</param>
    /// <returns><see langword="true"/> when the code is taken as it is.</returns>
    public static bool InProblemAnswer(ErrorCode code) =>
        code
            is ErrorCode.InvalidArgument
                or ErrorCode.Malformed
                or ErrorCode.WrongProduct
                or ErrorCode.Expired
                or ErrorCode.FeatureNotLicensed
                or ErrorCode.FeatureVersionNotLicensed
                or ErrorCode.FeatureExpired
                or ErrorCode.FingerprintUnavailable
                or ErrorCode.ProofInvalid
                or ErrorCode.NonceRequired
                or ErrorCode.Replayed
                or ErrorCode.PayloadTooLarge
                or ErrorCode.LicenseNotFound
                or ErrorCode.LicenseSuspended
                or ErrorCode.LicenseRevoked
                or ErrorCode.ActivationLimit
                or ErrorCode.DeactivationLimit
                or ErrorCode.TrialUsed
                or ErrorCode.CloneDetected
                or ErrorCode.RequestExpired
                or ErrorCode.TrialNotOffered
                or ErrorCode.NoSeats
                or ErrorCode.UnknownLease
                or ErrorCode.BorrowNotAllowed
                or ErrorCode.ServerRecovering
                or ErrorCode.ReturnNotAllowed
                or ErrorCode.Unauthorized
                or ErrorCode.Forbidden
                or ErrorCode.RateLimited
                or ErrorCode.ServerBusy
                or ErrorCode.NotFound
                or ErrorCode.Conflict
                or ErrorCode.ActivationNotFound
                or ErrorCode.InternalError;

    /// <summary>
    /// Returns whether a client takes <paramref name="code"/> from the <c>code</c> of one lease in a reclaim answer:
    /// <c>ELM_OK</c> for a lease the server took back, and the codes a server refuses a lease's reclaim with.
    /// </summary>
    /// <param name="code">The code the answer names for the lease.</param>
    /// <returns><see langword="true"/> when the code is taken as it is.</returns>
    public static bool InReclaimAnswer(ErrorCode code) =>
        code
            is ErrorCode.Ok
                or ErrorCode.Malformed
                or ErrorCode.BadSignature
                or ErrorCode.ClockTampered
                or ErrorCode.WrongProduct
                or ErrorCode.WrongInstallation
                or ErrorCode.Expired
                or ErrorCode.LeaseOutOfBounds
                or ErrorCode.CloneDetected
                or ErrorCode.NoSeats
                or ErrorCode.BorrowNotAllowed;
}
