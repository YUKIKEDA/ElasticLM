namespace ElasticLM.Licensing;

/// <summary>
/// Lists the ElasticLM error codes. The numbers and names are stable once published (design §19).
/// </summary>
public enum ErrorCode
{
    /// <summary>The operation succeeded.</summary>
    Ok = 0,

    /// <summary>An argument is invalid.</summary>
    InvalidArgument = 1,

    /// <summary>The input has the wrong format or size.</summary>
    Malformed = 2,

    /// <summary>The signing key is not trusted.</summary>
    UntrustedKey = 3,

    /// <summary>A signature does not verify.</summary>
    BadSignature = 4,

    /// <summary>The token was issued outside the validity period of its key certificate.</summary>
    CertificateExpired = 5,

    /// <summary>The clock has been set back or the token is issued in the future.</summary>
    ClockTampered = 6,

    /// <summary>The license is for another product.</summary>
    WrongProduct = 7,

    /// <summary>The license is bound to another installation key.</summary>
    WrongInstallation = 8,

    /// <summary>The machine fingerprints do not match.</summary>
    FingerprintMismatch = 9,

    /// <summary>The license is past its expiry and grace period.</summary>
    Expired = 10,

    /// <summary>The feature is not in the license.</summary>
    FeatureNotLicensed = 11,

    /// <summary>The feature version is outside the licensed range.</summary>
    FeatureVersionNotLicensed = 12,

    /// <summary>The feature is past its own expiry.</summary>
    FeatureExpired = 13,

    /// <summary>No machine fingerprint can be computed on this machine.</summary>
    FingerprintUnavailable = 14,

    /// <summary>The installation key cannot be read.</summary>
    InstallationKeyUnreadable = 15,

    /// <summary>The license has another kind than the one required.</summary>
    WrongKind = 16,

    /// <summary>The key has been revoked in the trust store.</summary>
    KeyRevoked = 17,

    /// <summary>The lease is outside the bounds of the server license.</summary>
    LeaseOutOfBounds = 18,

    /// <summary>The call is not allowed in the current state.</summary>
    InvalidState = 19,

    /// <summary>The request proof is invalid.</summary>
    ProofInvalid = 20,

    /// <summary>The nonce is missing or expired.</summary>
    NonceRequired = 21,

    /// <summary>The proof was already used.</summary>
    Replayed = 22,

    /// <summary>The on-premises server is not the one expected.</summary>
    ServerPinMismatch = 23,

    /// <summary>The request body is too large.</summary>
    PayloadTooLarge = 24,

    /// <summary>The license key or the license does not exist.</summary>
    LicenseNotFound = 30,

    /// <summary>The license is suspended.</summary>
    LicenseSuspended = 31,

    /// <summary>The license is revoked.</summary>
    LicenseRevoked = 32,

    /// <summary>The activation limit is reached.</summary>
    ActivationLimit = 33,

    /// <summary>The deactivation limit is reached.</summary>
    DeactivationLimit = 34,

    /// <summary>The trial was already used on this machine.</summary>
    TrialUsed = 35,

    /// <summary>A cloned installation was detected.</summary>
    CloneDetected = 36,

    /// <summary>The offline request file has expired.</summary>
    RequestExpired = 37,

    /// <summary>The offline response does not match the pending request.</summary>
    RequestMismatch = 38,

    /// <summary>The product offers no trial.</summary>
    TrialNotOffered = 39,

    /// <summary>No seat is free.</summary>
    NoSeats = 40,

    /// <summary>The server does not know the lease.</summary>
    UnknownLease = 41,

    /// <summary>Borrowing is not allowed or exceeds the limit.</summary>
    BorrowNotAllowed = 42,

    /// <summary>The server is recovering leases after a restart.</summary>
    ServerRecovering = 43,

    /// <summary>The early return is not allowed.</summary>
    ReturnNotAllowed = 44,

    /// <summary>Admin authentication failed.</summary>
    Unauthorized = 50,

    /// <summary>The admin has no permission.</summary>
    Forbidden = 51,

    /// <summary>The request rate is limited.</summary>
    RateLimited = 52,

    /// <summary>A server record limit is reached.</summary>
    ServerBusy = 53,

    /// <summary>An administration API call names an entity, such as an API key, that does not exist (design §14.5).</summary>
    NotFound = 54,

    /// <summary>A request conflicts with an entity's state or with a unique value, such as a used code or an activation of an
    /// unbound license (design §14.5, §12.4).</summary>
    Conflict = 55,

    /// <summary>No activation has the ID: an administrator removed it, or it was deactivated (design §12.5, §12.6).</summary>
    ActivationNotFound = 56,

    /// <summary>The network call failed.</summary>
    NetworkError = 90,

    /// <summary>An internal error occurred.</summary>
    InternalError = 99,
}
