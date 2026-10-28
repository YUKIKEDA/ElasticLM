namespace ElasticLM.Service;

/// <content>
/// Licenses (design §9.3 to §9.5, §14.5, M4a3). Each change is written with its audit row in one transaction, and a change
/// reads the row it changes in that transaction.
/// </content>
internal abstract partial class Database
{
    /// <summary>Stores a license with its audit row.</summary>
    public abstract Task CreateLicenseAsync(LicenseRow license, AuditEntry audit, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Returns a license, or <see langword="null"/>.</summary>
    public abstract Task<LicenseRow?> FindLicenseAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Returns licenses after an ID, in ID order, of a product, of a policy, and in a state (design §14.5), each filter left
    /// out when <see langword="null"/>. The state is computed at <paramref name="now"/> as <see cref="Licenses.State"/> does.
    /// </summary>
    public abstract Task<IReadOnlyList<LicenseRow>> ListLicensesAsync(
        Guid? productId,
        Guid? policyId,
        string? state,
        long now,
        Guid? after,
        int limit,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Changes a license in one write transaction: locks and reads its row, applies <paramref name="change"/> (which may
    /// throw to refuse), and writes the result with the audit row that <paramref name="audit"/> makes from the row before and
    /// after. The product, policy, key, and creation time are not written.
    /// </summary>
    /// <returns>The changed license, or <see langword="null"/> when none has the ID.</returns>
    public abstract Task<LicenseRow?> UpdateLicenseAsync(
        Guid id,
        Func<LicenseRow, LicenseRow> change,
        Func<LicenseRow, LicenseRow, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );
}
