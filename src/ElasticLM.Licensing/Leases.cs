using System.Security.Cryptography;
using System.Text.Json;

namespace ElasticLM.Licensing;

/// <summary>
/// An on-premises license server as the client knows it: a verified server license and the server's public key, whose
/// thumbprint is the license's <c>cnf.jkt</c> (design §13.5).
/// </summary>
public sealed class ServerIdentity : IDisposable
{
    private ServerIdentity(VerifiedLicense license, VerificationKey key)
    {
        License = license;
        Key = key;
        KeyId = key.KeyId;
    }

    /// <summary>Gets the server license.</summary>
    public VerifiedLicense License { get; }

    /// <summary>Gets the key ID of the server's key, equal to the license's <c>cnf.jkt</c>.</summary>
    public string KeyId { get; }

    // Created once: the client checks every renewal and every borrowed lease with it (M1: creating an ECDsa costs more than a
    // verification).
    internal VerificationKey Key { get; }

    /// <summary>
    /// Creates a server identity from a server license that <see cref="LicenseVerifier"/> verified with
    /// <see cref="LicenseKind.Server"/>, and the server's public key as received from <c>GET /v1/server/license</c>.
    /// </summary>
    /// <param name="license">The verified server license.</param>
    /// <param name="publicKeyJwk">The public key as a JWK (EC P-256 with <c>kty</c>, <c>crv</c>, <c>x</c>, <c>y</c>).</param>
    /// <param name="identity">The identity, or <see langword="null"/> on failure.</param>
    /// <param name="error">Why it failed: <c>ELM_WRONG_KIND</c> for a license that is not a server license,
    /// <c>ELM_MALFORMED</c> for a key that is not such a JWK, <c>ELM_SERVER_PIN_MISMATCH</c> for a key that is not the
    /// license's <c>cnf.jkt</c>.</param>
    /// <returns><see langword="true"/> when the identity was created.</returns>
    public static bool TryCreate(VerifiedLicense license, ReadOnlySpan<byte> publicKeyJwk, out ServerIdentity? identity, out LicenseError? error)
    {
        ArgumentNullException.ThrowIfNull(license);
        identity = null;
        if (license.Kind != LicenseKind.Server || license.InstallationKeyThumbprint is null)
        {
            error = new LicenseError(ErrorCode.WrongKind, "The license is not a server license.");
            return false;
        }

        if (!TryReadJwk(publicKeyJwk, out ECParameters parameters) || !VerificationKey.TryCreate(parameters, out VerificationKey? key))
        {
            error = new LicenseError(ErrorCode.Malformed, "The server key is not an EC P-256 public key in JWK form.");
            return false;
        }

        if (key!.KeyId != license.InstallationKeyThumbprint)
        {
            key.Dispose();
            error = new LicenseError(ErrorCode.ServerPinMismatch, "The server key is not the key of the server license.");
            return false;
        }

        identity = new ServerIdentity(license, key);
        error = null;
        return true;
    }

    /// <summary>
    /// Verifies a server license received from a server, as a client does (design §13.5 step 2): §11.2 with the kind
    /// <see cref="LicenseKind.Server"/>, its <c>cnf.jkt</c> equal to the thumbprint of the key the server sent, and without
    /// the fingerprints, which belong to the server machine and which the server checks at start.
    /// </summary>
    /// <param name="verifier">The verifier, with the vendor's trust store.</param>
    /// <param name="licenseFile">The server license file.</param>
    /// <param name="publicKeyJwk">The server's public key as a JWK.</param>
    /// <param name="productId">The client's product ID.</param>
    /// <param name="identity">The identity, or <see langword="null"/> on failure.</param>
    /// <param name="error">Why it failed: the license verification error, <c>ELM_MALFORMED</c> for a key that is not an EC
    /// P-256 JWK, or <c>ELM_SERVER_PIN_MISMATCH</c> for a key that is not the license's <c>cnf.jkt</c>.</param>
    /// <returns><see langword="true"/> when the license verified and the identity was created.</returns>
    public static bool TryVerify(
        LicenseVerifier verifier,
        ReadOnlySpan<byte> licenseFile,
        ReadOnlySpan<byte> publicKeyJwk,
        string productId,
        out ServerIdentity? identity,
        out LicenseError? error
    )
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentException.ThrowIfNullOrEmpty(productId);
        identity = null;
        if (!TryReadJwk(publicKeyJwk, out ECParameters parameters))
        {
            error = new LicenseError(ErrorCode.Malformed, "The server key is not an EC P-256 public key in JWK form.");
            return false;
        }

