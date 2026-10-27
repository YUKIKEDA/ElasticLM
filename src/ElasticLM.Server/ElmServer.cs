using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ElasticLM.Client;
using ElasticLM.Issuing;
using ElasticLM.Leasing;
using ElasticLM.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ElasticLM.Server;

/// <summary>
/// How the server is hosted: the clock, the environment, the log level, and the start options. Tests pass a fake clock.
/// </summary>
internal sealed class ServerHostOptions
{
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    public LogLevel MinimumLogLevel { get; init; } = LogLevel.Information;

    /// <summary>Gets a value indicating whether a broken borrow record is moved aside instead of stopping the start
    /// (<c>--set-aside-broken-records</c>, design §13.4).</summary>
    public bool SetAsideBrokenRecords { get; init; }
}

/// <summary>
/// The on-premises license server (design §13): it starts as §13.1 says, serves the nonce, the server license, and the
/// lease protocol (§12.7, §13.4), writes the usage log and the borrow record (§13.4, §18.1), and answers the local
/// administration API on a loopback address (§13.7).
/// </summary>
internal sealed partial class ElmServer : IAsyncDisposable
{
    public const string ServerLicenseHeader = "ELM-Server-License";
    public const string NonceHeader = "ELM-Nonce";
    public const string ProofHeader = "ELM-Proof";
    public const int MaxBodyLength = 64 * 1024;
    public const int MaxReplayEntries = 100_000;

    private const int MaxLicenseFileLength = 64 * 1024;
    private const int MaxFingerprints = 8;
    private const int MaxClientNameBytes = 255;
    private const int MaxAppBytes = 64;
    private const int MaxVersionLength = 32;
    private const int MaxProofIdLength = 64;
    private const long MaxBorrowSeconds = 3_162_240_000;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SealInterval = TimeSpan.FromSeconds(60);

    private readonly WebApplication _app;
    private readonly ServerSettings _settings;
    private readonly InstallationKeyStore _store;
    private readonly InstallationKey _key;
    private readonly TrustStore _trust;
    private readonly byte[] _keyJwk;
    private readonly X509Certificate2 _certificate;
    private readonly NonceService _nonces;
    private readonly MemoryReplayStore _replays;
    private readonly ProofVerifier _proofs;
    private readonly IpRateLimiter _limiter;
    private readonly AccessRules _access;
    private readonly ILogger _log;
    private readonly UsageLog _usage;
    private readonly BorrowJournal _journal;
    private readonly byte[] _adminToken;
    private readonly List<ServerIdentity> _identities = [];
    private readonly Lock _reloadLock = new();
    private byte[] _licenseFile;
    private ITimer? _cleanup;
    private ITimer? _seal;
    private IReadOnlyList<Uri> _publicUrls;
    private Uri? _adminUrl;
    private bool _stopped;
    private bool _disposed;

    private ElmServer(Parts parts)
    {
        _app = parts.App;
        _settings = parts.Settings;
        _store = parts.Store;
        _key = parts.Key;
        _trust = parts.Trust;
        _identities.Add(parts.Identity);
        Engine = parts.Engine;
        _licenseFile = parts.LicenseFile;
        _keyJwk = parts.Key.WithPrivateKey(KeyFiles.ExportPublicJwk);
        _certificate = parts.Certificate;
        _publicUrls = parts.PublicUrls;
        _usage = parts.Usage;
        _journal = parts.Journal;
        _adminToken = Encoding.ASCII.GetBytes(parts.AdminToken);
        Time = parts.Time;
        _access = parts.Settings.Access;
        _limiter = new IpRateLimiter(parts.Settings.RateLimitPerIp, parts.Time);

        // design §13.7: a new secret at each start, never stored, so nonces from before a restart are void.
        _nonces = new NonceService(RandomNumberGenerator.GetBytes(32), parts.Time);
        _replays = new MemoryReplayStore(parts.Time, MaxReplayEntries);
        _proofs = new ProofVerifier(_nonces, _replays);
        _log = parts.Log;
    }

    /// <summary>Gets the lease engine.</summary>
    public LeaseEngine Engine { get; }

    /// <summary>Gets the URLs clients use; proofs name one of them (design §12.1).</summary>
    public IReadOnlyList<Uri> PublicUrls => _publicUrls;

    /// <summary>Gets the URL of the local administration API.</summary>
    public Uri AdminUrl => _adminUrl!;

    /// <summary>Gets the installation key thumbprint, which is the server license's <c>cnf.jkt</c>.</summary>
    public string KeyId => Engine.Server.KeyId;

