using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
/// How the server is hosted: the clock, the environment, and the log level. Tests pass a fake clock.
/// </summary>
internal sealed class ServerHostOptions
{
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    public LogLevel MinimumLogLevel { get; init; } = LogLevel.Information;
}

/// <summary>
/// The on-premises license server (design §13): it starts as §13.1 says and serves the nonce, the server license, and the
/// lease protocol (§12.7.1, §13.4).
/// </summary>
internal sealed class ElmServer : IAsyncDisposable
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
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);

    private readonly WebApplication _app;
    private readonly InstallationKey _key;
    private readonly ServerIdentity _identity;
    private readonly byte[] _licenseFile;
    private readonly byte[] _keyJwk;
    private readonly X509Certificate2 _certificate;
    private readonly NonceService _nonces;
    private readonly MemoryReplayStore _replays;
    private readonly ProofVerifier _proofs;
    private readonly IpRateLimiter _limiter;
    private readonly AccessRules _access;
    private readonly ILogger _log;
    private ITimer? _cleanup;
    private IReadOnlyList<Uri> _publicUrls;
    private bool _stopped;
    private bool _disposed;

    private ElmServer(
        WebApplication app,
        InstallationKey key,
        ServerIdentity identity,
        LeaseEngine engine,
        byte[] licenseFile,
        X509Certificate2 certificate,
        ServerSettings settings,
        IReadOnlyList<Uri> publicUrls,
        TimeProvider time
    )
    {
        _app = app;
        _key = key;
        _identity = identity;
        Engine = engine;
        _licenseFile = licenseFile;
        _keyJwk = key.WithPrivateKey(KeyFiles.ExportPublicJwk);
        _certificate = certificate;
        _publicUrls = publicUrls;
        Time = time;
        _access = settings.Access;
        _limiter = new IpRateLimiter(settings.RateLimitPerIp, time);

        // design §13.7: a new secret at each start, never stored, so nonces from before a restart are void.
        _nonces = new NonceService(RandomNumberGenerator.GetBytes(32), time);
        _replays = new MemoryReplayStore(time, MaxReplayEntries);
        _proofs = new ProofVerifier(_nonces, _replays);
        _log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("elm-server");
    }

    /// <summary>Gets the lease engine.</summary>
    public LeaseEngine Engine { get; }

    /// <summary>Gets the URLs clients use; proofs name one of them (design §12.1).</summary>
    public IReadOnlyList<Uri> PublicUrls => _publicUrls;

    /// <summary>Gets the installation key thumbprint, which is the server license's <c>cnf.jkt</c>.</summary>
    public string KeyId => _identity.KeyId;

    /// <summary>Gets the clock.</summary>
    public TimeProvider Time { get; }

    /// <summary>
    /// Starts the server as design §13.1 says: settings, installation key, trust store, server license, TLS, then listening
    /// with the reclaim window open.
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
            (VerifiedLicense license, byte[] licenseFile) = VerifyLicense(settings, store, key!, trust, host.Time);
            if (
                !ServerIdentity.TryCreate(
                    license,
                    key!.WithPrivateKey(KeyFiles.ExportPublicJwk),
                    out ServerIdentity? identity,
                    out LicenseError? identityError
                )
            )
            {
                throw new ServerStartException(identityError!.Message);
            }

            disposables.Add(identity!);
            IReadOnlyList<Uri> publicUrls =
                settings.PublicUrls.Count > 0 ? settings.PublicUrls : DefaultPublicUrls(settings.Listen, settings.Listen.Port);
            X509Certificate2 certificate = settings.TlsCertificate is string certificatePath
                ? TlsCertificates.FromFiles(certificatePath, settings.TlsKey, host.Environment(ServerSettings.TlsPasswordVariable))
                : TlsCertificates.SelfSigned(key!, publicUrls, host.Time);
            disposables.Add(certificate);

            var engine = new LeaseEngine(
                identity!,
                (spec, issuedAt) => key!.WithPrivateKey(ecdsa => LeaseIssuer.Issue(ecdsa, spec, issuedAt)),
                new LeaseVerifier(trust, host.Time, StatelessClockGuard.Instance),
                host.Time
            );
            WebApplication app = Build(settings, certificate, host);
            var server = new ElmServer(app, key!, identity!, engine, licenseFile, certificate, settings, publicUrls, host.Time);
            server.Map();
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            server.AfterStart(settings, host.Time.GetElapsedTime(started));
            return server;
        }
        catch
        {
            foreach (IDisposable disposable in disposables)
            {
                disposable.Dispose();
            }

            throw;
        }
    }

    /// <summary>Waits until the host is asked to stop (Ctrl+C, SIGTERM, or a service stop).</summary>
    public Task WaitForShutdownAsync() => _app.WaitForShutdownAsync();

    /// <summary>
    /// Stops listening and the timers (design §13.2). Leases not returned are dropped; clients reclaim them after a restart.
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

        await _app.StopAsync().ConfigureAwait(false);
        ServerLog.Stopped(_log, Engine.LeaseCount);
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
        _certificate.Dispose();
        _identity.Dispose();
        _key.Dispose();
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

    // design §13.1 step 3: the server license must verify for this installation, or the server does not start.
    private static (VerifiedLicense License, byte[] File) VerifyLicense(
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
                throw new ServerStartException(
                    $"The server license '{settings.LicensePath}' does not exist. Ask the vendor for a server license bound to this "
                        + $"server: installation key {key.Thumbprint}, fingerprints {string.Join(", ", fingerprints)}."
                );
            }

            if (info.Length > MaxLicenseFileLength)
            {
                throw new ServerStartException($"The server license '{settings.LicensePath}' is larger than 64 KiB.");
            }

            file = File.ReadAllBytes(settings.LicensePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ServerStartException($"The server license '{settings.LicensePath}' cannot be read: {e.Message}");
        }

        var verifier = new LicenseVerifier(trust, time, new FileClockGuard(Path.Combine(store.Directory, "clock")));
        var requirement = new LicenseRequirement(settings.Product, LicenseKind.Server, null, null, key.Identity(fingerprints));
        return verifier.Verify(file, requirement) switch
        {
            LicenseVerificationResult.Valid valid => (valid.License, file),
            LicenseVerificationResult.Invalid invalid => throw new ServerStartException(
                $"The server license does not verify ({ErrorCodes.ToWireName(invalid.Error.Code)}): {invalid.Error.Message} "
                    + $"This server's installation key is {key.Thumbprint}."
            ),
            _ => throw new InvalidOperationException("Unknown license verification result."),
        };
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
            Listen(kestrel, settings.Listen, certificate);
        });
        return builder.Build();
    }

    private static void Listen(KestrelServerOptions kestrel, Uri listen, X509Certificate2 certificate)
    {
        // ServerSettings.Load checks the host already; settings built in code are checked here.
        IPAddress address = ServerSettings.ListenAddress(listen);
        kestrel.Listen(address, listen.Port, options => options.UseHttps(certificate));
    }

    private static int StatusOf(ErrorCode code) =>
        code switch
        {
            ErrorCode.InvalidArgument or ErrorCode.Malformed or ErrorCode.FingerprintUnavailable => StatusCodes.Status400BadRequest,
            ErrorCode.BadSignature or ErrorCode.ProofInvalid or ErrorCode.NonceRequired or ErrorCode.Replayed => StatusCodes.Status401Unauthorized,
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

    private static bool FitsUtf8(string? value, int maxBytes) => value is null || System.Text.Encoding.UTF8.GetByteCount(value) <= maxBytes;

    private void AfterStart(ServerSettings settings, TimeSpan startup)
    {
        IServerAddressesFeature? addresses = _app
            .Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>();
        int port = addresses?.Addresses.Select(a => new Uri(a).Port).FirstOrDefault() ?? settings.Listen.Port;
        if (settings.PublicUrls.Count == 0)
        {
            _publicUrls = DefaultPublicUrls(settings.Listen, port);
        }

        _cleanup = Time.CreateTimer(_ => Cleanup(), null, CleanupInterval, CleanupInterval);
        ServerLog.Started(_log, port, _publicUrls, KeyId, _identity.License.LicenseId, (long)startup.TotalMilliseconds, Engine.RecoveryEndsAt);
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

    private void Map()
    {
        _app.Use(GuardAsync);
        _app.MapGet("/v1/nonce", (RequestDelegate)NonceAsync);
        _app.MapGet("/v1/server/license", (RequestDelegate)ServerLicenseAsync);
        _app.MapPost("/v1/leases", (RequestDelegate)AcquireAsync);
        _app.MapPost("/v1/leases/reclaim", (RequestDelegate)ReclaimAsync);
        _app.MapPost("/v1/leases/{id}/renew", (RequestDelegate)RenewAsync);
        _app.MapDelete("/v1/leases/{id}", (RequestDelegate)ReleaseAsync);
    }

    // The IP rules and the rate limit, before any other work (design §13.8, T16).
    private async Task GuardAsync(HttpContext context, RequestDelegate next)
    {
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
            writer.WriteRawValue(_licenseFile);
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
        )
        {
            await ProblemAsync(context, ErrorCode.Malformed, "The request body is not a valid lease request.").ConfigureAwait(false);
            return;
        }

        if (request.Borrow is long borrow)
        {
            // Borrowing comes with the borrow records in the next row (M3c).
            await (
                borrow < 1
                    ? ProblemAsync(context, ErrorCode.InvalidArgument, "A borrow period is at least one second.")
                    : ProblemAsync(context, ErrorCode.BorrowNotAllowed, "This server does not lend borrows.")
            ).ConfigureAwait(false);
            return;
        }

        if (!_access.AllowsClient(request.Client?.Host, request.Client?.User))
        {
            await ProblemAsync(context, ErrorCode.Forbidden, "The server's settings do not allow this host or user.").ConfigureAwait(false);
            return;
        }

        LeaseOutcome outcome = Engine.Acquire(
            new LeaseRequest(request.Feature, request.Version, request.Count, proof.KeyId, proof.Key, proof.ProofId)
        );
        if (outcome is LeaseOutcome.Granted granted)
        {
            ServerLog.Acquired(_log, granted.LeaseId, request.Feature, request.Count, proof.KeyId);
        }

        await AnswerAsync(context, outcome, StatusCodes.Status201Created).ConfigureAwait(false);
    }

    private async Task RenewAsync(HttpContext context)
    {
        string id = context.Request.RouteValues["id"] as string ?? string.Empty;
        if (!Engine.TryGetLeaseKey(id, out string? keyId, out ECParameters key))
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
        await AnswerAsync(context, outcome, StatusCodes.Status200OK).ConfigureAwait(false);
    }

    private async Task ReleaseAsync(HttpContext context)
    {
        string id = context.Request.RouteValues["id"] as string ?? string.Empty;
        if (!Engine.TryGetLeaseKey(id, out string? keyId, out ECParameters key))
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

        switch (Engine.Release(id, proof.KeyId))
        {
            case ReleaseOutcome.Released:
                ServerLog.Released(_log, id);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                break;
            case ReleaseOutcome.NotFound:
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                break;
            case ReleaseOutcome.WrongKey:
                await ProblemAsync(context, ErrorCode.ProofInvalid, "The proof is not signed by the key the lease is bound to.")
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
        foreach (ReclaimResult result in results)
        {
            if (result.Error is null)
            {
                ServerLog.Reclaimed(_log, result.LeaseId, proof.KeyId);
            }
            else if (result.Error.Code == ErrorCode.CloneDetected)
            {
                ServerLog.CloneDetected(_log, result.LeaseId, proof.KeyId);
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
                context.Response.Headers[ServerLicenseHeader] = _identity.License.TokenId;
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

    private static Task ProblemAsync(HttpContext context, ErrorCode code, string message, TimeSpan? retryAfter = null)
    {
        int status = StatusOf(code);
        string wire = ErrorCodes.ToWireName(code);
        if (retryAfter is TimeSpan wait)
        {
            context.Response.Headers.RetryAfter = ((long)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return WriteJsonAsync(
            context,
            status,
            new Problem("about:blank", wire, status, message, wire),
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
}
