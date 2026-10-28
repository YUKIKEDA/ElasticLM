using System.Net;
using System.Net.Http.Headers;
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

    /// <summary>Refreshes fail (the network, the service); the kept license file is used until it ends.</summary>
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

    /// <summary>Nothing was kept.</summary>
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
/// The methods may be called from several threads; the requests of one device, across its processes, are sent one at a
/// time under the lock file. <see cref="StateChanged"/> is raised outside the client's lock; exceptions thrown by a handler
/// on a timer thread are ignored. The service's TLS certificate is checked by the operating system.
/// </remarks>
public sealed class ActivationClient : IAsyncDisposable
{
    private const int MaxAppBytes = 64;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    private readonly ActivationClientOptions _options;
    private readonly Uri _service;
    private readonly InstallationKeyStore _keys;
    private readonly ActivationStore _files;
    private readonly IReadOnlyList<string> _fingerprints;
    private readonly ClientInfoBody? _clientInfo;
    private readonly LicenseVerifier _verifier;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();
    private InstallationKey? _key;
    private string? _nonce;
    private ITimer? _timer;
    private Task _timerWork = Task.CompletedTask;
    private ActivationState _state;
    private volatile string? _activationId;
    private bool _disposed;

    private ActivationClient(ActivationClientOptions options, InstallationKeyStore keys, InstallationKey key, IReadOnlyList<string> fingerprints)
    {
        _options = options;
        _service = options.ServiceUrl.AbsoluteUri.EndsWith('/') ? options.ServiceUrl : new Uri(options.ServiceUrl.AbsoluteUri + "/");
        _keys = keys;
        _key = key;
        _fingerprints = fingerprints;
        _time = options.TimeProvider;
        _files = new ActivationStore(keys.Directory, _time);
        _clientInfo = options.SendClientInfo ? LicenseClient.ClientInfo() : null;
        IClockGuard guard = options.ClockGuard ?? new FileClockGuard(Path.Combine(keys.Directory, "clock"));
        _verifier = new LicenseVerifier(options.Trust, _time, guard, options.VerifierOptions);
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        _http = new HttpClient(options.WrapHandler?.Invoke(handler) ?? handler) { Timeout = RequestTimeout };
    }

    /// <summary>
    /// Occurs when the activation's state changes: an activation, a deactivation, a refresh that fails or recovers, a refusal,
    /// a detected clone, or an activation an administrator removed.
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
    /// Creates a client: loads or creates the installation key (design §10.2), computes the machine fingerprints, reads the
    /// kept activation, and schedules its refresh. It does not contact the service.
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
        var keys = new InstallationKeyStore(options.ProductId, options.KeyScope, options.DataDirectory);
        if (!keys.TryLoadOrCreate(out InstallationKey? key, out error))
        {
            return false;
        }

