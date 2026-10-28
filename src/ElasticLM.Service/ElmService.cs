using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ElasticLM.Service;

/// <summary>
/// How the service is hosted: the clock, the environment, and the log level. Tests pass a fake clock.
/// </summary>
internal sealed class ServiceHostOptions
{
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    public LogLevel MinimumLogLevel { get; init; } = LogLevel.Information;
}

/// <summary>
/// The hosted activation service <c>elm-service</c> (design §14): M4a1 is its foundation. It reads the settings, the trust
/// store, and the signing key with its certificates (decision 0.75), brings the database schema up to date (decision 0.69),
/// and serves the administration API for API keys and the audit log (design §14.5).
/// </summary>
internal sealed partial class ElmService : IAsyncDisposable
{
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProofPurgeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DenialInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan CertificateWarning = TimeSpan.FromDays(30);

    private readonly WebApplication _app;
    private readonly ServiceSettings _settings;
    private readonly ECDsa _signingKey;
    private readonly IReadOnlyList<string> _signingCertificates;
    private readonly TrustStore _trust;
    private readonly X509Certificate2? _tls;
    private readonly RateLimiter<IPAddress> _perIp;
    private readonly RateLimiter<Guid> _perKey;
    private readonly RateLimiter<IPAddress> _clientPerIp;
    private readonly RateLimiter<string> _clientPerKey;
    private readonly NonceService _nonces;
    private readonly ProofVerifier _proofs;
    private readonly ILogger _log;
    private readonly Lock _denialsLock = new();
    private readonly PeriodicWork _denialWork;
    private readonly PeriodicWork _proofWork;
    private readonly PeriodicWork _retentionWork;
    private long _denials;
    private ITimer? _purge;
    private ITimer? _denialTimer;
    private ITimer? _retention;
    private ITimer? _proofPurge;
    private IReadOnlyList<Uri> _publicUrls = [];
    private bool _stopped;
    private bool _disposed;

    private ElmService(
        WebApplication app,
        ServiceSettings settings,
        Database database,
        ECDsa signingKey,
        IReadOnlyList<string> signingCertificates,
        SigningKeyStatus signing,
        TrustStore trust,
        X509Certificate2? tls,
        TimeProvider time,
        ILogger log
    )
    {
        _app = app;
        _settings = settings;
        Database = database;
        _signingKey = signingKey;
        _signingCertificates = signingCertificates;
        _trust = trust;
        Signing = signing;
        _tls = tls;
        Time = time;
        _log = log;
        _perIp = new RateLimiter<IPAddress>(settings.RateLimitPerIp, time);
        _perKey = new RateLimiter<Guid>(settings.RateLimitPerKey, time);
        _clientPerIp = new RateLimiter<IPAddress>(settings.RateLimitClientPerIp, time);
        _clientPerKey = new RateLimiter<string>(settings.RateLimitClientPerKey, time);
        _nonces = new NonceService(settings.NonceSecret, time);

        // The jti is recorded in the database after the other checks (design §12.2 rule 5), so the verifier's store accepts all.
        _proofs = new ProofVerifier(_nonces, AcceptingReplays.Instance);
        _denialWork = new PeriodicWork(WriteDenialsAsync);
        _proofWork = new PeriodicWork(RemoveOldProofsAsync);
        _retentionWork = new PeriodicWork(RemoveOldAuditAsync);
    }

    /// <summary>Gets the URL the service answers on.</summary>
    public Uri Url { get; private set; } = new("http://127.0.0.1/");

    /// <summary>Gets the database.</summary>
    public Database Database { get; }

    /// <summary>Gets the signing key's status at the start.</summary>
    public SigningKeyStatus Signing { get; }

    /// <summary>Gets the clock.</summary>
    public TimeProvider Time { get; }