    /// <summary>Gets the data folder: the installation key, the administrator token, and the borrow record.</summary>
    public string DataDirectory => _store.Directory;

    /// <summary>Gets the clock.</summary>
    public TimeProvider Time { get; }

    private SigningKeyAccess KeyAccess => use => _key.WithPrivateKey(use);

    /// <summary>
    /// Starts the server as design §13.1 says: settings, installation key, trust store, server license, the borrow record,
    /// TLS, the usage log, then listening with the reclaim window open.
    /// </summary>
    /// <exception cref="ServerStartException">The server cannot start; the message says why.</exception>
    public static async Task<ElmServer> StartAsync(ServerSettings settings, ServerHostOptions host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(host);
        long started = host.Time.GetTimestamp();
        TrustStore trust = ReadTrust(settings, host.Environment);
        var store = new InstallationKeyStore(settings.Product, KeyScope.Machine, settings.DataDirectory);
        if (!store.TryLoadOrCreate(out InstallationKey? key, out LicenseError? keyError))
        {
            throw new ServerStartException(keyError!.Message);
        }

        var disposables = new List<IDisposable> { key! };
        try
        {
            (VerifiedLicense? license, byte[]? licenseFile, LicenseError? licenseError) = TryVerifyLicense(settings, store, key!, trust, host.Time);
            if (licenseError is not null)
            {
                throw new ServerStartException(licenseError.Message);
            }

            byte[] keyJwk = key!.WithPrivateKey(KeyFiles.ExportPublicJwk);
            if (!ServerIdentity.TryCreate(license!, keyJwk, out ServerIdentity? identity, out LicenseError? identityError))
            {
                throw new ServerStartException(identityError!.Message);
            }

            disposables.Add(identity!);
            BorrowJournal journal = Journal(() => BorrowJournal.Load(store.Directory, trust, identity!, host.SetAsideBrokenRecords, host.Time));
            disposables.Add(journal);
            string adminToken =
                AdminToken.LoadOrCreate(store.Directory)
                ?? throw new ServerStartException($"The administrator token in '{store.Directory}' cannot be created or read.");
            IReadOnlyList<Uri> publicUrls =
                settings.PublicUrls.Count > 0 ? settings.PublicUrls : DefaultPublicUrls(settings.Listen, settings.Listen.Port);
            X509Certificate2 certificate = settings.TlsCertificate is string certificatePath
                ? TlsCertificates.FromFiles(certificatePath, settings.TlsKey, host.Environment(ServerSettings.TlsPasswordVariable))
                : TlsCertificates.SelfSigned(key, publicUrls, host.Time);
            disposables.Add(certificate);

            WebApplication app = Build(settings, certificate, host);
            ILogger log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("elm-server");
            SigningKeyAccess access = use => key.WithPrivateKey(use);
            UsageLog usage = OpenUsageLog(settings, store, access, licenseFile!, keyJwk, host.Time, log);
            disposables.Add(usage);
            LeaseEngine? created = null;
            var engine = new LeaseEngine(
                identity!,
                (spec, issuedAt) => key.WithPrivateKey(ecdsa => LeaseIssuer.Issue(ecdsa, spec, issuedAt)),
                new LeaseVerifier(trust, host.Time, StatelessClockGuard.Instance),
                host.Time,
                new LeaseEngineOptions
                {
                    MaxBorrow = settings.BorrowMaxDuration,
                    BorrowReserve = settings.BorrowReserve,
                    Journal = journal,
                    Expired = lease =>
                        usage.Lease(lease.IsBorrow ? "borrow.expired" : "lease.expired", lease, (created?.Server ?? identity!).License.LicenseId),
                }
            );
            created = engine;
            disposables.Add(engine);
            engine.Restore(journal.Borrows, journal.Returns);
            Journal(() =>
            {
                journal.Open(access, licenseFile!, keyJwk);
                return journal;
            });
            var server = new ElmServer(
                new Parts(
                    app,
                    settings,
                    store,
                    key,
                    trust,
                    identity!,
                    engine,
                    licenseFile!,
                    certificate,
                    publicUrls,
                    usage,
                    journal,
                    adminToken,
                    host.Time,
                    log
                )
            );
            server.Map();
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            server.AfterStart(host.Time.GetElapsedTime(started));
            return server;
        }
        catch
        {
            foreach (IDisposable disposable in Enumerable.Reverse(disposables))
            {
                disposable.Dispose();
            }

            throw;
        }
    }

