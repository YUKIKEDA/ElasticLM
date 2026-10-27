namespace ElasticLM.Service;

/// <content>
/// Products, features, and policies (design §9.1, §9.3, §14.5, M4a2). Each change is written with its audit row in one
/// transaction, and a change reads the row it changes in that transaction, so two changes of different members at once both
/// stay (design §14.5, decision 0.87). Nothing is deleted.
/// </content>
internal abstract partial class Database
{
    /// <summary>Stores a product with its audit row.</summary>
    /// <returns><see langword="false"/> when another product has the code; nothing is written then.</returns>
    public abstract Task<bool> CreateProductAsync(ProductRow product, AuditEntry audit, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Returns a product, or <see langword="null"/>.</summary>
    public abstract Task<ProductRow?> FindProductAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Returns products after an ID, in ID order.</summary>
    public abstract Task<IReadOnlyList<ProductRow>> ListProductsAsync(Guid? after, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Changes a product in one write transaction: locks and reads its row, applies <paramref name="change"/>, and writes the
    /// result with the audit row that <paramref name="audit"/> makes from the row before and after.
    /// </summary>
    /// <returns>The changed product, or <see langword="null"/> when none has the ID.</returns>
    public abstract Task<ProductRow?> UpdateProductAsync(
        Guid id,
        Func<ProductRow, ProductRow> change,
        Func<ProductRow, ProductRow, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );

    /// <summary>Stores a feature with its audit row.</summary>
    /// <returns><see langword="false"/> when another feature of the product has the code; nothing is written then.</returns>
    public abstract Task<bool> CreateFeatureAsync(FeatureRow feature, AuditEntry audit, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Returns a feature of a product, or <see langword="null"/>.</summary>
    public abstract Task<FeatureRow?> FindFeatureAsync(Guid productId, Guid featureId, CancellationToken cancellationToken);

    /// <summary>Returns a product's features after an ID, in ID order.</summary>
    public abstract Task<IReadOnlyList<FeatureRow>> ListFeaturesAsync(Guid productId, Guid? after, int limit, CancellationToken cancellationToken);

    /// <summary>Changes a feature of a product in one write transaction, as <see cref="UpdateProductAsync"/> does.</summary>
    /// <returns>The changed feature, or <see langword="null"/> when the product has no feature with the ID.</returns>
    public abstract Task<FeatureRow?> UpdateFeatureAsync(
        Guid productId,
        Guid featureId,
        Func<FeatureRow, FeatureRow> change,
        Func<FeatureRow, FeatureRow, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );

    /// <summary>Stores a policy with its audit row.</summary>
    public abstract Task CreatePolicyAsync(PolicyRow policy, AuditEntry audit, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Returns a policy, or <see langword="null"/>.</summary>
    public abstract Task<PolicyRow?> FindPolicyAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Returns policies after an ID, in ID order, of one product or of all.</summary>
    public abstract Task<IReadOnlyList<PolicyRow>> ListPoliciesAsync(Guid? productId, Guid? after, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Changes a policy in one write transaction, as <see cref="UpdateProductAsync"/> does. Its product, kind, and binding
    /// are not written. <paramref name="change"/> may throw to refuse the change; nothing is written then.
    /// </summary>
    /// <returns>The changed policy, or <see langword="null"/> when none has the ID.</returns>
    public abstract Task<PolicyRow?> UpdatePolicyAsync(
        Guid id,
        Func<PolicyRow, PolicyRow> change,
        Func<PolicyRow, PolicyRow, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );
}