    /// <summary>
    /// Starts the service: settings, trust store, signing key and certificates, database and schema, then listening.
    /// </summary>
    /// <exception cref="ServiceStartException">The service cannot start; the message says why.</exception>
    public static async Task<ElmService> StartAsync(ServiceSettings settings, ServiceHostOptions host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(host);
        if (settings.NonceSecret.Length < NonceService.MinSecretLength)
        {
            throw new ServiceStartException($"Set {ServiceSettings.NonceSecretVariable} or nonce.secretFile to the nonces' secret.");
        }

        long started = host.Time.GetTimestamp();
        TrustStore trust = ReadTrust(settings, host.Environment);
        (ECDsa signingKey, IReadOnlyList<string> certificates, SigningKeyStatus signing) = ReadSigningKey(settings, trust, host);
        var disposables = new List<IAsyncDisposable>();
        X509Certificate2? tls = null;
        try
        {
            Database database = Database.Create(settings.Database, settings.ConnectionString);
            disposables.Add(database);
            IReadOnlyList<int> applied;
            try
            {
                applied = await Migrator.RunAsync(database, Migrator.Embedded(settings.Database), host.Time, cancellationToken).ConfigureAwait(false);
            }
            catch (System.Data.Common.DbException e)
            {
                throw new ServiceStartException("The database cannot be used: " + e.Message);
            }

            if (settings.Listen.Scheme == Uri.UriSchemeHttps)
            {
                tls = TlsCertificateFiles.TryLoad(settings.TlsCertificate!, settings.TlsKey, null, out X509Certificate2? loaded, out string? problem)
                    ? loaded
                    : throw new ServiceStartException(problem!);
            }

            WebApplication app = Build(settings, tls, host);
            disposables.Add(app);
            ILogger log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("elm-service");
            var service = new ElmService(app, settings, database, signingKey, certificates, signing, trust, tls, host.Time, log);
            service.MapAdmin();
            try
            {
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException e)
            {
                // Kestrel reports an address in use, or one it may not bind, as an IOException.
                throw new ServiceStartException($"elm-service cannot listen on {settings.Listen}: {e.Message}");
            }

            service.AfterStart(applied, host.Time.GetElapsedTime(started));
            return service;
        }
        catch
        {
            tls?.Dispose();
            signingKey.Dispose();
            foreach (IAsyncDisposable disposable in Enumerable.Reverse(disposables))
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Waits until the host is asked to stop.</summary>
    public Task WaitForShutdownAsync() => _app.WaitForShutdownAsync();

    /// <summary>
    /// Stops listening and the timers, and writes the authentication failures counted since the last minute.
    /// </summary>
    public async Task StopAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        foreach (ITimer? timer in new[] { _purge, _denialTimer, _retention, _proofPurge })
        {
            if (timer is not null)
            {
                await timer.DisposeAsync().ConfigureAwait(false);
            }
        }

        // A timer's work that already started finishes before the last denials are written and the database is closed.
        await WaitForTimersAsync().ConfigureAwait(false);
        await _app.StopAsync().ConfigureAwait(false);
        await WriteDenialsAsync().ConfigureAwait(false);
        ServiceLog.Stopped(_log);
    }

    /// <summary>Waits for the timers' work that has started (the denial count, the jti records, retention) to end.</summary>
    internal async Task WaitForTimersAsync()
    {
        await Task.WhenAll(_denialWork.WhenIdleAsync(), _proofWork.WhenIdleAsync(), _retentionWork.WhenIdleAsync()).ConfigureAwait(false);
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
        await Database.DisposeAsync().ConfigureAwait(false);
        _tls?.Dispose();
        _signingKey.Dispose();
    }

    private static TrustStore ReadTrust(ServiceSettings settings, Func<string, string?> environment)
    {
        var keys = new List<string>();
        foreach (string path in settings.TrustRootKeyFiles)
        {
            try
            {
                keys.AddRange(KeyFiles.SplitPem(File.ReadAllText(path)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ServiceStartException($"trust.rootKeys file '{path}' cannot be read: {e.Message}");
            }
        }

        if (keys.Count == 0 && environment(ServiceSettings.TrustRootKeysVariable) is { Length: > 0 } pem)
        {
            keys.AddRange(KeyFiles.SplitPem(pem));
        }

        if (keys.Count == 0)
        {
            throw new ServiceStartException($"Set trust.rootKeys or {ServiceSettings.TrustRootKeysVariable} to the vendor's root public keys.");
        }

        try
        {
            return TrustStore.Create([.. keys], [.. settings.TrustRevokedKeyIds]);
        }
        catch (ArgumentException e)
        {
            throw new ServiceStartException("The trust settings are not valid: " + e.Message);
        }
    }

    // design §14.8, decision 0.75: one signing key, read at the start; its certificates must let it sign licenses and leases now.
    private static (ECDsa Key, IReadOnlyList<string> Certificates, SigningKeyStatus Status) ReadSigningKey(
        ServiceSettings settings,
        TrustStore trust,
        ServiceHostOptions host
    )
    {
        string pem;
        var certificates = new List<string>();
        try
        {
            pem = File.ReadAllText(
                settings.SigningKeyFile ?? throw new ServiceStartException("Set signing.key to the signing key file (PKCS#8 PEM).")
            );
            foreach (string path in settings.SigningCertificateFiles)
            {
                certificates.Add(File.ReadAllText(path));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ServiceStartException($"The signing key or a certificate file cannot be read: {e.Message}");
        }

        string passphrase =
            host.Environment(ServiceSettings.SigningPassphraseVariable)
            ?? throw new ServiceStartException($"Set {ServiceSettings.SigningPassphraseVariable} to the signing key's passphrase.");
        if (!KeyFiles.TryImportEncrypted(pem, passphrase, out ECDsa? key))
        {
            throw new ServiceStartException("The signing key cannot be read: it is not an encrypted PKCS#8 P-256 key, or the passphrase is wrong.");
        }

        if (
            !SigningKeyCertificates.TryCheck(
                key!,
                certificates,
                trust,
                KeyUses.License | KeyUses.Lease,
                host.Time.GetUtcNow(),
                out SigningKeyStatus? status,
                out string? problem
            )
        )
        {
            key!.Dispose();
            throw new ServiceStartException(problem!);
        }

        return (key!, certificates, status!);
    }

    private static WebApplication Build(ServiceSettings settings, X509Certificate2? tls, ServiceHostOptions host)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.SetMinimumLevel(host.MinimumLogLevel);
        if (tls is not null)
        {
            builder.WebHost.UseKestrelHttpsConfiguration();
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;

            // The handlers read at most 64 KiB and answer 413 themselves (design §12.1); Kestrel's own limit stops what is far larger.
            kestrel.Limits.MaxRequestBodySize = 1024 * 1024;
            kestrel.Listen(
                ServiceSettings.ListenAddress(settings.Listen),
                settings.Listen.Port,
                options =>
                {
                    if (tls is not null)
                    {
                        options.UseHttps(tls);
                    }
                }
            );
        });
        return builder.Build();
    }

    private void AfterStart(IReadOnlyList<int> applied, TimeSpan startup)
    {
        IServerAddressesFeature? addresses = _app
            .Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>();
        Uri bound = addresses?.Addresses.Select(a => new Uri(a)).FirstOrDefault() ?? _settings.Listen;
        Url = new UriBuilder(_settings.Listen) { Port = bound.Port, Path = "/" }.Uri;
        _publicUrls = _settings.PublicUrls.Count > 0 ? _settings.PublicUrls : [Url];

        _purge = Time.CreateTimer(_ => Purge(), null, PurgeInterval, PurgeInterval);
        _denialTimer = Time.CreateTimer(_ => _denialWork.TryStart(), null, DenialInterval, DenialInterval);
        _proofPurge = Time.CreateTimer(_ => _proofWork.TryStart(), null, ProofPurgeInterval, ProofPurgeInterval);
        if (_settings.AuditRetention is not null)
        {
            _retention = Time.CreateTimer(_ => _retentionWork.TryStart(), null, RetentionInterval, RetentionInterval);
        }

        if (applied.Count > 0 && _log.IsEnabled(LogLevel.Information))
        {
            string versions = string.Join(", ", applied.Select(v => v.ToString(CultureInfo.InvariantCulture)));
            ServiceLog.Migrated(_log, versions);
        }

        if (Signing.ExpiresAt - Time.GetUtcNow() < CertificateWarning)
        {
            ServiceLog.CertificateEndsSoon(_log, Signing.ExpiresAt);
        }

        ServiceLog.Started(_log, Url, _settings.Database, Signing.KeyId, Signing.ExpiresAt, (long)startup.TotalMilliseconds);
    }

    private void Purge()
    {
        _perIp.Purge();
        _perKey.Purge();
        _clientPerIp.Purge();
        _clientPerKey.Purge();
    }

    // design §14.4: once a minute, the jti records whose nonce has expired.
    private async Task RemoveOldProofsAsync()
    {
        try
        {
            await Database.RemoveProofsBeforeAsync(Time.GetUtcNow().ToUnixTimeSeconds(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException)
        {
            ServiceLog.PeriodicFailed(_log, e);
        }
    }

    // The service records each jti in its database (RecordProofAsync), where every node sees it.
    private sealed class AcceptingReplays : IReplayStore
    {
        public static AcceptingReplays Instance { get; } = new();

        public ReplayCheck TryUse(string jti, DateTimeOffset expiresAt) => ReplayCheck.Accepted;
    }

    // Counted authentication failures, written as one row a minute (design §14.5), so failing requests cannot grow the log line by line.
    private void CountDenial()
    {
        lock (_denialsLock)
        {
            _denials++;
        }
    }

    private async Task WriteDenialsAsync()
    {
        long count;
        lock (_denialsLock)
        {
            count = _denials;
            _denials = 0;
        }

        if (count == 0)
        {
            return;
        }

        try
        {
            string detail = AuditLog.Detail(w => w.WriteNumber("count", count));
            var entry = new AuditEntry("system", "auth", AuditActions.AuthFailed, null, "ELM_UNAUTHORIZED", null, detail);
            await Database.AppendAuditAsync(entry, Time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException)
        {
            // The failures are counted again, so the next minute's row includes them.
            lock (_denialsLock)
            {
                _denials += count;
            }

            ServiceLog.PeriodicFailed(_log, e);
        }
    }

    private async Task RemoveOldAuditAsync()
    {
        try
        {
            DateTimeOffset before = Time.GetUtcNow() - _settings.AuditRetention!.Value;
            long removed = await Database.RemoveAuditOlderThanAsync(before, CancellationToken.None).ConfigureAwait(false);
            if (removed > 0)
            {
                ServiceLog.AuditRemoved(_log, removed, before);
            }
        }
        catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException)
        {
            ServiceLog.PeriodicFailed(_log, e);
        }
    }
}
