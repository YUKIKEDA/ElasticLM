using AuraORM;
using static AuraORM.Sql;

namespace ElasticLM.Service;

/// <content>
/// Licenses (M4a3).
/// </content>
internal sealed partial class ServiceStore<TDialect>
{
    public override Task CreateLicenseAsync(LicenseRow license, AuditEntry audit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(license);
        return _db.InTransactionAsync(
            async tx =>
            {
                await InsertLicenseAsync(tx, license, cancellationToken).ConfigureAwait(false);
                await AppendAsync(tx, audit, now, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken
        );
    }

    public override Task<LicenseRow?> FindLicenseAsync(Guid id, CancellationToken cancellationToken) =>
        _db.From(LicenseRow.Table).Where(l => Eq(l.Id, id)).Select(l => l).SingleOrDefaultAsync(cancellationToken);

    public override async Task<IReadOnlyList<LicenseRow>> ListLicensesAsync(
        Guid? productId,
        Guid? policyId,
        string? state,
        long now,
        Guid? after,
        int limit,
        CancellationToken cancellationToken
    ) =>
        // The state filter follows Licenses.State: revoked, then suspended, then expired, then active.
        await _db.From(LicenseRow.Table)
            .Where(l =>
                And([
                    productId is Guid product ? Eq(l.ProductId, product) : null,
                    policyId is Guid policy ? Eq(l.PolicyId, policy) : null,
                    after is Guid id ? Gt(l.Id, id) : null,
                    state switch
                    {
                        Licenses.Revoked => IsNotNull(l.RevokedAt),
                        Licenses.Suspended => And(IsNull(l.RevokedAt), IsNotNull(l.SuspendedAt)),
                        Licenses.Expired => And(IsNull(l.RevokedAt), IsNull(l.SuspendedAt), Le(l.ExpiresAt, now)),
                        Licenses.Active => And(IsNull(l.RevokedAt), IsNull(l.SuspendedAt), Or(IsNull(l.ExpiresAt), Gt(l.ExpiresAt, now))),
                        _ => null,
                    },
                ])
            )
            .Select(l => l)
            .OrderBy(l => [Asc(l.Id)])
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public override Task<LicenseRow?> UpdateLicenseAsync(
        Guid id,
        Func<LicenseRow, LicenseRow> change,
        Func<LicenseRow, LicenseRow, AuditEntry> audit,
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
                LicenseRow? before = await tx.Update(LicenseRow.Table)
                    .Set(l => [Set(l.UpdatedAt, l.UpdatedAt)])
                    .Where(l => Eq(l.Id, id))
                    .Returning(l => l)
                    .SingleOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (before is null)
                {
                    return null;
                }

                LicenseRow after = change(before);
                await tx.Update(LicenseRow.Table)
                    .Set(l =>
                        [
                            Set(l.Name, after.Name),
                            Set(l.MaxActivations, after.MaxActivations),
                            Set(l.ExpiresAt, after.ExpiresAt),
                            Set(l.SuspendedAt, after.SuspendedAt),
                            Set(l.RevokedAt, after.RevokedAt),
                            Set(l.Features, after.Features),
                            Set(l.Meta, after.Meta),
                            Set(l.Deactivations, after.Deactivations),
                            Set(l.UpdatedAt, after.UpdatedAt),
                        ]
                    )
                    .Where(l => Eq(l.Id, id))
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                await AppendAsync(tx, audit(before, after), now, cancellationToken).ConfigureAwait(false);
                return after;
            },
            cancellationToken
        );
    }

    private static Task<int> InsertLicenseAsync(Transaction<TDialect> tx, LicenseRow license, CancellationToken cancellationToken) =>
        tx.Insert(LicenseRow.Table)
            .Values(
                new LicenseRow.Insert
                {
                    Id = license.Id,
                    ProductId = license.ProductId,
                    PolicyId = license.PolicyId,
                    Name = license.Name,
                    KeyHash = license.KeyHash,
                    MaxActivations = license.MaxActivations,
                    ExpiresAt = license.ExpiresAt,
                    SuspendedAt = license.SuspendedAt,
                    RevokedAt = license.RevokedAt,
                    Features = license.Features,
                    Meta = license.Meta,
                    Deactivations = license.Deactivations,
                    CreatedAt = license.CreatedAt,
                    UpdatedAt = license.UpdatedAt,
                }
            )
            .ExecuteAsync(cancellationToken);
}
