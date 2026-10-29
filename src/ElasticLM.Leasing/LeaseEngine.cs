using System.Collections.Concurrent;
using System.Security.Cryptography;
using ElasticLM.Issuing;
using ElasticLM.Licensing;

namespace ElasticLM.Leasing;

/// <summary>
/// The lease rules of an on-premises server, with the state in memory (design §12.7, §10.4, §13.1–§13.4): seats with
/// overage, acquire, renew, release, borrow and early return, clone detection by sequence number, reclaim after a restart,
/// borrows restored from their record, and a server license replaced while the server runs.
/// </summary>
/// <remarks>
/// Each feature has one lock; counting and adding happen inside it (design §13.3), so requests for other features do not wait.
/// Replacing the server license or restoring the record waits for every request and holds them off while it runs. The engine
/// is safe to call from several threads.
/// </remarks>
public sealed class LeaseEngine : IDisposable
{
    /// <summary>The most lease tokens one reclaim request may carry (design §8.6).</summary>
    public const int MaxReclaimTokens = 64;

    /// <summary>The most proof IDs a renewal may name as earlier attempts (design §8.6).</summary>
    public const int MaxRetryProofIds = 16;

    private readonly LeaseSigner _sign;
    private readonly LeaseVerifier _verifier;
    private readonly TimeProvider _time;
    private readonly LeaseEngineOptions _options;
    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);
    private readonly ConcurrentDictionary<string, LeaseRecord> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTimeOffset>> _returns = new(StringComparer.Ordinal);
    private readonly Lock _returnsLock = new();
    private readonly DateTimeOffset _recoveryEnds;
    private LicenseState _state;
    private volatile bool _recoveryOver;

    /// <summary>
    /// Initializes a new instance of the <see cref="LeaseEngine"/> class. The reclaim window (design §13.4) starts now and
    /// lasts the server license's lease duration.
    /// </summary>
    /// <param name="server">The server's own verified server license and public key.</param>
    /// <param name="sign">Signs lease tokens with the server's installation key.</param>
    /// <param name="verifier">Checks reclaimed lease tokens as a client would (design §12.7.5).</param>
    /// <param name="time">The time provider.</param>
    /// <param name="options">The operator's borrow settings and the record of borrows, or <see langword="null"/>.</param>
    public LeaseEngine(ServerIdentity server, LeaseSigner sign, LeaseVerifier verifier, TimeProvider time, LeaseEngineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(sign);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(time);
        _options = options ?? new LeaseEngineOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(_options.BorrowReserve, nameof(options));
        _sign = sign;
        _verifier = verifier;
        _time = time;
        _state = new LicenseState(server);
        _recoveryEnds = time.GetUtcNow() + _state.Lease;
    }

    /// <summary>Gets the end of the reclaim window, unless every seat was reclaimed earlier.</summary>
    public DateTimeOffset RecoveryEndsAt => _recoveryEnds;

    /// <summary>Gets the number of leases the engine holds, including expired ones not yet purged.</summary>
    public int LeaseCount => _leases.Count;

    /// <summary>Gets the server identity the engine issues under.</summary>
    public ServerIdentity Server => Volatile.Read(ref _state).Server;

    /// <summary>
    /// Returns whether the reclaim window is still open: new leases are refused until it closes (design §13.4).
    /// </summary>
    /// <returns><see langword="true"/> while the window is open.</returns>
    public bool IsRecovering() => !_recoveryOver && _time.GetUtcNow() < _recoveryEnds;

    /// <summary>
    /// Returns the seats held by unexpired leases of a feature, including stopped clones and borrows.
    /// </summary>
    /// <param name="featureId">The feature ID.</param>
    /// <returns>The seat count; 0 for a feature no lease holds.</returns>
    public long SeatsInUse(string featureId) => Usage().FirstOrDefault(u => u.FeatureId == featureId)?.InUse ?? 0;

    /// <summary>
    /// Returns the use of every feature of the server license, and of features it no longer grants while leases hold them.
    /// </summary>
    /// <returns>One entry per feature.</returns>
    public IReadOnlyList<FeatureUsage> Usage()
    {
        _gate.EnterReadLock();
        try
        {
            long now = Now();
            var usage = new List<FeatureUsage>();
            foreach (FeaturePool pool in _state.Pools.Values)
            {
                lock (pool.Lock)
                {
                    RemoveExpired(pool, now);
                    usage.Add(
                        new FeatureUsage(
                            pool.FeatureId,
                            pool.Seats,
                            pool.Allowed,
                            pool.Used,
                            pool.Leases.Values.Where(l => l.IsBorrow).Sum(l => (long)l.Count)
                        )
                    );
                }
            }

            return usage;
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns every unexpired lease, for the administrator (design §13.7).
    /// </summary>
    /// <returns>The leases.</returns>
    public IReadOnlyList<LeaseInfo> Leases()
    {
        _gate.EnterReadLock();
        try
        {
            long now = Now();
            var leases = new List<LeaseInfo>();
            foreach (FeaturePool pool in _state.Pools.Values)
            {
                lock (pool.Lock)
                {
                    RemoveExpired(pool, now);
                    leases.AddRange(pool.Leases.Values.Select(r => r.Info()));
                }
            }

            return leases;
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns the key a lease is bound to, so the server can verify a proof that does not carry the key (design §12.2).
    /// </summary>
    /// <param name="leaseId">The lease ID.</param>
    /// <param name="keyId">The installation key thumbprint.</param>
    /// <param name="key">The public key, or <see langword="null"/> for a borrow restored from the record: the proof then
    /// carries the key.</param>
    /// <returns><see langword="false"/> when the server does not know the lease.</returns>
    public bool TryGetLeaseKey(string leaseId, out string? keyId, out ECParameters? key)
    {
        ArgumentNullException.ThrowIfNull(leaseId);
        if (_leases.TryGetValue(leaseId, out LeaseRecord? record))
        {
            keyId = record.KeyId;
            key = record.Key;
            return true;
        }

        keyId = null;
        key = null;
        return false;
    }

    /// <summary>
    /// Acquires a new lease, or borrows one when the request has a borrow period (design §12.7.1, §12.7.2, §12.7.4).
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The lease, or why it was refused.</returns>
    public LeaseOutcome Acquire(LeaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _gate.EnterReadLock();
        try
        {
            return AcquireCore(request, _state);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    /// <summary>
    /// Renews a lease, judging the sequence number as design §10.4 step 3 says.
    /// </summary>
    /// <param name="leaseId">The lease ID.</param>
    /// <param name="sequence">The sequence number of the last token the client received.</param>
    /// <param name="keyId">The installation key thumbprint from the proof.</param>
    /// <param name="proofId">The proof's <c>jti</c>.</param>
    /// <param name="retryOf">The <c>jti</c> of the client's earlier requests that got no answer, at most
    /// <see cref="MaxRetryProofIds"/>. A request one sequence number behind gets the last token again only when the request
    /// that produced it is among them; otherwise it is a clone, which cannot know another copy's <c>jti</c> (design §10.4).</param>
    /// <returns>The new token, the last token again for a retry, or why it was refused.</returns>
    public LeaseOutcome Renew(string leaseId, long sequence, string keyId, string proofId, IReadOnlyCollection<string> retryOf)
    {
        ArgumentNullException.ThrowIfNull(leaseId);
        ArgumentNullException.ThrowIfNull(keyId);
        ArgumentNullException.ThrowIfNull(proofId);
        ArgumentNullException.ThrowIfNull(retryOf);
        _gate.EnterReadLock();
        try
        {
            return RenewCore(leaseId, sequence, keyId, proofId, retryOf, _state);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    /// <summary>
    /// Releases a lease (design §12.7.1). A release is idempotent. A borrow is returned early only within the rules of design
    /// §12.7.4.
    /// </summary>
    /// <param name="leaseId">The lease ID.</param>
    /// <param name="keyId">The installation key thumbprint from the proof.</param>
    /// <param name="lease">The lease as it was before the release, or <see langword="null"/> when the server does not know it.</param>
    /// <returns>What the release did.</returns>
    public ReleaseOutcome Release(string leaseId, string keyId, out LeaseInfo? lease)
    {
        ArgumentNullException.ThrowIfNull(leaseId);
        ArgumentNullException.ThrowIfNull(keyId);
        _gate.EnterReadLock();
        try
        {
            return ReleaseCore(leaseId, keyId, _state, out lease);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    /// <summary>
    /// Removes a lease on the administrator's order (design §13.7), whatever its state. A borrow is not removed: it ends only
    /// by the client's early return or at its expiry (design §12.7.4), so the lease returned says <see cref="LeaseInfo.IsBorrow"/>
    /// and the caller refuses the order.
    /// </summary>
    /// <param name="leaseId">The lease ID.</param>
    /// <returns>The lease, removed unless it is a borrow, or <see langword="null"/> when the server does not know it.</returns>
    public LeaseInfo? ForceRelease(string leaseId)
    {
        ArgumentNullException.ThrowIfNull(leaseId);
        _gate.EnterReadLock();
        try
        {
            if (!_leases.TryGetValue(leaseId, out LeaseRecord? found) || !_state.Pools.TryGetValue(found.FeatureId, out FeaturePool? pool))
            {
                return null;
            }

            lock (pool.Lock)
            {
                if (!pool.Leases.TryGetValue(leaseId, out LeaseRecord? record))
                {
                    return null;
                }

                if (!record.IsBorrow)
                {
                    Remove(pool, record);
                }

                return record.Info();
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    /// <summary>
    /// Takes back leases that clients still hold after a restart (design §13.4). Each token is checked as a client checks a
    /// lease (design §12.7.5), except that it may name an earlier server license of this server; it must be bound to the
    /// proof's key and fit in the free seats.
    /// </summary>
    /// <param name="tokens">The lease tokens, at most <see cref="MaxReclaimTokens"/>.</param>
    /// <param name="keyId">The installation key thumbprint from the proof.</param>
    /// <param name="key">The installation public key from the proof.</param>
    /// <returns>One result per token, in order.</returns>
    public IReadOnlyList<ReclaimResult> Reclaim(IReadOnlyList<string> tokens, string keyId, ECParameters key)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(keyId);
        if (tokens.Count > MaxReclaimTokens)
        {
            throw new ArgumentException($"A reclaim carries at most {MaxReclaimTokens} tokens.", nameof(tokens));
        }

        _gate.EnterReadLock();
        try
        {
            LicenseState state = _state;
            var results = new List<ReclaimResult>(tokens.Count);
            var expectation = new LeaseExpectation(state.Server.License.ProductId, keyId);
            foreach (string token in tokens)
            {
                results.Add(ReclaimOne(token, expectation, key, state));
            }

            return results;
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    /// <summary>
    /// Restores borrows and early returns from their record at start, before the server listens (design §13.4). Expired
    /// borrows, tokens this server did not sign, and returns of unknown borrows are skipped. Restored seats count toward the
    /// early end of the reclaim window.
    /// </summary>
    /// <param name="borrows">The borrows recorded, in order.</param>
    /// <param name="returns">The early returns recorded.</param>
    /// <returns>The number of borrows restored.</returns>
    public int Restore(IEnumerable<BorrowRecord> borrows, IEnumerable<BorrowReturn> returns)
    {
        ArgumentNullException.ThrowIfNull(borrows);
        ArgumentNullException.ThrowIfNull(returns);
        _gate.EnterWriteLock();
        try
        {
            LicenseState state = _state;
            long now = Now();
            foreach (BorrowRecord borrow in borrows)
            {
                if (
                    !LeaseVerifier.TryReadOwnLease(borrow.Token, state.Server, out VerifiedLease? lease)
                    || !lease!.IsBorrow
                    || lease.ExpiresAt.ToUnixTimeSeconds() < now
                    || _leases.ContainsKey(lease.LeaseId)
                )
                {
                    continue;
                }

                FeaturePool pool = state.PoolFor(lease.FeatureId);
                var record = new LeaseRecord(lease.LeaseId, lease.FeatureId, lease.Seats, lease.InstallationKeyThumbprint, null)
                {
                    Sequence = lease.Sequence,
                    LastToken = lease.Token,
                    ExpiresAt = lease.ExpiresAt.ToUnixTimeSeconds(),
                    Borrow = borrow,
                };
                Add(pool, record);
            }

            foreach (BorrowReturn returned in returns)
            {
                if (
                    _leases.TryGetValue(returned.LeaseId, out LeaseRecord? record) && state.Pools.TryGetValue(record.FeatureId, out FeaturePool? pool)
                )
                {
                    Remove(pool, record);
                }

                RecordReturn(returned.KeyId, returned.Fingerprints, returned.At);
            }

            EndRecoveryIfFull(state);
            return _leases.Values.Count(r => r.IsBorrow);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    /// <summary>
    /// Replaces the server license while the server runs (design §13.1). The caller has verified the new license for this
    /// server's key. Leases stay until they expire; a lease whose feature the new license no longer grants is not renewed
    /// (<c>ELM_FEATURE_NOT_LICENSED</c>), and while a feature holds more seats than the new license allows, its renewals are
    /// refused (<c>ELM_NO_SEATS</c>) until enough leases expire.
    /// </summary>
    /// <param name="server">The new server identity.</param>
    public void ReplaceLicense(ServerIdentity server)
    {
        ArgumentNullException.ThrowIfNull(server);
        _gate.EnterWriteLock();
        try
        {
            var state = new LicenseState(server);
            foreach (LeaseRecord record in _leases.Values)
            {
                FeaturePool pool = state.PoolFor(record.FeatureId);
                pool.Leases.Add(record.Id, record);
                pool.AddUsed(record.Count);
            }

            Volatile.Write(ref _state, state);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes expired leases (design §13.2) and early returns older than 30 days. The server calls it every 30 seconds.
    /// </summary>
    /// <returns>The number of leases removed.</returns>
    public int Purge()
    {
        _gate.EnterReadLock();
        try
        {
            PurgeReturns(_time.GetUtcNow());
            long nowSeconds = Now();
            int removed = 0;
            foreach (FeaturePool pool in _state.Pools.Values)
            {
                lock (pool.Lock)
                {
                    removed += RemoveExpired(pool, nowSeconds);
                }
            }

            return removed;
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    /// <summary>Releases the engine's lock.</summary>
    public void Dispose() => _gate.Dispose();

    private static LeaseOutcome.Refused Refuse(ErrorCode code, string message) => new(new LicenseError(code, message));

    private static LeaseOutcome.Refused UnknownLease() =>
        Refuse(ErrorCode.UnknownLease, "The server does not know the lease; reclaim it if you still hold its token.");

    private static string NewLeaseId() => Base64UrlCodec.Encode(RandomNumberGenerator.GetBytes(16));

    // design §12.7.2, decision 0.64: the shared rule, on the server license's feature.
    private static LicenseError? CheckFeature(LicensedFeature? feature, string? version, long nowSeconds) =>
        LeaseRules.CheckFeature(feature is not null, feature?.ExpiresAt, feature?.VersionRange, version, nowSeconds);

    private LeaseOutcome AcquireCore(LeaseRequest request, LicenseState state)
    {
        DateTimeOffset now = _time.GetUtcNow();
        long nowSeconds = now.ToUnixTimeSeconds();
        if (state.LicenseExpired(nowSeconds))
        {
            return Refuse(ErrorCode.Expired, "The server license has expired.");
        }

        if (!state.Pools.TryGetValue(request.FeatureId ?? string.Empty, out FeaturePool? pool) || pool.Feature is null)
        {
            return Refuse(ErrorCode.FeatureNotLicensed, "The license does not grant the feature.");
        }

        LicenseError? featureError = CheckFeature(pool.Feature, request.Version, nowSeconds);
        if (featureError is not null)
        {
            return new LeaseOutcome.Refused(featureError);
        }

        TimeSpan? borrow = request.Borrow;
        TimeSpan limit = _options.MaxBorrow is TimeSpan max && max < state.BorrowLimit ? max : state.BorrowLimit;
        if (LeaseRules.CheckRequest(request.Count, pool.Seats, borrow, limit, request.Fingerprints is { Count: > 0 }) is LicenseError requestError)
        {
            return new LeaseOutcome.Refused(requestError);
        }

        if (!_recoveryOver && now < _recoveryEnds)
        {
            // Retry-After is in whole seconds, rounded up so the client does not come back early.
            TimeSpan wait = TimeSpan.FromSeconds(Math.Ceiling((_recoveryEnds - now).TotalSeconds));
            return new LeaseOutcome.Refused(new LicenseError(ErrorCode.ServerRecovering, "The server is reclaiming leases after a restart."), wait);
        }

        lock (pool.Lock)
        {
            RemoveExpired(pool, nowSeconds);
            if (!LeaseRules.Fits(pool.Used, request.Count, pool.Allowed, _options.BorrowReserve, borrow is not null))
            {
                return Refuse(
                    ErrorCode.NoSeats,
                    borrow is null ? "No seats are free for the feature." : "No seats are free to borrow for the feature."
                );
            }

            var record = new LeaseRecord(NewLeaseId(), pool.FeatureId, request.Count, request.KeyId, request.Key);
            LeaseOutcome outcome = IssueNext(record, now, request.ProofId, state, borrow);
            if (outcome is not LeaseOutcome.Granted)
            {
                return outcome;
            }

            if (borrow is not null)
            {
                var borrowed = new BorrowRecord(
                    record.LastToken,
                    state.Server.License.LicenseId,
                    pool.Seats,
                    state.BorrowLimit,
                    [.. request.Fingerprints!]
                );
                try
                {
                    _options.Journal?.Borrowed(borrowed);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    return Refuse(ErrorCode.InternalError, "The borrow cannot be recorded: " + e.Message);
                }

                record.Borrow = borrowed;
            }

            Add(pool, record);

            // The feature's use is known here, inside its lock, so the caller need not walk every lease to find an overage.
            return (LeaseOutcome.Granted)outcome with
            {
                InUse = pool.Used,
                Seats = pool.Seats,
            };
        }
    }

    private LeaseOutcome RenewCore(
        string leaseId,
        long sequence,
        string keyId,
        string proofId,
        IReadOnlyCollection<string> retryOf,
        LicenseState state
    )
    {
        if (!_leases.TryGetValue(leaseId, out LeaseRecord? found) || !state.Pools.TryGetValue(found.FeatureId, out FeaturePool? pool))
        {
            return UnknownLease();
        }

        DateTimeOffset now = _time.GetUtcNow();
        long nowSeconds = now.ToUnixTimeSeconds();
        lock (pool.Lock)
        {
            RemoveExpired(pool, nowSeconds);
            if (!pool.Leases.TryGetValue(leaseId, out LeaseRecord? record))
            {
                return UnknownLease();
            }

            if (record.KeyId != keyId)
            {
                return Refuse(ErrorCode.ProofInvalid, "The proof is not signed by the key the lease is bound to.");
            }

            if (record.Stopped)
            {
                return Refuse(ErrorCode.CloneDetected, "The lease was stopped because a clone of the installation was detected.");
            }

            if (record.IsBorrow)
            {
                return Refuse(ErrorCode.InvalidArgument, "A borrowed lease is not renewed; it is valid until it expires.");
            }

            if (state.LicenseExpired(nowSeconds))
            {
                return Refuse(ErrorCode.Expired, "The server license has expired.");
            }

            LicenseError? featureError = CheckFeature(pool.Feature, version: null, nowSeconds);
            if (featureError is not null)
            {
                return new LeaseOutcome.Refused(featureError);
            }

            if (pool.Used > pool.Allowed)
            {
                // The server license now allows fewer seats: renewals wait until enough leases expire (design §13.1).
                return Refuse(ErrorCode.NoSeats, "The server license now allows fewer seats than are held.");
            }

            switch (LeaseRules.Judge(sequence, record.Sequence, record.IssuedBy, retryOf))
            {
                case LeaseSequence.Current:
                    return IssueNext(record, now, proofId, state, borrow: null);
                case LeaseSequence.Retry:
                    // The client did not receive the last answer and retries; give it the same token again.
                    return new LeaseOutcome.Granted(record.LastToken, record.Id, DateTimeOffset.FromUnixTimeSeconds(record.ExpiresAt), false);
            }

            if (state.CloneStrategy == CloneStrategy.Deny)
            {
                // Kept until it expires, so neither copy can reclaim the seat with the token it holds.
                record.Stopped = true;
                return Refuse(ErrorCode.CloneDetected, "Another copy of the installation renewed the lease; a clone was detected.");
            }

            LeaseOutcome flagged = IssueNext(record, now, proofId, state, borrow: null);
            return flagged is LeaseOutcome.Granted granted ? granted with { CloneFlagged = true } : flagged;
        }
    }

    private ReleaseOutcome ReleaseCore(string leaseId, string keyId, LicenseState state, out LeaseInfo? lease)
    {
        lease = null;
        if (!_leases.TryGetValue(leaseId, out LeaseRecord? found) || !state.Pools.TryGetValue(found.FeatureId, out FeaturePool? pool))
        {
            return ReleaseOutcome.NotFound;
        }

        lock (pool.Lock)
        {
            DateTimeOffset now = _time.GetUtcNow();
            RemoveExpired(pool, now.ToUnixTimeSeconds());
            if (!pool.Leases.TryGetValue(leaseId, out LeaseRecord? record))
            {
                return ReleaseOutcome.NotFound;
            }

            lease = record.Info();
            if (record.KeyId != keyId)
            {
                return ReleaseOutcome.WrongKey;
            }

            if (record.Stopped)
            {
                return ReleaseOutcome.Stopped;
            }

            if (record.Borrow is BorrowRecord borrow)
            {
                if (!MayReturnEarly(record, borrow, pool, state, now))
                {
                    return ReleaseOutcome.ReturnNotAllowed;
                }

                try
                {
                    _options.Journal?.Returned(new BorrowReturn(record.Id, record.KeyId, borrow.Fingerprints, now));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    return ReleaseOutcome.ReturnNotAllowed;
                }

                RecordReturn(record.KeyId, borrow.Fingerprints, now);
            }

            Remove(pool, record);
            return ReleaseOutcome.Released;
        }
    }

    // design §12.7.4: the early returns of the last 30 days of the key and of each fingerprint are under the license's limit,
    // and the server license has not changed in content since the borrow (the license ID, the feature, its seats, and the
    // borrow limit).
    // A feature the new license no longer grants has a pool of 0 seats, so the seat comparison covers it.
    private bool MayReturnEarly(LeaseRecord record, BorrowRecord borrow, FeaturePool pool, LicenseState state, DateTimeOffset now) =>
        LeaseRules.MayReturnEarly(
            CountReturns(LeaseRules.ReturnCounters(record.KeyId, borrow.Fingerprints), now),
            state.BorrowReturnLimit,
            state.Server.License.LicenseId == borrow.LicenseId,
            pool.Seats,
            borrow.FeatureSeats,
            state.BorrowLimit,
            borrow.FeatureBorrowLimit
        );

    // The largest count of early returns in the last 30 days among the counters.
    private int CountReturns(IEnumerable<string> counters, DateTimeOffset now)
    {
        lock (_returnsLock)
        {
            int most = 0;
            foreach (string counter in counters)
            {
                if (_returns.TryGetValue(counter, out List<DateTimeOffset>? times))
                {
                    most = Math.Max(most, times.Count(t => now - t < LeaseRules.ReturnWindow));
                }
            }

            return most;
        }
    }

    private void RecordReturn(string keyId, IReadOnlyList<string> fingerprints, DateTimeOffset at)
    {
        lock (_returnsLock)
        {
            foreach (string counter in LeaseRules.ReturnCounters(keyId, fingerprints))
            {
                if (!_returns.TryGetValue(counter, out List<DateTimeOffset>? times))
                {
                    _returns[counter] = times = [];
                }

                times.Add(at);
            }
        }
    }

    // Keeps the table of early returns to the returns of the last 30 days.
    private void PurgeReturns(DateTimeOffset now)
    {
        lock (_returnsLock)
        {
            foreach ((string counter, List<DateTimeOffset> times) in _returns.ToArray())
            {
                times.RemoveAll(t => now - t >= LeaseRules.ReturnWindow);
                if (times.Count == 0)
                {
                    _returns.Remove(counter);
                }
            }
        }
    }

    private void Add(FeaturePool pool, LeaseRecord record)
    {
        pool.Leases.Add(record.Id, record);
        pool.AddUsed(record.Count);
        _leases[record.Id] = record;
    }

    private void Remove(FeaturePool pool, LeaseRecord record)
    {
        pool.Leases.Remove(record.Id);
        pool.AddUsed(-record.Count);
        _leases.TryRemove(new KeyValuePair<string, LeaseRecord>(record.Id, record));
    }

    private long Now() => _time.GetUtcNow().ToUnixTimeSeconds();

    // Issues the record's next token: sequence + 1, and an expiry that does not pass the server license's exp + grace, so the
    // client's check of design §12.7.5 step 5 holds.
    private LeaseOutcome IssueNext(LeaseRecord record, DateTimeOffset now, string proofId, LicenseState state, TimeSpan? borrow)
    {
        long nowSeconds = now.ToUnixTimeSeconds();
        long period = borrow is TimeSpan b ? (long)b.TotalSeconds : state.LeaseSeconds;
        if (LeaseRules.Expiry(nowSeconds, period, state.LicenseLimit) is not long expires)
        {
            return Refuse(ErrorCode.Expired, "The server license has expired.");
        }

        long sequence = record.Sequence + 1;
        var spec = new LeaseSpec
        {
            LeaseId = record.Id,
            ProductId = state.Server.License.ProductId,
            LicenseId = state.Server.License.LicenseId,
            FeatureId = record.FeatureId,
            Seats = record.Count,
            InstallationKeyThumbprint = record.KeyId,
            Duration = TimeSpan.FromSeconds(expires - nowSeconds),
            IsBorrow = borrow is not null,
            ServerLicenseTokenId = state.Server.License.TokenId,
            Sequence = sequence,
        };
        string token = _sign(spec, DateTimeOffset.FromUnixTimeSeconds(nowSeconds));
        record.Sequence = sequence;
        record.LastToken = token;
        record.IssuedBy = proofId;
        record.ExpiresAt = expires;
        return new LeaseOutcome.Granted(token, record.Id, DateTimeOffset.FromUnixTimeSeconds(expires), false);
    }

    private ReclaimResult ReclaimOne(string? token, LeaseExpectation expectation, ECParameters key, LicenseState state)
    {
        if (token is null)
        {
            return new ReclaimResult(null, new LicenseError(ErrorCode.Malformed, "The lease token is empty."));
        }

        VerifiedLease lease;
        switch (_verifier.VerifyOwnLease(token, state.Server, expectation))
        {
            case LeaseVerificationResult.Valid valid:
                lease = valid.Lease;
                break;
            case LeaseVerificationResult.Invalid invalid:
                return new ReclaimResult(null, invalid.Error);
            default:
                throw new InvalidOperationException("Unknown lease verification result.");
        }

        if (lease.IsBorrow)
        {
            return new ReclaimResult(lease.LeaseId, new LicenseError(ErrorCode.BorrowNotAllowed, "A borrowed lease is not reclaimed."));
        }

        // The verifier checked that the feature is in the server license.
        FeaturePool pool = state.Pools[lease.FeatureId];
        long nowSeconds = Now();
        lock (pool.Lock)
        {
            RemoveExpired(pool, nowSeconds);
            if (pool.Leases.TryGetValue(lease.LeaseId, out LeaseRecord? existing))
            {
                return ReclaimKnown(existing, lease, state);
            }

            if (pool.Used + lease.Seats > pool.Allowed)
            {
                return new ReclaimResult(lease.LeaseId, new LicenseError(ErrorCode.NoSeats, "No seats are free to reclaim the lease."));
            }

            var record = new LeaseRecord(lease.LeaseId, lease.FeatureId, lease.Seats, expectation.InstallationKeyThumbprint, key)
            {
                Sequence = lease.Sequence,
                LastToken = lease.Token,
                ExpiresAt = lease.ExpiresAt.ToUnixTimeSeconds(),
            };
            Add(pool, record);
        }

        EndRecoveryIfFull(state);
        return new ReclaimResult(lease.LeaseId, null);
    }

    // A token for a lease the server already holds: the same sequence number is the same client; anything else is another
    // copy (design §10.4). A reclaim carries no retry, so a token one behind cannot be told from a lockstep clone (0.47).
    private static ReclaimResult ReclaimKnown(LeaseRecord existing, VerifiedLease lease, LicenseState state)
    {
        if (existing.Stopped)
        {
            return new ReclaimResult(lease.LeaseId, new LicenseError(ErrorCode.CloneDetected, "The lease was stopped as a clone."));
        }

        // design §13.4: a reclaim carries no retry list, so only the same sequence number is the same client (decision 0.47).
        if (LeaseRules.Judge(lease.Sequence, existing.Sequence, null, []) == LeaseSequence.Current)
        {
            return new ReclaimResult(lease.LeaseId, null);
        }

        if (state.CloneStrategy == CloneStrategy.Deny)
        {
            existing.Stopped = true;
            return new ReclaimResult(lease.LeaseId, new LicenseError(ErrorCode.CloneDetected, "Another copy of the installation holds the lease."));
        }

        return new ReclaimResult(lease.LeaseId, null, CloneFlagged: true);
    }

    // The window ends early once every feature's seats are reclaimed or restored from the borrow record (design §13.4 step 3).
    // Other pools are read without their locks: a stale count only delays the end to the next reclaim or to the window's end.
    private void EndRecoveryIfFull(LicenseState state)
    {
        if (!_recoveryOver && state.Pools.Values.Where(p => p.Feature is not null).All(p => p.Used >= p.Seats))
        {
            _recoveryOver = true;
        }
    }

    private int RemoveExpired(FeaturePool pool, long nowSeconds)
    {
        List<LeaseRecord>? expired = null;
        foreach (LeaseRecord record in pool.Leases.Values)
        {
            if (nowSeconds > record.ExpiresAt)
            {
                (expired ??= []).Add(record);
            }
        }

        foreach (LeaseRecord record in expired ?? [])
        {
            Remove(pool, record);
            _options.Expired?.Invoke(record.Info());
        }

        return expired?.Count ?? 0;
    }

    // The values of one server license, replaced as a whole under the engine's write lock.
    private sealed class LicenseState
    {
        private readonly int _overage;

        public LicenseState(ServerIdentity server)
        {
            // ServerIdentity holds only server licenses, and the payload rules require these claims for them.
            VerifiedLicense license = server.License;
            Server = server;
            Lease = license.LeaseDuration!.Value;
            LeaseSeconds = (long)Lease.TotalSeconds;
            BorrowLimit = license.BorrowLimit!.Value;
            BorrowReturnLimit = license.BorrowReturnLimit!.Value;
            CloneStrategy = license.CloneStrategy!.Value;
            _overage = license.OveragePercent!.Value;
            LicenseLimit = license.ExpiresAt is DateTimeOffset expires ? (expires + license.GracePeriod).ToUnixTimeSeconds() : null;
            Pools = license.Features.ToDictionary(
                f => f.Id,
                f => new FeaturePool(f.Id, f, f.Seats ?? 0, Allowed(f.Seats ?? 0)),
                StringComparer.Ordinal
            );
        }

        public ServerIdentity Server { get; }

        public TimeSpan Lease { get; }

        public long LeaseSeconds { get; }

        public TimeSpan BorrowLimit { get; }

        public int BorrowReturnLimit { get; }

        public CloneStrategy CloneStrategy { get; }

        public long? LicenseLimit { get; }

        public Dictionary<string, FeaturePool> Pools { get; }

        public bool LicenseExpired(long nowSeconds) => LicenseLimit is long limit && nowSeconds >= limit;

        // The pool of a feature; a feature the license no longer grants gets a pool with no seats, so its leases are counted
        // until they expire and are not renewed.
        public FeaturePool PoolFor(string featureId)
        {
            if (!Pools.TryGetValue(featureId, out FeaturePool? pool))
            {
                Pools[featureId] = pool = new FeaturePool(featureId, null, 0, 0);
            }

            return pool;
        }

        // design §9.3: floor(seats × (100 + overage) / 100). Seats and overage are bounded (int, 1,000 %), so long cannot overflow.
        private long Allowed(int seats) => LeaseRules.Allowed(seats, _overage);
    }

    // Leases changes only under Lock. Used is also read without the lock (EndRecoveryIfFull), so it is updated atomically.
    private sealed class FeaturePool(string featureId, LicensedFeature? feature, int seats, long allowed)
    {
        private long _used;

        public Lock Lock { get; } = new();

        public Dictionary<string, LeaseRecord> Leases { get; } = new(StringComparer.Ordinal);

        public long Used => Interlocked.Read(ref _used);

        public string FeatureId { get; } = featureId;

        // Null when the current server license no longer grants the feature.
        public LicensedFeature? Feature { get; } = feature;

        public int Seats { get; } = seats;

        public long Allowed { get; } = allowed;

        public void AddUsed(long seats) => Interlocked.Add(ref _used, seats);
    }

    // Mutable fields change only under the pool's lock.
    private sealed class LeaseRecord(string id, string featureId, int count, string keyId, ECParameters? key)
    {
        public string Id { get; } = id;

        public string FeatureId { get; } = featureId;

        public int Count { get; } = count;

        public string KeyId { get; } = keyId;

        // Null for a borrow restored from the record, whose proof key the server has not seen since the start.
        public ECParameters? Key { get; } = key;

        public long Sequence { get; set; }

        public string LastToken { get; set; } = string.Empty;

        // The jti of the request that produced LastToken; null for a reclaimed or restored lease.
        public string? IssuedBy { get; set; }

        public long ExpiresAt { get; set; }

        public bool Stopped { get; set; }

        // The record of a borrow, with the server license's values at the borrow; null for a lease that is renewed.
        public BorrowRecord? Borrow { get; set; }

        public bool IsBorrow => Borrow is not null;

        public LeaseInfo Info() => new(Id, FeatureId, Count, KeyId, DateTimeOffset.FromUnixTimeSeconds(ExpiresAt), IsBorrow, Stopped);
    }
}
