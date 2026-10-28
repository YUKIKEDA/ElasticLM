using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.Client;

/// <summary>
/// The state of this installation's activation (design §12.6.2).
/// </summary>
public enum ActivationState
{
    /// <summary>No activation is kept.</summary>
    None,

    /// <summary>An activation is kept and its last refresh, if any, succeeded.</summary>
    Active,

    /// <summary>Refreshes fail (the network, the service, a changed key); the kept license file is used until it ends.</summary>
    RefreshFailing,

    /// <summary>The service refused the license: revoked, suspended, or expired. The kept file is used until it ends.</summary>
    Refused,

    /// <summary>The service found another copy of this installation (design §10.4). The kept file is used until it ends.</summary>
    CloneDetected,
}

/// <summary>
/// The arguments of <see cref="ActivationClient.StateChanged"/>.
/// </summary>
/// <param name="previous">The state before.</param>
/// <param name="current">The state now.</param>
/// <param name="error">Why, when the change comes from a refusal or a failure.</param>
public sealed class ActivationStateChangedEventArgs(ActivationState previous, ActivationState current, LicenseError? error) : EventArgs
{
    /// <summary>Gets the state before.</summary>
    public ActivationState Previous { get; } = previous;

    /// <summary>Gets the state now.</summary>
    public ActivationState Current { get; } = current;

    /// <summary>Gets why, or <see langword="null"/>.</summary>
    public LicenseError? Error { get; } = error;
}

/// <summary>
/// The result of an activation, a trial, or a refresh: <see cref="Activated"/> or <see cref="Failed"/>.
/// </summary>
public abstract record ActivationResult
{
    private ActivationResult() { }

    /// <summary>The service issued a license file, which was checked and kept.</summary>
    /// <param name="License">The verified license.</param>
    public sealed record Activated(VerifiedLicense License) : ActivationResult;

    /// <summary>Nothing new was kept.</summary>
    /// <param name="Error">Why.</param>
    /// <param name="RetryAfter">How long the service asked to wait, or <see langword="null"/>.</param>
    public sealed record Failed(LicenseError Error, TimeSpan? RetryAfter) : ActivationResult;
}

