using System.Collections.Concurrent;
using System.Security.Cryptography;
using ElasticLM.Issuing;
using ElasticLM.Licensing;

namespace ElasticLM.Leasing;

/// <summary>
/// The lease rules of an on-premises server, with the state in memory (design §12.7.1, §12.7.2, §10.4, §13.2–§13.4):
/// seats with overage, acquire, renew, release, clone detection by sequence number, and reclaim after a restart.
/// </summary>
/// <remarks>
/// Each feature has one lock; counting and adding happen inside it (design §13.3), so requests for other features do not wait.
/// The engine is safe to call from several threads.
/// </remarks>
public sealed class LeaseEngine
{
    /// <summary>The most lease tokens one reclaim request may carry (design §8.6).</summary>
    public const int MaxReclaimTokens = 64;

    /// <summary>The most proof IDs a renewal may name as earlier attempts (design §8.6).</summary>
    public const int MaxRetryProofIds = 16;

    private readonly ServerIdentity _server;
    private readonly LeaseSigner _sign;
    private readonly LeaseVerifier _verifier;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, FeaturePool> _pools;
    private readonly ConcurrentDictionary<string, LeaseRecord> _leases = new(StringComparer.Ordinal);
    private readonly long _leaseSeconds;
    private readonly long? _licenseLimit;
    private readonly CloneStrategy _cloneStrategy;
    private readonly DateTimeOffset _recoveryEnds;
    private volatile bool _recoveryOver;

    /// <summary>
    /// Initializes a new instance of the <see cref="LeaseEngine"/> class. The reclaim window (design §13.4) starts now and
    /// lasts the server license's lease duration.
    /// </summary>
    /// <param name="server">The server's own verified server license and public key.</param>
    /// <param name="sign">Signs lease tokens with the server's installation key.</param>
    /// <param name="verifier">Checks reclaimed lease tokens as a client would (design §12.7.5).</param>
    /// <param name="time">The time provider.</param>
    public LeaseEngine(ServerIdentity server, LeaseSigner sign, LeaseVerifier verifier, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(sign);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(time);

        // ServerIdentity holds only server licenses, and the payload rules require lease, overage, and clone for them.
        VerifiedLicense license = server.License;
        TimeSpan lease = license.LeaseDuration!.Value;
        int overage = license.OveragePercent!.Value;
        CloneStrategy clone = license.CloneStrategy!.Value;

        _server = server;
        _sign = sign;
        _verifier = verifier;
        _time = time;
        _leaseSeconds = (long)lease.TotalSeconds;
        _licenseLimit = license.ExpiresAt is DateTimeOffset expires ? (expires + license.GracePeriod).ToUnixTimeSeconds() : null;
        _cloneStrategy = clone;
        _recoveryEnds = time.GetUtcNow() + lease;

        // design §9.3: floor(seats × (100 + overage) / 100). Seats and overage are bounded (int, 1,000 %), so long cannot overflow.
        _pools = license.Features.ToDictionary(
            f => f.Id,
            f => new FeaturePool(f, f.Seats ?? 0, (long)(f.Seats ?? 0) * (100 + overage) / 100),
            StringComparer.Ordinal
        );
    }

    /// <summary>Gets the end of the reclaim window, unless every seat was reclaimed earlier.</summary>
    public DateTimeOffset RecoveryEndsAt => _recoveryEnds;

    /// <summary>Gets the number of leases the engine holds, including expired ones not yet purged.</summary>
    public int LeaseCount => _leases.Count;

    /// <summary>
    /// Returns whether the reclaim window is still open: new leases are refused until it closes (design §13.4).
    /// </summary>
    /// <returns><see langword="true"/> while the window is open.</returns>
    public bool IsRecovering() => !_recoveryOver && _time.GetUtcNow() < _recoveryEnds;

    /// <summary>
    /// Returns the seats held by unexpired leases of a feature, including stopped clones.
    /// </summary>
    /// <param name="featureId">The feature ID.</param>
    /// <returns>The seat count; 0 for a feature the license does not grant.</returns>
    public long SeatsInUse(string featureId)
    {
        if (!_pools.TryGetValue(featureId, out FeaturePool? pool))
        {
            return 0;
        }

        lock (pool.Lock)
        {
            RemoveExpired(pool, Now());
            return pool.Used;
        }
    }

