using System.Data.Common;
using ElasticLM.Issuing;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The start of <c>elm-service</c>: schema migrations (design §14.2, decision 0.69), the signing key (decision 0.75), setup
/// (decisions 0.63 and 0.80), and the settings (decision 0.74).
/// </summary>
public sealed class StartTests
{
    /// <summary>
    /// The embedded migrations run from an empty database once; a second run applies nothing; two runs at once apply each
    /// version once.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty database on the provider.
    ///
    /// Steps:
    /// - Run the migrations twice in parallel; run them again; count the rows of schema_migrations.
    ///
    /// Expected:
    /// - One run applied version 1 and the other nothing; the third applied nothing; one row.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AdminApiTests.Providers), MemberType = typeof(AdminApiTests))]
    public async Task Migrations_RunOnce(string provider)
    {
        await using TestService test = AdminApiTests.Open(provider);
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        IReadOnlyList<Migration> migrations = Migrator.Embedded(test.Provider);

        IReadOnlyList<int>[] runs = await Task.WhenAll(
            Task.Run(() => Migrator.RunAsync(database, migrations, test.Time, cancel), cancel),
            Task.Run(() => Migrator.RunAsync(database, migrations, test.Time, cancel), cancel)
        );
        Assert.Equal([1], runs.SelectMany(r => r).Order());
        Assert.Empty(await Migrator.RunAsync(database, migrations, test.Time, cancel));
        await using DbConnection connection = await database.OpenAsync(cancel);
        await using DbCommand count = Database.Command(connection, null, "SELECT COUNT(*) FROM schema_migrations");
        Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync(cancel), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A recorded migration whose file changed, or one this build does not have, stops the start; file names and numbering
    /// are checked.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A database migrated with the embedded version 1.
    ///
    /// Steps:
    /// - Run with version 1's text changed by one space; run with only a version 2 file renamed as 1; insert a recorded
    ///   version 9 and run the embedded migrations; parse names 1_a.sql, 0001-a.sql, 0001_A.sql; order versions 1 and 3; 1
    ///   and 1.
    ///
    /// Expected:
    /// - ServiceStartException naming the changed file; the same; one naming version 9; an exception for each name and for
    ///   each order.
    /// </remarks>
    [Fact]
    public async Task Migrations_ChangedOrUnknownStopTheStart()
    {
        await using TestService test = AdminApiTests.Open("sqlite");
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        Migration original = Migrator.Embedded(test.Provider)[0];
        await Migrator.RunAsync(database, [original], test.Time, cancel);

        var changed = await Assert.ThrowsAsync<ServiceStartException>(() =>
            Migrator.RunAsync(database, [original with { Sql = original.Sql + " " }], test.Time, cancel)
        );
        Assert.Contains("0001_foundation.sql", changed.Message, StringComparison.Ordinal);
        Assert.Contains("changed", changed.Message, StringComparison.Ordinal);

        await test.ExecuteAsync("INSERT INTO schema_migrations (version, name, sha256, applied_at) VALUES (9, '0009_later.sql', x'00', 0)");
        var unknown = await Assert.ThrowsAsync<ServiceStartException>(() => Migrator.RunAsync(database, [original], test.Time, cancel));
        Assert.Contains("version 9", unknown.Message, StringComparison.Ordinal);

        foreach (string name in new[] { "1_a.sql", "0001-a.sql", "0001_A.sql" })
        {
            Assert.Throws<ServiceStartException>(() => Migrator.Parse(name, string.Empty));
        }

        Assert.Throws<ServiceStartException>(() =>
            Migrator.Order([new Migration(1, "0001_a.sql", string.Empty), new Migration(3, "0003_c.sql", string.Empty)])
        );
        Assert.Throws<ServiceStartException>(() =>
            Migrator.Order([new Migration(1, "0001_a.sql", string.Empty), new Migration(1, "0001_b.sql", string.Empty)])
        );
    }

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
    public async Task SigningKey_MustBeCertifiedNow()
    {
        await using TestService test = AdminApiTests.Open("sqlite");
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
        using (System.Security.Cryptography.ECDsa other = KeyFiles.CreateKey())
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

        Assert.Contains("passphrase", await Refused(test.FilePath("signing.cert"), "wrong passphrase for the key"), StringComparison.Ordinal);
        Assert.Contains(ServiceSettings.SigningPassphraseVariable, await Refused(test.FilePath("signing.cert"), null), StringComparison.Ordinal);

        ElmService service = await test.StartAsync();
        Assert.Equal((KeyFiles.KeyId(test.SigningKey), KeyUses.License | KeyUses.Lease), (service.Signing.KeyId, service.Signing.Uses));
    }

    /// <summary>
    /// setup writes only the key to standard output and its description to standard error, records itself in the audit log,
    /// may run again, and refuses without bootstrapOwner (exit 2) or with a disabled owner (exit 1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty SQLite database.
    ///
    /// Steps:
    /// - Run setup without bootstrapOwner; with it, twice; disable the owner in the database and run again; run with a
    ///   connection string that is not valid.
    ///
    /// Expected:
    /// - 2 with nothing on standard output; 0 twice, each output one elm_ key of 47 characters and a line on standard error
    ///   naming the owner, two different keys and two setup rows in the audit log; 1; 2.
    /// </remarks>
    [Fact]
    public async Task Setup_PrintsOnlyTheKey()
    {
        await using TestService test = AdminApiTests.Open("sqlite");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        async Task<(int Code, string Out, string Error)> Run(ServiceSettings settings)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            int code = await Setup.RunAsync(settings, test.Time, output, error, cancel);
            return (code, output.ToString(), error.ToString());
        }

        (int noOwner, string noOwnerOut, _) = await Run(test.Settings(s => s with { BootstrapOwner = null }));
        Assert.Equal((2, string.Empty), (noOwner, noOwnerOut));

        var keys = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            (int code, string output, string error) = await Run(test.Settings());
            Assert.Equal(0, code);
            string key = Assert.Single(output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            Assert.Equal((47, true), (key.Length, key.StartsWith("elm_", StringComparison.Ordinal)));
            Assert.Contains("https://idp.example owner-1", error, StringComparison.Ordinal);
            keys.Add(key);
        }

        Assert.NotEqual(keys[0], keys[1]);
        ElmService service = await test.StartAsync();
        Assert.Equal(2, (await AdminApiTests.AuditRowsAsync(test, service, keys[1])).Count(r => r.GetProperty("actorId").GetString() == "setup"));

        await test.ExecuteAsync("UPDATE admins SET enabled = 0");
        Assert.Equal(1, (await Run(test.Settings())).Code);
        Assert.Equal(2, (await Run(test.Settings(s => s with { ConnectionString = "Data Source=x;Unknown Key=1" }))).Code);
    }

