using System.Security.Cryptography;
using ElasticLM.Licensing;

namespace ElasticLM.Issuing;

/// <summary>
/// What a lease token says (design §8.4.3).
/// </summary>
public sealed record LeaseSpec
{
    /// <summary>Gets the lease ID.</summary>
    public required string LeaseId { get; init; }

    /// <summary>Gets the product ID.</summary>
    public required string ProductId { get; init; }

    /// <summary>Gets the license ID (on premises, the server license's).</summary>
    public required string LicenseId { get; init; }

    /// <summary>Gets the feature ID.</summary>
    public required string FeatureId { get; init; }

    /// <summary>Gets the number of seats, at least 1.</summary>
    public required int Seats { get; init; }

    /// <summary>Gets the installation key thumbprint the lease is bound to.</summary>
    public required string InstallationKeyThumbprint { get; init; }

    /// <summary>Gets the lease duration.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Gets a value indicating whether the lease is a borrow.</summary>
    public bool IsBorrow { get; init; }

    /// <summary>Gets the token ID of the server license the lease is issued under (on premises), or <see langword="null"/>.</summary>
    public string? ServerLicenseTokenId { get; init; }

    /// <summary>Gets the lease sequence number (design §10.4).</summary>
    public long Sequence { get; init; }

    /// <summary>Gets the <c>jti</c> of the proof of the request the token answers, 1 to 64 characters (design §8.4.3, §12.2).
    /// The client that sent the request refuses a token that names another proof (design §12.7.5).</summary>
    public required string AnsweredProofId { get; init; }
}

/// <summary>
/// Issues lease tokens (design §8.4.3). The signer is an on-premises server's installation key, or the hosted service's
/// signing key certified for <c>lease</c>.
/// </summary>
public static class LeaseIssuer
{
    /// <summary>
    /// Issues a lease token.
    /// </summary>
    /// <param name="signingKey">The key that signs, with its private part. Its key ID becomes <c>iss</c> and <c>srv</c>.</param>
    /// <param name="spec">The lease.</param>
    /// <param name="time">The time provider, for the issue time.</param>
    /// <returns>The lease token.</returns>
    /// <exception cref="ArgumentException">A required value is empty, the seat count is less than 1, the duration is not
    /// positive or longer than design §8.6 allows, the sequence number is negative, or the ID of the answered proof is not
    /// 1 to 64 characters.</exception>
    public static string Issue(ECDsa signingKey, LeaseSpec spec, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return Issue(signingKey, spec, time.GetUtcNow());
    }

    /// <summary>
    /// Issues a lease token with a given issue time. A server that caps the duration at a time it computed from the same
    /// clock reading uses this, so the cap holds even when the clock moves to the next second in between.
    /// </summary>
    /// <param name="signingKey">The key that signs, with its private part. Its key ID becomes <c>iss</c> and <c>srv</c>.</param>
    /// <param name="spec">The lease.</param>
    /// <param name="issuedAt">The issue time; its whole seconds become <c>iat</c>.</param>
    /// <returns>The lease token.</returns>
    /// <exception cref="ArgumentException">A required value is empty, the seat count is less than 1, the duration is not
    /// positive or longer than design §8.6 allows, the sequence number is negative, or the ID of the answered proof is not
    /// 1 to 64 characters.</exception>
    public static string Issue(ECDsa signingKey, LeaseSpec spec, DateTimeOffset issuedAt)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        ArgumentNullException.ThrowIfNull(spec);
        foreach (string? value in new[] { spec.LeaseId, spec.ProductId, spec.LicenseId, spec.FeatureId, spec.InstallationKeyThumbprint })
        {
            ArgumentException.ThrowIfNullOrEmpty(value, nameof(spec));
        }

        if (!Limits.IsLeaseId(spec.LeaseId))
        {
            // design §8.4.3.
            throw new ArgumentException("The lease ID must be 22 characters of base64url.", nameof(spec));
        }

        if (spec.Seats < 1 || spec.Sequence < 0)
        {
            throw new ArgumentException("The seat count must be at least 1 and the sequence number not negative.", nameof(spec));
        }

        if (!Limits.IsProofId(spec.AnsweredProofId))
        {
            // design §8.4.3 (V1d5): the form a proof's jti has (design §12.2).
            throw new ArgumentException("The ID of the answered proof must be 1 to 64 characters.", nameof(spec));
        }

        long seconds = (long)Math.Floor(spec.Duration.TotalSeconds);
        if (seconds < 1 || !Limits.IsDuration(seconds))
        {
            // The limit of design §8.6: Limits.MaxDurationSeconds, 100 years of 366 days.
            throw new ArgumentException("The lease duration must be at least one second and at most 100 years.", nameof(spec));
        }

        long now = issuedAt.ToUnixTimeSeconds();
        if (!Limits.IsTime(now + seconds))
        {
            // The times of design §8.6: from 1970-01-01T00:00:00Z to Limits.MaxUnixSeconds, 9999-12-31T23:59:59Z.
            throw new ArgumentException("The lease must end between 1970 and 9999.", nameof(spec));
        }

        string kid = KeyFiles.KeyId(signingKey);
        var payload = new LeasePayload
        {
            Ver = 1,
            Iss = kid,
            Iat = now,
            Exp = now + seconds,
            Jti = JwsWriter.NewTokenId(),
            Lse = spec.LeaseId,
            Prd = spec.ProductId,
            Lic = spec.LicenseId,
            Feat = spec.FeatureId,
            N = spec.Seats,
            Cnf = new ConfirmationClaim { Jkt = spec.InstallationKeyThumbprint },
            Brw = spec.IsBorrow,
            Srv = kid,
            Slic = spec.ServerLicenseTokenId,
            Seq = spec.Sequence,
            Areq = spec.AnsweredProofId,
        };
        return JwsWriter.Sign(signingKey, TokenTypes.Lease, payload, LicensingJsonContext.Default.LeasePayload);
    }
}
