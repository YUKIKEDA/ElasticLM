using System.Text.Json;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The settings of <c>elm-service</c> (design §14.8, decision 0.74): a JSON file, then <c>ELM_*</c> variables, which win.
/// </summary>
public sealed class ServiceSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "elm-service-settings-" + Guid.CreateVersion7().ToString("N"));

    public ServiceSettingsTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(ConnectionFile, "Host=db;Database=elm\n");
        File.WriteAllText(NonceFile, Convert.ToBase64String(new byte[32]) + "\n");
    }

    private string ConnectionFile => Path.Combine(_folder, "connection.txt");

    private string NonceFile => Path.Combine(_folder, "nonce.txt");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>
    /// The file's values are read, and each <c>ELM_*</c> variable replaces its key; secrets come from a variable or a file.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A complete settings file.
    ///
    /// Steps:
    /// - Load it with no variables; load it with every variable set to another value.
    ///
    /// Expected:
    /// - The file's values, the connection read from its file without its line break; then each variable's value, lists split
    ///   at ';'.
    /// </remarks>
    [Fact]
    public void Load_FileAndEnvironment_EnvironmentWins()
    {
        string path = Write(
            Base(
                ",\"listen\":\"http://0.0.0.0:9000\",\"trust\":{\"rootKeys\":[\"r.pem\"],\"revokedKeyIds\":[\"k1\"]},"
                    + "\"bootstrapOwner\":{\"iss\":\"i\",\"sub\":\"s\"},\"rateLimit\":{\"perIp\":7,\"perKey\":3,\"clientPerIp\":9,\"clientPerKey\":5},"
                    + "\"audit\":{\"retentionDays\":30},\"publicUrls\":[\"https://elm.example/\"]"
            )
        );
        ServiceSettings file = ServiceSettings.Load(path, _ => null);
        Assert.Equal(
            "http://0.0.0.0:9000/ | Postgres | Host=db;Database=elm | k.pem | c | r.pem | k1 | AdminIdentity { Issuer = i, Subject = s } | 7 | 3 | 30 | "
                + "https://elm.example/ | 9 | 5 | 32",
            Shape(file)
        );

        var environment = new Dictionary<string, string>
        {
            ["ELM_LISTEN"] = "http://127.0.0.1:9100",
            ["ELM_DATABASE_PROVIDER"] = "sqlite",
            [ServiceSettings.ConnectionVariable] = "Data Source=elm.db",
            ["ELM_SIGNING_KEY"] = "k2.pem",
            ["ELM_SIGNING_CERTIFICATES"] = "c2;c3",
            ["ELM_TRUST_ROOTKEYS"] = "r2.pem",
            ["ELM_TRUST_REVOKEDKEYIDS"] = "k2;k3",
            ["ELM_BOOTSTRAPOWNER_ISS"] = "i2",
            ["ELM_BOOTSTRAPOWNER_SUB"] = "s2",
            ["ELM_RATELIMIT_PERIP"] = "8",
            ["ELM_RATELIMIT_PERKEY"] = "4",
            ["ELM_AUDIT_RETENTIONDAYS"] = "31",
            ["ELM_PUBLICURLS"] = "https://a.example/;https://b.example/elm",
            ["ELM_RATELIMIT_CLIENTPERIP"] = "11",
            ["ELM_RATELIMIT_CLIENTPERKEY"] = "6",
            [ServiceSettings.NonceSecretVariable] = Convert.ToBase64String(new byte[48]),
        };
        ServiceSettings env = ServiceSettings.Load(path, name => environment.GetValueOrDefault(name));
        Assert.Equal(
            "http://127.0.0.1:9100/ | Sqlite | Data Source=elm.db | k2.pem | c2;c3 | r2.pem | k2;k3 | AdminIdentity { Issuer = i2, Subject = s2 } | 8 | 4 | 31 | "
                + "https://a.example/;https://b.example/elm | 11 | 6 | 48",
            Shape(env)
        );

        ServiceSettings defaults = ServiceSettings.Load(Write(Base()), _ => null);
        Assert.Equal(
            (20, 10, (TimeSpan?)null, 20, 2, 0),
            (
                defaults.RateLimitPerIp,
                defaults.RateLimitPerKey,
                defaults.AuditRetention,
                defaults.RateLimitClientPerIp,
                defaults.RateLimitClientPerKey,
                defaults.PublicUrls.Count
            )
        );
    }

    /// <summary>
    /// Each limit is accepted exactly at its bound: the rates 1 and 10000, retention 36600 days, an owner name of 255
    /// characters, a settings file of 64 KiB, a connection file of 16 KiB, and a nonce secret of 32 bytes; setup does not
    /// need the signing settings or the nonce secret.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Settings files written in the test.
    ///
    /// Steps:
    /// - Load each bound; load a file without signing for setup.
    ///
    /// Expected:
    /// - Each loads with its value; the setup settings have no signing key.
    /// </remarks>
    [Fact]
    public void Load_AtEachLimit_Accepts()
    {
        foreach (int rate in new[] { 1, 10_000 })
        {
            ServiceSettings settings = ServiceSettings.Load(
                Write(Base($",\"rateLimit\":{{\"perIp\":{rate},\"perKey\":{rate},\"clientPerIp\":{rate},\"clientPerKey\":{rate}}}")),
                _ => null
            );
            Assert.Equal(
                (rate, rate, rate, rate),
                (settings.RateLimitPerIp, settings.RateLimitPerKey, settings.RateLimitClientPerIp, settings.RateLimitClientPerKey)
            );
        }

        Assert.Equal(32, ServiceSettings.Load(Write(Base()), _ => null).NonceSecret.Length);

        Assert.Equal(TimeSpan.FromDays(36_600), ServiceSettings.Load(Write(Base(",\"audit\":{\"retentionDays\":36600}")), _ => null).AuditRetention);
        string longest = new('i', 255);
        Assert.Equal(
            new AdminIdentity(longest, longest),
            ServiceSettings.Load(Write(Base($",\"bootstrapOwner\":{{\"iss\":\"{longest}\",\"sub\":\"{longest}\"}}")), _ => null).BootstrapOwner
        );

        string full = Base();
        full += new string(' ', (64 * 1024) - full.Length);
        Assert.Equal("Host=db;Database=elm", ServiceSettings.Load(Write(full), _ => null).ConnectionString);

        File.WriteAllText(ConnectionFile, new string('x', 16 * 1024));
        Assert.Equal(16 * 1024, ServiceSettings.Load(Write(Base()), _ => null).ConnectionString.Length);

        ServiceSettings setup = ServiceSettings.Load(
            Write("{\"database\":{\"connectionFile\":" + JsonSerializer.Serialize(ConnectionFile) + "}}"),
            _ => null,
            requireSigning: false
        );
        Assert.Equal((null, 0, 0), (setup.SigningKeyFile, setup.SigningCertificateFiles.Count, setup.NonceSecret.Length));
    }

    /// <summary>
    /// Every value that is not valid stops the start with a message that names its key, one past each limit included, and
    /// an empty value or list entry is not taken as "the default".
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Settings files written in the test.
    ///
    /// Steps:
    /// - Load files with one fault each: an unknown key; provider mysql; https without tls; no connection; an empty
    ///   connection file; one of 16 KiB + 1; no signing key; no certificates; bootstrapOwner without sub; an owner of 256
    ///   characters; perIp, perKey, clientPerIp, clientPerKey 0 and 10001; retentionDays 0 and 36601; a listen with a path;
    ///   a listen host name; listen ""; signing.key ""; a certificate entry ""; a null certificate entry; tls.certificate "";
    ///   a settings file of 64 KiB + 1; a perKey variable that is not a number; no nonce secret; one of 31 bytes; one that
    ///   is not base64; nonce.secretFile ""; a public URL over http, with a query, and ""; listen on 0.0.0.0 without
    ///   publicUrls.
    ///
    /// Expected:
    /// - A ServiceStartException with the case's own message each time.
    /// </remarks>
    [Fact]
    public void Load_InvalidValue_ThrowsNamingTheKey()
    {
        void Refused(string json, string message, Func<string, string?>? environment = null)
        {
            var e = Assert.Throws<ServiceStartException>(() => ServiceSettings.Load(Write(json), environment ?? (_ => null)));
            Assert.Equal(message, e.Message);
        }

        string Connection(string path) => "\"database\":{\"connectionFile\":" + JsonSerializer.Serialize(path) + "}";
        string WithSigning(string more) => "{\"signing\":{\"key\":\"k\",\"certificates\":[\"c\"]}," + more + "}";

        Assert.StartsWith(
            "The settings file '",
            Assert.Throws<ServiceStartException>(() => ServiceSettings.Load(Write(Base(",\"other\":1")), _ => null)).Message,
            StringComparison.Ordinal
        );
        Refused(
            WithSigning("\"database\":{\"provider\":\"mysql\",\"connectionFile\":\"c\"}"),
            "database.provider must be sqlite or postgres: 'mysql'."
        );
        Refused(Base(",\"listen\":\"https://0.0.0.0:9000\""), "An https listen needs tls.certificate and tls.key (PEM files).");
        Refused(
            "{\"signing\":{\"key\":\"k\",\"certificates\":[\"c\"]}}",
            $"Set {ServiceSettings.ConnectionVariable} or database.connectionFile to the database connection string."
        );

        string empty = Path.Combine(_folder, "empty.txt");
        File.WriteAllText(empty, "\n");
        Refused(WithSigning(Connection(empty)), $"database.connectionFile '{empty}' is empty.");
        string large = Path.Combine(_folder, "large.txt");
        File.WriteAllText(large, new string('x', (16 * 1024) + 1));
        Refused(WithSigning(Connection(large)), $"database.connectionFile '{large}' does not exist or is larger than 16 KiB.");

        Func<string, string?> withConnection = name => name == ServiceSettings.ConnectionVariable ? "Data Source=x" : null;
        Refused("{\"signing\":{\"certificates\":[\"c\"]}}", "Set signing.key to the signing key file (PKCS#8 PEM).", withConnection);
        Refused("{\"signing\":{\"key\":\"k\"}}", "Set signing.certificates to the signing key's certificate files.", withConnection);

        const string Owner = "bootstrapOwner needs both iss and sub, each 1 to 255 characters.";
        Refused(Base(",\"bootstrapOwner\":{\"iss\":\"i\"}"), Owner);
        Refused(Base($",\"bootstrapOwner\":{{\"iss\":\"{new string('i', 256)}\",\"sub\":\"s\"}}"), Owner);
        foreach (string rate in new[] { "perIp", "perKey", "clientPerIp", "clientPerKey" })
        {
            Refused(Base($",\"rateLimit\":{{\"{rate}\":0}}"), $"rateLimit.{rate} must be between 1 and 10000.");
            Refused(Base($",\"rateLimit\":{{\"{rate}\":10001}}"), $"rateLimit.{rate} must be between 1 and 10000.");
        }

        const string Retention = "audit.retentionDays must be between 1 and 36600, or left out to keep every row.";
        Refused(Base(",\"audit\":{\"retentionDays\":0}"), Retention);
        Refused(Base(",\"audit\":{\"retentionDays\":36601}"), Retention);
        Refused(
            Base(",\"listen\":\"http://0.0.0.0:9000/x\""),
            "listen must be an http or https URL without path, query, or fragment: 'http://0.0.0.0:9000/x'."
        );
        Refused(
            Base(",\"listen\":\"http://service.example:9000\""),
            "listen must name an IP address, localhost, 0.0.0.0, or [::]: 'http://service.example:9000/'."
        );

        Refused(Base(",\"listen\":\"\""), "listen must not be empty; leave it out instead.");
        Refused(
            "{\"signing\":{\"key\":\"\",\"certificates\":[\"c\"]}," + Connection(ConnectionFile) + "}",
            "signing.key must not be empty; leave it out instead."
        );
        Refused(
            "{\"signing\":{\"key\":\"k\",\"certificates\":[\"\"]}," + Connection(ConnectionFile) + "}",
            "signing.certificates must not hold an empty or null entry."
        );
        Refused(
            "{\"signing\":{\"key\":\"k\",\"certificates\":[null]}," + Connection(ConnectionFile) + "}",
            "signing.certificates must not hold an empty or null entry."
        );
        Refused(Base(",\"tls\":{\"certificate\":\"\",\"key\":\"k\"}"), "tls.certificate must not be empty; leave it out instead.");

        string tooLarge = Base();
        tooLarge += new string(' ', (64 * 1024) + 1 - tooLarge.Length);
        string tooLargePath = Write(tooLarge);
        Assert.Equal(
            $"The settings file '{tooLargePath}' is larger than 64 KiB.",
            Assert.Throws<ServiceStartException>(() => ServiceSettings.Load(tooLargePath, _ => null)).Message
        );
        Refused(Base(), "rateLimit.perKey must be a whole number: '1.5'.", name => name == "ELM_RATELIMIT_PERKEY" ? "1.5" : null);

        string nonce = $"Set {ServiceSettings.NonceSecretVariable} or nonce.secretFile to the nonces' secret: at least 32 random bytes in base64.";
        string withoutNonce = "{\"signing\":{\"key\":\"k\",\"certificates\":[\"c\"]}," + Connection(ConnectionFile) + "}";
        Refused(withoutNonce, nonce);
        Refused(withoutNonce, nonce, name => name == ServiceSettings.NonceSecretVariable ? Convert.ToBase64String(new byte[31]) : null);
        Refused(withoutNonce, nonce, name => name == ServiceSettings.NonceSecretVariable ? "not base64!" : null);
        Refused(withoutNonce[..^1] + ",\"nonce\":{\"secretFile\":\"\"}}", "nonce.secretFile must not be empty; leave it out instead.");
        Refused(Base(",\"publicUrls\":[\"http://elm.example/\"]"), "publicUrls must be https URLs without query or fragment: 'http://elm.example/'.");
        Refused(
            Base(",\"publicUrls\":[\"https://elm.example/?a=1\"]"),
            "publicUrls must be https URLs without query or fragment: 'https://elm.example/?a=1'."
        );
        Refused(Base(",\"publicUrls\":[\"\"]"), "publicUrls must not hold an empty or null entry.");
        Refused(
            Base(",\"listen\":\"http://0.0.0.0:9000\""),
            "Set publicUrls to the https URLs devices reach the service under; listen is not a loopback address."
        );
    }

    // The values a test compares, as one line.
    private static string Shape(ServiceSettings settings) =>
        string.Join(
            " | ",
            settings.Listen,
            settings.Database,
            settings.ConnectionString,
            settings.SigningKeyFile,
            string.Join(';', settings.SigningCertificateFiles),
            string.Join(';', settings.TrustRootKeyFiles),
            string.Join(';', settings.TrustRevokedKeyIds),
            settings.BootstrapOwner,
            settings.RateLimitPerIp,
            settings.RateLimitPerKey,
            settings.AuditRetention?.TotalDays,
            string.Join(';', settings.PublicUrls),
            settings.RateLimitClientPerIp,
            settings.RateLimitClientPerKey,
            settings.NonceSecret.Length
        );

    private string Base(string more = "") =>
        "{\"signing\":{\"key\":\"k.pem\",\"certificates\":[\"c\"]},\"database\":{\"provider\":\"postgres\",\"connectionFile\":"
        + JsonSerializer.Serialize(ConnectionFile)
        + "},\"nonce\":{\"secretFile\":"
        + JsonSerializer.Serialize(NonceFile)
        + "}"
        + more
        + "}";

    private string Write(string json)
    {
        string path = Path.Combine(_folder, Guid.CreateVersion7().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }
}
