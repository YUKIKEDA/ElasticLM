namespace ElasticLM.Service;

/// <content>
/// Products, features, and policies (design §9.1, §9.3, §14.5, M4a2). Each change is written with its audit row in one
/// transaction. Nothing is deleted (decision 0.87).
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

    /// <summary>Writes a product's name and trial policy with the audit row.</summary>
    public abstract Task UpdateProductAsync(ProductRow product, AuditEntry audit, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Stores a feature with its audit row.</summary>
    /// <returns><see langword="false"/> when another feature of the product has the code; nothing is written then.</returns>
    public abstract Task<bool> CreateFeatureAsync(FeatureRow feature, AuditEntry audit, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Returns a feature of a product, or <see langword="null"/>.</summary>
    public abstract Task<FeatureRow?> FindFeatureAsync(Guid productId, Guid featureId, CancellationToken cancellationToken);

    /// <summary>Returns a product's features after an ID, in ID order.</summary>
    public abstract Task<IReadOnlyList<FeatureRow>> ListFeaturesAsync(Guid productId, Guid? after, int limit, CancellationToken cancellationToken);

    /// <summary>Writes a feature's name with the audit row.</summary>
    public abstract Task UpdateFeatureAsync(FeatureRow feature, AuditEntry audit, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Stores a policy with its audit row.</summary>
    public abstract Task CreatePolicyAsync(PolicyRow policy, AuditEntry audit, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Returns a policy, or <see langword="null"/>.</summary>
    public abstract Task<PolicyRow?> FindPolicyAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Returns policies after an ID, in ID order, of one product or of all.</summary>
    public abstract Task<IReadOnlyList<PolicyRow>> ListPoliciesAsync(Guid? productId, Guid? after, int limit, CancellationToken cancellationToken);

    /// <summary>Writes every rule of a policy but its product, kind, and binding, with the audit row.</summary>
    public abstract Task UpdatePolicyAsync(PolicyRow policy, AuditEntry audit, DateTimeOffset now, CancellationToken cancellationToken);
}
