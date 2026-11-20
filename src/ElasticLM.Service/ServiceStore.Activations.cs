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
        return InWriteTransactionAsync(
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

    // design §12.6.1 (V1d26): the key's activations still at sequence 1 whose file a request named in retry answered, found
    // through activations_by_key (migration 0011), then the one of a trial of the product. A key can hold two trials of a
    // product (a trial record removed by an administrator, or one fingerprint a trial), so the retry, not the product, picks
    // it. As one query with the product's trials in a subquery, SQLite 3.54.1 without statistics read every trial of the
    // product and looked the key up in each, and PostgreSQL 18 read every activation without the index (EXPLAIN QUERY PLAN,
    // EXPLAIN ANALYZE).
    public override async Task<ActivationRow?> FindTrialRetryAsync(
        Guid productId,
        string keyId,
        IReadOnlyCollection<string> retry,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(retry);
        if (retry.Count == 0)
        {
            return null;
        }

        string[] named = [.. retry];
        List<ActivationRow> answered = await _db.From(ActivationRow.Table)
            .Where(a => And(Eq(a.KeyId, keyId), Eq(a.Seq, 1L), In(a.LastProof, named)))
            .Select(a => a)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (answered.Count == 0)
        {
            return null;
        }

        Guid[] licenses = [.. answered.Select(a => a.LicenseId)];
        List<Guid> trials = await _db.From(TrialRow.Table)
            .Where(t => And(Eq(t.ProductId, productId), In(t.LicenseId, licenses)))
            .Select(t => t.LicenseId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return answered.FirstOrDefault(a => trials.Contains(a.LicenseId));
    }

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

    public override async Task<TrialWrite> CreateTrialAsync(
        LicenseRow license,
        ActivationRow activation,
        IReadOnlyList<string> fingerprints,
        long dailyLimit,
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
            await InWriteTransactionAsync(
                    async tx =>
                    {
                        // design §12.9 (V1d17): a self-assignment locks the product's row on PostgreSQL, so the trials of one
                        // product are counted and written one at a time; SQLite holds the write lock (design §14.4).
                        await tx.Update(ProductRow.Table)
                            .Set(p => [Set(p.Name, p.Name)])
                            .Where(p => Eq(p.Id, license.ProductId))
                            .ExecuteAsync(cancellationToken)
                            .ConfigureAwait(false);
                        long since = license.CreatedAt - Policies.Day;
                        long made = await tx.From(TrialRow.Table)
                            .Where(t => And(Eq(t.ProductId, license.ProductId), Gt(t.CreatedAt, since)))
                            .Select(t => Count())
                            .SingleAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (made >= dailyLimit)
                        {
                            // At the limit nothing is written: a fingerprint that had a trial is still told first (design
                            // §12.6.1, the order of V1d17's question 17-2). The product's lock keeps a trial with the same
                            // fingerprint from being written between this read and the answer.
                            long used = await tx.From(TrialFingerprintRow.Table)
                                .Where(f => And(Eq(f.ProductId, license.ProductId), In(f.Fingerprint, fingerprints)))
                                .Select(f => Count())
                                .SingleAsync(cancellationToken)
                                .ConfigureAwait(false);
                            if (used > 0)
                            {
                                throw new TrialUsedException();
                            }

                            // A place frees when the (made − limit + 1)-th oldest trial of the 24 hours leaves them: the oldest
                            // one when made is the limit, a later one when the limit was lowered below the count. Every trial
                            // counted is later than since, so the seconds are at least 1.
                            List<long> freeing = await tx.From(TrialRow.Table)
                                .Where(t => And(Eq(t.ProductId, license.ProductId), Gt(t.CreatedAt, since)))
                                .Select(t => t.CreatedAt)
                                .OrderBy(t => [Asc(t.CreatedAt)])
                                .Limit(1)
                                .Offset(checked((int)(made - dailyLimit)))
                                .ToListAsync(cancellationToken)
                                .ConfigureAwait(false);
                            throw new TrialLimitedException(freeing[0] + Policies.Day - license.CreatedAt);
                        }

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
            return new TrialWrite.Created();
        }
        catch (TrialUsedException)
        {
            return new TrialWrite.Used();
        }
        catch (TrialLimitedException e)
        {
            return new TrialWrite.Limited(e.RetryAfter);
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
        InWriteTransactionAsync(
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

    public override Task<LeaseRow?> FindLeaseAsync(string id, long now, CancellationToken cancellationToken) =>
        _db.From(LeaseRow.Table).Where(l => And(Eq(l.Id, id), Gt(l.ExpiresAt, now))).Select(l => l).SingleOrDefaultAsync(cancellationToken);

    // design §12.7.7: found through leases_by_key (migration 0010). Whether the row is still sequence 1 (no renewal since)
    // and has not expired is judged when it is read again under the license's lock, which a renewal also takes.
    public override async Task<LeaseRow?> FindLeaseByRequestAsync(string keyId, string requestId, CancellationToken cancellationToken) =>
        (
            await _db.From(LeaseRow.Table)
                .Where(l => And(Eq(l.KeyId, keyId), Eq(l.IssuedBy, requestId)))
                .Select(l => l)
                .OrderBy(l => [Asc(l.Id)])
                .Limit(1)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false)
        ).FirstOrDefault();

    public override async Task<IReadOnlyList<LeaseRow>> ListLeasesAsync(
        Guid licenseId,
        string? after,
        int limit,
        long now,
        CancellationToken cancellationToken
    ) =>
        await _db.From(LeaseRow.Table)
            .Where(l => And([Eq(l.LicenseId, licenseId), Gt(l.ExpiresAt, now), after is string id ? Gt(l.Id, id) : null]))
            .Select(l => l)
            .OrderBy(l => [Asc(l.Id)])
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public override async Task<IReadOnlyList<LeaseRow>> RemoveOldLeasesAsync(long now, long returnsBefore, CancellationToken cancellationToken)
    {
        List<LeaseRow> leases = await _db.Delete(LeaseRow.Table)
            .Where(l => Le(l.ExpiresAt, now))
            .Returning(l => l)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        await _db.Delete(LeaseReturnRow.Table).Where(r => Lt(r.ReturnedAt, returnsBefore)).ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return leases;
    }

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

    // Stops a trial's transaction when the product has made its limit of trials in the last 24 hours (design §12.9, V1d17).
    private sealed class TrialLimitedException(long retryAfter) : Exception
    {
        public long RetryAfter { get; } = retryAfter;
    }

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

        public override Task<LeaseRow?> FindLeaseAsync(string id, long now, CancellationToken cancellationToken) =>
            tx.From(LeaseRow.Table)
                .Where(l => And([Eq(l.Id, id), Eq(l.LicenseId, license.Id), Gt(l.ExpiresAt, now)]))
                .Select(l => l)
                .SingleOrDefaultAsync(cancellationToken);

        // Summed in the database, so counting the seats does not read every lease row with its token.
        public override async Task<long> SeatsHeldAsync(string feature, long now, CancellationToken cancellationToken) =>
            await tx.From(LeaseRow.Table)
                .Where(l => And([Eq(l.LicenseId, license.Id), Eq(l.Feature, feature), Gt(l.ExpiresAt, now)]))
                .Select(l => Sum(l.Seats))
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false)
            ?? 0;

        public override Task InsertLeaseAsync(LeaseRow lease, CancellationToken cancellationToken) =>
            tx.Insert(LeaseRow.Table)
                .Values(
                    new LeaseRow.Insert
                    {
                        Id = lease.Id,
                        LicenseId = lease.LicenseId,
                        Feature = lease.Feature,
                        Seats = lease.Seats,
                        KeyId = lease.KeyId,
                        KeyX = lease.KeyX,
                        KeyY = lease.KeyY,
                        Seq = lease.Seq,
                        LastToken = lease.LastToken,
                        IssuedBy = lease.IssuedBy,
                        ExpiresAt = lease.ExpiresAt,
                        Borrow = lease.Borrow,
                        BorrowFingerprints = lease.BorrowFingerprints,
                        BorrowFeatureSeats = lease.BorrowFeatureSeats,
                        BorrowLimit = lease.BorrowLimit,
                        Stopped = lease.Stopped,
                        ClientHost = lease.ClientHost,
                        ClientUser = lease.ClientUser,
                        App = lease.App,
                        CreatedAt = lease.CreatedAt,
                    }
                )
                .ExecuteAsync(cancellationToken);

        public override async Task<bool> UpdateLeaseAsync(LeaseRow lease, CancellationToken cancellationToken) =>
            await tx.Update(LeaseRow.Table)
                .Set(l =>
                    [
                        Set(l.Seq, lease.Seq),
                        Set(l.LastToken, lease.LastToken),
                        Set(l.IssuedBy, lease.IssuedBy),
                        Set(l.ExpiresAt, lease.ExpiresAt),
                        Set(l.Stopped, lease.Stopped),
                    ]
                )
                .Where(l => Eq(l.Id, lease.Id))
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false) > 0;

        public override async Task<bool> DeleteLeaseAsync(string id, CancellationToken cancellationToken) =>
            await tx.Delete(LeaseRow.Table).Where(l => Eq(l.Id, id)).ExecuteAsync(cancellationToken).ConfigureAwait(false) > 0;

        public override async Task<int> CountReturnsAsync(IEnumerable<string> counters, long since, CancellationToken cancellationToken)
        {
            int most = 0;
            foreach (string counter in counters)
            {
                long count = await tx.From(LeaseReturnRow.Table)
                    .Where(r => And([Eq(r.LicenseId, license.Id), Eq(r.Counter, counter), Gt(r.ReturnedAt, since)]))
                    .Select(r => Count())
                    .SingleAsync(cancellationToken)
                    .ConfigureAwait(false);
                most = (int)Math.Max(most, count);
            }

            return most;
        }

        // design §12.7.6 (V1d26): one row a return and counter, also for two returns of one counter in the same second. The
        // lease's row was removed in the same transaction, so its ID is returned once.
        public override async Task RecordReturnAsync(IEnumerable<string> counters, string leaseId, long at, CancellationToken cancellationToken)
        {
            foreach (string counter in counters.Distinct(StringComparer.Ordinal))
            {
                await tx.Insert(LeaseReturnRow.Table)
                    .Values(
                        new LeaseReturnRow.Insert
                        {
                            LicenseId = license.Id,
                            Counter = counter,
                            ReturnedAt = at,
                            LeaseId = leaseId,
                        }
                    )
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        public override Task SetExpiryAsync(long expiresAt, long at, CancellationToken cancellationToken) =>
            tx.Update(LicenseRow.Table)
                .Set(l => [Set(l.ExpiresAt, expiresAt), Set(l.UpdatedAt, at)])
                .Where(l => Eq(l.Id, license.Id))
                .ExecuteAsync(cancellationToken);

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
