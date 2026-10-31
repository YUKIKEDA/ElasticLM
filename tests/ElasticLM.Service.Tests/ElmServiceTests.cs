using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ElasticLM.Issuing;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The start and stop of <c>elm-service</c> (design §14.8): the trust store, the signing key (decision 0.75), listening, and
/// the timers' work.
/// </summary>
public sealed class ElmServiceTests
{
    /// <summary>
    /// The service starts only with a signing key whose certificates the trust store vouches for, for licenses and leases, at
    /// the start time; and with the right passphrase.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A vendor, a signing key, and certificates written in the test.
    ///
    /// Steps:
    /// - Start with: a certificate for licenses only; one that ended yesterday; one that starts tomorrow; one for another key;
    ///   the right certificate and a wrong passphrase; no passphrase; the right certificate.
    ///
    /// Expected:
    /// - ServiceStartException naming Lease; naming License, Lease; the same; the same; one about the key or passphrase; one
    ///   naming the variable; a running service whose signing status names the key and both uses.
    /// </remarks>
    [Fact]
    public async Task Start_SigningKeyNotCertifiedNow_Throws()
    {
        await using TestService test = TestService.Open("sqlite");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        async Task<string> Refused(string certificate, string? passphrase = TestService.Passphrase)
        {
            ServiceSettings settings = test.Settings(s => s with { SigningCertificateFiles = [certificate] });
            return (
                await Assert.ThrowsAsync<ServiceStartException>(() => ElmService.StartAsync(settings, test.HostOptions(passphrase), cancel))
            ).Message;
        }

        Assert.Contains(
            "Lease",
            await Refused(test.WriteCertificate("license-only", KeyUses.License, TestService.Start.AddDays(-1), TimeSpan.FromDays(30))),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "License, Lease",
            await Refused(test.WriteCertificate("ended", KeyUses.License | KeyUses.Lease, TestService.Start.AddDays(-31), TimeSpan.FromDays(30))),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "License, Lease",
            await Refused(test.WriteCertificate("later", KeyUses.License | KeyUses.Lease, TestService.Start.AddDays(1), TimeSpan.FromDays(30))),
            StringComparison.Ordinal
        );
        using (ECDsa other = KeyFiles.CreateKey())
        {
            string otherCertificate = test.WriteCertificate(
                "other",
                KeyUses.License | KeyUses.Lease,
                TestService.Start.AddDays(-1),
                TimeSpan.FromDays(30),
                other
            );
            Assert.Contains("License, Lease", await Refused(otherCertificate), StringComparison.Ordinal);
        }

        Assert.Equal(
            "The signing key cannot be read: it is not an encrypted PKCS#8 P-256 key, or the passphrase is wrong.",
            await Refused(test.FilePath("signing.cert"), "wrong passphrase for the key")
        );
        Assert.Equal(
            $"Set {ServiceSettings.SigningPassphraseVariable} or signing.passphraseFile to the signing key's passphrase.",
            await Refused(test.FilePath("signing.cert"), null)
        );

        ElmService service = await test.StartAsync();
        Assert.Equal((KeyFiles.KeyId(test.SigningKey), KeyUses.License | KeyUses.Lease), (service.Signing.KeyId, service.Signing.Uses));
    }

