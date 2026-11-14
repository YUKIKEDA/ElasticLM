using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.Client;

// design §12.7, §13.5, §12.7.6, decision 0.94.

/// <summary>
/// A client of an on-premises license server: it checks the server against the vendor's trust store
/// and its pin, acquires leases, renews them every third of their duration, reclaims them after a server restart, and
/// returns them. With <see cref="LicenseClientOptions.LicenseKey"/> it leases from <c>elm-service</c> instead: TLS is
/// checked with the OS's trust, leases with the service's certificates, and a lease the
/// service does not know is lost, not reclaimed. It has the lifetime rules of the C ABI: <see cref="DisposeAsync"/> stops the
/// timers and returns the leases it holds that are not borrows. For an acquisition whose answer does not come or is not
/// taken, it asks the server to release the lease issued for that request.
/// </summary>
/// <remarks>
/// The methods may be called from several threads; requests to the server are made one at a time. <see cref="LeaseStateChanged"/>
/// is raised on a timer thread, outside the client's lock, so a handler may call <see cref="ReleaseLeaseAsync"/>.
/// Exceptions thrown by a handler on a timer thread are ignored.
/// </remarks>
public sealed class LicenseClient : IAsyncDisposable
{
    private const string AnswerTooLarge = "The server's answer is larger than the limit.";
    private const int MaxClientNameBytes = 255;
    private const int MaxAppBytes = 64;
    private const int MaxIdentities = 8;
    private const int MaxReclaimTokens = 64;
    private const int MaxBorrowFileLength = 256 * 1024;
    private const int MaxBorrowFiles = 1_000;
    private const int MaxCertificates = 4; // design §8.6
    private const int MaxCertificateLength = 16 * 1024; // design §8.6: one token
    private const int MaxOwedReleases = 16; // design §8.6, §12.7.7
    private const string BorrowFolder = "borrows";
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly LicenseClientOptions _options;
    private readonly Uri _server;
    private readonly InstallationKey _key;
    private readonly System.Security.Cryptography.ECDsa? _guardKey;
    private readonly IReadOnlyList<string> _fingerprints;
    private readonly ClientInfoBody? _clientInfo;
    private readonly LicenseVerifier _licenseVerifier;
    private readonly LeaseVerifier _leaseVerifier;
    private readonly ServerPins _pins;
    private readonly string _storeDirectory;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly bool _hosted;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Lease> _leases = [];

    // design §12.7.7 (V1d5b): the proof IDs of acquisitions and borrows whose answer the client did not take and whose release
    // has not got through, oldest first; kept in memory only, under the lock.
    private readonly List<string> _owedReleases = [];

    // Server identities by server license jti: the current one, and older ones that leases issued under them still need.
    private readonly List<ServerIdentity> _identities = [];

    // The server license file and key JSON behind each identity, by server license jti: a borrow is saved with them.
    private readonly Dictionary<string, (byte[] License, byte[] Key)> _identityFiles = new(StringComparer.Ordinal);
    private ServerIdentity? _current;
    private volatile string? _pinnedKeyId;
    private volatile bool _pinFailed;
    private string? _nonce;
    private volatile bool _disposed;