        client = new ActivationClient(options, keys, key!, MachineFingerprint.Compute(options.ProductId));
        client.Start();
        return true;
    }

    /// <summary>
    /// Activates this installation with a license key (design §12.4). A kept activation's sequence number and unanswered
    /// requests go with it, so the service judges a reactivation as design §10.4 says; on success the new activation replaces
    /// the kept one.
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
            async (stored, events) =>
            {
                Reply reply = await SendRecordedAsync(
                        "v1/activations",
                        s =>
                            Body(w =>
                            {
                                w.WriteString("licenseKey", licenseKey);
                                WriteDevice(w);
                                WriteSequence(w, s, includeSeq: s is not null);
                            }),
                        stored,
                        includeKey: true,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return Keep(reply, [LicenseKind.Standard, LicenseKind.Trial, LicenseKind.Server], refused: stored?.ActivationId is not null, events);
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Starts a trial of the product (design §12.9). With an activation kept it sends nothing and answers
    /// <c>ELM_INVALID_STATE</c>, so a trial never replaces a bought license.
    /// </summary>
    /// <param name="cancellationToken">Cancels the waits and the request.</param>
    /// <returns>The trial license, or why there is none.</returns>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public Task<ActivationResult> StartTrialAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return WithLockAsync(
            async (stored, events) =>
            {
                if (stored?.ActivationId is not null)
                {
                    return new ActivationResult.Failed(
                        new LicenseError(ErrorCode.InvalidState, "An activation is kept; deactivate it before starting a trial."),
                        null
                    );
                }

                Reply reply = await SendRecordedAsync(
                        "v1/trials",
                        s =>
                            Body(w =>
                            {
                                w.WriteString("product", _options.ProductId);
                                WriteDevice(w);
                                WriteSequence(w, s, includeSeq: false);
                            }),
                        stored,
                        includeKey: true,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return Keep(reply, [LicenseKind.Trial], refused: false, events);
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
        return WithLockAsync((stored, events) => RefreshCoreAsync(stored, onlyWhenDue: false, events, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Deactivates this installation (design §12.6): the service frees its place, and the license file and the installation
    /// key are removed, so a copy of the file left behind cannot be used. An activation the service no longer has counts as
    /// deactivated.
    /// </summary>
    /// <param name="cancellationToken">Cancels the waits and the request.</param>
    /// <returns><see langword="null"/> when deactivated, otherwise why not; nothing is removed then.</returns>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public async Task<LicenseError?> DeactivateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ActivationResult result = await WithLockAsync(
                async (stored, events) =>
                {
                    if (stored?.ActivationId is not string id)
                    {
                        return new ActivationResult.Failed(new LicenseError(ErrorCode.InvalidState, "No activation is kept."), null);
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
                        Refuse(error, events);
                        return new ActivationResult.Failed(error, reply.RetryAfter);
                    }

                    _files.Delete();
                    _keys.Delete();
                    _key?.Dispose();
                    _key = null;
                    _activationId = null;
                    StopTimer();
                    SetState(ActivationState.None, null, events);
                    return null!;
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return (result as ActivationResult.Failed)?.Error;
    }

    /// <summary>
    /// Checks the kept license file offline (design §11.2), bound to this installation's key and its machine fingerprints
    /// now. It does not contact the service.
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

        return stored?.License is byte[] license && _key is InstallationKey key
            ? _verifier.Verify(license, new LicenseRequirement(_options.ProductId, kind, featureId, version, key.Identity(_fingerprints)))
            : new LicenseVerificationResult.Invalid(new LicenseError(ErrorCode.LicenseNotFound, "No activation is kept."));
    }

    /// <summary>Stops the refresh timer and waits for a refresh that has started. The activation stays.</summary>
    /// <returns>A task that ends when the client is disposed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopTimer();
        await _timerWork.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        _http.Dispose();
        _key?.Dispose();
        _gate.Dispose();
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

    private async Task<ActivationResult> RefreshCoreAsync(
        StoredActivation? stored,
        bool onlyWhenDue,
        List<ActivationStateChangedEventArgs> events,
        CancellationToken cancellationToken
    )
    {
        if (stored?.ActivationId is not string id)
        {
            return new ActivationResult.Failed(new LicenseError(ErrorCode.InvalidState, "No activation is kept."), null);
        }

        // Another process of this device may have refreshed while this one waited for the lock (design §12.5).
        if (onlyWhenDue && DueIn(stored) is TimeSpan wait && wait > TimeSpan.Zero)
        {
            Schedule(wait);
            return new ActivationResult.Failed(new LicenseError(ErrorCode.InvalidState, "The refresh is not due."), null);
        }

        Reply reply = await SendRecordedAsync(
                $"v1/activations/{id}/refresh",
                s => Body(w => WriteSequence(w, s, includeSeq: true)),
                stored,
                includeKey: false,
                cancellationToken
            )
            .ConfigureAwait(false);
        return Keep(reply, [LicenseKind.Standard, LicenseKind.Trial, LicenseKind.Server], refused: true, events, refreshing: true);
    }

    // Keeps an issued license file once it verifies for this installation; a failure leaves the kept file as it was.
    private ActivationResult Keep(
        Reply reply,
        LicenseKind[] kinds,
        bool refused,
        List<ActivationStateChangedEventArgs> events,
        bool refreshing = false
    )
    {
        if (reply.Error is LicenseError error)
        {
            if (refused)
            {
                Refuse(error, events, refreshing);
            }

            return new ActivationResult.Failed(error, reply.RetryAfter);
        }

        if (ReadIssued(reply.Body) is not (string id, byte[] license))
        {
            return new ActivationResult.Failed(new LicenseError(ErrorCode.Malformed, "The service's answer is not an issued license."), null);
        }

        LicenseVerificationResult checkedFile = Verify(license, kinds);
        if (checkedFile is not LicenseVerificationResult.Valid valid)
        {
            // A file this installation cannot use never replaces the kept one (design §12.6.2).
            return new ActivationResult.Failed(((LicenseVerificationResult.Invalid)checkedFile).Error, null);
        }

        var stored = new StoredActivation(id, valid.License.Sequence ?? 0, license, []);
        _files.Write(stored);
        _activationId = id;
        SetState(ActivationState.Active, null, events);
        Schedule(DueIn(stored));
        return new ActivationResult.Activated(valid.License);
    }

    // design §12.6.2: what a refusal of the kept activation says about it.
    private void Refuse(LicenseError error, List<ActivationStateChangedEventArgs> events, bool refreshing = false)
    {
        switch (error.Code)
        {
            case ErrorCode.ActivationNotFound:
                // An administrator removed it: the kept file is no longer this installation's activation.
                _files.Delete();
                _activationId = null;
                StopTimer();
                SetState(ActivationState.None, error, events);
                break;
            case ErrorCode.CloneDetected:
                SetState(ActivationState.CloneDetected, error, events);
                break;
            case ErrorCode.LicenseRevoked or ErrorCode.LicenseSuspended or ErrorCode.Expired:
                SetState(ActivationState.Refused, error, events);
                break;
            default:
                if (refreshing && _activationId is not null)
                {
                    SetState(ActivationState.RefreshFailing, error, events);
                }

                break;
        }
    }

    private LicenseVerificationResult Verify(byte[] license, LicenseKind[] kinds)
    {
        LicenseVerificationResult result = new LicenseVerificationResult.Invalid(new LicenseError(ErrorCode.WrongKind, "No kind matched."));
        foreach (LicenseKind kind in kinds)
        {
            result = _verifier.Verify(license, new LicenseRequirement(_options.ProductId, kind, null, null, _key!.Identity(_fingerprints)));
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
        if (stored.License is not byte[] license || _key is null)
        {
            return null;
        }

        if (Verify(license, [LicenseKind.Standard, LicenseKind.Trial, LicenseKind.Server]) is not LicenseVerificationResult.Valid valid)
        {
            return TimeSpan.Zero;
        }

        return valid.License is { ExpiresAt: DateTimeOffset expires, RefreshWindow: TimeSpan window } ? expires - window - _time.GetUtcNow() : null;
    }

    // Runs an operation under the client's gate and the device's lock file, on the activation read again under the lock;
    // raises the state events after both are released.
    private async Task<ActivationResult> WithLockAsync(
        Func<StoredActivation?, List<ActivationStateChangedEventArgs>, Task<ActivationResult>> operation,
        CancellationToken cancellationToken
    )
    {
        var events = new List<ActivationStateChangedEventArgs>();
        ActivationResult result;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using IDisposable? held = await _files.LockAsync(cancellationToken).ConfigureAwait(false);
            if (held is null)
            {
                return new ActivationResult.Failed(
                    new LicenseError(ErrorCode.InvalidState, "Another process of this device holds the activation lock."),
                    null
                );
            }

            if (_key is null)
            {
                // A deactivation removed the key; a new activation makes a new one (design §10.2).
                if (!_keys.TryLoadOrCreate(out InstallationKey? key, out LicenseError? error))
                {
                    return new ActivationResult.Failed(error!, null);
                }

                _key = key;
            }

            result = await operation(_files.Read(out _), events).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        foreach (ActivationStateChangedEventArgs change in events)
        {
            StateChanged?.Invoke(this, change);
        }

        return result;
    }

    // Sends a request with a proof (design §12.2). Before each send, the proof's jti is written into the kept activation's
    // retry list, so a request whose answer is lost is named when it is sent again (design §10.4 step 2). The nonce comes
    // from the last answer, or from GET /v1/nonce; ELM_NONCE_REQUIRED is answered by one more send with the new nonce.
    private async Task<Reply> SendRecordedAsync(
        string path,
        Func<StoredActivation?, byte[]> body,
        StoredActivation? stored,
        bool includeKey,
        CancellationToken cancellationToken
    )
    {
        var target = new Uri(_service, path);
        Reply reply = default;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (_nonce is null)
            {
                Reply nonceReply = await SendAsync(HttpMethod.Get, new Uri(_service, "v1/nonce"), null, null, cancellationToken)
                    .ConfigureAwait(false);
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
            stored = (stored ?? new StoredActivation(null, 0, null, [])).WithRetry(proofId);
            _files.Write(stored);
            reply = await SendAsync(HttpMethod.Post, target, bytes, proof, cancellationToken).ConfigureAwait(false);
            _nonce = reply.Nonce ?? (reply.Error?.Code == ErrorCode.NonceRequired ? null : _nonce);
            if (reply.Error?.Code != ErrorCode.NonceRequired)
            {
                return reply;
            }
        }

        return reply;
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
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return Reply.Failure(new LicenseError(ErrorCode.NetworkError, "The service cannot be reached: " + e.Message));
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
    private static void WriteSequence(Utf8JsonWriter w, StoredActivation? stored, bool includeSeq)
    {
        if (includeSeq)
        {
            w.WriteNumber("seq", stored?.Seq ?? 0);
        }

        if (stored is { Retry.Count: > 0 })
        {
            w.WriteStartArray("retry");
            foreach (string jti in stored.Retry)
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

    // One timer for the next refresh; a failed one is tried again an hour later, or later when the service asks.
    private void Schedule(TimeSpan? due)
    {
        lock (_stateLock)
        {
            _timer?.Dispose();
            _timer =
                due is TimeSpan wait && !_disposed
                    ? _time.CreateTimer(_ => OnTimer(), null, wait < TimeSpan.Zero ? TimeSpan.Zero : wait, Timeout.InfiniteTimeSpan)
                    : null;
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

    private void OnTimer()
    {
        lock (_stateLock)
        {
            if (_disposed || !_timerWork.IsCompleted)
            {
                return;
            }

            // The timer fired once and is done; the work schedules the next one, or the fallback below does.
            _timer?.Dispose();
            _timer = null;
            _timerWork = Task.Run(RefreshOnTimerAsync);
        }
    }

    private async Task RefreshOnTimerAsync()
    {
        try
        {
            ActivationResult result = await WithLockAsync(
                    (stored, events) => RefreshCoreAsync(stored, onlyWhenDue: true, events, CancellationToken.None),
                    CancellationToken.None
                )
                .ConfigureAwait(false);

            // A success and a refresh not yet due schedule the next one themselves. Any other failure, the lock held by another
            // process included, tries again an hour later, or later when the service asks, while an activation is kept.
            if (result is ActivationResult.Failed failed && _activationId is not null && TimerIdle())
            {
                Schedule(failed.RetryAfter is TimeSpan asked && asked > RetryInterval ? asked : RetryInterval);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // The disk or a disposal: try again at the next interval.
            Schedule(RetryInterval);
        }
    }

    private bool TimerIdle()
    {
        lock (_stateLock)
        {
            return _timer is null;
        }
    }

    // Unanswered requests are named in the next one's retry list.
    private readonly record struct Reply(byte[] Body, LicenseError? Error, string? Nonce, TimeSpan? RetryAfter)
    {
        public static Reply Failure(LicenseError error) => new([], error, null, null);
    }
}