/// <summary>
/// A client of the hosted activation service <c>elm-service</c> (design §12.4 to §12.6.2, §12.9): it activates this
/// installation with a license key or starts a trial, keeps the license file beside the installation key, refreshes it while
/// it runs, deactivates it, and checks it offline.
/// </summary>
/// <remarks>
/// The methods may be called from several threads; the calls of one client run one at a time, and the requests of one device,
/// across its processes, are sent one at a time under the lock file. <see cref="StateChanged"/> is raised outside the
/// client's lock; exceptions thrown by a handler are ignored. The service's TLS certificate is checked by the operating system.
/// </remarks>
public sealed class ActivationClient : IAsyncDisposable
{
    private const int MaxAppBytes = 64;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan LongestWait = TimeSpan.FromHours(24);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);
    private static readonly LicenseKind[] AnyKind = [LicenseKind.Standard, LicenseKind.Trial, LicenseKind.Server];

    // Set while the timer's refresh raises its events, so a handler that disposes the client does not wait for itself.
    private static readonly AsyncLocal<bool> OnTimer = new();

    private readonly ActivationClientOptions _options;
    private readonly Uri _service;
    private readonly InstallationKeyStore _keys;
    private readonly ActivationStore _files;
    private readonly IReadOnlyList<string> _fingerprints;
    private readonly ClientInfoBody? _clientInfo;
    private readonly LicenseVerifier _received;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly CancellationTokenSource _stopping = new();
    private InstallationKey? _key;
    private ECDsa? _guardKey;
    private IClockGuard? _guard;
    private LicenseVerifier? _verifier;
    private string? _nonce;
    private ITimer? _timer;
    private bool _missed;
    private Task _timerWork = Task.CompletedTask;
    private ActivationState _state;
    private volatile string? _activationId;
    private volatile bool _disposed;

    private ActivationClient(ActivationClientOptions options, InstallationKeyStore keys, InstallationKey? key, IReadOnlyList<string> fingerprints)
    {
        _options = options;
        _service = options.ServiceUrl.AbsoluteUri.EndsWith('/') ? options.ServiceUrl : new Uri(options.ServiceUrl.AbsoluteUri + "/");
        _keys = keys;
        _fingerprints = fingerprints;
        _time = options.TimeProvider;
        _files = new ActivationStore(keys.Directory, _time);
        _clientInfo = options.SendClientInfo ? LicenseClient.ClientInfo() : null;

        // A received file is checked without the clock guard, whose state the service's time then replaces (design §11.4 step 4).
        _received = new LicenseVerifier(options.Trust, _time, StatelessClockGuard.Instance, options.VerifierOptions);
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        _http = new HttpClient(options.WrapHandler?.Invoke(handler) ?? handler) { Timeout = RequestTimeout };
        if (key is not null)
        {
            UseKey(key);
        }
    }

    /// <summary>
    /// Occurs when the activation's state changes: an activation, a deactivation, a refresh that fails or recovers, a refusal,
    /// a detected clone, or an activation an administrator removed. Not raised after <see cref="DisposeAsync"/>.
    /// </summary>
    public event EventHandler<ActivationStateChangedEventArgs>? StateChanged;

    /// <summary>Gets the activation's state.</summary>
    public ActivationState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <summary>Gets the kept activation's ID, or <see langword="null"/>.</summary>
    public string? ActivationId => _activationId;

    /// <summary>
    /// Creates a client: reads the installation key when there is one (design §10.2; a new one is made by the first
    /// activation or trial), computes the machine fingerprints, reads the kept activation, and schedules its refresh. It
    /// does not contact the service.
    /// </summary>
    /// <param name="options">The settings.</param>
    /// <param name="client">The client, or <see langword="null"/> on failure.</param>
    /// <param name="error">Why it failed: <c>ELM_INSTALLATION_KEY_UNREADABLE</c>.</param>
    /// <returns><see langword="true"/> when the client was created.</returns>
    /// <exception cref="ArgumentException">The product ID is not valid, the service URL is neither https nor http on a loopback
    /// address, or the app version is longer than 64 bytes.</exception>
    public static bool TryCreate(ActivationClientOptions options, out ActivationClient? client, out LicenseError? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ServiceUrl);
        ArgumentNullException.ThrowIfNull(options.Trust);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        Uri url = options.ServiceUrl;
        if (!url.IsAbsoluteUri || !(url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)))
        {
            throw new ArgumentException("The service URL must be an absolute https URL, or http on a loopback address.", nameof(options));
        }

        if (options.AppVersion is not null && Encoding.UTF8.GetByteCount(options.AppVersion) > MaxAppBytes)
        {
            throw new ArgumentException("The app version is longer than 64 bytes.", nameof(options));
        }

        client = null;
        error = null;
        var keys = new InstallationKeyStore(options.ProductId, options.KeyScope, options.DataDirectory);
        InstallationKey? key = null;
        if (keys.HasKey && !keys.TryLoad(out key, out error))
        {
            return false;
        }

        client = new ActivationClient(options, keys, key, MachineFingerprint.Compute(options.ProductId));
        client.Start();
        return true;
    }

    /// <summary>
    /// Activates this installation with a license key (design §12.4). With an activation kept that is not a trial, it sends
    /// nothing and answers <c>ELM_INVALID_STATE</c>: deactivate first. A kept trial is replaced on success.
    /// </summary>
    /// <param name="licenseKey">The license key (design §9.5).</param>
    /// <param name="cancellationToken">Cancels the waits and the request; a canceled request ends as <c>ELM_NETWORK_ERROR</c>.</param>
    /// <returns>The license, or why there is none.</returns>
    /// <exception cref="ArgumentException">The license key is empty.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public Task<ActivationResult> ActivateAsync(string licenseKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(licenseKey);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return WithLockAsync(
            createKey: true,
            async (stored, events) =>
            {
                if (stored is { ActivationId: not null, Kind: not "trial" })
                {
                    return Fail(ErrorCode.InvalidState, "An activation is kept; deactivate it before activating another license.");
                }

                // Always seq 0 with the retry list: a new activation ignores it, and the retry of one whose answer was lost is
                // judged one behind the service's first sequence number (design §10.4 step 4), with a kept trial too.
                Reply reply = await SendRecordedAsync(
                        "v1/activations",
                        s =>
                            Body(w =>
                            {
                                w.WriteString("licenseKey", licenseKey);
                                w.WriteString("product", _options.ProductId);
                                WriteDevice(w);
                                w.WriteNumber("seq", 0);
                                WriteSequence(w, s, includeSeq: false, pending: true);
                            }),
                        stored,
                        includeKey: true,
                        cancellationToken,
                        pending: true
                    )
                    .ConfigureAwait(false);
                return Keep(reply, AnyKind, events, refreshing: false);
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Starts a trial of the product (design §12.9). With an activation kept, a trial included, it sends nothing and answers
    /// <c>ELM_INVALID_STATE</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the waits and the request.</param>
    /// <returns>The trial license, or why there is none.</returns>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public Task<ActivationResult> StartTrialAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return WithLockAsync(
            createKey: true,
            async (stored, events) =>
            {
                if (stored?.ActivationId is not null)
                {
                    return Fail(ErrorCode.InvalidState, "An activation is kept; deactivate it before starting a trial.");
                }

                Reply reply = await SendRecordedAsync(
                        "v1/trials",
                        s =>
                            Body(w =>
                            {
                                w.WriteString("product", _options.ProductId);
                                WriteDevice(w);
                                WriteSequence(w, s, includeSeq: false, pending: true);
                            }),
                        stored,
                        includeKey: true,
                        cancellationToken,
                        pending: true
                    )
                    .ConfigureAwait(false);
                return Keep(reply, [LicenseKind.Trial], events, refreshing: false);
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Refreshes the kept activation now (design §12.5). The client also refreshes on its own when the token's refresh window
    /// starts.
    /// </summary>
    /// <param name="cancellationToken">Cancels the waits and the request.</param>
    /// <returns>The new license, or why there is none.</returns>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public Task<ActivationResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return WithLockAsync(
            createKey: false,
            (stored, events) => RefreshCoreAsync(stored, onlyWhenDue: false, events, cancellationToken),
            cancellationToken
        );
    }

    /// <summary>
    /// Deactivates this installation (design §12.6): the service frees its place and the kept license file is removed. The
    /// installation key stays (design §12.6.2). An activation the service no longer has counts as deactivated.
    /// </summary>
    /// <param name="cancellationToken">Cancels the waits and the request.</param>
    /// <returns><see langword="null"/> when deactivated, otherwise why not; nothing is removed then.</returns>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public async Task<LicenseError?> DeactivateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ActivationResult result = await WithLockAsync(
                createKey: false,
                async (stored, events) =>
                {
                    if (stored?.ActivationId is not string id || _key is null)
                    {
                        return Fail(ErrorCode.InvalidState, "No activation is kept.");
                    }

                    Reply reply = await SendRecordedAsync(
                            $"v1/activations/{id}/deactivate",
                            s => Body(w => WriteSequence(w, s, includeSeq: true)),
                            stored,
                            includeKey: false,
                            cancellationToken
                        )
                        .ConfigureAwait(false);

                    // A retry after a lost answer finds the activation gone: it was deactivated (design §12.6.1).
                    if (reply.Error is LicenseError error && error.Code != ErrorCode.ActivationNotFound)
                    {
                        Refuse(error, events, refreshing: false);
                        return new ActivationResult.Failed(error, reply.RetryAfter);
                    }

                    if (!TryRemoveKept())
                    {
                        return Fail(ErrorCode.InternalError, "The activation file cannot be removed.");
                    }

                    StopTimer();
                    SetState(ActivationState.None, null, events);
                    return Fail(ErrorCode.Ok, "Deactivated.");
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return result is ActivationResult.Failed { Error.Code: not ErrorCode.Ok } failed ? failed.Error : null;
    }

    /// <summary>
    /// Checks the kept license file offline (design §11.2), bound to this installation's key and its machine fingerprints
    /// now. It reads the file again at each call and does not contact the service.
    /// </summary>
    /// <param name="featureId">The feature to check, or <see langword="null"/>.</param>
    /// <param name="version">The app's version of the feature (<c>major.minor[.patch]</c>), or <see langword="null"/>.</param>
    /// <param name="kind">The kind the app requires.</param>
    /// <returns>The verified license, or why it does not hold: <c>ELM_LICENSE_NOT_FOUND</c> when none is kept,
    /// <c>ELM_MALFORMED</c> when the kept file is damaged.</returns>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public LicenseVerificationResult Check(string? featureId, string? version, LicenseKind kind = LicenseKind.Standard)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StoredActivation? stored = _files.Read(out bool damaged);
        if (damaged)
        {
            return new LicenseVerificationResult.Invalid(new LicenseError(ErrorCode.Malformed, "The kept activation file is damaged."));
        }

        LicenseVerifier? verifier = Volatile.Read(ref _verifier);
        InstallationKey? key = Volatile.Read(ref _key);
        return stored?.License is byte[] license && verifier is not null && key is not null
            ? verifier.Verify(license, new LicenseRequirement(_options.ProductId, kind, featureId, version, key.Identity(_fingerprints)))
            : new LicenseVerificationResult.Invalid(new LicenseError(ErrorCode.LicenseNotFound, "No activation is kept."));
    }

    /// <summary>
    /// Stops the refresh timer, cancels a refresh that has started, and waits at most 5 seconds for it (design §17.2). The
    /// activation stays. A handler of <see cref="StateChanged"/> may call it.
    /// </summary>
    /// <returns>A task that ends when the client is disposed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopTimer();
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (!OnTimer.Value)
        {
            await Task.WhenAny(_timerWork, Task.Delay(DisposeTimeout, _time)).ConfigureAwait(false);
        }

        _http.Dispose();
        _key?.Dispose();
        _guardKey?.Dispose();
    }

    // Reads the kept activation and schedules its refresh, at the start.
    private void Start()
    {
        StoredActivation? stored = _files.Read(out _);
        if (stored?.ActivationId is string id)
        {
            _activationId = id;
            _state = ActivationState.Active;
            Schedule(DueIn(stored));
        }
    }

    private void UseKey(InstallationKey key)
    {
        _key = key;

        // design §11.4 step 3: the guard's state is signed with the installation key (a copy the guard keeps).
        if (_options.ClockGuard is null)
        {
            _guardKey = key.CopyForClockGuard();
        }

        _guard = _options.ClockGuard ?? new FileClockGuard(Path.Combine(_keys.Directory, "clock"), _guardKey);
        Volatile.Write(ref _verifier, new LicenseVerifier(_options.Trust, _time, _guard, _options.VerifierOptions));
    }

    private async Task<ActivationResult> RefreshCoreAsync(
        StoredActivation? stored,
        bool onlyWhenDue,
        List<ActivationStateChangedEventArgs> events,
        CancellationToken cancellationToken
    )
    {
        if (stored?.ActivationId is not string id || _key is null)
        {
            return Fail(ErrorCode.InvalidState, "No activation is kept.");
        }

        // Another process of this device may have refreshed while this one waited for the lock (design §12.5).
        if (onlyWhenDue && DueIn(stored) is TimeSpan wait && wait > TimeSpan.Zero)
        {
            Schedule(wait);
            return Fail(ErrorCode.InvalidState, "The refresh is not due.");
        }

        Reply reply = await SendRecordedAsync(
                $"v1/activations/{id}/refresh",
                s => Body(w => WriteSequence(w, s, includeSeq: true)),
                stored,
                includeKey: false,
                cancellationToken
            )
            .ConfigureAwait(false);
        return Keep(reply, AnyKind, events, refreshing: true);
    }

    // Keeps an issued license file once it verifies for this installation; a failure leaves the kept file as it was.
    private ActivationResult Keep(Reply reply, LicenseKind[] kinds, List<ActivationStateChangedEventArgs> events, bool refreshing)
    {
        if (reply.Error is LicenseError error)
        {
            Refuse(error, events, refreshing);
            return new ActivationResult.Failed(error, reply.RetryAfter);
        }

        if (ReadIssued(reply.Body) is not (string id, byte[] license))
        {
            return Unkept(new LicenseError(ErrorCode.Malformed, "The service's answer is not an issued license."), events, refreshing);
        }

        LicenseVerificationResult checkedFile = Verify(_received, license, kinds);
        if (checkedFile is not LicenseVerificationResult.Valid valid)
        {
            // A file this installation cannot use never replaces the kept one (design §12.6.2).
            return Unkept(((LicenseVerificationResult.Invalid)checkedFile).Error, events, refreshing);
        }

        // A refresh clears its own list and keeps the activations and trials not yet answered; a new activation clears both.
        var stored = new StoredActivation(id, valid.License.Sequence ?? 0, KindName(valid.License.Kind), license, [])
        {
            Pending = refreshing ? _files.Read(out _)?.Pending ?? [] : [],
        };
        try
        {
            _files.Write(stored);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The retry list keeps this request's jti, so the next request gets the same file again (design §10.4).
            return Unkept(new LicenseError(ErrorCode.InternalError, "The activation file cannot be written: " + e.Message), events, refreshing);
        }

        _guard?.AcceptServerTime(valid.License.IssuedAt);
        _activationId = id;
        SetState(ActivationState.Active, null, events);

        // From the file just checked, not from the kept one checked again through the clock guard (M4b2's measurement).
        // A refresh cannot move exp past the license's expiry (design §12.6), so after a saved file the next automatic
        // refresh waits at least the retry interval, or a refresh already due would be sent again at once (design §12.6.2).
        TimeSpan? due = DueIn(valid.License);
        Schedule(due < RetryInterval ? RetryInterval : due);
        return new ActivationResult.Activated(valid.License);
    }

    // A successful answer whose file is not kept: the retry list still names the request, and a refresh tells the state.
    private ActivationResult.Failed Unkept(LicenseError error, List<ActivationStateChangedEventArgs> events, bool refreshing)
    {
        if (refreshing && _activationId is not null)
        {
            SetState(ActivationState.RefreshFailing, error, events);
        }

        return new ActivationResult.Failed(error, null);
    }

    // design §12.6.2: what a refusal says about the kept activation.
    private void Refuse(LicenseError error, List<ActivationStateChangedEventArgs> events, bool refreshing)
    {
        switch (error.Code)
        {
            case ErrorCode.ActivationNotFound when refreshing:
                // An administrator removed it: the kept file is no longer this installation's activation.
                TryRemoveKept();
                StopTimer();
                SetState(ActivationState.None, error, events);
                break;
            case ErrorCode.CloneDetected when _activationId is not null:
                StopTimer();
                SetState(ActivationState.CloneDetected, error, events);
                break;
            case ErrorCode.LicenseRevoked or ErrorCode.LicenseSuspended or ErrorCode.Expired when refreshing:
                SetState(ActivationState.Refused, error, events);
                break;
            case ErrorCode.ProofInvalid when refreshing:
                // The key is not the activation's any more: refreshing again cannot help.
                StopTimer();
                SetState(ActivationState.RefreshFailing, error, events);
                break;
            default:
                if (refreshing && _activationId is not null)
                {
                    SetState(ActivationState.RefreshFailing, error, events);
                }

                break;
        }
    }

    private bool TryRemoveKept()
    {
        try
        {
            _files.Delete();
            _activationId = null;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private LicenseVerificationResult Verify(LicenseVerifier verifier, byte[] license, LicenseKind[] kinds)
    {
        LicenseVerificationResult result = new LicenseVerificationResult.Invalid(new LicenseError(ErrorCode.WrongKind, "No kind matched."));
        foreach (LicenseKind kind in kinds)
        {
            result = verifier.Verify(license, new LicenseRequirement(_options.ProductId, kind, null, null, _key!.Identity(_fingerprints)));
            if (result is not LicenseVerificationResult.Invalid { Error.Code: ErrorCode.WrongKind })
            {
                return result;
            }
        }

        return result;
    }

    // When the refresh is due: at the token's expiry less its refresh window; now when the kept file does not verify; never
    // (null) for a token without a refresh window.
    private TimeSpan? DueIn(StoredActivation stored)
    {
        if (stored.License is not byte[] license || Volatile.Read(ref _verifier) is not LicenseVerifier verifier)
        {
            return null;
        }

        if (Verify(verifier, license, AnyKind) is not LicenseVerificationResult.Valid valid)
        {
            return TimeSpan.Zero;
        }

        return DueIn(valid.License);
    }

    private TimeSpan? DueIn(VerifiedLicense license) =>
        license is { ExpiresAt: DateTimeOffset expires, RefreshWindow: TimeSpan window } ? expires - window - _time.GetUtcNow() : null;

    // Runs an operation under the client's gate and the device's lock file, on the activation read again under the lock;
    // raises the state events after both are released.
    private async Task<ActivationResult> WithLockAsync(
        bool createKey,
        Func<StoredActivation?, List<ActivationStateChangedEventArgs>, Task<ActivationResult>> operation,
        CancellationToken cancellationToken
    )
    {
        var events = new List<ActivationStateChangedEventArgs>();
        ActivationResult result;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_key is null && (createKey || _keys.HasKey))
            {
                // design §10.2: the key, and its folder where the lock file lives, are made at the first activation or trial;
                // another process of this device may have made it since this client started.
                InstallationKey? key;
                LicenseError? error;
                if (!(createKey ? _keys.TryLoadOrCreate(out key, out error) : _keys.TryLoad(out key, out error)))
                {
                    return new ActivationResult.Failed(error!, null);
                }

                UseKey(key!);
            }
            else if (!Directory.Exists(_keys.Directory))
            {
                return Fail(ErrorCode.InvalidState, "No activation is kept.");
            }

            (IDisposable? held, LicenseError? lockError) = await _files.LockAsync(cancellationToken).ConfigureAwait(false);
            using (held)
            {
                if (lockError is not null)
                {
                    return new ActivationResult.Failed(lockError, null);
                }

                result = await operation(_files.Read(out _), events).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        foreach (ActivationStateChangedEventArgs change in events)
        {
            if (_disposed)
            {
                break;
            }

            try
            {
                StateChanged?.Invoke(this, change);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // design §12.6.2: a handler's exception is ignored.
            }
        }

        return result;
    }

    // Sends a request with a proof (design §12.2). The proof's jti is written into the kept retry list before the request is
    // sent, so a request whose answer is lost is named when it is sent again (design §10.4 step 2); it is taken out again when
    // the outcome is known: an answer came, or the request did not leave. The nonce comes from the last answer, or from
    // GET /v1/nonce; ELM_NONCE_REQUIRED is answered by one more send with the new nonce.
    private async Task<Reply> SendRecordedAsync(
        string path,
        Func<StoredActivation?, byte[]> body,
        StoredActivation? stored,
        bool includeKey,
        CancellationToken cancellationToken,
        bool pending = false
    )
    {
        var target = new Uri(_service, path);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        Reply reply = default;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (stored is not null && stored.ListFor(pending).Count >= StoredActivation.MaxRetry)
            {
                return Reply.Failure(
                    new LicenseError(
                        ErrorCode.InvalidState,
                        "16 requests got no answer; an administrator deactivates this installation before it activates again."
                    )
                );
            }

            if (_nonce is null)
            {
                Reply nonceReply = await SendAsync(HttpMethod.Get, new Uri(_service, "v1/nonce"), null, null, linked.Token).ConfigureAwait(false);
                if (nonceReply.Error is not null)
                {
                    return nonceReply;
                }

                _nonce = LicenseClient.ReadNonce(nonceReply.Body);
                if (_nonce is null)
                {
                    return Reply.Failure(new LicenseError(ErrorCode.Malformed, "The service's nonce answer is not valid."));
                }
            }

            byte[] bytes = body(stored);
            string proof = ProofBuilder.Create(_key!, "POST", target, _nonce, bytes, includeKey, _time, out string proofId);
            StoredActivation before = stored ?? new StoredActivation(null, 0, null, null, []);
            stored = before.WithRetry(proofId, pending);
            try
            {
                _files.Write(stored);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Reply.Failure(new LicenseError(ErrorCode.InternalError, "The activation file cannot be written: " + e.Message));
            }

            reply = await SendAsync(HttpMethod.Post, target, bytes, proof, linked.Token).ConfigureAwait(false);
            if (!reply.Unanswered && reply.Error is not null)
            {
                // Refused, or never sent: this request is not the one the service acted on (design §12.6.2). A success keeps
                // its jti until its file is kept, so a file that cannot be kept is asked for again and is not a clone.
                stored = before;
                TryWrite(stored);
            }

            _nonce = reply.Nonce ?? (reply.Error?.Code == ErrorCode.NonceRequired ? null : _nonce);
            if (reply.Error?.Code != ErrorCode.NonceRequired)
            {
                return reply;
            }
        }

        return reply;
    }

    private void TryWrite(StoredActivation stored)
    {
        try
        {
            _files.Write(stored);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left in the list: a jti of a request the service answered does no harm (design §10.4 uses only the last file's).
        }
    }

    private async Task<Reply> SendAsync(HttpMethod method, Uri target, byte[]? body, string? proof, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, target);
        if (proof is not null)
        {
            request.Headers.Add(ProofBuilder.HeaderName, proof);
        }

        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            byte[]? content = await LicenseClient.ReadLimitedAsync(response.Content).ConfigureAwait(false);
            string? nonce = response.Headers.TryGetValues("ELM-Nonce", out IEnumerable<string>? nonces) ? nonces.FirstOrDefault() : null;
            TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
            if (content is null)
            {
                return Reply.Failure(new LicenseError(ErrorCode.Malformed, "The service's answer is larger than the limit."));
            }

            LicenseError? error = response.IsSuccessStatusCode ? null : LicenseClient.ProblemOf(response.StatusCode, content);
            return new Reply(content, error, nonce is { Length: <= 128 } ? nonce : null, retryAfter);
        }
        catch (HttpRequestException e)
            when (e.HttpRequestError
                    is HttpRequestError.NameResolutionError
                        or HttpRequestError.ConnectionError
                        or HttpRequestError.SecureConnectionError
                        or HttpRequestError.ProxyTunnelError
            )
        {
            // The request did not leave this device.
            return Reply.Failure(new LicenseError(ErrorCode.NetworkError, "The service cannot be reached: " + e.Message));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            return Reply.Failure(new LicenseError(ErrorCode.NetworkError, "The service did not answer: " + e.Message)) with { Unanswered = true };
        }
    }

    // {activationId, license} (design §12.6.1): the license file is kept as the bytes of its JSON.
    private static (string Id, byte[] License)? ReadIssued(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8, AllowDuplicateProperties = false });
            JsonElement root = document.RootElement;
            return
                root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("activationId", out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                && Guid.TryParseExact(id.GetString(), "D", out _)
                && root.TryGetProperty("license", out JsonElement license)
                && license.ValueKind == JsonValueKind.Object
                ? (id.GetString()!, Encoding.UTF8.GetBytes(license.GetRawText()))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string KindName(LicenseKind kind) =>
        kind switch
        {
            LicenseKind.Trial => "trial",
            LicenseKind.Server => "server",
            _ => "standard",
        };

    private static ActivationResult.Failed Fail(ErrorCode code, string message) => new(new LicenseError(code, message), null);

    private void WriteDevice(Utf8JsonWriter w)
    {
        w.WriteStartArray("fingerprints");
        foreach (string fingerprint in _fingerprints)
        {
            w.WriteStringValue(fingerprint);
        }

        w.WriteEndArray();
        if (_clientInfo is ClientInfoBody client)
        {
            w.WriteStartObject("client");
            w.WriteString("host", client.Host);
            w.WriteString("user", client.User);
            w.WriteEndObject();
        }

        if (_options.AppVersion is string app)
        {
            w.WriteString("app", app);
        }
    }

    // The kept sequence number (0 before the first activation answered, design §10.4 step 4) and the unanswered requests.
    private static void WriteSequence(Utf8JsonWriter w, StoredActivation? stored, bool includeSeq, bool pending = false)
    {
        if (includeSeq)
        {
            w.WriteNumber("seq", stored?.Seq ?? 0);
        }

        if (stored is not null && stored.ListFor(pending).Count > 0)
        {
            w.WriteStartArray("retry");
            foreach (string jti in stored.ListFor(pending))
            {
                w.WriteStringValue(jti);
            }

            w.WriteEndArray();
        }
    }

    private static byte[] Body(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private void SetState(ActivationState state, LicenseError? error, List<ActivationStateChangedEventArgs> events)
    {
        lock (_stateLock)
        {
            if (_state == state)
            {
                return;
            }

            events.Add(new ActivationStateChangedEventArgs(_state, state, error));
            _state = state;
        }
    }

    // One timer for the next refresh, waiting at most a day at a time (a timer cannot wait past about 49.7 days); a refresh not
    // yet due when it fires is scheduled again.
    private void Schedule(TimeSpan? due)
    {
        lock (_stateLock)
        {
            _timer?.Dispose();
            _timer = null;
            if (due is TimeSpan wait && !_disposed)
            {
                TimeSpan clamped =
                    wait < TimeSpan.Zero ? TimeSpan.Zero
                    : wait > LongestWait ? LongestWait
                    : wait;

                // Kept before it is started: a timer due now may fire at once, and its callback must see itself as the timer.
                _timer = _time.CreateTimer(_ => OnTimerFired(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _timer.Change(clamped, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void StopTimer()
    {
        lock (_stateLock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnTimerFired()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            if (!_timerWork.IsCompleted)
            {
                // The last refresh still runs; it schedules again when it ends (below), so this tick is not lost.
                _timer?.Dispose();
                _timer = null;
                _missed = true;
                return;
            }

            // The timer fired once and is done; the work schedules the next one, or the fallback below does.
            _timer?.Dispose();
            _timer = null;
            Task work = Task.Run(RefreshOnTimerAsync);
            _timerWork = work;

            // After the refresh has ended, so a tick it scheduled finds it ended and runs.
            _ = work.ContinueWith(_ => ScheduleMissed(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task RefreshOnTimerAsync()
    {
        OnTimer.Value = true;
        try
        {
            ActivationResult result = await WithLockAsync(
                    createKey: false,
                    (stored, events) => RefreshCoreAsync(stored, onlyWhenDue: true, events, _stopping.Token),
                    _stopping.Token
                )
                .ConfigureAwait(false);

            // A success and a refresh not yet due schedule the next one themselves. Any other failure, the lock held by another
            // process included, tries again an hour later, or later when the service asks (at most a day), while an activation
            // is kept and the state does not need the user or an administrator.
            if (
                result is ActivationResult.Failed failed
                && _activationId is not null
                && TimerIdle()
                && State is not ActivationState.CloneDetected
                && failed.Error.Code != ErrorCode.ProofInvalid
            )
            {
                TimeSpan wait = failed.RetryAfter is TimeSpan asked && asked > RetryInterval ? asked : RetryInterval;
                Schedule(wait > LongestWait ? LongestWait : wait);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException)
        {
            // The disk, a disposal, or a cancellation: try again at the next interval unless disposed.
            Schedule(RetryInterval);
        }
    }

    // A tick came while this refresh ran: schedule from the kept file now that the refresh has ended.
    private void ScheduleMissed()
    {
        bool missed;
        lock (_stateLock)
        {
            missed = _missed;
            _missed = false;
        }

        if (missed && _activationId is not null && TimerIdle() && _files.Read(out _) is StoredActivation kept)
        {
            Schedule(DueIn(kept));
        }
    }

    private bool TimerIdle()
    {
        lock (_stateLock)
        {
            return _timer is null;
        }
    }

    // Unanswered: the request may have reached the service, which may have acted on it.
    private readonly record struct Reply(byte[] Body, LicenseError? Error, string? Nonce, TimeSpan? RetryAfter)
    {
        public bool Unanswered { get; init; }

        public static Reply Failure(LicenseError error) => new([], error, null, null);
    }
}