    private LicenseClient(LicenseClientOptions options, InstallationKeyStore store, InstallationKey key, IReadOnlyList<string> fingerprints)
    {
        _options = options;
        _server = options.ServerUrl.AbsoluteUri.EndsWith('/') ? options.ServerUrl : new Uri(options.ServerUrl.AbsoluteUri + "/");
        _key = key;
        _fingerprints = fingerprints;
        _time = options.TimeProvider;
        _hosted = options.LicenseKey is not null;
        _clientInfo = options.SendClientInfo ? ClientInfo() : null;

        // design §11.4 step 3: the guard's state is signed with the installation key (a copy the guard keeps).
        _guardKey = options.ClockGuard is null ? key.CopyForClockGuard() : null;
        IClockGuard guard = options.ClockGuard ?? new FileClockGuard(Path.Combine(store.Directory, "clock"), _guardKey);
        _licenseVerifier = new LicenseVerifier(options.Trust, _time, guard, options.VerifierOptions);
        _leaseVerifier = new LeaseVerifier(options.Trust, _time, guard, options.VerifierOptions);
        _pins = new ServerPins(store.Directory, _time);
        _storeDirectory = store.Directory;

        // design §12.7.6 (M4d2's Grill): elm-service is checked with the OS's trust, as ActivationClient does.
        // design §12.1: a redirect would send the body, a license key with it, to another place; a 3xx is an error.
        SocketsHttpHandler handler = BoundedAnswer.CreateHandler();
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2);
        handler.SslOptions = _hosted
            ? new SslClientAuthenticationOptions()
            : new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = ValidateServerCertificate };
        _http = BoundedAnswer.CreateClient(options.WrapHandler?.Invoke(handler) ?? handler);
    }

    /// <summary>
    /// Occurs when a lease changes state: renewals that fail or recover, reclaims, losses, and releases.
    /// </summary>
    public event EventHandler<LeaseStateChangedEventArgs>? LeaseStateChanged;

    /// <summary>Gets the thumbprint of this installation's key, which leases are bound to.</summary>
    public string InstallationKeyThumbprint => _key.Thumbprint;

    // design §10.2.

    /// <summary>
    /// Creates a client: loads or creates the installation key and computes the machine fingerprints. It does
    /// not contact the server.
    /// </summary>
    /// <param name="options">The settings.</param>
    /// <param name="client">The client, or <see langword="null"/> on failure.</param>
    /// <param name="error">Why it failed: <c>ELM_INSTALLATION_KEY_UNREADABLE</c>.</param>
    /// <returns><see langword="true"/> when the client was created.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/>, its server URL, its trust store, or its time
    /// provider is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The product ID is not valid; the server URL is not an absolute https URL (or, with
    /// a license key, http on a loopback address); the license key is empty or comes with an expected server license ID; or
    /// the app version is longer than 64 bytes.</exception>
    public static bool TryCreate(LicenseClientOptions options, out LicenseClient? client, out LicenseError? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ServerUrl);
        ArgumentNullException.ThrowIfNull(options.Trust);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        if (options.LicenseKey is string licenseKey)
        {
            Uri url = options.ServerUrl;
            if (!url.IsAbsoluteUri || !(url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)))
            {
                throw new ArgumentException("The service URL must be an absolute https URL, or http on a loopback address.", nameof(options));
            }

            if (licenseKey.Length == 0 || options.ExpectedServerLicenseId is not null)
            {
                throw new ArgumentException("A license key must not be empty, and has no expected server license ID.", nameof(options));
            }
        }
        else if (!options.ServerUrl.IsAbsoluteUri || options.ServerUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The server URL must be an absolute https URL.", nameof(options));
        }

        if (options.AppVersion is not null && Encoding.UTF8.GetByteCount(options.AppVersion) > MaxAppBytes)
        {
            throw new ArgumentException("The app version is longer than 64 bytes.", nameof(options));
        }

        client = null;
        var store = new InstallationKeyStore(options.ProductId, options.KeyScope, options.DataDirectory);
        if (!store.TryLoadOrCreate(out InstallationKey? key, out error))
        {
            return false;
        }

        client = new LicenseClient(options, store, key!, MachineFingerprint.Compute(options.ProductId));
        return true;
    }

    // design §12.7.1, §13.5, §12.7.5.

    /// <summary>
    /// Acquires a lease and starts renewing it. From an on-premises server, the first call fetches and checks
    /// the server license and the server's key; from <c>elm-service</c>, each lease is checked with the
    /// certificates of its answer. When the answer does not come or the client does not take it, the client asks the server
    /// to release whatever lease it issued for this request, before this call returns or, if that does not get through,
    /// before its next request to the server and when it is disposed.
    /// </summary>
    /// <param name="featureId">The feature.</param>
    /// <param name="version">The app's version of the feature (<c>major.minor[.patch]</c>), or <see langword="null"/>.</param>
    /// <param name="count">The number of seats, at least 1.</param>
    /// <param name="cancellationToken">Cancels the wait for the client's lock and the requests; a canceled request ends as
    /// <c>ELM_NETWORK_ERROR</c>.</param>
    /// <returns>The lease, or why there is none: <c>ELM_REQUEST_MISMATCH</c> when the server's answer carries a lease that is
    /// not the answer to this request, which the client neither keeps nor returns by its ID; from an on-premises server,
    /// <c>ELM_FEATURE_VERSION_NOT_LICENSED</c> when <paramref name="version"/> is outside the feature's range in the server
    /// license, even if the server lent a lease, which the client then releases.</returns>
    /// <exception cref="ArgumentException">The feature ID is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is less than 1.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public Task<LeaseResult> AcquireLeaseAsync(string featureId, string? version = null, int count = 1, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(featureId);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        return AcquireCoreAsync(featureId, version, count, borrow: null, cancellationToken);
    }

    // design §12.7.4.

    /// <summary>
    /// Borrows a lease for a period: it is not renewed, is valid until it expires, and is saved next to the
    /// installation key with what verifies it (the server license and key, or the certificates from <c>elm-service</c>), so
    /// <see cref="GetBorrowedLeases"/> finds it after a restart without the server. <see cref="ReleaseLeaseAsync"/> returns it early within the server's rules. A borrow
    /// whose answer does not come, is not taken, or cannot be saved is released as an acquisition is, and the server counts it
    /// as an early return.
    /// </summary>
    /// <param name="featureId">The feature.</param>
    /// <param name="period">The borrow period, at least one second; the server refuses more than it allows.</param>
    /// <param name="version">The app's version of the feature, or <see langword="null"/>.</param>
    /// <param name="count">The number of seats, at least 1.</param>
    /// <param name="cancellationToken">Cancels the wait for the client's lock and the requests.</param>
    /// <returns>The borrowed lease, or why there is none: <c>ELM_REQUEST_MISMATCH</c> when the server's answer carries a lease
    /// that is not the answer to this request, which the client neither keeps nor returns by its ID; from an on-premises
    /// server, <c>ELM_FEATURE_VERSION_NOT_LICENSED</c> when <paramref name="version"/> is outside the feature's range in the
    /// server license, even if the server lent a borrow, which the client then releases.</returns>
    /// <exception cref="ArgumentException">The feature ID is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is less than 1, or the period is shorter
    /// than one second.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public Task<LeaseResult> BorrowLeaseAsync(
        string featureId,
        TimeSpan period,
        string? version = null,
        int count = 1,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(featureId);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, TimeSpan.FromSeconds(1));
        return AcquireCoreAsync(featureId, version, count, (long)period.TotalSeconds, cancellationToken);
    }

    // design §12.7.5.

    /// <summary>
    /// Reads the borrowed leases saved for this server (at most 1,000 files) and checks each as when it was borrowed: with
    /// the server license and key saved beside it and against the server pin or the expected server license,
    /// or, from <c>elm-service</c>, with the certificates saved beside it; no request is sent. A client reads only the files
    /// of its own URL and mode. Expired borrows are deleted; a file that does not verify is left alone and not returned.
    /// </summary>
    /// <returns>The borrowed leases that are still valid.</returns>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public IReadOnlyList<Lease> GetBorrowedLeases()
    {
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var borrowed = new List<Lease>();
            string folder = Path.Combine(_storeDirectory, BorrowFolder);
            if (!Directory.Exists(folder))
            {
                return borrowed;
            }

            foreach (string path in Directory.EnumerateFiles(folder, "*.json").Take(MaxBorrowFiles))
            {
                if (LoadBorrow(path) is Lease lease)
                {
                    Lease? known = _leases.Find(l => l.Id == lease.Id);
                    if (known is null)
                    {
                        _leases.Add(lease);
                    }

                    borrowed.Add(known ?? lease);
                }
            }

            return borrowed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LeaseResult> AcquireCoreAsync(string featureId, string? version, int count, long? borrow, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false) is LicenseError connectError)
            {
                return new LeaseResult.Failed(connectError, null);
            }

            await SendOwedReleaseAsync(cancellationToken).ConfigureAwait(false);
            var body = new AcquireRequestBody(
                featureId,
                version,
                count,
                _fingerprints.Count > 0 ? _fingerprints : null,
                _clientInfo,
                _options.AppVersion,
                borrow,
                _options.LicenseKey,
                _hosted ? _options.ProductId : null
            );
            Reply reply = await SendSignedAsync(
                    HttpMethod.Post,
                    "v1/leases",
                    JsonSerializer.SerializeToUtf8Bytes(body, ClientWireContext.Default.AcquireRequestBody),
                    includeKey: true,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (reply.Error is not null)
            {
                if (reply.Unanswered)
                {
                    // design §12.7.7: the server may have issued a lease for this request; no request after it names one.
                    await ReleaseUntakenAsync(reply.ProofId, cancellationToken).ConfigureAwait(false);
                }

                return new LeaseResult.Failed(reply.Error, reply.RetryAfter);
            }

            (LeaseVerificationResult result, IReadOnlyList<string> certificates, ServerIdentity? identity) = await ReadLeaseAsync(reply)
                .ConfigureAwait(false);
            switch (result)
            {
                case LeaseVerificationResult.Valid valid
                    when (
                        AcquireMismatch(valid.Lease, featureId, count, borrow, reply.ProofId) ?? VersionOutsideLicense(identity, featureId, version)
                    )
                        is LicenseError mismatch:
                    // design §12.7.5 (V1d5): the lease is not kept, not saved, and not returned by its ID. It may be another
                    // holder's lease, which a return would end; it ends at its own expiry. What the server issued for this
                    // request, if anything, is released by the request's proof ID (§12.7.7), which that lease does not answer.
                    // A version outside the server license's range (V1d18) is refused the same way.
                    await ReleaseUntakenAsync(reply.ProofId, cancellationToken).ConfigureAwait(false);
                    return new LeaseResult.Failed(mismatch, null);
                case LeaseVerificationResult.Valid valid:
                    var lease = new Lease(valid.Lease) { Owner = this, Certificates = certificates };
                    if (lease.IsBorrow && SaveBorrow(lease) is LicenseError saveError)
                    {
                        await ReleaseUntakenAsync(reply.ProofId, cancellationToken).ConfigureAwait(false);
                        return new LeaseResult.Failed(saveError, null);
                    }

                    _leases.Add(lease);
                    if (!lease.IsBorrow)
                    {
                        ScheduleRenewal(lease, lease.Token);
                    }

                    return new LeaseResult.Acquired(lease);
                case LeaseVerificationResult.Invalid invalid:
                    await ReleaseUntakenAsync(reply.ProofId, cancellationToken).ConfigureAwait(false);
                    return new LeaseResult.Failed(invalid.Error, null);
                default:
                    throw new InvalidOperationException("Unknown lease verification result.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // design §12.7.7 (V1d5b): asks the server to release what it issued for an acquisition or a borrow whose answer the client
    // did not take, once; a release that does not get through is owed and sent again before the next request. A canceled
    // call does not wait for it: the canceled token keeps it from being sent, and it is owed.
    private async Task ReleaseUntakenAsync(string? proofId, CancellationToken cancellationToken)
    {
        if (proofId is not null && !await SendRequestReleaseAsync(proofId, cancellationToken).ConfigureAwait(false))
        {
            if (_owedReleases.Count == MaxOwedReleases)
            {
                // The oldest one's lease, if any, holds its seats until it expires.
                _owedReleases.RemoveAt(0);
            }

            _owedReleases.Add(proofId);
        }
    }

    // Sends the oldest owed release under the lock, before a request of the app or of the renewal timer (design §12.7.7).
    // One per request: elm-service limits the requests of one installation key per second (2 by default), and more releases
    // before the request would leave it refused with ELM_RATE_LIMITED. A server that cannot be reached is waited for once.
    private async Task SendOwedReleaseAsync(CancellationToken cancellationToken)
    {
        if (_owedReleases.Count > 0 && await SendRequestReleaseAsync(_owedReleases[0], cancellationToken).ConfigureAwait(false))
        {
            _owedReleases.RemoveAt(0);
        }
    }

    // DELETE /v1/leases/requests/{jti} with a proof that carries the key (design §12.7.7). It got through when an answer of
    // at most 256 KiB came back with a status other than 429 and 5xx: the server decided, and asking again changes nothing.
    private async Task<bool> SendRequestReleaseAsync(string proofId, CancellationToken cancellationToken)
    {
        Reply reply = await SendSignedAsync(
                HttpMethod.Delete,
                "v1/leases/requests/" + Uri.EscapeDataString(proofId),
                body: null,
                includeKey: true,
                cancellationToken
            )
            .ConfigureAwait(false);
        return reply.Status is int status and not 429 and < 500;
    }

    // design §12.7.5 (V1d5). The fields come before the request's ID: a server that answers this request with a lease of
    // another feature, count, kind, or period is told apart from an answer to another request.

    /// <summary>
    /// Compares a verified lease from the answer to an acquisition or a borrow with what was asked: the kind, the feature,
    /// the number of seats, for a borrow that it is no longer than the period, and that the token names the proof of this
    /// request.
    /// </summary>
    /// <param name="token">The verified lease of the answer.</param>
    /// <param name="featureId">The feature asked for.</param>
    /// <param name="count">The number of seats asked for.</param>
    /// <param name="borrowSeconds">The borrow period asked for, or <see langword="null"/> for a lease that is renewed.</param>
    /// <param name="proofId">The <c>jti</c> of the proof the request was sent with.</param>
    /// <returns><see langword="null"/> when the lease is what was asked for, otherwise <c>ELM_REQUEST_MISMATCH</c>.</returns>
    internal static LicenseError? AcquireMismatch(VerifiedLease token, string featureId, int count, long? borrowSeconds, string? proofId)
    {
        if (token.IsBorrow != (borrowSeconds is not null))
        {
            return Mismatch("The server's lease is not of the kind asked for.");
        }

        if (!string.Equals(token.FeatureId, featureId, StringComparison.Ordinal))
        {
            return Mismatch("The server's lease is for another feature than the one asked for.");
        }

        if (token.Seats != count)
        {
            return Mismatch("The server's lease is for another number of seats than the one asked for.");
        }

        // A server may cut a borrow at the end of its license, so a shorter one is what was asked for.
        if (borrowSeconds is long period && token.ExpiresAt.ToUnixTimeSeconds() - token.IssuedAt.ToUnixTimeSeconds() > period)
        {
            return Mismatch("The server's borrow is longer than the period asked for.");
        }

        return token.AnsweredProofId != proofId ? Mismatch("The server's lease answers another request.") : null;
    }

    // design §12.7.5 step 7 (V1d18). The lease token carries no version, so the version asked for is checked against the
    // server license that verified the lease, with the public check of its feature (the owner's answer to K1 of PR #446).

    /// <summary>
    /// Checks the version asked for in an acquisition or a borrow from an on-premises server against the range of the
    /// feature in the server license that verified the lease. A version that is not <c>major.minor[.patch]</c> is outside
    /// any range.
    /// </summary>
    /// <param name="identity">The server whose license verified the lease, or <see langword="null"/> for a lease from
    /// <c>elm-service</c>, which checks the version itself.</param>
    /// <param name="featureId">The feature asked for.</param>
    /// <param name="version">The version asked for, or <see langword="null"/>.</param>
    /// <returns><see langword="null"/> when no version was asked for, the feature has no range, or the version is in it;
    /// otherwise <c>ELM_FEATURE_VERSION_NOT_LICENSED</c>.</returns>
    internal static LicenseError? VersionOutsideLicense(ServerIdentity? identity, string featureId, string? version) =>
        version is not null
        && identity?.License.Features.FirstOrDefault(f => f.Id == featureId) is { VersionRange: not null } feature
        && !feature.CoversVersion(version)
            ? new LicenseError(ErrorCode.FeatureVersionNotLicensed, "The feature version is outside the range of the server license.")
            : null;

    // design §12.7.5, §10.4 (V1d5). The fields come before the lease ID, the lease ID before the sequence number, and the
    // request's ID last, so that each names the first thing that differs.

    /// <summary>
    /// Compares a verified lease from the answer to a renewal with the lease held: not a borrow, the same feature, number of
    /// seats, and lease ID, a later sequence number, and a token that names the proof of this request or of an earlier
    /// request for this lease that got no answer the client took, whose token a server returns again.
    /// </summary>
    /// <param name="token">The verified lease of the answer.</param>
    /// <param name="lease">The lease held.</param>
    /// <param name="proofId">The <c>jti</c> of the proof the request was sent with.</param>
    /// <returns><see langword="null"/> when the token renews the lease, otherwise <c>ELM_REQUEST_MISMATCH</c>.</returns>
    internal static LicenseError? RenewalMismatch(VerifiedLease token, Lease lease, string? proofId)
    {
        if (token.IsBorrow)
        {
            return Mismatch("The server's renewal is a borrow.");
        }

        if (!string.Equals(token.FeatureId, lease.FeatureId, StringComparison.Ordinal))
        {
            return Mismatch("The server's renewal is for another feature than the lease held.");
        }

        if (token.Seats != lease.Seats)
        {
            return Mismatch("The server's renewal is for another number of seats than the lease held.");
        }

        if (token.LeaseId != lease.Id)
        {
            return Mismatch("The server's renewal is for another lease than the one held.");
        }

        // A server returns more than the next number after it flagged a clone or after a request whose answer was lost.
        if (token.Sequence <= lease.Token.Sequence)
        {
            return Mismatch("The server's renewal has no later sequence number than the lease held.");
        }

        return token.AnsweredProofId != proofId && !lease.UnansweredProofIds.Contains(token.AnsweredProofId)
            ? Mismatch("The server's renewal answers another request.")
            : null;
    }

    // design §12.7.1.

    /// <summary>
    /// Returns a lease to the server and stops renewing it. A lease that is not a borrow is
    /// <see cref="LeaseState.Released"/> afterwards even when the server could not be reached; the server then drops it at its
    /// expiry. A borrow that the server refuses to take back or cannot be reached for, other than with
    /// <c>ELM_UNKNOWN_LEASE</c>, keeps its state and its file. A lease that is already lost or released is not sent again.
    /// </summary>
    /// <param name="lease">The lease, made by this client.</param>
    /// <param name="cancellationToken">Cancels the wait for the client's lock and the requests; a canceled request ends as
    /// <c>ELM_NETWORK_ERROR</c>.</param>
    /// <returns><see langword="null"/> when the server confirmed the return or the lease was already lost or released,
    /// otherwise why it was not returned.</returns>
    /// <exception cref="ArgumentException">The lease was made by another client.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public async Task<LicenseError?> ReleaseLeaseAsync(Lease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!ReferenceEquals(lease.Owner, this))
        {
            throw new ArgumentException("The lease was made by another client.", nameof(lease));
        }

        var events = new List<LeaseStateChangedEventArgs>();
        LicenseError? error;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            error = await ReleaseCoreAsync(lease, events, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Raise(events, ignoreHandlerErrors: false);
        return error;
    }

    // design §13.5.

    /// <summary>
    /// Deletes the pin of a server, for a server that was moved to another machine on purpose. The next
    /// request checks the server again as on the first connection.
    /// </summary>
    /// <param name="serverUrl">The server URL.</param>
    /// <returns><see langword="true"/> when a pin was deleted; <see langword="false"/> when there was none or the file
    /// cannot be deleted; always <see langword="false"/> for a client with a license key, which has no pin and leaves the pins
    /// of an on-premises client sharing its folder.</returns>
    public bool ResetServerPin(Uri serverUrl)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        if (_hosted)
        {
            return false;
        }

        bool deleted = _pins.Delete(serverUrl);
        if (HttpTargetUri.Normalize(serverUrl) == HttpTargetUri.Normalize(_server))
        {
            Volatile.Write(ref _current, null);
        }

        return deleted;
    }

    // design §17.6.

    /// <summary>
    /// Stops the renewal timers, sends the releases still owed for acquisitions whose answer the client did not take, returns
    /// the leases the client holds that are not borrows, and releases the key and the connections. It gives up returning
    /// after 5 seconds and ignores failures; the server drops a lease not returned at its expiry, and
    /// a borrow stays valid and on the disk. No new event starts once it has begun; an event already being raised may still
    /// run around the time it returns.
    /// </summary>
    /// <returns>A task that completes when the leases are returned or the 5 seconds have passed.</returns>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // design §17.2: returning the leases takes at most 5 seconds; leases not returned by then end at their expiry.
            using var timeout = new CancellationTokenSource(DisposeTimeout, _time);

            // design §12.7.7: the owed releases go first, all of them, oldest first, within the same 5 seconds, until one does
            // not get through; that one and the rest are dropped, so the returns below send none again.
            if (_owedReleases.Count > 0 && await EnsureConnectedAsync(timeout.Token).ConfigureAwait(false) is null)
            {
                while (_owedReleases.Count > 0 && await SendRequestReleaseAsync(_owedReleases[0], timeout.Token).ConfigureAwait(false))
                {
                    _owedReleases.RemoveAt(0);
                }
            }

            _owedReleases.Clear();

            foreach (Lease lease in _leases.Where(l => !l.IsBorrow).ToArray())
            {
                await ReleaseCoreAsync(lease, events: [], timeout.Token).ConfigureAwait(false);
            }

            _http.Dispose();
            foreach (ServerIdentity identity in _identities)
            {
                identity.Dispose();
            }

            _key.Dispose();
            _guardKey?.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    // A borrow is saved as {"ver": 1, "server", "lease", "license", "key"} in borrows/<lease ID>.json next to the installation
    // key, or with "certificates" instead of "license" and "key" from elm-service (design §12.7.6), written to a temporary
    // file and moved into place. The folder holds at most MaxBorrowFiles borrows.
    private LicenseError? SaveBorrow(Lease lease)
    {
        (byte[] License, byte[] Key) files = default;
        if (!_hosted && !_identityFiles.TryGetValue(lease.Token.ServerLicenseTokenId ?? string.Empty, out files))
        {
            return new LicenseError(ErrorCode.InternalError, "The server license of the borrow is not known.");
        }

        string folder = Path.Combine(_storeDirectory, BorrowFolder);
        string path = Path.Combine(folder, lease.Id + ".json");
        string temporary = path + ".tmp";
        try
        {
            ProtectedFiles.CreateDirectory(folder, _options.KeyScope);
            if (Directory.EnumerateFiles(folder, "*.json").Skip(MaxBorrowFiles - 1).Any())
            {
                return new LicenseError(ErrorCode.InternalError, $"{MaxBorrowFiles} borrows are saved already; return or let some expire first.");
            }

            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("ver", 1);
                writer.WriteString("server", HttpTargetUri.Normalize(_server));
                writer.WriteString("lease", lease.Token.Token);
                if (_hosted)
                {
                    writer.WriteStartArray("certificates");
                    foreach (string certificate in lease.Certificates)
                    {
                        writer.WriteStringValue(certificate);
                    }

                    writer.WriteEndArray();
                }
                else
                {
                    writer.WritePropertyName("license");
                    writer.WriteRawValue(files.License);
                    writer.WritePropertyName("key");
                    writer.WriteRawValue(files.Key);
                }

                writer.WriteEndObject();
            }

            // The lease ID is new, so no reader holds a file at this path (design §12.6.2).
            File.Move(temporary, path, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new LicenseError(ErrorCode.InternalError, "The borrow cannot be saved next to the installation key: " + e.Message);
        }
    }

    private void DeleteBorrow(string leaseId)
    {
        try
        {
            File.Delete(Path.Combine(_storeDirectory, BorrowFolder, leaseId + ".json"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left behind; it is skipped once it has expired, and deleted by the next GetBorrowedLeases.
        }
    }

    // Reads and verifies one saved borrow of this server; null when it is of another server, damaged, or no longer valid.
    private Lease? LoadBorrow(string path)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxBorrowFileLength)
            {
                return null;
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (_hosted)
        {
            return
                TryReadHostedBorrowFile(bytes, out string? hostedServer, out string? hostedToken, out IReadOnlyList<string>? certificates)
                && hostedServer == HttpTargetUri.Normalize(_server)
                ? Restored(
                    path,
                    _leaseVerifier.VerifyHosted(hostedToken!, certificates!, new LeaseExpectation(_options.ProductId, _key.Thumbprint)),
                    certificates!
                )
                : null;
        }

        if (
            !TryReadBorrowFile(bytes, out string? server, out string? token, out byte[]? license, out byte[]? key)
            || server != HttpTargetUri.Normalize(_server)
        )
        {
            return null;
        }

        if (!ServerIdentity.TryVerify(_licenseVerifier, license, key, _options.ProductId, out ServerIdentity? identity, out _))
        {
            return null;
        }

        if (!PinAccepts(identity!.License.LicenseId, identity.KeyId))
        {
            identity.Dispose();
            return null;
        }

        LeaseVerificationResult result = _leaseVerifier.VerifyOnPremises(
            token!,
            identity!,
            new LeaseExpectation(_options.ProductId, _key.Thumbprint)
        );
        identity!.Dispose();
        return Restored(path, result, []);
    }

    // A saved borrow that verified is used; an expired one is deleted; anything else is left alone and not returned.
    // design §12.7.5 (V1d5): no request is sent here, so the token is not compared with one. It was compared with its
    // request before it was saved, and the verifier still requires the proof ID it names to have its form.
    private Lease? Restored(string path, LeaseVerificationResult result, IReadOnlyList<string> certificates)
    {
        switch (result)
        {
            case LeaseVerificationResult.Valid { Lease.IsBorrow: true } valid:
                return new Lease(valid.Lease) { Owner = this, Certificates = certificates };
            case LeaseVerificationResult.Invalid { Error.Code: ErrorCode.Expired }:
                TryDelete(path);
                return null;
            default:
                return null;
        }
    }

    internal static bool TryReadBorrowFile(byte[] bytes, out string? server, out string? token, out byte[] license, out byte[] key)
    {
        server = null;
        token = null;
        license = [];
        key = [];
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (
                !TryReadBorrowHeader(root, out server, out token)
                || !root.TryGetProperty("license", out JsonElement f)
                || f.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("key", out JsonElement k)
                || k.ValueKind != JsonValueKind.Object
            )
            {
                return false;
            }

            license = Encoding.UTF8.GetBytes(f.GetRawText());
            key = Encoding.UTF8.GetBytes(k.GetRawText());
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // JsonElement throws InvalidOperationException from GetString, GetRawText, and TryGetProperty on text it cannot
            // read: a lone surrogate escape, or bytes that are not UTF-8 (measured on .NET 10.0.15, Windows).
            return false;
        }
    }

    // A borrow from elm-service (design §12.7.6): the common members and "certificates", one to four strings.
    internal static bool TryReadHostedBorrowFile(byte[] bytes, out string? server, out string? token, out IReadOnlyList<string>? certificates)
    {
        server = null;
        token = null;
        certificates = null;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (
                !TryReadBorrowHeader(root, out server, out token)
                || !root.TryGetProperty("certificates", out JsonElement c)
                || c.ValueKind != JsonValueKind.Array
                || c.GetArrayLength() is 0 or > MaxCertificates
                || c.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String || e.GetString()!.Length > MaxCertificateLength)
            )
            {
                return false;
            }

            certificates = [.. c.EnumerateArray().Select(e => e.GetString()!)];
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // JsonElement throws InvalidOperationException from GetString, GetRawText, and TryGetProperty on text it cannot
            // read: a lone surrogate escape, or bytes that are not UTF-8 (measured on .NET 10.0.15, Windows).
            return false;
        }
    }

    // The members every saved borrow has: "ver" 1, "server", and "lease".
    private static bool TryReadBorrowHeader(JsonElement root, out string? server, out string? token)
    {
        server = null;
        token = null;
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("ver", out JsonElement ver)
            || ver.ValueKind != JsonValueKind.Number
            || !ver.TryGetInt32(out int version)
            || version != 1
            || !root.TryGetProperty("server", out JsonElement s)
            || s.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("lease", out JsonElement l)
            || l.ValueKind != JsonValueKind.String
        )
        {
            return false;
        }

        server = s.GetString();
        token = l.GetString();
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Tried again at the next GetBorrowedLeases.
        }
    }

    // design §19 (V1d5): only the client makes this code; ProblemCodes takes it from no answer.
    private static LicenseError Mismatch(string message) => new(ErrorCode.RequestMismatch, message);

    internal static ClientInfoBody? ClientInfo()
    {
        string host = Environment.MachineName;
        string user = Environment.UserName;
        return Encoding.UTF8.GetByteCount(host) <= MaxClientNameBytes && Encoding.UTF8.GetByteCount(user) <= MaxClientNameBytes
            ? new ClientInfoBody(host, user)
            : null;
    }

    private static string? KeyIdOf(X509Certificate? certificate)
    {
        if (certificate is null)
        {
            return null;
        }

        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using ECDsa? key = leaf.GetECDsaPublicKey();
        return key is { KeySize: 256 } ? KeyIds.Of(key) : null;
    }

    // design §12.1 (the V1d1 Grill, Q1 to Q3). The status is not compared with the code's own status in design §19.

    /// <summary>
    /// Reads an answer that is not a success as an error. The answer carries no signature, so only a code that a server sends
    /// in a problem answer is taken from it. Any other code, a name that is not a code, a body without a code or that is
    /// not JSON, and every redirect are <c>ELM_INTERNAL_ERROR</c> with a message that holds nothing of the body.
    /// </summary>
    /// <param name="status">The answer's status.</param>
    /// <param name="body">The answer's body.</param>
    /// <param name="keepDetail"><see langword="true"/> to use the answer's <c>detail</c>, when it is 1 to 512 characters
    /// long, as the message of a code that is taken. <see langword="false"/> for an answer from a server that is not
    /// verified yet: the message then never holds text the answer chose.</param>
    /// <returns>The error.</returns>
    internal static LicenseError ProblemOf(HttpStatusCode status, byte[] body, bool keepDetail)
    {
        ProblemBody? problem = null;
        try
        {
            problem = JsonSerializer.Deserialize(body, ClientWireContext.Default.ProblemBody);
        }
        catch (JsonException)
        {
            problem = null;
        }

        int number = (int)status;
        if (number is >= 300 and < 400 || !ErrorCodes.TryParseWireName(problem?.Code, out ErrorCode code) || !ProblemCodes.InProblemAnswer(code))
        {
            return new LicenseError(ErrorCode.InternalError, $"The server answered {number} without a known error code.");
        }

        return new LicenseError(code, keepDetail && problem?.Detail is { Length: > 0 and <= 512 } text ? text : $"The server answered {number}.");
    }

    // Accepts a connection that the OS validates (an operator's certificate) or whose key is the pinned server key (the
    // self-signed default). Checked on every TLS connection, including reconnections (design §13.5).
    private bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        string? pinned = _pinnedKeyId;
        bool matches = pinned is not null && KeyIdOf(certificate) == pinned;
        if (!matches)
        {
            _pinFailed = true;
        }

        return matches;
    }

    private void Raise(List<LeaseStateChangedEventArgs> events, bool ignoreHandlerErrors)
    {
        foreach (LeaseStateChangedEventArgs change in events)
        {
            // The events are raised after the lock is released, so DisposeAsync can run in between (design §17.6).
            if (_disposed)
            {
                break;
            }

            try
            {
                LeaseStateChanged?.Invoke(this, change);
            }
            catch (Exception) when (ignoreHandlerErrors)
            {
                // A handler that throws on a timer thread would end the process; the client ignores it, as documented.
            }
        }
    }

    private void SetState(Lease lease, LeaseState state, LicenseError? error, List<LeaseStateChangedEventArgs> events)
    {
        LeaseState previous = lease.SetState(state, error);
        if (state is LeaseState.Lost or LeaseState.Released)
        {
            lease.Timer?.Dispose();
            lease.Timer = null;
            _leases.Remove(lease);
        }

        if (previous != state || error is not null)
        {
            events.Add(new LeaseStateChangedEventArgs(lease, previous, state, error));
        }
    }

    // design §13.5 steps 1–3, under the lock: fetch the server license and key over a connection whose certificate is only
    // recorded, verify the license from the trust store, compare the TLS key with cnf.jkt unless the OS validated the
    // certificate, and check or store the pin.
    private async Task<LicenseError?> EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        // design §12.7.6: elm-service has no server license; its leases verify with the certificates of each answer.
        if (_hosted || Volatile.Read(ref _current) is not null)
        {
            return null;
        }

        string? seenKeyId = null;
        bool osValidated = false;
        SocketsHttpHandler handler = BoundedAnswer.CreateHandler();
        handler.SslOptions = new SslClientAuthenticationOptions
        {
            // The certificate is recorded here and checked against the server license once it has verified
            // (design §13.5, first connection); what this connection returns is discarded on a mismatch.
            RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                osValidated = errors == SslPolicyErrors.None;
                seenKeyId = KeyIdOf(certificate);
                return certificate is not null;
            },
        };
        byte[] body;
        using (HttpClient bootstrap = BoundedAnswer.CreateClient(handler))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_server, "v1/server/license"));
                BoundedAnswer answer = await BoundedAnswer
                    .SendAsync(bootstrap, request, BoundedAnswer.MaxLength, _time, cancellationToken)
                    .ConfigureAwait(false);
                if (answer is not BoundedAnswer.Whole whole)
                {
                    // This connection takes any certificate, so anyone on the path can answer it, with a body of any
                    // length (design §12.1, §13.5).
                    return new LicenseError(ErrorCode.Malformed, AnswerTooLarge);
                }

                body = whole.Body.ToArray();
                if (!whole.IsSuccess)
                {
                    // The code is read, and the detail, which whoever answers would choose, never becomes the app's
                    // message (design §12.1).
                    return ProblemOf(whole.Status, body, keepDetail: false);
                }
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or TimeoutException)
            {
                return new LicenseError(ErrorCode.NetworkError, "The server cannot be reached: " + e.Message);
            }
        }

        if (!TryReadServerLicense(body, out byte[]? licenseFile, out byte[]? keyJwk))
        {
            return new LicenseError(ErrorCode.Malformed, "The server's license answer is not valid.");
        }

        if (
            !ServerIdentity.TryVerify(
                _licenseVerifier,
                licenseFile,
                keyJwk,
                _options.ProductId,
                out ServerIdentity? identity,
                out LicenseError? error
            )
        )
        {
            return error;
        }

        LicenseError? pinError =
            !osValidated && seenKeyId != identity!.KeyId
                ? new LicenseError(ErrorCode.ServerPinMismatch, "The server's TLS key is not the key of its server license.")
                : CheckPin(identity!.License.LicenseId, identity.KeyId);
        if (pinError is not null)
        {
            identity!.Dispose();
            return pinError;
        }

        _identityFiles[identity!.License.TokenId] = (licenseFile, keyJwk);
        AddIdentity(identity!);
        return null;
    }

    // The pin's judgement of a saved borrow's server, without writing a pin (design §12.7.4): the expected license ID when it is
    // set, otherwise the pinned license and key when there is a pin.
    private bool PinAccepts(string licenseId, string keyId) =>
        _options.ExpectedServerLicenseId is string expected
            ? licenseId == expected
            : _pins.TryRead(_server, out PinFile? pin) && (pin is null || (pin.Lic == licenseId && pin.Jkt == keyId));

    private LicenseError? CheckPin(string licenseId, string keyId)
    {
        if (!_pins.TryRead(_server, out PinFile? pin))
        {
            return new LicenseError(ErrorCode.ServerPinMismatch, "The server pin is damaged; reset it to connect again.");
        }

        if (
            _options.ExpectedServerLicenseId is string expected
                ? licenseId != expected
                : pin is not null && (pin.Lic != licenseId || pin.Jkt != keyId)
        )
        {
            return new LicenseError(ErrorCode.ServerPinMismatch, "The server is not the one this client is pinned to.");
        }

        if (pin is not null && pin.Lic == licenseId && pin.Jkt == keyId)
        {
            return null;
        }

        return _pins.TryWrite(_server, licenseId, keyId)
            ? null
            : new LicenseError(ErrorCode.InternalError, "The server pin cannot be saved next to the installation key.");
    }

    private void AddIdentity(ServerIdentity identity)
    {
        _identities.Add(identity);
        _pinnedKeyId = identity.KeyId;
        _pinFailed = false;
        Volatile.Write(ref _current, identity);
        if (_identities.Count > MaxIdentities)
        {
            ServerIdentity? unused = _identities.Find(i => i != identity && !_leases.Exists(l => l.Token.ServerLicenseTokenId == i.License.TokenId));
            if (unused is not null)
            {
                _identities.Remove(unused);
                unused.Dispose();
            }
        }
    }

    internal static bool TryReadServerLicense(byte[] body, out byte[] licenseFile, out byte[] keyJwk)
    {
        licenseFile = [];
        keyJwk = [];
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            if (
                document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("license", out JsonElement license)
                || license.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("key", out JsonElement key)
                || key.ValueKind != JsonValueKind.Object
            )
            {
                return false;
            }

            licenseFile = Encoding.UTF8.GetBytes(license.GetRawText());
            keyJwk = Encoding.UTF8.GetBytes(key.GetRawText());
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // JsonElement throws InvalidOperationException from GetString, GetRawText, and TryGetProperty on text it cannot
            // read: a lone surrogate escape, or bytes that are not UTF-8 (measured on .NET 10.0.15, Windows).
            return false;
        }
    }

    // Reads a lease answer and verifies the token with the server license in use; when the answer names a server license
    // the client does not know (design §13.5, the ELM-Server-License header), that one is fetched and checked first. From elm-service, the token is
    // verified with the certificates of the answer (design §12.7.5 steps 1, 2, and 6), which are returned with the result.
    // The identity that verified an on-premises token is returned too, for the version check of an acquisition (step 7).
    private async Task<(LeaseVerificationResult Result, IReadOnlyList<string> Certificates, ServerIdentity? Identity)> ReadLeaseAsync(Reply reply)
    {
        LeaseAnswerBody? answer = null;
        try
        {
            answer = JsonSerializer.Deserialize(reply.Body, ClientWireContext.Default.LeaseAnswerBody);
        }
        catch (JsonException)
        {
            answer = null;
        }

        var expectation = new LeaseExpectation(_options.ProductId, _key.Thumbprint);
        if (
            answer is null
            || (_hosted && (answer.Certificates is null || answer.Certificates.Exists(c => c is null || c.Length > MaxCertificateLength)))
        )
        {
            return (new LeaseVerificationResult.Invalid(new LicenseError(ErrorCode.Malformed, "The server's lease answer is not valid.")), [], null);
        }

        if (_hosted)
        {
            return (_leaseVerifier.VerifyHosted(answer.Lease, answer.Certificates!, expectation), answer.Certificates!, null);
        }

        // design §13.5 (V1d18, the owner's answer to K4 of PR #446): an acquisition or a renewal is verified with the server
        // license in use only. An earlier server license kept for the leases tied to it never verifies a new token, so an
        // answer that names one is checked against the current license and fails at slic; only a license the client does
        // not know yet is fetched.
        ServerIdentity? identity = Volatile.Read(ref _current);
        if (
            identity is null
            || (identity.License.TokenId != reply.ServerLicenseTokenId && !_identities.Exists(i => i.License.TokenId == reply.ServerLicenseTokenId))
        )
        {
            Volatile.Write(ref _current, null);
            if (await EnsureConnectedAsync().ConfigureAwait(false) is LicenseError error)
            {
                return (new LeaseVerificationResult.Invalid(error), [], null);
            }

            identity = _current!;
        }

        return (_leaseVerifier.VerifyOnPremises(answer.Lease, identity, expectation), [], identity);
    }

    private void ScheduleRenewal(Lease lease, VerifiedLease token)
    {
        // design §12.7.3: every third of the lease duration (about 200 s for the default 10 minutes).
        TimeSpan duration = token.ExpiresAt - token.IssuedAt;
        Schedule(lease, duration / 3);
    }

    private void Schedule(Lease lease, TimeSpan due)
    {
        lease.Timer?.Dispose();
        lease.Timer = _time.CreateTimer(_ => OnTimer(lease), null, due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer(Lease lease) => _ = RenewOnTimerAsync(lease);

    private async Task RenewOnTimerAsync(Lease lease)
    {
        var events = new List<LeaseStateChangedEventArgs>();
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (!_disposed && lease.State is not (LeaseState.Lost or LeaseState.Released))
            {
                await RenewCoreAsync(lease, events, allowReclaim: true).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        Raise(events, ignoreHandlerErrors: true);
    }

    private async Task RenewCoreAsync(Lease lease, List<LeaseStateChangedEventArgs> events, bool allowReclaim)
    {
        if (_time.GetUtcNow() > lease.ExpiresAt)
        {
            SetState(
                lease,
                LeaseState.Lost,
                lease.LastError ?? new LicenseError(ErrorCode.Expired, "The lease expired before it was renewed."),
                events
            );
            return;
        }

        LicenseError? error = await EnsureConnectedAsync().ConfigureAwait(false);
        if (error is null)
        {
            await SendOwedReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            Reply reply = await SendSignedAsync(
                    HttpMethod.Post,
                    $"v1/leases/{Uri.EscapeDataString(lease.Id)}/renew",
                    JsonSerializer.SerializeToUtf8Bytes(
                        new RenewRequestBody(lease.Token.Sequence, lease.UnansweredProofIds.Count > 0 ? [.. lease.UnansweredProofIds] : null),
                        ClientWireContext.Default.RenewRequestBody
                    ),
                    includeKey: false
                )
                .ConfigureAwait(false);
            error = reply.Error;
            if (reply.Unanswered && reply.ProofId is string unanswered)
            {
                // The server may have renewed without the answer reaching us; the retry names this request.
                lease.AddUnanswered(unanswered);
            }

            if (error is null)
            {
                (LeaseVerificationResult result, IReadOnlyList<string> certificates, _) = await ReadLeaseAsync(reply).ConfigureAwait(false);
                switch (result)
                {
                    case LeaseVerificationResult.Valid valid when RenewalMismatch(valid.Lease, lease, reply.ProofId) is LicenseError mismatch:
                        error = mismatch;
                        break;
                    case LeaseVerificationResult.Valid valid:
                        // The next renewal is set before the new token is visible, so whoever sees the new token also
                        // finds the renewal after it already scheduled.
                        ScheduleRenewal(lease, valid.Lease);
                        lease.Certificates = certificates;
                        lease.Update(valid.Lease);
                        SetState(lease, LeaseState.Held, null, events);
                        return;
                    case LeaseVerificationResult.Invalid invalid:
                        error = invalid.Error;
                        break;
                }

                if (reply.ProofId is string refused)
                {
                    // design §12.7.3, §10.4 (V1d5): an answer came and the client does not take it. The server may have
                    // renewed for this request; the retry names it, so that server returns its token again and does not
                    // take the retry for a clone.
                    lease.AddUnanswered(refused);
                }
            }
        }

        if (error!.Code == ErrorCode.UnknownLease && _hosted)
        {
            // design §12.7.3: elm-service keeps its leases in its database, so a lease it does not know is gone; no reclaim.
            SetState(lease, LeaseState.Lost, error, events);
            return;
        }

        if (error.Code == ErrorCode.UnknownLease && allowReclaim)
        {
            // design §13.4 step 4: the server restarted; reclaim every lease held there, then renew again.
            SetState(lease, LeaseState.Reclaiming, null, events);
            await ReclaimAllAsync(events).ConfigureAwait(false);
            if (lease.State == LeaseState.Reclaiming)
            {
                await RenewCoreAsync(lease, events, allowReclaim: false).ConfigureAwait(false);
            }

            return;
        }

        if (error.Code == ErrorCode.CloneDetected)
        {
            SetState(lease, LeaseState.Lost, error, events);
            return;
        }

        // design §12.7.3: retry from 15 s, doubling up to 60 s, until the lease expires. The retry is set before the state is
        // visible, as the next renewal is before a new token: whoever sees Retrying and then moves the clock finds the timer.
        TimeSpan untilExpiry = lease.ExpiresAt - _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        TimeSpan delay = lease.NextRetryDelay();
        Schedule(lease, delay < untilExpiry ? delay : untilExpiry);
        SetState(lease, LeaseState.Retrying, error, events);
    }

    // Reclaims every lease this client holds on the server, 64 tokens per request (design §13.4). A lease whose reclaim is
    // refused is lost (design §13.4 step 5); a failed request leaves its leases to the retry.
    private async Task ReclaimAllAsync(List<LeaseStateChangedEventArgs> events)
    {
        Lease[] held = [.. _leases.Where(l => !l.IsBorrow && l.State is not (LeaseState.Lost or LeaseState.Released))];
        foreach (Lease[] batch in held.Chunk(MaxReclaimTokens))
        {
            await ReclaimBatchAsync(batch, events).ConfigureAwait(false);
        }
    }

    private async Task ReclaimBatchAsync(Lease[] batch, List<LeaseStateChangedEventArgs> events)
    {
        var body = new ReclaimRequestBody([.. batch.Select(l => l.Token.Token)]);
        Reply reply = await SendSignedAsync(
                HttpMethod.Post,
                "v1/leases/reclaim",
                JsonSerializer.SerializeToUtf8Bytes(body, ClientWireContext.Default.ReclaimRequestBody),
                includeKey: true
            )
            .ConfigureAwait(false);
        ReclaimAnswerBody? answer = null;
        if (reply.Error is null)
        {
            try
            {
                answer = JsonSerializer.Deserialize(reply.Body, ClientWireContext.Default.ReclaimAnswerBody);
            }
            catch (JsonException)
            {
                answer = null;
            }
        }

        if (answer is null || answer.Results.Count != batch.Length || answer.Results.Contains(null!))
        {
            LicenseError error = reply.Error ?? new LicenseError(ErrorCode.Malformed, "The server's reclaim answer is not valid.");
            foreach (Lease lease in batch.Where(l => l.State == LeaseState.Reclaiming))
            {
                // The retry is set before the state is visible, as above.
                Schedule(lease, lease.NextRetryDelay());
                SetState(lease, LeaseState.Retrying, error, events);
            }

            return;
        }

        for (int i = 0; i < batch.Length; i++)
        {
            // design §19: a lease is kept only for ELM_OK. A code a server does not give a lease here, a name that is not a
            // code, and an item without a code are read as ELM_INTERNAL_ERROR, and the lease is lost.
            ErrorCode code =
                ErrorCodes.TryParseWireName(answer.Results[i].Code, out ErrorCode parsed) && ProblemCodes.InReclaimAnswer(parsed)
                    ? parsed
                    : ErrorCode.InternalError;
            if (code != ErrorCode.Ok)
            {
                SetState(batch[i], LeaseState.Lost, new LicenseError(code, "The server refused to reclaim the lease."), events);
            }
        }
    }

    private async Task<LicenseError?> ReleaseCoreAsync(
        Lease lease,
        List<LeaseStateChangedEventArgs> events,
        CancellationToken cancellationToken = default
    )
    {
        if (lease.State is LeaseState.Lost or LeaseState.Released)
        {
            return null;
        }

        lease.Timer?.Dispose();
        lease.Timer = null;
        LicenseError? error = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (error is null)
        {
            await SendOwedReleaseAsync(cancellationToken).ConfigureAwait(false);

            // A borrow's return carries the key (design §12.7.1): an on-premises server restored from its record may not know it.
            // elm-service checks it against the key the lease keeps.
            Reply reply = await SendSignedAsync(
                    HttpMethod.Delete,
                    $"v1/leases/{Uri.EscapeDataString(lease.Id)}",
                    body: null,
                    includeKey: lease.IsBorrow,
                    cancellationToken
                )
                .ConfigureAwait(false);
            error = reply.Error;
        }

        if (lease.IsBorrow)
        {
            if (error is not null && error.Code != ErrorCode.UnknownLease)
            {
                // Not returned (refused or not reached): the borrow stays valid and on the disk until it expires.
                return error;
            }

            DeleteBorrow(lease.Id);
            error = null;
        }

        SetState(lease, LeaseState.Released, error, events);
        return error;
    }

    // Sends a request with a proof (design §12.2). The nonce comes from the last answer, or from GET /v1/nonce; a request
    // refused with ELM_NONCE_REQUIRED is sent once more with the fresh nonce of that answer.
    private async Task<Reply> SendSignedAsync(
        HttpMethod method,
        string path,
        byte[]? body,
        bool includeKey,
        CancellationToken cancellationToken = default
    )
    {
        var target = new Uri(_server, path);
        Reply reply = default;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (_nonce is null)
            {
                Reply nonceReply = await SendAsync(HttpMethod.Get, new Uri(_server, "v1/nonce"), body: null, proof: null, cancellationToken)
                    .ConfigureAwait(false);
                if (nonceReply.Error is not null)
                {
                    // Only the status of the request's own answer tells whether a release got through (design §12.7.7).
                    return nonceReply with { Status = null };
                }

                _nonce = ReadNonce(nonceReply.Body);
                if (_nonce is null)
                {
                    return Reply.Failure(new LicenseError(ErrorCode.Malformed, "The server's nonce answer is not valid."));
                }
            }

            string proof = ProofBuilder.Create(_key, method.Method, target, _nonce, body ?? [], includeKey, _time, out string proofId);
            reply = await SendAsync(method, target, body, proof, cancellationToken).ConfigureAwait(false) with { ProofId = proofId };
            _nonce = reply.Nonce ?? (reply.Error?.Code == ErrorCode.NonceRequired ? null : _nonce);
            if (reply.Error?.Code != ErrorCode.NonceRequired)
            {
                return reply;
            }
        }

        return reply;
    }

    // design §12.6.2, V1c11.

    /// <summary>
    /// Returns <paramref name="nonce"/> when it is 1 to 128 characters long, and <see langword="null"/> otherwise, for a nonce
    /// from an answer's body or its <c>ELM-Nonce</c> header alike. A value outside that range is taken
    /// as no nonce, so the next request fetches one instead of building a proof that <see cref="ProofBuilder"/> refuses.
    /// </summary>
    /// <param name="nonce">The nonce the server sent, or <see langword="null"/>.</param>
    /// <returns>The nonce, or <see langword="null"/>.</returns>
    internal static string? ValidNonce(string? nonce) => nonce is { Length: > 0 and <= 128 } ? nonce : null;

    internal static string? ReadNonce(byte[] body)
    {
        try
        {
            return ValidNonce(JsonSerializer.Deserialize(body, ClientWireContext.Default.NonceAnswerBody)?.Nonce);
        }
        catch (JsonException)
        {
            return null;
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
            _pinFailed = false;
            BoundedAnswer answer = await BoundedAnswer
                .SendAsync(_http, request, BoundedAnswer.MaxLength, _time, cancellationToken)
                .ConfigureAwait(false);
            if (answer is not BoundedAnswer.Whole whole)
            {
                // design §12.1, §12.7.3: nothing of an answer that is too large is read, its nonce and Retry-After
                // included. A server that answered with a success may have acted on the request, so the request counts
                // as one without an answer; a refusal changed nothing there.
                return Reply.Failure(new LicenseError(ErrorCode.Malformed, AnswerTooLarge), unanswered: answer.IsSuccess);
            }

            byte[] content = whole.Body.ToArray();
            string? nonce = whole.Headers.TryGetValues("ELM-Nonce", out IEnumerable<string>? nonces) ? nonces.FirstOrDefault() : null;
            string? serverLicense = whole.Headers.TryGetValues("ELM-Server-License", out IEnumerable<string>? licenses)
                ? licenses.FirstOrDefault()
                : null;
            LicenseError? error = whole.IsSuccess ? null : ProblemOf(whole.Status, content, keepDetail: true);
            return new Reply(content, error, ValidNonce(nonce), serverLicense, whole.Headers.RetryAfter?.Delta) { Status = (int)whole.Status };
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or TimeoutException)
        {
            if (_pinFailed)
            {
                // The server now shows another key. Check it again as on the first connection at the next request: with
                // ExpectedServerLicenseId a moved server is accepted, otherwise the pin refuses it (design §13.5).
                Volatile.Write(ref _current, null);
            }

            LicenseError error = _pinFailed
                ? new LicenseError(ErrorCode.ServerPinMismatch, "The server's TLS key is not the pinned server key.")
                : new LicenseError(ErrorCode.NetworkError, "The server cannot be reached: " + e.Message);
            return Reply.Failure(error, unanswered: !_pinFailed);
        }
    }

    // Unanswered: the request was sent and no answer came back, so the server may have acted on it.
    private readonly record struct Reply(byte[] Body, LicenseError? Error, string? Nonce, string? ServerLicenseTokenId, TimeSpan? RetryAfter)
    {
        public bool Unanswered { get; init; }

        public string? ProofId { get; init; }

        // The status of a whole answer of at most 256 KiB; null when none came.
        public int? Status { get; init; }

        public static Reply Failure(LicenseError error, bool unanswered = false) => new([], error, null, null, null) { Unanswered = unanswered };
    }
}
