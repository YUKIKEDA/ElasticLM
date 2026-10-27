namespace ElasticLM.Licensing;

/// <summary>
/// Converts <see cref="ErrorCode"/> values to their wire names (design §19).
/// </summary>
public static class ErrorCodes
{
    /// <summary>
    /// Reads a wire name such as <c>ELM_NO_SEATS</c> (design §19).
    /// </summary>
    /// <param name="wireName">The wire name.</param>
    /// <param name="code">The code; <see cref="ErrorCode.InternalError"/> when the name is unknown.</param>
    /// <returns><see langword="false"/> when the name is not a known code.</returns>
    public static bool TryParseWireName(string? wireName, out ErrorCode code)
    {
        foreach (ErrorCode candidate in Enum.GetValues<ErrorCode>())
        {
            if (ToWireName(candidate) == wireName)
            {
                code = candidate;
                return true;
            }
        }

        code = ErrorCode.InternalError;
        return false;
    }

    /// <summary>
    /// Returns the wire name of an error code, such as <c>ELM_MALFORMED</c>.
    /// </summary>
    /// <param name="code">The error code.</param>
    /// <returns>The wire name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="code"/> is not a defined value.</exception>
    public static string ToWireName(ErrorCode code) =>
        code switch
        {
            ErrorCode.Ok => "ELM_OK",
            ErrorCode.InvalidArgument => "ELM_INVALID_ARGUMENT",
            ErrorCode.Malformed => "ELM_MALFORMED",
            ErrorCode.UntrustedKey => "ELM_UNTRUSTED_KEY",
            ErrorCode.BadSignature => "ELM_BAD_SIGNATURE",
            ErrorCode.CertificateExpired => "ELM_CERTIFICATE_EXPIRED",
            ErrorCode.ClockTampered => "ELM_CLOCK_TAMPERED",
            ErrorCode.WrongProduct => "ELM_WRONG_PRODUCT",
            ErrorCode.WrongInstallation => "ELM_WRONG_INSTALLATION",
            ErrorCode.FingerprintMismatch => "ELM_FINGERPRINT_MISMATCH",
            ErrorCode.Expired => "ELM_EXPIRED",
            ErrorCode.FeatureNotLicensed => "ELM_FEATURE_NOT_LICENSED",
            ErrorCode.FeatureVersionNotLicensed => "ELM_FEATURE_VERSION_NOT_LICENSED",
            ErrorCode.FeatureExpired => "ELM_FEATURE_EXPIRED",
            ErrorCode.FingerprintUnavailable => "ELM_FINGERPRINT_UNAVAILABLE",
            ErrorCode.InstallationKeyUnreadable => "ELM_INSTALLATION_KEY_UNREADABLE",
            ErrorCode.WrongKind => "ELM_WRONG_KIND",
            ErrorCode.KeyRevoked => "ELM_KEY_REVOKED",
            ErrorCode.LeaseOutOfBounds => "ELM_LEASE_OUT_OF_BOUNDS",
            ErrorCode.InvalidState => "ELM_INVALID_STATE",
            ErrorCode.ProofInvalid => "ELM_PROOF_INVALID",
            ErrorCode.NonceRequired => "ELM_NONCE_REQUIRED",
            ErrorCode.Replayed => "ELM_REPLAYED",
            ErrorCode.ServerPinMismatch => "ELM_SERVER_PIN_MISMATCH",
            ErrorCode.PayloadTooLarge => "ELM_PAYLOAD_TOO_LARGE",
            ErrorCode.LicenseNotFound => "ELM_LICENSE_NOT_FOUND",
            ErrorCode.LicenseSuspended => "ELM_LICENSE_SUSPENDED",
            ErrorCode.LicenseRevoked => "ELM_LICENSE_REVOKED",
            ErrorCode.ActivationLimit => "ELM_ACTIVATION_LIMIT",
            ErrorCode.DeactivationLimit => "ELM_DEACTIVATION_LIMIT",
            ErrorCode.TrialUsed => "ELM_TRIAL_USED",
            ErrorCode.CloneDetected => "ELM_CLONE_DETECTED",
            ErrorCode.RequestExpired => "ELM_REQUEST_EXPIRED",
            ErrorCode.RequestMismatch => "ELM_REQUEST_MISMATCH",
            ErrorCode.TrialNotOffered => "ELM_TRIAL_NOT_OFFERED",
            ErrorCode.NoSeats => "ELM_NO_SEATS",
            ErrorCode.UnknownLease => "ELM_UNKNOWN_LEASE",
            ErrorCode.BorrowNotAllowed => "ELM_BORROW_NOT_ALLOWED",
            ErrorCode.ServerRecovering => "ELM_SERVER_RECOVERING",
            ErrorCode.ReturnNotAllowed => "ELM_RETURN_NOT_ALLOWED",
            ErrorCode.Unauthorized => "ELM_UNAUTHORIZED",
            ErrorCode.Forbidden => "ELM_FORBIDDEN",
            ErrorCode.RateLimited => "ELM_RATE_LIMITED",
            ErrorCode.ServerBusy => "ELM_SERVER_BUSY",
            ErrorCode.NotFound => "ELM_NOT_FOUND",
            ErrorCode.NetworkError => "ELM_NETWORK_ERROR",
            ErrorCode.InternalError => "ELM_INTERNAL_ERROR",
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "The error code is not defined."),
        };
}