        var requirement = new LicenseRequirement(
            productId,
            LicenseKind.Server,
            null,
            null,
            InstallationIdentity.ForRemoteServer(EcPublicJwk.Thumbprint(parameters))
        );
        switch (verifier.Verify(licenseFile, requirement))
        {
            case LicenseVerificationResult.Valid valid:
                return TryCreate(valid.License, publicKeyJwk, out identity, out error);
            case LicenseVerificationResult.Invalid { Error.Code: ErrorCode.WrongInstallation }:
                error = new LicenseError(ErrorCode.ServerPinMismatch, "The server key is not the key of the server license.");
                return false;
            case LicenseVerificationResult.Invalid invalid:
                error = invalid.Error;
                return false;
            default:
                throw new InvalidOperationException("Unknown license verification result.");
        }
    }

    /// <summary>
    /// Releases the server's key. Leases are not checked with this identity afterwards.
    /// </summary>
    public void Dispose() => Key.Dispose();

    private static bool TryReadJwk(ReadOnlySpan<byte> publicKeyJwk, out ECParameters parameters)
    {
        parameters = default;
        if (publicKeyJwk.Length > 1024)
        {
            return false;
        }

        EcPublicJwk? jwk;
        try
        {
            jwk = JsonSerializer.Deserialize(publicKeyJwk, LicensingJsonContext.Default.EcPublicJwk);
        }
        catch (JsonException)
        {
            return false;
        }

        return jwk is not null && jwk.TryGetParameters(out parameters);
    }
}

/// <summary>
/// What the client expects of a lease: its product and its installation key (design §12.7.5 step 2).
/// </summary>
/// <param name="ProductId">The product ID.</param>
/// <param name="InstallationKeyThumbprint">The thumbprint of this installation's key.</param>
public sealed record LeaseExpectation(string ProductId, string InstallationKeyThumbprint);

/// <summary>
/// A lease token that passed the checks of design §12.7.5.
/// </summary>
public sealed class VerifiedLease
{
    internal VerifiedLease(LeasePayload payload, string token)
    {
        LeaseId = payload.Lse;
        LicenseId = payload.Lic;
        ProductId = payload.Prd;
        FeatureId = payload.Feat;
        Seats = payload.N;
        IsBorrow = payload.Brw;
        ServerKeyId = payload.Srv;
        ServerLicenseTokenId = payload.Slic;
        Sequence = payload.Seq;
        IssuedAt = DateTimeOffset.FromUnixTimeSeconds(payload.Iat);
        ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.Exp);
        Token = token;
    }

    /// <summary>Gets the lease ID.</summary>
    public string LeaseId { get; }

    /// <summary>Gets the license ID (on premises, the server license's).</summary>
    public string LicenseId { get; }

    /// <summary>Gets the product ID.</summary>
    public string ProductId { get; }

    /// <summary>Gets the feature ID.</summary>
    public string FeatureId { get; }

    /// <summary>Gets the number of seats leased.</summary>
    public int Seats { get; }

    /// <summary>Gets a value indicating whether the lease is a borrow.</summary>
    public bool IsBorrow { get; }

    /// <summary>Gets the key ID of the server that issued the lease.</summary>
    public string ServerKeyId { get; }

    /// <summary>Gets the token ID of the server license the lease was issued under, or <see langword="null"/> for hosted leases.</summary>
    public string? ServerLicenseTokenId { get; }

    /// <summary>Gets the lease sequence number (design §10.4).</summary>
    public long Sequence { get; }

    /// <summary>Gets the time the lease was issued.</summary>
    public DateTimeOffset IssuedAt { get; }

    /// <summary>Gets the lease expiry.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>Gets the lease token, to present on renewal, return, and reclaim.</summary>
    public string Token { get; }
}

/// <summary>
/// The outcome of a lease check: either <see cref="Valid"/> or <see cref="Invalid"/>.
/// </summary>
public abstract record LeaseVerificationResult
{
    private LeaseVerificationResult() { }

    /// <summary>
    /// The lease passed every check.
    /// </summary>
    /// <param name="Lease">The verified lease.</param>
    public sealed record Valid(VerifiedLease Lease) : LeaseVerificationResult;

    /// <summary>
    /// The lease failed a check.
    /// </summary>
    /// <param name="Error">The error.</param>
    public sealed record Invalid(LicenseError Error) : LeaseVerificationResult;
}
