namespace ElasticLM.Licensing;

/// <summary>
/// Describes why a license or token was rejected.
/// </summary>
/// <param name="Code">The error code (design §19).</param>
/// <param name="Message">One English sentence for people. It never contains key material or a full token.</param>
public sealed record LicenseError(ErrorCode Code, string Message)
{
    /// <summary>
    /// Gets the wire name of <see cref="Code"/>, such as <c>ELM_EXPIRED</c>.
    /// </summary>
    public string WireName => ErrorCodes.ToWireName(Code);
}