    /// <summary>
    /// Returns the key a lease is bound to, so the server can verify a proof that does not carry the key (design §12.2).
    /// </summary>
    /// <param name="leaseId">The lease ID.</param>
    /// <param name="keyId">The installation key thumbprint.</param>
    /// <param name="key">The public key.</param>
    /// <returns><see langword="false"/> when the server does not know the lease.</returns>
    public bool TryGetLeaseKey(string leaseId, out string? keyId, out ECParameters key)
    {
        ArgumentNullException.ThrowIfNull(leaseId);
        if (_leases.TryGetValue(leaseId, out LeaseRecord? record))
        {
            keyId = record.KeyId;
            key = record.Key;
            return true;
        }

        keyId = null;
        key = default;
        return false;
    }

    /// <summary>
    /// Acquires a new lease (design §12.7.1, §12.7.2).
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The lease, or why it was refused.</returns>
    public LeaseOutcome Acquire(LeaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset now = _time.GetUtcNow();
        long nowSeconds = now.ToUnixTimeSeconds();
        if (LicenseExpired(nowSeconds))
        {
            return Refuse(ErrorCode.Expired, "The server license has expired.");
        }

        if (!_pools.TryGetValue(request.FeatureId ?? string.Empty, out FeaturePool? pool))
        {
            return Refuse(ErrorCode.FeatureNotLicensed, "The server license does not grant the feature.");
        }

        LicenseError? featureError = CheckFeature(pool.Feature, request.Version, nowSeconds);
        if (featureError is not null)
        {
            return new LeaseOutcome.Refused(featureError);
        }

        if (request.Count < 1 || request.Count > pool.Seats)
        {
            return Refuse(ErrorCode.InvalidArgument, "The seat count must be at least 1 and at most the feature's seats.");
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
            if (pool.Used + request.Count > pool.Allowed)
            {
                return Refuse(ErrorCode.NoSeats, "No seats are free for the feature.");
            }

            var record = new LeaseRecord(NewLeaseId(), pool.Feature.Id, request.Count, request.KeyId, request.Key);
            LeaseOutcome outcome = IssueNext(record, now, request.ProofId, cloneFlagged: false);
            if (outcome is LeaseOutcome.Granted)
            {
                Add(pool, record);
            }

            return outcome;
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
        if (!_leases.TryGetValue(leaseId, out LeaseRecord? found) || !_pools.TryGetValue(found.FeatureId, out FeaturePool? pool))
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

            if (LicenseExpired(nowSeconds))
            {
                return Refuse(ErrorCode.Expired, "The server license has expired.");
            }

            LicenseError? featureError = CheckFeature(pool.Feature, version: null, nowSeconds);
            if (featureError is not null)
            {
                return new LeaseOutcome.Refused(featureError);
            }

            if (sequence == record.Sequence)
            {
                return IssueNext(record, now, proofId, cloneFlagged: false);
            }

            if (sequence == record.Sequence - 1 && record.IssuedBy is string issuedBy && retryOf.Contains(issuedBy))
            {
                // The client did not receive the last answer and retries; give it the same token again.
                return new LeaseOutcome.Granted(record.LastToken, record.Id, DateTimeOffset.FromUnixTimeSeconds(record.ExpiresAt), false);
            }

            if (_cloneStrategy == CloneStrategy.Deny)
            {
                // Kept until it expires, so neither copy can reclaim the seat with the token it holds.
                record.Stopped = true;
                return Refuse(ErrorCode.CloneDetected, "Another copy of the installation renewed the lease; a clone was detected.");
            }

            return IssueNext(record, now, proofId, cloneFlagged: true);
        }
    }

