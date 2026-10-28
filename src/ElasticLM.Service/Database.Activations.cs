namespace ElasticLM.Service;

/// <summary>
/// The rows of one license inside a write transaction that holds its row locked (design §14.4): the activations are counted
/// and changed only here, so two activations of one license cannot both pass the limit.
/// </summary>
internal abstract class LicenseScope
{
    /// <summary>Gets the license as read under the lock.</summary>
    public abstract LicenseRow License { get; }

    /// <summary>Returns the license's activation with an ID, or <see langword="null"/>.</summary>
    public abstract Task<ActivationRow?> FindActivationAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Returns the license's activation of an installation key, or <see langword="null"/>.</summary>
    public abstract Task<ActivationRow?> FindActivationByKeyAsync(string keyId, CancellationToken cancellationToken);

    /// <summary>Returns the number of the license's activations.</summary>
    public abstract Task<long> CountActivationsAsync(CancellationToken cancellationToken);

    /// <summary>Adds an activation.</summary>
    public abstract Task InsertActivationAsync(ActivationRow activation, CancellationToken cancellationToken);

    /// <summary>Writes every column of an activation but its ID, license, and creation time.</summary>
    public abstract Task UpdateActivationAsync(ActivationRow activation, CancellationToken cancellationToken);

    /// <summary>Removes an activation.</summary>
    public abstract Task DeleteActivationAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Writes the license's expiry, deactivation count, and update time.</summary>
    public abstract Task UpdateLicenseAsync(LicenseRow license, CancellationToken cancellationToken);

    /// <summary>Appends an audit row in the same transaction.</summary>
    public abstract Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken);

    /// <summary>Returns the stored answers to offline requests with an installation key and a nonce (design §12.8.4).</summary>
    public abstract Task<IReadOnlyList<OfflineRequestRow>> FindOfflineRequestsAsync(string keyId, string nonce, CancellationToken cancellationToken);

    /// <summary>Stores the answer to an activation's offline request in the same transaction, in place of the one before.</summary>
    public abstract Task ReplaceOfflineRequestAsync(OfflineRequestRow request, CancellationToken cancellationToken);
}

/// <summary>A trial with the fingerprints it holds (design §12.9).</summary>
internal sealed record TrialRecord(TrialRow Trial, IReadOnlyList<string> Fingerprints);

/// <content>
/// Activations, trials, and the proofs' jti (design §10.4, §12.2, §12.4 to §12.6, §12.9, M4b1).
/// </content>
internal abstract partial class Database
{
    /// <summary>
    /// Runs <paramref name="work"/> in one write transaction that has locked the license with an ID, or with <see
    /// langword="null"/> when none has it. The transaction commits when the work returns and rolls back when it throws.
    /// </summary>
    public abstract Task<T> WithLicenseAsync<T>(Guid id, Func<LicenseScope?, Task<T>> work, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Returns the license whose key has a hash (design §9.5), or <see langword="null"/>.</summary>
    public abstract Task<LicenseRow?> FindLicenseByKeyAsync(byte[] keyHash, CancellationToken cancellationToken);

    /// <summary>Returns the product with a code (the tokens' <c>prd</c>), or <see langword="null"/>.</summary>
    public abstract Task<ProductRow?> FindProductByCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>Returns an installation key's activation of a trial of a product, or <see langword="null"/>.</summary>
    public abstract Task<ActivationRow?> FindTrialActivationAsync(Guid productId, string keyId, CancellationToken cancellationToken);

    /// <summary>Returns an activation, or <see langword="null"/>; read outside a transaction, to find its license.</summary>
    public abstract Task<ActivationRow?> FindActivationAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Returns a license's activations after an ID, in ID order.</summary>
    public abstract Task<IReadOnlyList<ActivationRow>> ListActivationsAsync(
        Guid licenseId,
        Guid? after,
        int limit,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Stores a trial in one transaction: its license, the trial, its activation, its fingerprints, and the audit row.
    /// </summary>
    /// <returns><see langword="false"/> when one of the fingerprints already had a trial of the product; nothing is written
    /// then.</returns>
    public abstract Task<bool> CreateTrialAsync(
        LicenseRow license,
        ActivationRow activation,
        IReadOnlyList<string> fingerprints,
        AuditEntry audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );

    /// <summary>Returns trials after a license ID, in ID order, of a product when one is given.</summary>
    public abstract Task<IReadOnlyList<TrialRecord>> ListTrialsAsync(Guid? productId, Guid? after, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a trial and its fingerprints with the audit row, in one transaction; its license and activation stay.
    /// </summary>
    /// <returns><see langword="false"/> when no trial has the license ID; nothing is written then.</returns>
    public abstract Task<bool> DeleteTrialAsync(Guid licenseId, AuditEntry audit, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Records a proof's jti until a time (design §12.2 rule 5).</summary>
    /// <returns><see langword="false"/> when the jti is already recorded.</returns>
    public abstract Task<bool> RecordProofAsync(string jti, long expiresAt, CancellationToken cancellationToken);

    /// <summary>Removes the jti records that expired before a time.</summary>
    /// <returns>The records removed.</returns>
    public abstract Task<long> RemoveProofsBeforeAsync(long before, CancellationToken cancellationToken);

    /// <summary>Removes the stored answers of offline requests that expired before a time (design §12.8.4).</summary>
    /// <returns>The rows removed.</returns>
    public abstract Task<long> RemoveOfflineRequestsBeforeAsync(long before, CancellationToken cancellationToken);
}
