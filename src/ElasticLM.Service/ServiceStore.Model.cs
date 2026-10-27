using AuraORM;
using static AuraORM.Sql;

namespace ElasticLM.Service;

/// <content>
/// Products, features, and policies (M4a2). A used code is found by <c>INSERT … ON CONFLICT DO NOTHING</c>, so two creates
/// at once give one row and one refusal, on both engines.
/// </content>
internal sealed partial class ServiceStore<TDialect>
{
    public override Task<bool> CreateProductAsync(ProductRow product, AuditEntry audit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);
        return _db.InTransactionAsync(
            async tx =>
            {
                int inserted = await tx.Insert(ProductRow.Table)
                    .Values(
                        new ProductRow.Insert
                        {
                            Id = product.Id,
                            Code = product.Code,
                            Name = product.Name,
                            TrialPolicyId = product.TrialPolicyId,
                            CreatedAt = product.CreatedAt,
                        }
                    )
                    .OnConflict(p => [p.Code])
                    .DoNothing()
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (inserted == 0)
                {
                    return false;
                }

                await AppendAsync(tx, audit, now, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken
        );
    }

    public override Task<ProductRow?> FindProductAsync(Guid id, CancellationToken cancellationToken) =>
        _db.From(ProductRow.Table).Where(p => Eq(p.Id, id)).Select(p => p).SingleOrDefaultAsync(cancellationToken);

    public override async Task<IReadOnlyList<ProductRow>> ListProductsAsync(Guid? after, int limit, CancellationToken cancellationToken) =>
        await _db.From(ProductRow.Table)
            .Where(p => And([after is Guid id ? Gt(p.Id, id) : null]))
            .Select(p => p)
            .OrderBy(p => [Asc(p.Id)])
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public override Task<ProductRow?> UpdateProductAsync(
        Guid id,
        Func<ProductRow, ProductRow> change,
        Func<ProductRow, ProductRow, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(audit);
        return _db.InTransactionAsync(
            async tx =>
            {
                // A self-assignment locks the row on PostgreSQL, as SELECT … FOR UPDATE would; SQLite holds the write lock.
                ProductRow? before = await tx.Update(ProductRow.Table)
                    .Set(p => [Set(p.Name, p.Name)])
                    .Where(p => Eq(p.Id, id))
                    .Returning(p => p)
                    .SingleOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (before is null)
                {
                    return null;
                }

                ProductRow after = change(before);
                await tx.Update(ProductRow.Table)
                    .Set(p => [Set(p.Name, after.Name), Set(p.TrialPolicyId, after.TrialPolicyId)])
                    .Where(p => Eq(p.Id, id))
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                await AppendAsync(tx, audit(before, after), now, cancellationToken).ConfigureAwait(false);
                return after;
            },
            cancellationToken
        );
    }

    public override Task<bool> CreateFeatureAsync(FeatureRow feature, AuditEntry audit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return _db.InTransactionAsync(
            async tx =>
            {
                int inserted = await tx.Insert(FeatureRow.Table)
                    .Values(
                        new FeatureRow.Insert
                        {
                            Id = feature.Id,
                            ProductId = feature.ProductId,
                            Code = feature.Code,
                            Name = feature.Name,
                            CreatedAt = feature.CreatedAt,
                        }
                    )
                    .OnConflict(f => [f.ProductId, f.Code])
                    .DoNothing()
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (inserted == 0)
                {
                    return false;
                }

                await AppendAsync(tx, audit, now, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken
        );
    }

    public override Task<FeatureRow?> FindFeatureAsync(Guid productId, Guid featureId, CancellationToken cancellationToken) =>
        _db.From(FeatureRow.Table)
            .Where(f => And(Eq(f.Id, featureId), Eq(f.ProductId, productId)))
            .Select(f => f)
            .SingleOrDefaultAsync(cancellationToken);

    public override async Task<IReadOnlyList<FeatureRow>> ListFeaturesAsync(
        Guid productId,
        Guid? after,
        int limit,
        CancellationToken cancellationToken
    ) =>
        await _db.From(FeatureRow.Table)
            .Where(f => And([Eq(f.ProductId, productId), after is Guid id ? Gt(f.Id, id) : null]))
            .Select(f => f)
            .OrderBy(f => [Asc(f.Id)])
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public override Task<FeatureRow?> UpdateFeatureAsync(
        Guid productId,
        Guid featureId,
        Func<FeatureRow, FeatureRow> change,
        Func<FeatureRow, FeatureRow, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(audit);
        return _db.InTransactionAsync(
            async tx =>
            {
                FeatureRow? before = await tx.Update(FeatureRow.Table)
                    .Set(f => [Set(f.Name, f.Name)])
                    .Where(f => And(Eq(f.Id, featureId), Eq(f.ProductId, productId)))
                    .Returning(f => f)
                    .SingleOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (before is null)
                {
                    return null;
                }

                FeatureRow after = change(before);
                await tx.Update(FeatureRow.Table)
                    .Set(f => [Set(f.Name, after.Name)])
                    .Where(f => Eq(f.Id, featureId))
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                await AppendAsync(tx, audit(before, after), now, cancellationToken).ConfigureAwait(false);
                return after;
            },
            cancellationToken
        );
    }

    public override Task CreatePolicyAsync(PolicyRow policy, AuditEntry audit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return _db.InTransactionAsync(
            async tx =>
            {
                await tx.Insert(PolicyRow.Table)
                    .Values(
                        new PolicyRow.Insert
                        {
                            Id = policy.Id,
                            ProductId = policy.ProductId,
                            Name = policy.Name,
                            LicenseKind = policy.LicenseKind,
                            Binding = policy.Binding,
                            MaxActivations = policy.MaxActivations,
                            DeactivationLimit = policy.DeactivationLimit,
                            ExpirationBasis = policy.ExpirationBasis,
                            Duration = policy.Duration,
                            TokenLifetime = policy.TokenLifetime,
                            RefreshWindow = policy.RefreshWindow,
                            GracePeriod = policy.GracePeriod,
                            FingerprintMatching = policy.FingerprintMatching,
                            CloneStrategy = policy.CloneStrategy,
                            Floating = policy.Floating,
                            LeaseDuration = policy.LeaseDuration,
                            Overage = policy.Overage,
                            BorrowLimit = policy.BorrowLimit,
                            BorrowReturnLimit = policy.BorrowReturnLimit,
                            ClientInfoStrategy = policy.ClientInfoStrategy,
                            TrialDuration = policy.TrialDuration,
                            CreatedAt = policy.CreatedAt,
                            UpdatedAt = policy.UpdatedAt,
                        }
                    )
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                await AppendAsync(tx, audit, now, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken
        );
    }

    public override Task<PolicyRow?> FindPolicyAsync(Guid id, CancellationToken cancellationToken) =>
        _db.From(PolicyRow.Table).Where(p => Eq(p.Id, id)).Select(p => p).SingleOrDefaultAsync(cancellationToken);

    public override async Task<IReadOnlyList<PolicyRow>> ListPoliciesAsync(
        Guid? productId,
        Guid? after,
        int limit,
        CancellationToken cancellationToken
    ) =>
        await _db.From(PolicyRow.Table)
            .Where(p => And([productId is Guid product ? Eq(p.ProductId, product) : null, after is Guid id ? Gt(p.Id, id) : null]))
            .Select(p => p)
            .OrderBy(p => [Asc(p.Id)])
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public override Task<PolicyRow?> UpdatePolicyAsync(
        Guid id,
        Func<PolicyRow, PolicyRow> change,
        Func<PolicyRow, PolicyRow, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(audit);
        return _db.InTransactionAsync(
            async tx =>
            {
                PolicyRow? before = await tx.Update(PolicyRow.Table)
                    .Set(p => [Set(p.UpdatedAt, p.UpdatedAt)])
                    .Where(p => Eq(p.Id, id))
                    .Returning(p => p)
                    .SingleOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (before is null)
                {
                    return null;
                }

                PolicyRow policy = change(before);
                await tx.Update(PolicyRow.Table)
                    .Set(p =>
                        [
                            Set(p.Name, policy.Name),
                            Set(p.MaxActivations, policy.MaxActivations),
                            Set(p.DeactivationLimit, policy.DeactivationLimit),
                            Set(p.ExpirationBasis, policy.ExpirationBasis),
                            Set(p.Duration, policy.Duration),
                            Set(p.TokenLifetime, policy.TokenLifetime),
                            Set(p.RefreshWindow, policy.RefreshWindow),
                            Set(p.GracePeriod, policy.GracePeriod),
                            Set(p.FingerprintMatching, policy.FingerprintMatching),
                            Set(p.CloneStrategy, policy.CloneStrategy),
                            Set(p.Floating, policy.Floating),
                            Set(p.LeaseDuration, policy.LeaseDuration),
                            Set(p.Overage, policy.Overage),
                            Set(p.BorrowLimit, policy.BorrowLimit),
                            Set(p.BorrowReturnLimit, policy.BorrowReturnLimit),
                            Set(p.ClientInfoStrategy, policy.ClientInfoStrategy),
                            Set(p.TrialDuration, policy.TrialDuration),
                            Set(p.UpdatedAt, policy.UpdatedAt),
                        ]
                    )
                    .Where(p => Eq(p.Id, id))
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                await AppendAsync(tx, audit(before, policy), now, cancellationToken).ConfigureAwait(false);
                return policy;
            },
            cancellationToken
        );
    }
}