    /// <summary>
    /// Releases a lease (design §12.7.1). A release is idempotent.
    /// </summary>
    /// <param name="leaseId">The lease ID.</param>
    /// <param name="keyId">The installation key thumbprint from the proof.</param>
    /// <returns>What the release did.</returns>
    public ReleaseOutcome Release(string leaseId, string keyId)
    {
        ArgumentNullException.ThrowIfNull(leaseId);
        ArgumentNullException.ThrowIfNull(keyId);
        if (!_leases.TryGetValue(leaseId, out LeaseRecord? found) || !_pools.TryGetValue(found.FeatureId, out FeaturePool? pool))
        {
            return ReleaseOutcome.NotFound;
        }

        lock (pool.Lock)
        {
            RemoveExpired(pool, Now());
            if (!pool.Leases.TryGetValue(leaseId, out LeaseRecord? record))
            {
                return ReleaseOutcome.NotFound;
            }

            if (record.KeyId != keyId)
            {
                return ReleaseOutcome.WrongKey;
            }

            if (record.Stopped)
            {
                return ReleaseOutcome.Stopped;
            }

            Remove(pool, record);
            return ReleaseOutcome.Released;
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

        var results = new List<ReclaimResult>(tokens.Count);
        var expectation = new LeaseExpectation(_server.License.ProductId, keyId);
        foreach (string token in tokens)
        {
            results.Add(ReclaimOne(token, expectation, key));
        }

        return results;
    }

    /// <summary>
    /// Removes expired leases (design §13.2). The server calls it every 30 seconds.
    /// </summary>
    /// <returns>The number of leases removed.</returns>
    public int Purge()
    {
        long nowSeconds = Now();
        int removed = 0;
        foreach (FeaturePool pool in _pools.Values)
        {
            lock (pool.Lock)
            {
                removed += RemoveExpired(pool, nowSeconds);
            }
        }

        return removed;
    }

    private static LeaseOutcome.Refused Refuse(ErrorCode code, string message) => new(new LicenseError(code, message));

    private static LeaseOutcome.Refused UnknownLease() =>
        Refuse(ErrorCode.UnknownLease, "The server does not know the lease; reclaim it if you still hold its token.");

    private static string NewLeaseId() => Base64UrlCodec.Encode(RandomNumberGenerator.GetBytes(16));

    private static LicenseError? CheckFeature(LicensedFeature feature, string? version, long nowSeconds)
    {
        if (feature.ExpiresAt is DateTimeOffset featureExpires && nowSeconds > featureExpires.ToUnixTimeSeconds())
        {
            return new LicenseError(ErrorCode.FeatureExpired, "The feature has expired.");
        }

        if (version is null)
        {
            return null;
        }

        if (!FeatureVersion.TryParse(version, out FeatureVersion parsed))
        {
            return new LicenseError(ErrorCode.InvalidArgument, "The version must be major.minor[.patch].");
        }

        // The range was validated when the server license was verified, so parsing it again does not fail.
        return
            feature.VersionRange is not null
            && (!FeatureVersionRange.TryParse(feature.VersionRange, out FeatureVersionRange? range) || !range!.Contains(parsed))
            ? new LicenseError(ErrorCode.FeatureVersionNotLicensed, "The feature version is outside the licensed range.")
            : null;
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

    private bool LicenseExpired(long nowSeconds) => _licenseLimit is long limit && nowSeconds >= limit;

    // Issues the record's next token: sequence + 1, and an expiry that does not pass the server license's exp + grace, so the
    // client's check of design §12.7.5 step 5 holds.
    private LeaseOutcome IssueNext(LeaseRecord record, DateTimeOffset now, string proofId, bool cloneFlagged)
    {
        long nowSeconds = now.ToUnixTimeSeconds();
        long expires = _licenseLimit is long limit ? Math.Min(nowSeconds + _leaseSeconds, limit) : nowSeconds + _leaseSeconds;
        if (expires - nowSeconds < 1)
        {
            return Refuse(ErrorCode.Expired, "The server license has expired.");
        }

        long sequence = record.Sequence + 1;
        var spec = new LeaseSpec
        {
            LeaseId = record.Id,
            ProductId = _server.License.ProductId,
            LicenseId = _server.License.LicenseId,
            FeatureId = record.FeatureId,
            Seats = record.Count,
            InstallationKeyThumbprint = record.KeyId,
            Duration = TimeSpan.FromSeconds(expires - nowSeconds),
            ServerLicenseTokenId = _server.License.TokenId,
            Sequence = sequence,
        };
        string token = _sign(spec, DateTimeOffset.FromUnixTimeSeconds(nowSeconds));
        record.Sequence = sequence;
        record.LastToken = token;
        record.IssuedBy = proofId;
        record.ExpiresAt = expires;
        return new LeaseOutcome.Granted(token, record.Id, DateTimeOffset.FromUnixTimeSeconds(expires), cloneFlagged);
    }

    private ReclaimResult ReclaimOne(string? token, LeaseExpectation expectation, ECParameters key)
    {
        if (token is null)
        {
            return new ReclaimResult(null, new LicenseError(ErrorCode.Malformed, "The lease token is empty."));
        }

        VerifiedLease lease;
        switch (_verifier.VerifyOwnLease(token, _server, expectation))
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
        FeaturePool pool = _pools[lease.FeatureId];
        long nowSeconds = Now();
        lock (pool.Lock)
        {
            RemoveExpired(pool, nowSeconds);
            if (pool.Leases.TryGetValue(lease.LeaseId, out LeaseRecord? existing))
            {
                return ReclaimKnown(existing, lease);
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

        EndRecoveryIfFull();
        return new ReclaimResult(lease.LeaseId, null);
    }

    // A token for a lease the server already holds: the same sequence number is the same client; anything else is another
    // copy (design §10.4). A reclaim carries no retry, so a token one behind cannot be told from a lockstep clone (0.47).
    private ReclaimResult ReclaimKnown(LeaseRecord existing, VerifiedLease lease)
    {
        if (existing.Stopped)
        {
            return new ReclaimResult(lease.LeaseId, new LicenseError(ErrorCode.CloneDetected, "The lease was stopped as a clone."));
        }

        if (lease.Sequence == existing.Sequence)
        {
            return new ReclaimResult(lease.LeaseId, null);
        }

        if (_cloneStrategy == CloneStrategy.Deny)
        {
            existing.Stopped = true;
            return new ReclaimResult(lease.LeaseId, new LicenseError(ErrorCode.CloneDetected, "Another copy of the installation holds the lease."));
        }

        return new ReclaimResult(lease.LeaseId, null, CloneFlagged: true);
    }

    // The window ends early once every feature's seats are reclaimed (design §13.4 step 3). Other pools are read without their
    // locks: a stale count only delays the end to the next reclaim or to the window's end.
    private void EndRecoveryIfFull()
    {
        if (!_recoveryOver && _pools.Values.All(p => p.Used >= p.Seats))
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
        }

        return expired?.Count ?? 0;
    }

    // Leases changes only under Lock. Used is also read without the lock (EndRecoveryIfFull), so it is updated atomically.
    private sealed class FeaturePool(LicensedFeature feature, int seats, long allowed)
    {
        private long _used;

        public Lock Lock { get; } = new();

        public Dictionary<string, LeaseRecord> Leases { get; } = new(StringComparer.Ordinal);

        public long Used => Interlocked.Read(ref _used);

        public LicensedFeature Feature { get; } = feature;

        public int Seats { get; } = seats;

        public long Allowed { get; } = allowed;

        public void AddUsed(long seats) => Interlocked.Add(ref _used, seats);
    }

    // Mutable fields change only under the pool's lock.
    private sealed class LeaseRecord(string id, string featureId, int count, string keyId, ECParameters key)
    {
        public string Id { get; } = id;

        public string FeatureId { get; } = featureId;

        public int Count { get; } = count;

        public string KeyId { get; } = keyId;

        public ECParameters Key { get; } = key;

        public long Sequence { get; set; }

        public string LastToken { get; set; } = string.Empty;

        // The jti of the request that produced LastToken; null for a reclaimed lease, whose producing request is unknown.
        public string? IssuedBy { get; set; }

        public long ExpiresAt { get; set; }

        public bool Stopped { get; set; }
    }
}