    /// <summary>
    /// The settings come from the file and the environment, the environment winning; secrets come from variables or files;
    /// every invalid value stops the start with a message that names its key.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Settings files written in the test.
    ///
    /// Steps:
    /// - Load a complete file with ELM_RATELIMIT_PERKEY set; then files with one fault each: an unknown key, provider
    ///   mysql, https without tls, no connection, an empty connection file, no signing key, no certificates, bootstrapOwner
    ///   without sub, rateLimit.perIp 0 and 10001, audit.retentionDays 0, a listen with a path, a listen host name.
    ///
    /// Expected:
    /// - The file's values, perKey from the variable, the connection read from its file without its line break; then a
    ///   ServiceStartException naming the key each time.
    /// </remarks>
    [Fact]
    public void Settings_LoadAndRefuse()
    {
        string folder = Path.Combine(Path.GetTempPath(), "elm-service-settings-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string connectionFile = Path.Combine(folder, "connection.txt");
            File.WriteAllText(connectionFile, "Host=db;Database=elm\n");
            string Write(string json)
            {
                string path = Path.Combine(folder, Guid.CreateVersion7().ToString("N") + ".json");
                File.WriteAllText(path, json);
                return path;
            }

            string Base(string more = "") =>
                "{\"signing\":{\"key\":\"k.pem\",\"certificates\":[\"c\"]},\"database\":{\"provider\":\"postgres\",\"connectionFile\":"
                + System.Text.Json.JsonSerializer.Serialize(connectionFile)
                + "}"
                + more
                + "}";

            ServiceSettings settings = ServiceSettings.Load(
                Write(
                    Base(
                        ",\"listen\":\"http://0.0.0.0:9000\",\"bootstrapOwner\":{\"iss\":\"i\",\"sub\":\"s\"},\"rateLimit\":{\"perIp\":7},\"audit\":{\"retentionDays\":30}"
                    )
                ),
                name => name == "ELM_RATELIMIT_PERKEY" ? "9" : null
            );
            Assert.Equal(
                (
                    new Uri("http://0.0.0.0:9000"),
                    DatabaseProvider.Postgres,
                    "Host=db;Database=elm",
                    7,
                    9,
                    TimeSpan.FromDays(30),
                    new AdminIdentity("i", "s")
                ),
                (
                    settings.Listen,
                    settings.Database,
                    settings.ConnectionString,
                    settings.RateLimitPerIp,
                    settings.RateLimitPerKey,
                    settings.AuditRetention,
                    settings.BootstrapOwner
                )
            );

            void Refused(string json, string key, Func<string, string?>? environment = null)
            {
                var e = Assert.Throws<ServiceStartException>(() => ServiceSettings.Load(Write(json), environment ?? (_ => null)));
                Assert.Contains(key, e.Message, StringComparison.Ordinal);
            }

            File.WriteAllText(Path.Combine(folder, "empty.txt"), "\n");
            Refused(Base(",\"other\":1"), "other");
            Refused(
                "{\"signing\":{\"key\":\"k\",\"certificates\":[\"c\"]},\"database\":{\"provider\":\"mysql\",\"connectionFile\":\"c\"}}",
                "database.provider"
            );
            Refused(Base(",\"listen\":\"https://0.0.0.0:9000\""), "tls.certificate");
            Refused("{\"signing\":{\"key\":\"k\",\"certificates\":[\"c\"]}}", ServiceSettings.ConnectionVariable);
            Refused(
                "{\"signing\":{\"key\":\"k\",\"certificates\":[\"c\"]},\"database\":{\"connectionFile\":"
                    + System.Text.Json.JsonSerializer.Serialize(Path.Combine(folder, "empty.txt"))
                    + "}}",
                "database.connectionFile"
            );
            Refused(
                "{\"signing\":{\"certificates\":[\"c\"]}}",
                "signing.key",
                name => name == ServiceSettings.ConnectionVariable ? "Data Source=x" : null
            );
            Refused(
                "{\"signing\":{\"key\":\"k\"}}",
                "signing.certificates",
                name => name == ServiceSettings.ConnectionVariable ? "Data Source=x" : null
            );
            Refused(Base(",\"bootstrapOwner\":{\"iss\":\"i\"}"), "bootstrapOwner");
            Refused(Base(",\"rateLimit\":{\"perIp\":0}"), "rateLimit.perIp");
            Refused(Base(",\"rateLimit\":{\"perIp\":10001}"), "rateLimit.perIp");
            Refused(Base(",\"audit\":{\"retentionDays\":0}"), "audit.retentionDays");
            Refused(Base(",\"listen\":\"http://0.0.0.0:9000/x\""), "listen");
            Refused(Base(",\"listen\":\"http://service.example:9000\""), "listen");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