    /// <summary>
    /// The signing key's passphrase comes from ELM_SIGNING_PASSPHRASE or, when it is not set, from signing.passphraseFile
    /// (design §14.8, F2), so that every secret can be given as a file.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A vendor, a signing key, and certificates written in the test; a file with the passphrase and a line break.
    ///
    /// Steps:
    /// - Start with the file and no variable; with the variable and a file of a wrong passphrase; with an empty file and no
    ///   variable; with a file that does not exist.
    ///
    /// Expected:
    /// - A running service; a running service (the variable wins); ServiceStartException saying the file is empty; one
    ///   saying it does not exist.
    /// </remarks>
    [Fact]
    public async Task Start_PassphraseFromTheFile_Starts()
    {
        await using TestService test = TestService.Open("sqlite");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string File(string name, string text)
        {
            string path = test.FilePath(name);
            System.IO.File.WriteAllText(path, text);
            return path;
        }

        string right = File("passphrase", TestService.Passphrase + "\n");
        string wrong = File("wrong-passphrase", "not the passphrase\n");
        string empty = File("empty-passphrase", "\n");
        string missing = test.FilePath("no-such-passphrase");

        await using (
            ElmService fromFile = await ElmService.StartAsync(
                test.Settings(s => s with { SigningPassphraseFile = right }),
                test.HostOptions(null),
                cancel
            )
        )
        {
            Assert.Equal(KeyFiles.KeyId(test.SigningKey), fromFile.Signing.KeyId);
        }

        await using (
            ElmService fromVariable = await ElmService.StartAsync(
                test.Settings(s => s with { SigningPassphraseFile = wrong }),
                test.HostOptions(TestService.Passphrase),
                cancel
            )
        )
        {
            Assert.Equal(KeyFiles.KeyId(test.SigningKey), fromVariable.Signing.KeyId);
        }

        Assert.Equal($"signing.passphraseFile '{empty}' is empty.", await RefusedWith(empty));
        Assert.Equal($"signing.passphraseFile '{missing}' does not exist or is larger than 16 KiB.", await RefusedWith(missing));

        async Task<string> RefusedWith(string passphraseFile)
        {
            ServiceSettings settings = test.Settings(s => s with { SigningPassphraseFile = passphraseFile });
            return (await Assert.ThrowsAsync<ServiceStartException>(() => ElmService.StartAsync(settings, test.HostOptions(null), cancel))).Message;
        }
    }

    /// <summary>
    /// The root keys come from trust.rootKeys files or, when none is set, from the PEM text of ELM_TRUST_ROOT_KEYS; with
    /// neither the service does not start.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A vendor and settings without trust.rootKeys.
    ///
    /// Steps:
    /// - Start without the variable; start with the root key's PEM in it.
    ///
    /// Expected:
    /// - ServiceStartException naming both; a running service.
    /// </remarks>
    [Fact]
    public async Task Start_RootKeysFromTheVariable_Starts()
    {
        await using TestService test = TestService.Open("sqlite");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        ServiceSettings settings = test.Settings(s => s with { TrustRootKeyFiles = [] });
        ServiceHostOptions Host(string? pem) =>
            new()
            {
                Time = test.Time,
                Environment = name =>
                    name == ServiceSettings.SigningPassphraseVariable ? TestService.Passphrase
                    : name == ServiceSettings.TrustRootKeysVariable ? pem
                    : null,
            };

        Assert.Equal(
            $"Set trust.rootKeys or {ServiceSettings.TrustRootKeysVariable} to the vendor's root public keys.",
            (await Assert.ThrowsAsync<ServiceStartException>(() => ElmService.StartAsync(settings, Host(null), cancel))).Message
        );
        await using ElmService service = await ElmService.StartAsync(settings, Host(File.ReadAllText(test.FilePath("root.pub.pem"))), cancel);
        Assert.Equal(KeyFiles.KeyId(test.SigningKey), service.Signing.KeyId);
    }

