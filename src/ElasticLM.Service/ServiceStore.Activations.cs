using AuraORM;
using static AuraORM.Sql;

namespace ElasticLM.Service;

/// <content>
/// Activations, trials, and the proofs' jti (M4b1).
/// </content>
internal sealed partial class ServiceStore<TDialect>
{
    public override Task<T> WithLicenseAsync<T>(Guid id, Func<LicenseScope?, Task<T>> work, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        return _db.InTransactionAsync(
            async tx =>
            {
                // A self-assignment locks the row on PostgreSQL, as SELECT … FOR UPDATE would; SQLite holds the write lock (design §14.4).
                LicenseRow? license = await tx.Update(LicenseRow.Table)
                    .Set(l => [Set(l.UpdatedAt, l.UpdatedAt)])
                    .Where(l => Eq(l.Id, id))
                    .Returning(l => l)
                    .SingleOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                return await work(license is null ? null : new Scope(tx, license, now)).ConfigureAwait(false);
            },
            cancellationToken
        );
    }

    public override Task<LicenseRow?> FindLicenseByKeyAsync(byte[] keyHash, CancellationToken cancellationToken) =>
        _db.From(LicenseRow.Table).Where(l => Eq(l.KeyHash, keyHash)).Select(l => l).SingleOrDefaultAsync(cancellationToken);

    public override Task<ProductRow?> FindProductByCodeAsync(string code, CancellationToken cancellationToken) =>
        _db.From(ProductRow.Table).Where(p => Eq(p.Code, code)).Select(p => p).SingleOrDefaultAsync(cancellationToken);

    public override Task<ActivationRow?> FindTrialActivationAsync(Guid productId, string keyId, CancellationToken cancellationToken) =>
        _db.From(ActivationRow.Table)
            .Where(a =>
                And(Eq(a.KeyId, keyId), In(a.LicenseId, _db.From(TrialRow.Table).Where(t => Eq(t.ProductId, productId)).Select(t => t.LicenseId)))
            )
            .Select(a => a)
            .FirstOrDefaultAsync(cancellationToken);

    public override Task<ActivationRow?> FindActivationAsync(Guid id, CancellationToken cancellationToken) =>
        _db.From(ActivationRow.Table).Where(a => Eq(a.Id, id)).Select(a => a).SingleOrDefaultAsync(cancellationToken);