    /// <summary>Waits until the host is asked to stop (Ctrl+C, SIGTERM, or a service stop).</summary>
    public Task WaitForShutdownAsync() => _app.WaitForShutdownAsync();

    /// <summary>
    /// Stops listening and the timers (design §13.2), writes <c>server.stopped</c> with the leases not returned, and signs
    /// the logs. Leases not returned are dropped; clients reclaim them after a restart, and borrows come back from the record.
    /// </summary>
    public async Task StopAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync().ConfigureAwait(false);
        }

        if (_seal is not null)
        {
            await _seal.DisposeAsync().ConfigureAwait(false);
        }

        await _app.StopAsync().ConfigureAwait(false);
        int leases = Engine.LeaseCount;
        _usage.WriteDenials();
        _usage.Write("server.stopped", w => w.WriteNumber("leases", leases));
        ServerLog.Stopped(_log, leases);
        _usage.Dispose();
        _journal.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        Engine.Dispose();
        _certificate.Dispose();
        foreach (ServerIdentity identity in _identities)
        {
            identity.Dispose();
        }

        _key.Dispose();
    }

    /// <summary>
    /// Reads the server license file again and, when it verifies for this server as at the start (design §13.1), puts it in
    /// place: the borrow record and the usage log record the change first, then new leases are issued under it. Otherwise,
    /// or when the borrow record cannot record the change, the old license stays.
    /// </summary>
    /// <returns><see langword="null"/> when the new license is in place, otherwise why it was not.</returns>
    public LicenseError? ReloadLicense()
    {
        (VerifiedLicense? license, byte[]? file, LicenseError? error) = TryVerifyLicense(_settings, _store, _key, _trust, Time);
        if (error is not null)
        {
            return error;
        }

        if (!ServerIdentity.TryCreate(license!, _keyJwk, out ServerIdentity? identity, out LicenseError? identityError))
        {
            return identityError;
        }

        lock (_reloadLock)
        {
            try
            {
                _journal.ChangeKey(file!, _keyJwk, KeyAccess);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                identity!.Dispose();
                return new LicenseError(ErrorCode.InternalError, "The borrow record cannot record the new license: " + e.Message);
            }

            // A usage log that cannot record it says so in the operational log and starts its next file with the new license.
            _usage.ChangeKey(file!, _keyJwk, KeyAccess);
            Engine.ReplaceLicense(identity!);
            _identities.Add(identity!);
            Volatile.Write(ref _licenseFile, file!);
        }

        ServerLog.Reloaded(_log, identity!.License.LicenseId, identity.License.TokenId);
        return null;
    }

    private static UsageLog OpenUsageLog(
        ServerSettings settings,
        InstallationKeyStore store,
        SigningKeyAccess access,
        byte[] licenseFile,
        byte[] keyJwk,
        TimeProvider time,
        ILogger log
    )
    {
        string path = Path.IsPathRooted(settings.UsageLog) ? settings.UsageLog : Path.Combine(store.Directory, settings.UsageLog);
        try
        {
            return UsageLog.Open(path, access, licenseFile, keyJwk, time, log);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ServerStartException($"The usage log '{path}' cannot be written: {e.Message}");
        }
    }

    // design §13.1: a borrow record that cannot be read or written stops the start with a reason, like the usage log.
    private static BorrowJournal Journal(Func<BorrowJournal> step)
    {
        try
        {
            return step();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ServerStartException($"The borrow record cannot be read or written: {e.Message}");
        }
    }

    private static TrustStore ReadTrust(ServerSettings settings, Func<string, string?> environment)
    {
        var keys = new List<string>();
        if (settings.TrustRootKeyFiles.Count > 0)
        {
            foreach (string path in settings.TrustRootKeyFiles)
            {
                string text;
                try
                {
                    text = File.ReadAllText(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw new ServerStartException($"trust.rootKeys file '{path}' cannot be read: {e.Message}");
                }

                keys.AddRange(KeyFiles.SplitPem(text));
            }
        }
        else if (environment(ServerSettings.TrustRootKeysVariable) is { Length: > 0 } pem)
        {
            keys.AddRange(KeyFiles.SplitPem(pem));
        }

        if (keys.Count == 0)
        {
            // design §13.8: elm-server is a generic binary without the vendor's root key, so the operator must give it.
            throw new ServerStartException($"Set trust.rootKeys or {ServerSettings.TrustRootKeysVariable} to the vendor's root public keys.");
        }

        try
        {
            return TrustStore.Create([.. keys], [.. settings.TrustRevokedKeyIds]);
        }
        catch (ArgumentException e)
        {
            throw new ServerStartException("The trust settings are not valid: " + e.Message);
        }
    }

    // design §13.1 step 3: the server license must verify for this installation; at the start a failure stops the server,
    // at a reload it keeps the old license.
    private static (VerifiedLicense? License, byte[]? File, LicenseError? Error) TryVerifyLicense(
        ServerSettings settings,
        InstallationKeyStore store,
        InstallationKey key,
        TrustStore trust,
        TimeProvider time
    )
    {
        IReadOnlyList<string> fingerprints = MachineFingerprint.Compute(settings.Product);
        byte[] file;
        try
        {
            var info = new FileInfo(settings.LicensePath);
            if (!info.Exists)
            {
                string missing =
                    $"The server license '{settings.LicensePath}' does not exist. Ask the vendor for a server license bound to "
                    + $"this server: installation key {key.Thumbprint}, fingerprints {string.Join(", ", fingerprints)}.";
                return (null, null, new LicenseError(ErrorCode.LicenseNotFound, missing));
            }

            if (info.Length > MaxLicenseFileLength)
            {
                return (null, null, new LicenseError(ErrorCode.Malformed, $"The server license '{settings.LicensePath}' is larger than 64 KiB."));
            }

            file = File.ReadAllBytes(settings.LicensePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, null, new LicenseError(ErrorCode.Malformed, $"The server license '{settings.LicensePath}' cannot be read: {e.Message}"));
        }

        var verifier = new LicenseVerifier(trust, time, new FileClockGuard(Path.Combine(store.Directory, "clock")));
        var requirement = new LicenseRequirement(settings.Product, LicenseKind.Server, null, null, key.Identity(fingerprints));
        switch (verifier.Verify(file, requirement))
        {
            case LicenseVerificationResult.Valid valid:
                return (valid.License, file, null);
            case LicenseVerificationResult.Invalid invalid:
                string message =
                    $"The server license does not verify ({ErrorCodes.ToWireName(invalid.Error.Code)}): {invalid.Error.Message} "
                    + $"This server's installation key is {key.Thumbprint}.";
                return (null, null, new LicenseError(invalid.Error.Code, message));
            default:
                throw new InvalidOperationException("Unknown license verification result.");
        }
    }

    private static IReadOnlyList<Uri> DefaultPublicUrls(Uri listen, int port)
    {
        string host = listen.Host;
        if (host is "0.0.0.0" or "[::]")
        {
            return [new Uri($"https://{Dns.GetHostName().ToLowerInvariant()}:{port}/"), new Uri($"https://localhost:{port}/")];
        }

        return [new UriBuilder(listen) { Port = port, Path = "/" }.Uri];
    }

    private static WebApplication Build(ServerSettings settings, X509Certificate2 certificate, ServerHostOptions host)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.SetMinimumLevel(host.MinimumLogLevel);
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;

            // The handlers read at most 64 KiB and answer 413 themselves (design §12.1); Kestrel's own limit only stops
            // what is far larger.
            kestrel.Limits.MaxRequestBodySize = 1024 * 1024;

            // ServerSettings.Load checks the hosts already; settings built in code are checked here.
            kestrel.Listen(ServerSettings.ListenAddress(settings.Listen), settings.Listen.Port, options => options.UseHttps(certificate));
            kestrel.Listen(ServerSettings.ListenAddress(settings.AdminListen), settings.AdminListen.Port);
        });
        return builder.Build();
    }

    private static int StatusOf(ErrorCode code) =>
        code switch
        {
            ErrorCode.InvalidArgument or ErrorCode.Malformed or ErrorCode.FingerprintUnavailable => StatusCodes.Status400BadRequest,
            ErrorCode.BadSignature or ErrorCode.ProofInvalid or ErrorCode.NonceRequired or ErrorCode.Replayed or ErrorCode.Unauthorized =>
                StatusCodes.Status401Unauthorized,
            ErrorCode.Expired
            or ErrorCode.FeatureNotLicensed
            or ErrorCode.FeatureVersionNotLicensed
            or ErrorCode.FeatureExpired
            or ErrorCode.BorrowNotAllowed
            or ErrorCode.Forbidden => StatusCodes.Status403Forbidden,
            ErrorCode.UnknownLease => StatusCodes.Status404NotFound,
            ErrorCode.CloneDetected or ErrorCode.NoSeats or ErrorCode.ReturnNotAllowed => StatusCodes.Status409Conflict,
            ErrorCode.PayloadTooLarge => StatusCodes.Status413PayloadTooLarge,
            ErrorCode.RateLimited => StatusCodes.Status429TooManyRequests,
            ErrorCode.ServerRecovering or ErrorCode.ServerBusy => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };

    private static async Task<byte[]?> ReadBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength > MaxBodyLength)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyLength)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static T? Parse<T>(byte[] body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(body, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsFingerprint(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigitLower);

    private static bool FitsUtf8(string? value, int maxBytes) => value is null || Encoding.UTF8.GetByteCount(value) <= maxBytes;

    private static Task ProblemAsync(HttpContext context, ErrorCode code, string message, TimeSpan? retryAfter = null, int? status = null)
    {
        int statusCode = status ?? StatusOf(code);
        string wire = ErrorCodes.ToWireName(code);
        if (retryAfter is TimeSpan wait)
        {
            context.Response.Headers.RetryAfter = ((long)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return WriteJsonAsync(
            context,
            statusCode,
            new Problem("about:blank", wire, statusCode, message, wire),
            ServerJsonContext.Default.Problem,
            "application/problem+json"
        );
    }

    private static Task WriteJsonAsync<T>(
        HttpContext context,
        int status,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type,
        string contentType = "application/json"
    )
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        return context.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value, type), context.RequestAborted).AsTask();
    }

    private void AfterStart(TimeSpan startup)
    {
        IServerAddressesFeature? addresses = _app
            .Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>();
        Uri[] bound = [.. addresses?.Addresses.Select(a => new Uri(a)) ?? []];
        int port = bound.FirstOrDefault(u => u.Scheme == Uri.UriSchemeHttps)?.Port ?? _settings.Listen.Port;
        int adminPort = bound.FirstOrDefault(u => u.Scheme == Uri.UriSchemeHttp)?.Port ?? _settings.AdminListen.Port;
        _adminUrl = new UriBuilder(_settings.AdminListen) { Port = adminPort }.Uri;
        if (_settings.PublicUrls.Count == 0)
        {
            _publicUrls = DefaultPublicUrls(_settings.Listen, port);
        }

        _cleanup = Time.CreateTimer(_ => Cleanup(), null, CleanupInterval, CleanupInterval);
        _seal = Time.CreateTimer(_ => SealLogs(), null, SealInterval, SealInterval);
        if (_journal.SetAside is string aside)
        {
            ServerLog.RecordSetAside(_log, aside);
            _usage.Write("records.set_aside", w => w.WriteString("file", Path.GetFileName(aside)));
        }

        if (_usage.ResetReason is string reason)
        {
            ServerLog.ChainReset(_log, UsageLog.Stream, reason);
        }

        _usage.Write(
            "server.started",
            w =>
            {
                w.WriteString("lic", Engine.Server.License.LicenseId);
                w.WriteString("key", KeyId);
                w.WriteNumber("borrows", Engine.Leases().Count(l => l.IsBorrow));
            }
        );
        ServerLog.Started(_log, port, _publicUrls, KeyId, Engine.Server.License.LicenseId, (long)startup.TotalMilliseconds, Engine.RecoveryEndsAt);
    }

    // design §13.2: expired leases, proof IDs, and idle rate buckets go every 30 seconds.
    private void Cleanup()
    {
        try
        {
            Engine.Purge();
            _replays.Purge();
            _limiter.Purge();
        }
        catch (Exception e)
        {
            ServerLog.CleanupFailed(_log, e);
        }
    }

    // design §18.1: the refusals of the last minute are written as counts, the pending lines are signed every 60 seconds, and
    // a new UTC day starts new files.
    private void SealLogs()
    {
        try
        {
            _usage.WriteDenials();
            _usage.Seal();
            _journal.Seal();
        }
        catch (Exception e)
        {
            ServerLog.CleanupFailed(_log, e);
        }
    }

    private void Map()
    {
        _app.Use(GuardAsync);
        _app.MapGet("/v1/nonce", (RequestDelegate)NonceAsync);
        _app.MapGet("/v1/server/license", (RequestDelegate)ServerLicenseAsync);
        _app.MapPost("/v1/leases", (RequestDelegate)AcquireAsync);
        _app.MapPost("/v1/leases/reclaim", (RequestDelegate)ReclaimAsync);
        _app.MapPost("/v1/leases/{id}/renew", (RequestDelegate)RenewAsync);
        _app.MapDelete("/v1/leases/{id}", (RequestDelegate)ReleaseAsync);
        MapAdmin();
    }

    // The administration API answers only on its loopback http listener, and the lease protocol only on the https one. The
    // IP rules and the rate limit come before any other work of the lease protocol (design §13.8, T16).
    private async Task GuardAsync(HttpContext context, RequestDelegate next)
    {
        bool adminPath = context.Request.Path.StartsWithSegments("/admin", StringComparison.Ordinal);
        if (!context.Request.IsHttps)
        {
            if (!adminPath)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        if (adminPath)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        IPAddress? address = context.Connection.RemoteIpAddress;
        if (!_access.AllowsAddress(address))
        {
            await ProblemAsync(context, ErrorCode.Forbidden, "The server's settings do not allow requests from this address.").ConfigureAwait(false);
            return;
        }

        if (address is not null && !_limiter.TryAcquire(address))
        {
            await ProblemAsync(context, ErrorCode.RateLimited, "Too many requests from this address.", TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private Task NonceAsync(HttpContext context)
    {
        string nonce = _nonces.Issue();
        context.Response.Headers[NonceHeader] = nonce;
        return WriteJsonAsync(context, StatusCodes.Status200OK, new NonceAnswer(nonce), ServerJsonContext.Default.NonceAnswer);
    }

    // design §13.5 step 1: the server license file and the server's public key.
    private async Task ServerLicenseAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("license");
            writer.WriteRawValue(Volatile.Read(ref _licenseFile));
            writer.WritePropertyName("key");
            writer.WriteRawValue(_keyJwk);
            writer.WriteEndObject();
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(buffer.ToArray(), context.RequestAborted).ConfigureAwait(false);
    }

    private async Task AcquireAsync(HttpContext context)
    {
        if (await ReadAuthenticatedAsync(context, knownKey: null).ConfigureAwait(false) is not (byte[] body, ProofResult.Valid proof))
        {
            return;
        }

        AcquireBody? request = Parse(body, ServerJsonContext.Default.AcquireBody);
        if (
            request is null
            || request.Version is { Length: > MaxVersionLength }
            || (request.Fingerprints is { } fingerprints && (fingerprints.Count > MaxFingerprints || !fingerprints.TrueForAll(IsFingerprint)))
            || !FitsUtf8(request.Client?.Host, MaxClientNameBytes)
            || !FitsUtf8(request.Client?.User, MaxClientNameBytes)
            || !FitsUtf8(request.App, MaxAppBytes)
            || request.Borrow is < 0 or > MaxBorrowSeconds
        )
        {
            await ProblemAsync(context, ErrorCode.Malformed, "The request body is not a valid lease request.").ConfigureAwait(false);
            return;
        }

        if (!_access.AllowsClient(request.Client?.Host, request.Client?.User))
        {
            await ProblemAsync(context, ErrorCode.Forbidden, "The server's settings do not allow this host or user.").ConfigureAwait(false);
            return;
        }

        TimeSpan? borrow = request.Borrow is long seconds ? TimeSpan.FromSeconds(seconds) : null;
        LeaseOutcome outcome = Engine.Acquire(
            new LeaseRequest(request.Feature, request.Version, request.Count, proof.KeyId, proof.Key, proof.ProofId, borrow, request.Fingerprints)
        );
        string licenseId = Engine.Server.License.LicenseId;
        void Client(Utf8JsonWriter w)
        {
            if (request.Client is { } client)
            {
                w.WriteString("host", client.Host);
                w.WriteString("user", client.User);
            }

            if (request.App is string app)
            {
                w.WriteString("app", app);
            }
        }

        if (outcome is LeaseOutcome.Granted granted)
        {
            ServerLog.Acquired(_log, granted.LeaseId, request.Feature, request.Count, proof.KeyId);
            _usage.Lease(
                borrow is null ? "lease.acquired" : "borrow.started",
                granted.LeaseId,
                request.Feature,
                request.Count,
                proof.KeyId,
                licenseId,
                Client
            );
            if (granted.InUse > granted.Seats)
            {
                _usage.Lease(
                    "overage",
                    granted.LeaseId,
                    request.Feature,
                    request.Count,
                    proof.KeyId,
                    licenseId,
                    w => w.WriteNumber("inUse", granted.InUse)
                );
            }
        }
        else if (outcome is LeaseOutcome.Refused refused)
        {
            _usage.Denied(request.Feature, refused.Error.Code);
        }

        await AnswerAsync(context, outcome, StatusCodes.Status201Created).ConfigureAwait(false);
    }

    private async Task RenewAsync(HttpContext context)
    {
        string id = context.Request.RouteValues["id"] as string ?? string.Empty;
        if (!Engine.TryGetLeaseKey(id, out string? keyId, out ECParameters? key))
        {
            context.Response.Headers[NonceHeader] = _nonces.Issue();
            await ProblemAsync(context, ErrorCode.UnknownLease, "The server does not know the lease; reclaim it.").ConfigureAwait(false);
            return;
        }

        if (
            await ReadAuthenticatedAsync(context, kid => kid == keyId ? key : null).ConfigureAwait(false)
            is not
            (byte[] body, ProofResult.Valid proof)
        )
        {
            return;
        }

        RenewBody? request = Parse(body, ServerJsonContext.Default.RenewBody);
        if (
            request is null
            || request.Retry is { Count: > LeaseEngine.MaxRetryProofIds }
            || (request.Retry?.Exists(id => id is null or { Length: 0 or > MaxProofIdLength }) ?? false)
        )
        {
            await ProblemAsync(context, ErrorCode.Malformed, "The request body is not a valid renewal.").ConfigureAwait(false);
            return;
        }

        LeaseOutcome outcome = Engine.Renew(id, request.Seq, proof.KeyId, proof.ProofId, request.Retry ?? []);
        LogClone(outcome, id, proof.KeyId);
        string licenseId = Engine.Server.License.LicenseId;
        if (outcome is LeaseOutcome.Granted granted)
        {
            _usage.Lease(
                "lease.renewed",
                granted.LeaseId,
                null,
                null,
                proof.KeyId,
                licenseId,
                w => w.WriteBoolean("cloneFlagged", granted.CloneFlagged)
            );
        }
        else if (outcome is LeaseOutcome.Refused refused)
        {
            _usage.Denied(null, refused.Error.Code);
        }

        await AnswerAsync(context, outcome, StatusCodes.Status200OK).ConfigureAwait(false);
    }

    private async Task ReleaseAsync(HttpContext context)
    {
        string id = context.Request.RouteValues["id"] as string ?? string.Empty;
        if (!Engine.TryGetLeaseKey(id, out string? keyId, out ECParameters? key))
        {
            // A release is idempotent (design §12.7.1).
            context.Response.Headers[NonceHeader] = _nonces.Issue();
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (await ReadAuthenticatedAsync(context, kid => kid == keyId ? key : null).ConfigureAwait(false) is not (_, ProofResult.Valid proof))
        {
            return;
        }

        string licenseId = Engine.Server.License.LicenseId;
        switch (Engine.Release(id, proof.KeyId, out LeaseInfo? lease))
        {
            case ReleaseOutcome.Released:
                ServerLog.Released(_log, id);
                _usage.Lease(
                    lease?.IsBorrow == true ? "borrow.returned" : "lease.released",
                    id,
                    lease?.FeatureId,
                    lease?.Seats,
                    proof.KeyId,
                    licenseId
                );
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                break;
            case ReleaseOutcome.NotFound:
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                break;
            case ReleaseOutcome.WrongKey:
                await ProblemAsync(context, ErrorCode.ProofInvalid, "The proof is not signed by the key the lease is bound to.")
                    .ConfigureAwait(false);
                break;
            case ReleaseOutcome.ReturnNotAllowed:
                _usage.Denied(lease?.FeatureId, ErrorCode.ReturnNotAllowed);
                await ProblemAsync(
                        context,
                        ErrorCode.ReturnNotAllowed,
                        "The borrow cannot be returned early: the early returns of 30 days are used up, or the server license changed."
                    )
                    .ConfigureAwait(false);
                break;
            default:
                await ProblemAsync(context, ErrorCode.CloneDetected, "The lease was stopped as a clone; it ends at its expiry.")
                    .ConfigureAwait(false);
                break;
        }
    }

    private async Task ReclaimAsync(HttpContext context)
    {
        if (await ReadAuthenticatedAsync(context, knownKey: null).ConfigureAwait(false) is not (byte[] body, ProofResult.Valid proof))
        {
            return;
        }

        ReclaimBody? request = Parse(body, ServerJsonContext.Default.ReclaimBody);
        if (request is null || request.Leases.Count > LeaseEngine.MaxReclaimTokens || request.Leases.Contains(null!))
        {
            await ProblemAsync(context, ErrorCode.Malformed, $"A reclaim carries 0 to {LeaseEngine.MaxReclaimTokens} lease tokens.")
                .ConfigureAwait(false);
            return;
        }

        IReadOnlyList<ReclaimResult> results = Engine.Reclaim(request.Leases, proof.KeyId, proof.Key);
        string licenseId = Engine.Server.License.LicenseId;
        foreach (ReclaimResult result in results)
        {
            if (result.Error is null)
            {
                ServerLog.Reclaimed(_log, result.LeaseId, proof.KeyId);
                _usage.Lease(
                    "lease.reclaimed",
                    result.LeaseId,
                    null,
                    null,
                    proof.KeyId,
                    licenseId,
                    w => w.WriteBoolean("cloneFlagged", result.CloneFlagged)
                );
            }
            else
            {
                if (result.Error.Code == ErrorCode.CloneDetected)
                {
                    ServerLog.CloneDetected(_log, result.LeaseId, proof.KeyId);
                }

                _usage.Denied(null, result.Error.Code);
            }

            if (result.CloneFlagged)
            {
                ServerLog.CloneFlagged(_log, result.LeaseId, proof.KeyId);
            }
        }

        var answer = new ReclaimAnswer([.. results.Select(r => new ReclaimItem(r.LeaseId, ErrorCodes.ToWireName(r.Error?.Code ?? ErrorCode.Ok)))]);
        await WriteJsonAsync(context, StatusCodes.Status200OK, answer, ServerJsonContext.Default.ReclaimAnswer).ConfigureAwait(false);
    }

    // Reads the body within the limit and verifies the proof (design §12.2). Every answer of a lease endpoint carries a fresh
    // nonce, so the client rarely needs GET /v1/nonce. On failure the problem is already written and null is returned.
    private async Task<(byte[] Body, ProofResult.Valid Proof)?> ReadAuthenticatedAsync(HttpContext context, Func<string, ECParameters?>? knownKey)
    {
        context.Response.Headers[NonceHeader] = _nonces.Issue();
        byte[]? body = await ReadBodyAsync(context).ConfigureAwait(false);
        if (body is null)
        {
            await ProblemAsync(context, ErrorCode.PayloadTooLarge, "The request body is larger than 64 KiB.").ConfigureAwait(false);
            return null;
        }

        string? proof = context.Request.Headers[ProofHeader] is { Count: 1 } values ? values[0] : null;
        string path = context.Request.PathBase.Add(context.Request.Path).Value ?? string.Empty;
        Uri[] targets = [.. _publicUrls.Select(u => new UriBuilder(u) { Path = u.AbsolutePath.TrimEnd('/') + path }.Uri)];
        switch (_proofs.Verify(proof, context.Request.Method, targets, body, knownKey))
        {
            case ProofResult.Valid valid:
                return (body, valid);
            case ProofResult.Invalid invalid:
                await ProblemAsync(context, invalid.Error.Code, invalid.Error.Message).ConfigureAwait(false);
                return null;
            default:
                throw new InvalidOperationException("Unknown proof result.");
        }
    }

    private Task AnswerAsync(HttpContext context, LeaseOutcome outcome, int successStatus)
    {
        switch (outcome)
        {
            case LeaseOutcome.Granted granted:
                // design §12.7.1: the jti of the server license the lease was issued under, so the client can fetch a new copy.
                context.Response.Headers[ServerLicenseHeader] = Engine.Server.License.TokenId;
                return WriteJsonAsync(context, successStatus, new LeaseAnswer(granted.Token, []), ServerJsonContext.Default.LeaseAnswer);
            case LeaseOutcome.Refused refused:
                return ProblemAsync(context, refused.Error.Code, refused.Error.Message, refused.RetryAfter);
            default:
                throw new InvalidOperationException("Unknown lease outcome.");
        }
    }

    private void LogClone(LeaseOutcome outcome, string leaseId, string keyId)
    {
        if (outcome is LeaseOutcome.Refused { Error.Code: ErrorCode.CloneDetected })
        {
            ServerLog.CloneDetected(_log, leaseId, keyId);
        }
        else if (outcome is LeaseOutcome.Granted { CloneFlagged: true })
        {
            ServerLog.CloneFlagged(_log, leaseId, keyId);
        }
    }

    private sealed record Parts(
        WebApplication App,
        ServerSettings Settings,
        InstallationKeyStore Store,
        InstallationKey Key,
        TrustStore Trust,
        ServerIdentity Identity,
        LeaseEngine Engine,
        byte[] LicenseFile,
        X509Certificate2 Certificate,
        IReadOnlyList<Uri> PublicUrls,
        UsageLog Usage,
        BorrowJournal Journal,
        string AdminToken,
        TimeProvider Time,
        ILogger Log
    );
}
