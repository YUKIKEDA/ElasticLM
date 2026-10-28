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
/// The result of making an offline request or deactivation proof (design §12.8.5): <see cref="Created"/> or
/// <see cref="Failed"/>.
/// </summary>
public abstract record OfflineRequestResult
{
    private OfflineRequestResult() { }

    /// <summary>The file to carry to the service: an <c>elm-actreq+jws</c> (<c>.elmreq</c>) or <c>elm-deact+jws</c>
    /// (<c>.elmdeact</c>).</summary>
    /// <param name="File">The file's text. An activation request holds the license key: keep it where others cannot read it.</param>
    /// <param name="Replaced">Whether a request that had waited past its expiry, or one for another key, was replaced: tell the
    /// user to discard the earlier file, which the service would take for a clone after this one (design §5.4).</param>
    public sealed record Created(string File, bool Replaced) : OfflineRequestResult;

    /// <summary>Nothing was made.</summary>
    /// <param name="Error">Why.</param>
    public sealed record Failed(LicenseError Error) : OfflineRequestResult;
}

/// <summary>
/// A client of the hosted activation service <c>elm-service</c> (design §12.4 to §12.6.2, §12.9): it activates this
/// installation with a license key or starts a trial, keeps the license file beside the installation key, refreshes it while
/// it runs, deactivates it, and checks it offline. A device that cannot reach the service makes request files and imports
/// their answers instead (design §12.8, §12.8.5).
/// </summary>
/// <remarks>
/// The methods may be called from several threads; the calls of one client run one at a time, and the requests of one device,
/// across its processes, are sent one at a time under the lock file. <see cref="StateChanged"/> is raised outside the
/// client's lock; exceptions thrown by a handler are ignored. The service's TLS certificate is checked by the operating system.
/// </remarks>
public sealed class ActivationClient : IAsyncDisposable
{
    private const int MaxAppBytes = 64;
    private const int MaxAnswerBytes = 256 * 1024;
    private const long RequestLifetime = 7 * 24 * 60 * 60;
    private const string NoService = "No service URL is set; use the offline requests.";
    private const string ProofKept = "A deactivation proof is kept; pass it to the service and forget it first.";
    private const string NotWaited = "The answer is not for the request this installation waits for.";
    private const string DamagedFile = "The kept activation file is damaged.";
    private const string Full = "16 requests got no answer; an administrator deactivates this installation before it activates again.";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan LongestWait = TimeSpan.FromHours(24);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);
    private static readonly LicenseKind[] AnyKind = [LicenseKind.Standard, LicenseKind.Trial, LicenseKind.Server];

    // Set while the timer's refresh raises its events, so a handler that disposes the client does not wait for itself.
    private static readonly AsyncLocal<bool> OnTimer = new();

    private readonly ActivationClientOptions _options;
    private readonly Uri? _service;
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
        _service =
            options.ServiceUrl is not Uri url ? null
            : url.AbsoluteUri.EndsWith('/') ? url
            : new Uri(url.AbsoluteUri + "/");
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
        ArgumentNullException.ThrowIfNull(options.Trust);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        if (
            options.ServiceUrl is Uri url
            && (!url.IsAbsoluteUri || !(url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)))
        )
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
        if (_service is null)
        {
            return Task.FromResult<ActivationResult>(Fail(ErrorCode.InvalidState, NoService));
        }

        return WithLockAsync(
            createKey: _options.CreateKey,
            async (stored, events) =>
            {
                if (stored?.DeactivationProof is not null)
                {
                    return Fail(ErrorCode.InvalidState, ProofKept);
                }

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
                return Keep(reply, Kinds(AnyKind), events, refreshing: false);
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
        if (_service is null)
        {
            return Task.FromResult<ActivationResult>(Fail(ErrorCode.InvalidState, NoService));
        }

        return WithLockAsync(
            createKey: _options.CreateKey,
            async (stored, events) =>
            {
                if (stored?.DeactivationProof is not null)
                {
                    return Fail(ErrorCode.InvalidState, ProofKept);
                }

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
                return Keep(reply, Kinds([LicenseKind.Trial]), events, refreshing: false);
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
        if (_service is null)
        {
            return Task.FromResult<ActivationResult>(Fail(ErrorCode.InvalidState, NoService));
        }

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
        if (_service is null)
        {
            return new LicenseError(ErrorCode.InvalidState, NoService);
        }

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
    /// Makes an offline activation request (design §12.8.1, §12.8.5) for a device that cannot reach the service. The same
    /// request is returned, signed again, until it expires (7 days) or its answer is imported; a new one names the requests
    /// before it, so an answer the service gave to one of them is not taken for a clone. The license key is not kept: each
    /// call passes it, and another key makes another request. With an activation kept that is not a trial, or a deactivation
    /// proof kept, nothing is made (<c>ELM_INVALID_STATE</c>).
    /// </summary>
    /// <param name="licenseKey">The license key (design §9.5). The request holds it.</param>
    /// <param name="renew"><see langword="true"/> makes a new request before the waiting one expires: for when the service
    /// answered <c>ELM_REQUEST_EXPIRED</c> because this device's clock is behind.</param>
    /// <param name="cancellationToken">Cancels the wait for the lock.</param>
    /// <returns>The request file's text, or why there is none: <c>ELM_MALFORMED</c> when the kept activation file is damaged.</returns>
    /// <exception cref="ArgumentException">The license key is empty.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public Task<OfflineRequestResult> CreateActivationRequestAsync(
        string licenseKey,
        bool renew = false,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(licenseKey);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return WithLockAsync<OfflineRequestResult>(
            createKey: _options.CreateKey,
            (stored, _) =>
                Task.FromResult<OfflineRequestResult>(
                    Damaged() ? OfflineFail(ErrorCode.Malformed, DamagedFile)
                    : stored?.DeactivationProof is not null ? OfflineFail(ErrorCode.InvalidState, ProofKept)
                    : stored is { ActivationId: not null, Kind: not "trial" }
                        ? OfflineFail(ErrorCode.InvalidState, "An activation is kept; deactivate it before activating another license.")
                    : MakeRequest(stored, licenseKey, renew)
                ),
            e => new OfflineRequestResult.Failed(e),
            null,
            cancellationToken
        );
    }

    /// <summary>
    /// Makes an offline refresh request for the kept activation (design §12.8.3, §12.8.5). The same request is returned until
    /// it expires or its answer is imported. Without an activation, nothing is made (<c>ELM_INVALID_STATE</c>). The refresh is
    /// due when the kept token's expiry less its <see cref="VerifiedLicense.RefreshWindow"/> has passed (see <see cref="Check"/>).
    /// </summary>
    /// <param name="renew"><see langword="true"/> makes a new request before the waiting one expires.</param>
    /// <param name="cancellationToken">Cancels the wait for the lock.</param>
    /// <returns>The request file's text, or why there is none.</returns>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public Task<OfflineRequestResult> CreateRefreshRequestAsync(bool renew = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return WithLockAsync<OfflineRequestResult>(
            createKey: false,
            (stored, _) =>
                Task.FromResult<OfflineRequestResult>(
                    Damaged() ? OfflineFail(ErrorCode.Malformed, DamagedFile)
                    : stored?.ActivationId is null || _key is null ? OfflineFail(ErrorCode.InvalidState, "No activation is kept.")
                    : MakeRequest(stored, null, renew)
                ),
            e => new OfflineRequestResult.Failed(e),
            null,
            cancellationToken
        );
    }

    /// <summary>
    /// Imports the service's answer to the waiting offline request (design §12.8.1 step 4, §12.8.5): it is checked like an
    /// online answer and kept. Importing the kept answer again changes nothing.
    /// </summary>
    /// <param name="response">The answer's text (<c>.elmresp</c>), at most 256 KiB.</param>
    /// <param name="cancellationToken">Cancels the wait for the lock.</param>
    /// <returns>The license, or why it was not kept: <c>ELM_REQUEST_MISMATCH</c> when it is not the answer to the waiting
    /// request, <c>ELM_MALFORMED</c> when it is not an answer, or the verifier's error.</returns>
    /// <exception cref="ArgumentNullException">The answer is null.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public Task<ActivationResult> ImportResponseAsync(string response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] bytes = Encoding.UTF8.GetBytes(response.Trim());
        return WithLockAsync<ActivationResult>(
            createKey: false,
            (stored, events) => Task.FromResult(Damaged() ? Fail(ErrorCode.Malformed, DamagedFile) : Import(stored, bytes, events)),
            e => new ActivationResult.Failed(e, null),
            new LicenseError(ErrorCode.RequestMismatch, NotWaited),
            cancellationToken
        );
    }

    /// <summary>
    /// Makes an offline deactivation proof (design §12.8.2, §12.8.5) and removes the kept activation; the installation key
    /// stays. The proof is kept, and returned again, until <see cref="ForgetDeactivationProofAsync"/>; while it is kept,
    /// activations and trials are refused, as the service still counts the activation until it gets the proof.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait for the lock.</param>
    /// <returns>The proof's text, or why there is none: <c>ELM_INVALID_STATE</c> without an activation.</returns>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public Task<OfflineRequestResult> CreateDeactivationProofAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return WithLockAsync<OfflineRequestResult>(
            createKey: false,
            (stored, events) => Task.FromResult(Damaged() ? OfflineFail(ErrorCode.Malformed, DamagedFile) : MakeDeactivationProof(stored, events)),
            e => new OfflineRequestResult.Failed(e),
            null,
            cancellationToken
        );
    }

    /// <summary>
    /// Forgets the kept deactivation proof once the user has passed it to the service (design §12.8.5), so this installation
    /// may activate again. Without a proof it does nothing.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait for the lock.</param>
    /// <returns><see langword="null"/> when no proof is kept any more, otherwise why: <c>ELM_MALFORMED</c> when the kept
    /// activation file is damaged, <c>ELM_INTERNAL_ERROR</c> when it cannot be written.</returns>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public async Task<LicenseError?> ForgetDeactivationProofAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ActivationResult result = await WithLockAsync<ActivationResult>(
                createKey: false,
                (stored, _) => Task.FromResult<ActivationResult>(Damaged() ? Fail(ErrorCode.Malformed, DamagedFile) : Forget(stored)),
                e => new ActivationResult.Failed(e, null),
                new LicenseError(ErrorCode.Ok, "Nothing is kept."),
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
        return Keep(reply, Kinds(AnyKind), events, refreshing: true);
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
            return Unkept(new LicenseError(ErrorCode.Malformed, "The service's answer is not an issued license."), events, refreshing, reply.Body);
        }

        LicenseVerificationResult checkedFile = Verify(_received, license, kinds);
        if (checkedFile is not LicenseVerificationResult.Valid valid)
        {
            // A file this installation cannot use never replaces the kept one (design §12.6.2).
            return Unkept(((LicenseVerificationResult.Invalid)checkedFile).Error, events, refreshing, reply.Body);
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
            return Unkept(new LicenseError(ErrorCode.InternalError, "The activation file cannot be written: " + e.Message), events, refreshing, null);
        }

        return Accept(valid.License, id, license, events, serverTime: true);
    }

    // After a new file was kept: the guard takes the service's time when the file comes straight from the service (an offline
    // answer may be days old, design §12.8.5), the state is Active, and the next refresh is scheduled.
    private ActivationResult.Activated Accept(
        VerifiedLicense license,
        string id,
        byte[] file,
        List<ActivationStateChangedEventArgs> events,
        bool serverTime
    )
    {
        // The service's time goes to the guard first: elm-server's callback reloads through the same guard, which a clock set
        // forward and back would otherwise still refuse (PR #79's review).
        if (serverTime)
        {
            _guard?.AcceptServerTime(license.IssuedAt);
        }

        try
        {
            _options.LicenseKept?.Invoke(file);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // The file is kept; what the owner does with it is its own (design §13.7).
        }

        _activationId = id;
        SetState(ActivationState.Active, null, events);

        // From the file just checked, not from the kept one checked again through the clock guard (M4b2's measurement).
        // A refresh cannot move exp past the license's expiry (design §12.6), so after a saved file the next automatic
        // refresh waits at least the retry interval, or a refresh already due would be sent again at once (design §12.6.2).
        TimeSpan? due = DueIn(license);
        Schedule(due < RetryInterval ? RetryInterval : due);
        return new ActivationResult.Activated(license);
    }

    // A successful answer whose file is not kept: the retry list still names the request, the answer's hash is kept so the
    // same file answered again does not add a jti (design §12.6.2), and a refresh tells the state.
    private ActivationResult.Failed Unkept(LicenseError error, List<ActivationStateChangedEventArgs> events, bool refreshing, byte[]? answer)
    {
        if (answer is not null && _files.Read(out _) is StoredActivation kept)
        {
            TryWrite(kept with { Unkept = AnswerHash(answer) });
        }

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

    // The kinds an answer may have: those of the operation, or only the one the options require (design §13.7).
    private LicenseKind[] Kinds(LicenseKind[] kinds) => _options.RequiredKind is LicenseKind only ? [only] : kinds;

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
    private Task<ActivationResult> WithLockAsync(
        bool createKey,
        Func<StoredActivation?, List<ActivationStateChangedEventArgs>, Task<ActivationResult>> operation,
        CancellationToken cancellationToken
    ) => WithLockAsync(createKey, operation, e => new ActivationResult.Failed(e, null), null, cancellationToken);

    // noFolder answers when there is neither a key nor its folder: by default "No activation is kept.".
    private async Task<T> WithLockAsync<T>(
        bool createKey,
        Func<StoredActivation?, List<ActivationStateChangedEventArgs>, Task<T>> operation,
        Func<LicenseError, T> fail,
        LicenseError? noFolder,
        CancellationToken cancellationToken
    )
    {
        var events = new List<ActivationStateChangedEventArgs>();
        T result;
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
                    return fail(error!);
                }

                UseKey(key!);
            }
            else if (!Directory.Exists(_keys.Directory))
            {
                return fail(noFolder ?? new LicenseError(ErrorCode.InvalidState, "No activation is kept."));
            }

            (IDisposable? held, LicenseError? lockError) = await _files.LockAsync(cancellationToken).ConfigureAwait(false);
            using (held)
            {
                if (lockError is not null)
                {
                    return fail(lockError);
                }

                StoredActivation? stored = _files.Read(out _);
                if (_options.RequiredKind is LicenseKind only && stored?.Kind is string kind && kind != KindName(only))
                {
                    // design §13.7: an activation of another kind in a shared folder is not this owner's to touch.
                    return fail(
                        new LicenseError(
                            ErrorCode.InvalidState,
                            "The kept activation is not a server license; give the server its own dataDirectory."
                        )
                    );
                }

                result = await operation(stored, events).ConfigureAwait(false);
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
        var target = new Uri(_service!, path);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        Reply reply = default;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (stored is not null && stored.ListFor(pending).Count + (stored.Offline is null ? 0 : 1) >= StoredActivation.MaxRetry)
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
                Reply nonceReply = await SendAsync(HttpMethod.Get, new Uri(_service!, "v1/nonce"), null, null, linked.Token).ConfigureAwait(false);
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
            else if (reply.Error is null && before.Unkept is string unkept && AnswerHash(reply.Body) == unkept)
            {
                // The file this installation did not keep, answered again: the service took this request for a resend
                // (design §10.4), so the jti it acted on is still the earlier one, and this one is not kept.
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
    // The hash of an answered file (of the whole body when it is not an issued license), to know it when it comes again.
    private static string AnswerHash(byte[] body) =>
        System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(ReadIssued(body) is (_, byte[] license) ? license : body));

    private static OfflineRequestResult.Failed OfflineFail(ErrorCode code, string message) => new(new LicenseError(code, message));

    // design §12.8.5 steps 2 to 4 of a request: the waiting one again while it holds, or a new one. A replaced request's nonce
    // joins the list of its kind, as an unanswered request, until a new file is kept (the design review of M4c2, R1 and R2).
    // The waiting one holds only while its list is as it was: an online request whose answer was lost since then must be
    // named too, or the carried file is one behind without it (PR #75's review).
    private OfflineRequestResult MakeRequest(StoredActivation? stored, string? licenseKey, bool renew)
    {
        bool refresh = licenseKey is null;
        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        string? keyHash = licenseKey is null ? null : KeyHash(licenseKey);
        StoredActivation current = stored ?? new StoredActivation(null, 0, null, null, []);
        if (
            current.Offline is OfflineRequest waiting
            && !renew
            && now < waiting.ExpiresAt
            && waiting.Refresh == refresh
            && waiting.KeyHash == keyHash
            && waiting.ActivationId == (refresh ? current.ActivationId : null)
            && waiting.Retry.SequenceEqual(current.ListFor(pending: !refresh), StringComparer.Ordinal)
        )
        {
            return new OfflineRequestResult.Created(Build(waiting, licenseKey), false);
        }

        bool replaced = current.Offline is not null;
        if (current.Offline is OfflineRequest earlier)
        {
            if (current.ListFor(!earlier.Refresh).Count >= StoredActivation.MaxRetry)
            {
                return OfflineFail(ErrorCode.InvalidState, Full);
            }

            current = current.WithRetry(earlier.Nonce, pending: !earlier.Refresh) with { Offline = null };
        }

        var request = new OfflineRequest(
            refresh,
            System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)),
            now,
            now + RequestLifetime,
            refresh ? current.Seq : 0,
            refresh ? current.ActivationId : null,
            keyHash,
            current.ListFor(pending: !refresh),
            _fingerprints
        );
        try
        {
            _files.Write(current with { Offline = request });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return OfflineFail(ErrorCode.InternalError, "The activation file cannot be written: " + e.Message);
        }

        return new OfflineRequestResult.Created(Build(request, licenseKey), replaced);
    }

    // The request's file (design §8.4.4), made the same way from the same claims each time, so the signing input the service
    // compares is the same; only the signature changes.
    private string Build(OfflineRequest request, string? licenseKey)
    {
        ECParameters key = _key!.PublicParameters();
        byte[] payload = Body(w =>
        {
            w.WriteNumber("ver", 1);
            w.WriteString("op", request.Refresh ? "refresh" : "activate");
            if (request.Refresh)
            {
                w.WriteString("act", request.ActivationId);
            }
            else
            {
                w.WriteString("key", licenseKey);
                w.WriteString("prd", _options.ProductId);
            }

            w.WriteNumber("seq", request.Seq);
            WriteList(w, "retry", request.Retry);
            WriteList(w, "fp", request.Fingerprints);
            w.WriteStartObject("jwk");
            w.WriteString("kty", "EC");
            w.WriteString("crv", "P-256");
            w.WriteString("x", System.Buffers.Text.Base64Url.EncodeToString(key.Q.X));
            w.WriteString("y", System.Buffers.Text.Base64Url.EncodeToString(key.Q.Y));
            w.WriteEndObject();
            w.WriteString("nonce", request.Nonce);
            w.WriteNumber("iat", request.IssuedAt);
            w.WriteNumber("exp", request.ExpiresAt);
        });
        return Sign(TokenTypes.ActivationRequest, payload);
    }

    private static string KeyHash(string licenseKey) =>
        System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(licenseKey)));

    private bool Damaged()
    {
        _files.Read(out bool damaged);
        return damaged;
    }

    // design §12.8.5: an answer is kept only when it is for the waiting request and verifies for this installation.
    private ActivationResult Import(StoredActivation? stored, byte[] bytes, List<ActivationStateChangedEventArgs> events)
    {
        if (bytes.Length > MaxAnswerBytes || ReadIssued(bytes) is not (string id, byte[] license))
        {
            return Fail(ErrorCode.Malformed, "The text is not an answer of the service.");
        }

        LicenseVerificationResult checkedFile = Verify(_received, license, Kinds(AnyKind));
        if (stored?.ActivationId == id && stored.License is byte[] kept && kept.AsSpan().SequenceEqual(license))
        {
            // The kept answer again: nothing changes (design §12.8.1).
            return checkedFile is LicenseVerificationResult.Valid same
                ? new ActivationResult.Activated(same.License)
                : new ActivationResult.Failed(((LicenseVerificationResult.Invalid)checkedFile).Error, null);
        }

        if (checkedFile is not LicenseVerificationResult.Valid valid)
        {
            return new ActivationResult.Failed(((LicenseVerificationResult.Invalid)checkedFile).Error, null);
        }

        // A refresh request is for the kept activation: whatever replaces the activation (an online answer, an import, a
        // deactivation proof) drops the waiting request too, so the nonce alone names the activation.
        if (stored?.Offline is not OfflineRequest waiting || valid.License.OfflineRequestNonce != waiting.Nonce)
        {
            return Fail(ErrorCode.RequestMismatch, NotWaited);
        }

        // As an online answer is kept: an activation clears both lists, a refresh only its own (design §12.6.2).
        var next = new StoredActivation(id, valid.License.Sequence ?? 0, KindName(valid.License.Kind), license, [])
        {
            Pending = waiting.Refresh ? stored.Pending : [],
        };
        try
        {
            _files.Write(next);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail(ErrorCode.InternalError, "The activation file cannot be written: " + e.Message);
        }

        return Accept(valid.License, id, license, events, serverTime: false);
    }

    // design §12.8.2, §12.8.5: the proof names the activation, its sequence number, and the requests that got no answer. The
    // activation requests that got none stay recorded with the proof.
    private OfflineRequestResult MakeDeactivationProof(StoredActivation? stored, List<ActivationStateChangedEventArgs> events)
    {
        if (stored?.DeactivationProof is string kept)
        {
            return new OfflineRequestResult.Created(kept, false);
        }

        if (stored?.ActivationId is not string id || _key is null)
        {
            return OfflineFail(ErrorCode.InvalidState, "No activation is kept.");
        }

        StoredActivation current = stored;
        if (current.Offline is OfflineRequest waiting)
        {
            if (current.ListFor(!waiting.Refresh).Count >= StoredActivation.MaxRetry)
            {
                return OfflineFail(ErrorCode.InvalidState, Full);
            }

            current = current.WithRetry(waiting.Nonce, pending: !waiting.Refresh) with { Offline = null };
        }

        byte[] payload = Body(w =>
        {
            w.WriteNumber("ver", 1);
            w.WriteString("act", id);
            w.WriteNumber("seq", current.Seq);
            WriteList(w, "retry", current.Retry);
            w.WriteNumber("iat", _time.GetUtcNow().ToUnixTimeSeconds());
        });
        string proof = Sign(TokenTypes.DeactivationProof, payload);
        try
        {
            _files.Write(new StoredActivation(null, 0, null, null, []) { Pending = current.Pending, DeactivationProof = proof });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return OfflineFail(ErrorCode.InternalError, "The activation file cannot be written: " + e.Message);
        }

        _activationId = null;
        StopTimer();
        SetState(ActivationState.None, null, events);
        return new OfflineRequestResult.Created(proof, false);
    }

    // The proof is dropped; the activation requests that got no answer stay recorded, for the next activation to name.
    private ActivationResult.Failed Forget(StoredActivation? stored)
    {
        if (stored?.DeactivationProof is null)
        {
            return Fail(ErrorCode.Ok, "Nothing is kept.");
        }

        try
        {
            if (stored.Pending.Count == 0)
            {
                _files.Delete();
            }
            else
            {
                _files.Write(stored with { DeactivationProof = null });
            }

            return Fail(ErrorCode.Ok, "Forgotten.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail(ErrorCode.InternalError, "The activation file cannot be written: " + e.Message);
        }
    }

    // A compact JWS signed by the installation key, with the header of design §8.2.
    private string Sign(string typ, byte[] payload)
    {
        byte[] header = JsonSerializer.SerializeToUtf8Bytes(new ProofHeader("ES256", _key!.Thumbprint, typ), ClientJsonContext.Default.ProofHeader);
        string input = System.Buffers.Text.Base64Url.EncodeToString(header) + "." + System.Buffers.Text.Base64Url.EncodeToString(payload);
        return input + "." + System.Buffers.Text.Base64Url.EncodeToString(_key.Sign(Encoding.ASCII.GetBytes(input)));
    }

    private static void WriteList(Utf8JsonWriter w, string name, IEnumerable<string> items)
    {
        w.WriteStartArray(name);
        foreach (string item in items)
        {
            w.WriteStringValue(item);
        }

        w.WriteEndArray();
    }

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

        // design §12.8.5: a waiting offline request is named too, so its answer, if the service processed it, is not a clone.
        IEnumerable<string> retry = stored is null ? [] : stored.ListFor(pending);
        if (stored?.Offline is OfflineRequest offline)
        {
            retry = retry.Append(offline.Nonce);
        }

        if (retry.Any())
        {
            w.WriteStartArray("retry");
            foreach (string jti in retry)
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
            if (due is TimeSpan wait && !_disposed && _service is not null)
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
