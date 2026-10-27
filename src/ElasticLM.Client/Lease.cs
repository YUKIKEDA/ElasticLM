using ElasticLM.Licensing;

namespace ElasticLM.Client;

/// <summary>
/// The state of a held lease (design §12.7.3).
/// </summary>
public enum LeaseState
{
    /// <summary>The lease is valid and renewed every third of its duration.</summary>
    Held,

    /// <summary>The last renewal failed; the client retries from 15 seconds, doubling up to 60, until the lease expires.</summary>
    Retrying,

    /// <summary>The server did not know the lease (it restarted); the client is reclaiming it (design §13.4).</summary>
    Reclaiming,

    /// <summary>The lease expired, its reclaim was refused, or a clone was detected. The app decides what to do.</summary>
    Lost,

    /// <summary>The app released the lease, or the client was disposed.</summary>
    Released,
}

/// <summary>
/// A lease the client holds and renews (design §12.7). It is created by <see cref="LicenseClient.AcquireLeaseAsync"/>.
/// </summary>
public sealed class Lease
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LastRetry = TimeSpan.FromSeconds(60);

    private volatile VerifiedLease _token;
    private volatile LicenseError? _lastError;
    private int _state;

    internal Lease(VerifiedLease token)
    {
        _token = token;
        Id = token.LeaseId;
        FeatureId = token.FeatureId;
        Seats = token.Seats;
    }

    /// <summary>Gets the lease ID.</summary>
    public string Id { get; }

    /// <summary>Gets the feature ID.</summary>
    public string FeatureId { get; }

    /// <summary>Gets the number of seats.</summary>
    public int Seats { get; }

    /// <summary>Gets the current lease token, verified as design §12.7.5 says.</summary>
    public VerifiedLease Token => _token;

    /// <summary>Gets a value indicating whether the lease is a borrow: valid until it expires without renewal, kept on the disk
    /// for use without the server (design §12.7.4).</summary>
    public bool IsBorrow => _token.IsBorrow;

    /// <summary>Gets the expiry of the current token.</summary>
    public DateTimeOffset ExpiresAt => _token.ExpiresAt;

    /// <summary>Gets the state.</summary>
    public LeaseState State => (LeaseState)Volatile.Read(ref _state);

    /// <summary>Gets the error of the last failed renewal, reclaim, or release, or <see langword="null"/>.</summary>
    public LicenseError? LastError => _lastError;

    internal ITimer? Timer { get; set; }

    internal TimeSpan RetryDelay { get; private set; } = FirstRetry;

    /// <summary>
    /// Gets the jti of renewal attempts that got no answer since the last token, newest last, at most 16. A retry names them,
    /// so the server can give the token one of them produced again without taking the retry for a clone (design §10.4).
    /// </summary>
    internal List<string> UnansweredProofIds { get; } = [];

    internal void Update(VerifiedLease token)
    {
        _token = token;
        RetryDelay = FirstRetry;
        UnansweredProofIds.Clear();
    }

    internal void AddUnanswered(string proofId)
    {
        if (UnansweredProofIds.Count == 16)
        {
            UnansweredProofIds.RemoveAt(0);
        }

        UnansweredProofIds.Add(proofId);
    }

    /// <summary>Returns the delay before the next retry and doubles it up to 60 seconds.</summary>
    internal TimeSpan NextRetryDelay()
    {
        TimeSpan delay = RetryDelay;
        RetryDelay = delay * 2 > LastRetry ? LastRetry : delay * 2;
        return delay;
    }

    /// <summary>Sets the state and returns the previous one.</summary>
    internal LeaseState SetState(LeaseState state, LicenseError? error)
    {
        _lastError = error ?? _lastError;
        return (LeaseState)Interlocked.Exchange(ref _state, (int)state);
    }
}

/// <summary>
/// Data of <see cref="LicenseClient.LeaseStateChanged"/>.
/// </summary>
/// <param name="lease">The lease.</param>
/// <param name="previous">The state before.</param>
/// <param name="current">The state now.</param>
/// <param name="error">Why the state changed, for <see cref="LeaseState.Retrying"/>, <see cref="LeaseState.Lost"/>, and a
/// failed release; otherwise <see langword="null"/>.</param>
public sealed class LeaseStateChangedEventArgs(Lease lease, LeaseState previous, LeaseState current, LicenseError? error) : EventArgs
{
    /// <summary>Gets the lease.</summary>
    public Lease Lease { get; } = lease;

    /// <summary>Gets the state before.</summary>
    public LeaseState Previous { get; } = previous;

    /// <summary>Gets the state now.</summary>
    public LeaseState Current { get; } = current;

    /// <summary>Gets why the state changed, or <see langword="null"/>.</summary>
    public LicenseError? Error { get; } = error;
}

/// <summary>
/// The result of <see cref="LicenseClient.AcquireLeaseAsync"/>.
/// </summary>
public abstract record LeaseResult
{
    private LeaseResult() { }

    /// <summary>The lease was granted and verified; the client renews it until it is released.</summary>
    /// <param name="Lease">The lease.</param>
    public sealed record Acquired(Lease Lease) : LeaseResult;

    /// <summary>No lease.</summary>
    /// <param name="Error">Why.</param>
    /// <param name="RetryAfter">When the server asks to be called again (<c>ELM_SERVER_RECOVERING</c>, <c>ELM_RATE_LIMITED</c>),
    /// or <see langword="null"/>. The client does not wait by itself.</param>
    public sealed record Failed(LicenseError Error, TimeSpan? RetryAfter) : LeaseResult;
}
