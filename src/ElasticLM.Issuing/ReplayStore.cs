namespace ElasticLM.Issuing;

/// <summary>
/// The result of recording a proof's <c>jti</c>.
/// </summary>
public enum ReplayCheck
{
    /// <summary>The <c>jti</c> was new and is now recorded.</summary>
    Accepted,

    /// <summary>The <c>jti</c> was already used while its nonce is valid.</summary>
    Replayed,

    /// <summary>The store is full; the request is refused with <c>ELM_SERVER_BUSY</c>.</summary>
    Full,
}

/// <summary>
/// Records the <c>jti</c> of accepted proofs until their nonce expires (design §12.2 rule 5). On premises it lives in memory;
/// the hosted service keeps it in its database (M4).
/// </summary>
public interface IReplayStore
{
    /// <summary>
    /// Records a <c>jti</c> unless it was already recorded.
    /// </summary>
    /// <param name="jti">The proof's token ID.</param>
    /// <param name="expiresAt">When the record may be dropped (the nonce's expiry).</param>
    /// <returns>The result.</returns>
    ReplayCheck TryUse(string jti, DateTimeOffset expiresAt);
}

/// <summary>
/// An in-memory <see cref="IReplayStore"/> with a capacity (design §13.2: 100,000 records).
/// </summary>
public sealed class MemoryReplayStore : IReplayStore
{
    /// <summary>The default capacity.</summary>
    public const int DefaultCapacity = 100_000;

    private readonly Dictionary<string, DateTimeOffset> _used = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly int _capacity;
    private readonly Lock _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryReplayStore"/> class.
    /// </summary>
    /// <param name="time">The time provider.</param>
    /// <param name="capacity">The most records kept at once.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public MemoryReplayStore(TimeProvider time, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _time = time;
        _capacity = capacity;
    }

    /// <summary>Gets the number of records kept.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _used.Count;
            }
        }
    }

    /// <inheritdoc />
    public ReplayCheck TryUse(string jti, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(jti);
        lock (_lock)
        {
            if (_used.TryGetValue(jti, out DateTimeOffset existing) && existing >= _time.GetUtcNow())
            {
                return ReplayCheck.Replayed;
            }

            if (_used.Count >= _capacity && !_used.ContainsKey(jti))
            {
                PurgeLocked();
                if (_used.Count >= _capacity)
                {
                    return ReplayCheck.Full;
                }
            }

            _used[jti] = expiresAt;
            return ReplayCheck.Accepted;
        }
    }

    /// <summary>
    /// Drops records whose nonce has expired. The server calls it from its sweep timer (design §13.2).
    /// </summary>
    public void Purge()
    {
        lock (_lock)
        {
            PurgeLocked();
        }
    }

    private void PurgeLocked()
    {
        DateTimeOffset now = _time.GetUtcNow();
        foreach (string key in _used.Where(e => e.Value < now).Select(e => e.Key).ToList())
        {
            _used.Remove(key);
        }
    }
}
