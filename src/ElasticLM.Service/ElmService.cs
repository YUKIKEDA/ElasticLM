using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
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

    /// <summary>Gets a wrapper around the usage log's access to the signing key, so tests can make a write fail.</summary>
    public Func<SigningKeyAccess, SigningKeyAccess>? WrapUsageLogKey { get; init; }
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
    private static readonly TimeSpan UsageSealInterval = TimeSpan.FromSeconds(60);
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
    private readonly LeaseVerifier _leaseCheck;
    private readonly ILogger _log;
    private readonly Lock _denialsLock = new();
    private readonly PeriodicWork _denialWork;
    private readonly PeriodicWork _proofWork;
    private readonly PeriodicWork _retentionWork;
    private readonly PeriodicWork _usageWork;
    private readonly PeriodicWork _sessionWork;
    private readonly ServiceUsageLog? _usage;
    private long _denials;
    private ITimer? _usageTimer;
    private ITimer? _purge;
    private ITimer? _denialTimer;
    private ITimer? _retention;
    private ITimer? _proofPurge;
    private ITimer? _sessionTimer;
    private DataProtectionKeyStore? _keyStore;
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
        ILogger log,
        ServiceUsageLog? usage
    )
    {
        _app = app;
        _usage = usage;
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

        // design §12.7.6: each lease is checked as a client would before it is returned; the service keeps no clock record.
        _leaseCheck = new LeaseVerifier(trust, time, StatelessClockGuard.Instance);
        _denialWork = new PeriodicWork(WriteDenialsAsync);
        _proofWork = new PeriodicWork(RemoveOldProofsAsync);
        _retentionWork = new PeriodicWork(RemoveOldAuditAsync);
        _usageWork = new PeriodicWork(SealUsageAsync);
        _sessionWork = new PeriodicWork(SweepSessionsAsync);
    }

    /// <summary>Gets the URL the service answers on.</summary>
    public Uri Url { get; private set; } = new("http://127.0.0.1/");

    /// <summary>Gets the host's services, for tests that look at what the host registered (the Data Protection key ring, M5a).</summary>
    internal IServiceProvider Services => _app.Services;

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
        ServiceUsageLog? usage = null;
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

            var hooks = new WebHooks();
            WebApplication app = Build(settings, tls, host, database, hooks);
            disposables.Add(app);
            ILogger log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("elm-service");

            // design §18.1, 0.95: this node's usage log, signed with the signing key, whose certificates verify it.
            if (settings.UsageLogDirectory is string usageDirectory)
            {
                ECDsa key = signingKey;
                try
                {
                    SigningKeyAccess access = use => use(key);
                    usage = ServiceUsageLog.Open(
                        usageDirectory,
                        settings.NodeName,
                        host.WrapUsageLogKey?.Invoke(access) ?? access,
                        certificates,
                        host.Time,
                        log
                    );
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw new ServiceStartException($"The usage log cannot be written in '{usageDirectory}': {e.Message}");
                }
            }

            var service = new ElmService(app, settings, database, signingKey, certificates, signing, trust, tls, host.Time, log, usage)
            {
                _keyStore = hooks.KeyStore,
            };
            hooks.Service = service;
            service.MapAdmin();
            await service.EnsureDataProtectionKeyAsync(cancellationToken).ConfigureAwait(false);

            // design §18.1: the start is the first line after the chain line, before any request can write one.
            usage?.Write("service.started", w => w.WriteString("node", settings.NodeName));
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
            usage?.Dispose();
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
        foreach (ITimer? timer in new[] { _purge, _denialTimer, _retention, _proofPurge, _usageTimer, _sessionTimer })
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
        if (_usage is not null)
        {
            // design §18.1: the counted refusals, the stop, and the last signature before the file is closed.
            _usage.WriteDenials();
            _usage.Write("service.stopped", w => w.WriteString("node", _settings.NodeName));
            _usage.Dispose();
        }

        ServiceLog.Stopped(_log);
    }

    /// <summary>Waits for the timers' work that has started (the denial count, the jti records, retention) to end.</summary>
    internal async Task WaitForTimersAsync()
    {
        await Task.WhenAll(
                _denialWork.WhenIdleAsync(),
                _proofWork.WhenIdleAsync(),
                _retentionWork.WhenIdleAsync(),
                _usageWork.WhenIdleAsync(),
                _sessionWork.WhenIdleAsync()
            )
            .ConfigureAwait(false);
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

    // design §14.8, decision 0.75: one signing key, read at the start; its certificates must let it sign licenses and leases now,
    // and logs too when the usage log is written (decision 0.95).
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
                // A certificate file ends with a line break (elm key certify); the certificate is the text without it, as it
                // goes into license files, lease answers, and the usage log, where a stray line break fails verification.
                certificates.Add(File.ReadAllText(path).Trim());
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ServiceStartException($"The signing key or a certificate file cannot be read: {e.Message}");
        }

        // design §8.6, §14.8: a license file, a lease answer, and a usage log carry one to four certificates.
        if (certificates.Count > 4 || certificates.Exists(c => c.Length == 0))
        {
            throw new ServiceStartException("signing.certificates must name one to four certificate files, none empty.");
        }

        // design §14.8 (F2): the variable, or else the file, so that every secret can be given as a file.
        string passphrase =
            host.Environment(ServiceSettings.SigningPassphraseVariable)
            ?? (
                settings.SigningPassphraseFile is string passphraseFile
                    ? ServiceSettings.ReadSecret(passphraseFile, "signing.passphraseFile")
                    : throw new ServiceStartException(
                        $"Set {ServiceSettings.SigningPassphraseVariable} or signing.passphraseFile to the signing key's passphrase."
                    )
            );
        if (!KeyFiles.TryImportEncrypted(pem, passphrase, out ECDsa? key))
        {
            throw new ServiceStartException("The signing key cannot be read: it is not an encrypted PKCS#8 P-256 key, or the passphrase is wrong.");
        }

        if (
            !SigningKeyCertificates.TryCheck(
                key!,
                certificates,
                trust,
                settings.UsageLogDirectory is null ? KeyUses.License | KeyUses.Lease : KeyUses.License | KeyUses.Lease | KeyUses.Log,
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

    private static WebApplication Build(ServiceSettings settings, X509Certificate2? tls, ServiceHostOptions host, Database database, WebHooks hooks)
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
        if (settings.Oidc is OidcSettings oidc)
        {
            AddOidc(builder, oidc, settings.Web, database, host.Time, hooks);
        }

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

        if (_settings.Web is not null)
        {
            _sessionTimer = Time.CreateTimer(_ => _sessionWork.TryStart(), null, SessionSweepInterval, SessionSweepInterval);
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

        if (_usage is not null)
        {
            _usageTimer = Time.CreateTimer(_ => _usageWork.TryStart(), null, UsageSealInterval, UsageSealInterval);
            string files = Path.Combine(_usage.Directory, _usage.BaseName + "-*.jsonl");
            ServiceLog.UsageLogAt(_log, files);
        }
        else
        {
            ServiceLog.UsageLogAt(_log, "not written (usageLog.directory is not set)");
        }

        ServiceLog.Started(_log, Url, _settings.Database, Signing.KeyId, Signing.ExpiresAt, (long)startup.TotalMilliseconds);
    }

    // design §15.3: before the service listens, the Data Protection key is made when none is in force, under the database lock;
    // rows that all fail to decrypt mean another key encryption key, and the service does not start.
    private async Task EnsureDataProtectionKeyAsync(CancellationToken cancellationToken)
    {
        if (_keyStore is null)
        {
            return;
        }

        _keyStore.Log = _log;
        try
        {
            int made = await _keyStore
                .EnsureKeyAsync(_app.Services.GetRequiredService<IKeyManager>(), atStart: true, cancellationToken)
                .ConfigureAwait(false);
            if (made > 0)
            {
                ServiceLog.DataProtectionKeyMade(_log, made);
            }
        }
        catch (System.Data.Common.DbException e)
        {
            throw new ServiceStartException("The Data Protection keys cannot be read or written: " + e.Message);
        }
    }

    private void Purge()
    {
        _perIp.Purge();
        _perKey.Purge();
        _clientPerIp.Purge();
        _clientPerKey.Purge();
    }

    // design §14.4, §12.8.4, §12.7.6: once a minute, the jti records whose nonce has expired, the answers of expired offline
    // requests, the expired leases, and the early returns past the 30-day window.
    private async Task RemoveOldProofsAsync()
    {
        try
        {
            long now = Time.GetUtcNow().ToUnixTimeSeconds();
            await Database.RemoveProofsBeforeAsync(now, CancellationToken.None).ConfigureAwait(false);
            await Database.RemoveOfflineRequestsBeforeAsync(now, CancellationToken.None).ConfigureAwait(false);
            IReadOnlyList<LeaseRow> expired = await Database
                .RemoveOldLeasesAsync(now, now - (long)ElasticLM.Leasing.LeaseRules.ReturnWindow.TotalSeconds, CancellationToken.None)
                .ConfigureAwait(false);
            await WriteExpiredAsync(expired).ConfigureAwait(false);
        }
        catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException)
        {
            ServiceLog.PeriodicFailed(_log, e);
        }
    }

    // design §18.1, 0.95: one line for each lease the sweep removed; only the node that removed a row sees it, so it is
    // written once.
    private async Task WriteExpiredAsync(IReadOnlyList<LeaseRow> expired)
    {
        if (_usage is null || expired.Count == 0)
        {
            return;
        }

        var products = new Dictionary<Guid, string>();
        foreach (LeaseRow lease in expired)
        {
            if (!products.TryGetValue(lease.LicenseId, out string? code))
            {
                (_, ProductRow product) = await PolicyAndProductAsync(lease.LicenseId, CancellationToken.None).ConfigureAwait(false);
                code = product.Code;
                products[lease.LicenseId] = code;
            }

            _usage.Lease(lease.Borrow ? "borrow.expired" : "lease.expired", lease, code);
        }
    }

    // The usage log's minute (design §18.1): the counted refusals, then a signature over the pending lines.
    private Task SealUsageAsync()
    {
        _usage?.WriteDenials();
        _usage?.Seal();
        return Task.CompletedTask;
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