    public override async Task<IReadOnlyList<ActivationRow>> ListActivationsAsync(
        Guid licenseId,
        Guid? after,
        int limit,
        CancellationToken cancellationToken
    ) =>
        await _db.From(ActivationRow.Table)
            .Where(a => And([Eq(a.LicenseId, licenseId), after is Guid id ? Gt(a.Id, id) : null]))
            .Select(a => a)
            .OrderBy(a => [Asc(a.Id)])
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public override async Task<bool> CreateTrialAsync(
        LicenseRow license,
        ActivationRow activation,
        IReadOnlyList<string> fingerprints,
        AuditEntry audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(license);
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(fingerprints);
        try
        {
            await _db.InTransactionAsync(
                    async tx =>
                    {
                        await InsertLicenseAsync(tx, license, cancellationToken).ConfigureAwait(false);
                        await tx.Insert(TrialRow.Table)
                            .Values(
                                new TrialRow.Insert
                                {
                                    LicenseId = license.Id,
                                    ProductId = license.ProductId,
                                    CreatedAt = license.CreatedAt,
                                }
                            )
                            .ExecuteAsync(cancellationToken)
                            .ConfigureAwait(false);

                        // Two trials at once with a shared fingerprint: the second insert waits for the first and then does nothing.
                        // Both insert in the same order, so two trials that share two fingerprints cannot wait for each other.
                        foreach (string fingerprint in fingerprints.Order(StringComparer.Ordinal))
                        {
                            int inserted = await tx.Insert(TrialFingerprintRow.Table)
                                .Values(
                                    new TrialFingerprintRow.Insert
                                    {
                                        ProductId = license.ProductId,
                                        Fingerprint = fingerprint,
                                        LicenseId = license.Id,
                                    }
                                )
                                .OnConflict(f => [f.ProductId, f.Fingerprint])
                                .DoNothing()
                                .ExecuteAsync(cancellationToken)
                                .ConfigureAwait(false);
                            if (inserted == 0)
                            {
                                throw new TrialUsedException();
                            }
                        }

                        await InsertActivationAsync(tx, activation, cancellationToken).ConfigureAwait(false);
                        await AppendAsync(tx, audit, now, cancellationToken).ConfigureAwait(false);
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
            return true;
        }
        catch (TrialUsedException)
        {
            return false;
        }
    }

    public override async Task<IReadOnlyList<TrialRecord>> ListTrialsAsync(
        Guid? productId,
        Guid? after,
        int limit,
        CancellationToken cancellationToken
    )
    {
        List<TrialRow> trials = await _db.From(TrialRow.Table)
            .Where(t => And([productId is Guid product ? Eq(t.ProductId, product) : null, after is Guid id ? Gt(t.LicenseId, id) : null]))
            .Select(t => t)
            .OrderBy(t => [Asc(t.LicenseId)])
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (trials.Count == 0)
        {
            return [];
        }

        Guid[] ids = [.. trials.Select(t => t.LicenseId)];
        List<TrialFingerprintRow> prints = await _db.From(TrialFingerprintRow.Table)
            .Where(f => In(f.LicenseId, ids))
            .Select(f => f)
            .OrderBy(f => [Asc(f.Fingerprint)])
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ILookup<Guid, string> byTrial = prints.ToLookup(f => f.LicenseId, f => f.Fingerprint);
        return [.. trials.Select(t => new TrialRecord(t, [.. byTrial[t.LicenseId]]))];
    }

    public override Task<bool> DeleteTrialAsync(Guid licenseId, AuditEntry audit, DateTimeOffset now, CancellationToken cancellationToken) =>
        _db.InTransactionAsync(
            async tx =>
            {
                await tx.Delete(TrialFingerprintRow.Table)
                    .Where(f => Eq(f.LicenseId, licenseId))
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                int removed = await tx.Delete(TrialRow.Table)
                    .Where(t => Eq(t.LicenseId, licenseId))
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (removed == 0)
                {
                    return false;
                }

                await AppendAsync(tx, audit, now, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken
        );

    public override async Task<bool> RecordProofAsync(string jti, long expiresAt, CancellationToken cancellationToken) =>
        await _db.Insert(ProofJtiRow.Table)
            .Values(new ProofJtiRow.Insert { Jti = jti, ExpiresAt = expiresAt })
            .OnConflict(j => [j.Jti])
            .DoNothing()
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false) == 1;

    public override async Task<long> RemoveProofsBeforeAsync(long before, CancellationToken cancellationToken) =>
        await _db.Delete(ProofJtiRow.Table).Where(j => Lt(j.ExpiresAt, before)).ExecuteAsync(cancellationToken).ConfigureAwait(false);

    public override async Task<long> RemoveOfflineRequestsBeforeAsync(long before, CancellationToken cancellationToken) =>
        await _db.Delete(OfflineRequestRow.Table).Where(r => Lt(r.ExpiresAt, before)).ExecuteAsync(cancellationToken).ConfigureAwait(false);

    private static Task<int> InsertActivationAsync(Transaction<TDialect> tx, ActivationRow a, CancellationToken cancellationToken) =>
        tx.Insert(ActivationRow.Table)
            .Values(
                new ActivationRow.Insert
                {
                    Id = a.Id,
                    LicenseId = a.LicenseId,
                    KeyId = a.KeyId,
                    KeyX = a.KeyX,
                    KeyY = a.KeyY,
                    Fingerprints = a.Fingerprints,
                    ClientHost = a.ClientHost,
                    ClientUser = a.ClientUser,
                    App = a.App,
                    Seq = a.Seq,
                    LastFile = a.LastFile,
                    LastProof = a.LastProof,
                    BlockedAt = a.BlockedAt,
                    CreatedAt = a.CreatedAt,
                    UpdatedAt = a.UpdatedAt,
                }
            )
            .ExecuteAsync(cancellationToken);

    // Stops a trial's transaction when a fingerprint already had a trial, so everything it wrote rolls back.
    private sealed class TrialUsedException : Exception;

    private sealed class Scope(Transaction<TDialect> tx, LicenseRow license, DateTimeOffset now) : LicenseScope
    {
        public override LicenseRow License => license;

        public override Task<ActivationRow?> FindActivationAsync(Guid id, CancellationToken cancellationToken) =>
            tx.From(ActivationRow.Table)
                .Where(a => And(Eq(a.Id, id), Eq(a.LicenseId, license.Id)))
                .Select(a => a)
                .SingleOrDefaultAsync(cancellationToken);

        public override Task<ActivationRow?> FindActivationByKeyAsync(string keyId, CancellationToken cancellationToken) =>
            tx.From(ActivationRow.Table)
                .Where(a => And(Eq(a.LicenseId, license.Id), Eq(a.KeyId, keyId)))
                .Select(a => a)
                .SingleOrDefaultAsync(cancellationToken);

        public override Task<long> CountActivationsAsync(CancellationToken cancellationToken) =>
            tx.From(ActivationRow.Table).Where(a => Eq(a.LicenseId, license.Id)).Select(a => Count()).SingleAsync(cancellationToken);

        public override Task InsertActivationAsync(ActivationRow activation, CancellationToken cancellationToken) =>
            ServiceStore<TDialect>.InsertActivationAsync(tx, activation, cancellationToken);

        public override Task UpdateActivationAsync(ActivationRow activation, CancellationToken cancellationToken) =>
            tx.Update(ActivationRow.Table)
                .Set(a =>
                    [
                        Set(a.KeyX, activation.KeyX),
                        Set(a.KeyY, activation.KeyY),
                        Set(a.Fingerprints, activation.Fingerprints),
                        Set(a.ClientHost, activation.ClientHost),
                        Set(a.ClientUser, activation.ClientUser),
                        Set(a.App, activation.App),
                        Set(a.Seq, activation.Seq),
                        Set(a.LastFile, activation.LastFile),
                        Set(a.LastProof, activation.LastProof),
                        Set(a.BlockedAt, activation.BlockedAt),
                        Set(a.UpdatedAt, activation.UpdatedAt),
                    ]
                )
                .Where(a => Eq(a.Id, activation.Id))
                .ExecuteAsync(cancellationToken);

        public override Task DeleteActivationAsync(Guid id, CancellationToken cancellationToken) =>
            tx.Delete(ActivationRow.Table).Where(a => Eq(a.Id, id)).ExecuteAsync(cancellationToken);

        public override Task UpdateLicenseAsync(LicenseRow changed, CancellationToken cancellationToken) =>
            tx.Update(LicenseRow.Table)
                .Set(l => [Set(l.ExpiresAt, changed.ExpiresAt), Set(l.Deactivations, changed.Deactivations), Set(l.UpdatedAt, changed.UpdatedAt)])
                .Where(l => Eq(l.Id, license.Id))
                .ExecuteAsync(cancellationToken);

        public override Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken) =>
            AppendAsync(tx, entry, now, cancellationToken);

        public override async Task<IReadOnlyList<OfflineRequestRow>> FindOfflineRequestsAsync(
            string keyId,
            string nonce,
            CancellationToken cancellationToken
        ) =>
            await tx.From(OfflineRequestRow.Table)
                .Where(r => And(Eq(r.KeyId, keyId), Eq(r.Nonce, nonce)))
                .Select(r => r)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        public override async Task ReplaceOfflineRequestAsync(OfflineRequestRow request, CancellationToken cancellationToken)
        {
            await tx.Delete(OfflineRequestRow.Table)
                .Where(r => Eq(r.ActivationId, request.ActivationId))
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
            await tx.Insert(OfflineRequestRow.Table)
                .Values(
                    new OfflineRequestRow.Insert
                    {
                        ActivationId = request.ActivationId,
                        KeyId = request.KeyId,
                        Nonce = request.Nonce,
                        RequestHash = request.RequestHash,
                        LicenseId = request.LicenseId,
                        Response = request.Response,
                        ExpiresAt = request.ExpiresAt,
                    }
                )
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