    /// <summary>
    /// With an https listen and PEM files, the service answers over TLS with that certificate.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A self-signed certificate for 127.0.0.1 and its key, as PEM files.
    ///
    /// Steps:
    /// - Start on https://127.0.0.1:0; GET /health trusting only that certificate.
    ///
    /// Expected:
    /// - 200, and the certificate the client saw is the file's.
    /// </remarks>
    [Fact]
    public async Task Start_HttpsListen_ServesWithTheCertificate()
    {
        await using TestService test = TestService.Open("sqlite");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=elm-service-test", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using X509Certificate2 certificate = request.CreateSelfSigned(TestService.Start.AddDays(-1), TestService.Start.AddDays(30));
        File.WriteAllText(test.FilePath("tls.crt"), certificate.ExportCertificatePem());
        File.WriteAllText(test.FilePath("tls.key"), key.ExportPkcs8PrivateKeyPem());

        ElmService service = await test.StartAsync(s =>
            s with
            {
                Listen = new Uri("https://127.0.0.1:0"),
                TlsCertificate = test.FilePath("tls.crt"),
                TlsKey = test.FilePath("tls.key"),
            }
        );
        string? seen = null;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
            {
                seen = presented?.Thumbprint;
                return presented?.Thumbprint == certificate.Thumbprint;
            },
        };
        using var http = new HttpClient(handler) { BaseAddress = service.Url };
        Assert.Equal(Uri.UriSchemeHttps, service.Url.Scheme);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("health", cancel)).StatusCode);
        Assert.Equal(certificate.Thumbprint, seen);
    }

    /// <summary>
    /// A port another process holds stops the start with a ServiceStartException (exit 1), not a crash.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A loopback port held by a listener of the test.
    ///
    /// Steps:
    /// - Start on that port.
    ///
    /// Expected:
    /// - ServiceStartException whose message begins with the listen URL.
    /// </remarks>
    [Fact]
    public async Task Start_PortInUse_Throws()
    {
        await using TestService test = TestService.Open("sqlite");
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        int port = ((IPEndPoint)holder.LocalEndpoint).Port;

        var e = await Assert.ThrowsAsync<ServiceStartException>(() =>
            ElmService.StartAsync(
                test.Settings(s => s with { Listen = new Uri($"http://127.0.0.1:{port}") }),
                test.HostOptions(),
                TestContext.Current.CancellationToken
            )
        );
        Assert.StartsWith($"elm-service cannot listen on http://127.0.0.1:{port}/: ", e.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The authentication failures counted since the last minute are written when the service stops (design §14.5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service after setup.
    ///
    /// Steps:
    /// - Call twice without a key, in less than a minute; stop the service; read the audit log from the database.
    ///
    /// Expected:
    /// - One auth.failed row with {"count":2}.
    /// </remarks>
    [Fact]
    public async Task Stop_CountedFailures_AreWritten()
    {
        await using TestService test = TestService.Open("sqlite");
        await test.SetupAsync();
        ElmService service = await test.StartAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        HttpClient anonymous = test.Http(service);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("v1/admin/audit", cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("v1/admin/audit", cancel)).StatusCode);

        await service.StopAsync();
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        AuditRow row = Assert.Single(await database.ListAuditAsync(null, false, 10, cancel), r => r.Entry.Action == AuditActions.AuthFailed);
        Assert.Equal("{\"count\":2}", row.Entry.Detail);
    }

    /// <summary>
    /// A minute's count that cannot be written is kept and written with the next row, so a database failure loses no
    /// failure from the log (design §14.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service after setup.
    ///
    /// Steps:
    /// - Call twice without a key; rename audit_log so the write fails; advance one minute and wait for the timer's work;
    ///   rename it back; read the rows; stop the service; read the rows.
    ///
    /// Expected:
    /// - No auth.failed row before the stop (the minute's write failed); then one with {"count":2}.
    /// </remarks>
    [Fact]
    public async Task DenialWrite_DatabaseFails_KeepsTheCount()
    {
        await using TestService test = TestService.Open("sqlite");
        await test.SetupAsync();
        ElmService service = await test.StartAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        HttpClient anonymous = test.Http(service);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("v1/admin/audit", cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("v1/admin/audit", cancel)).StatusCode);

        await test.ExecuteAsync("ALTER TABLE audit_log RENAME TO audit_log_hidden");
        test.Time.Advance(TimeSpan.FromMinutes(1));
        await service.WaitForTimersAsync();
        await test.ExecuteAsync("ALTER TABLE audit_log_hidden RENAME TO audit_log");
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        Assert.DoesNotContain(await database.ListAuditAsync(null, false, 10, cancel), r => r.Entry.Action == AuditActions.AuthFailed);

        await service.StopAsync();
        AuditRow row = Assert.Single(await database.ListAuditAsync(null, false, 10, cancel), r => r.Entry.Action == AuditActions.AuthFailed);
        Assert.Equal("{\"count\":2}", row.Entry.Detail);
    }

    /// <summary>
    /// With audit.retentionDays set, the hourly timer removes the rows older than the period and leaves the chain checkable
    /// (design §18.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with retention of 1 day, after setup (row 1 at the start), on each engine.
    ///
    /// Steps:
    /// - Advance 1 day and 1 hour; wait for the timer's work; check the chain.
    ///
    /// Expected:
    /// - No row left; the chain intact with 0 rows checked.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Retention_TimerFires_RemovesOldRows(string provider)
    {
        await using TestService test = TestService.Open(provider);
        await test.SetupAsync();
        await test.StartAsync(s => s with { AuditRetention = TimeSpan.FromDays(1) });
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        Assert.Single(await database.ListAuditAsync(null, false, 10, cancel));

        test.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromHours(1));
        await test.WaitWithClockAsync(async () => (await database.ListAuditAsync(null, false, 10, cancel)).Count == 0);
        Assert.Equal(new AuditCheck(0, null), await database.VerifyAuditAsync(cancel));
    }
}
