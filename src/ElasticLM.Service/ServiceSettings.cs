using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ElasticLM.Service;

/// <summary>
/// A settings or start error: the service does not start, and the message says why.
/// </summary>
internal sealed class ServiceStartException(string message) : Exception(message);

/// <summary>
/// The database engine (design §14.2).
/// </summary>
internal enum DatabaseProvider
{
    /// <summary>SQLite, linked into the executable. One node only (design §14.3).</summary>
    Sqlite,

    /// <summary>PostgreSQL 14 to 18 (decision 0.62).</summary>
    Postgres,
}

/// <summary>
/// The issuer an administrator signs in with and their subject there (design §14.6 <c>bootstrapOwner</c>).
/// </summary>
/// <param name="Issuer">The OIDC issuer (<c>iss</c>).</param>
/// <param name="Subject">The subject (<c>sub</c>).</param>
internal sealed record AdminIdentity(string Issuer, string Subject);

/// <summary>
/// The service's settings (design §14.8, decision 0.74): a JSON file, then environment variables named <c>ELM_</c> + the key
/// path in upper case with <c>.</c> as <c>_</c>, which win. Secrets (the database connection, the signing key's passphrase)
/// come from environment variables or files, never from the settings file itself.
/// </summary>
internal sealed record ServiceSettings
{
    /// <summary>The variable that holds the database connection string.</summary>
    public const string ConnectionVariable = "ELM_DATABASE_CONNECTION";

    /// <summary>The variable that holds the signing key's passphrase.</summary>
    public const string SigningPassphraseVariable = "ELM_SIGNING_PASSPHRASE";

    /// <summary>The variable that holds the vendor's root keys as PEM text, instead of <c>trust.rootKeys</c> files.</summary>
    public const string TrustRootKeysVariable = "ELM_TRUST_ROOT_KEYS";

    private const int MaxSettingsFileLength = 64 * 1024;
    private const int MaxSecretFileLength = 16 * 1024;
    private const int MaxIdentityLength = 255;

    /// <summary>Gets where the service listens: http, or https with <see cref="TlsCertificate"/> and <see cref="TlsKey"/>.</summary>
    public Uri Listen { get; init; } = new("http://127.0.0.1:8080");

    public string? TlsCertificate { get; init; }

    public string? TlsKey { get; init; }

    public DatabaseProvider Database { get; init; } = DatabaseProvider.Sqlite;

    public required string ConnectionString { get; init; }

    public required string SigningKeyFile { get; init; }

    public required IReadOnlyList<string> SigningCertificateFiles { get; init; }

    public IReadOnlyList<string> TrustRootKeyFiles { get; init; } = [];

    public IReadOnlyList<string> TrustRevokedKeyIds { get; init; } = [];

    /// <summary>Gets the first owner (design §14.6), or <see langword="null"/> when it is not set.</summary>
    public AdminIdentity? BootstrapOwner { get; init; }

    /// <summary>Gets the requests per second from one source before authentication (design §14.7).</summary>
    public int RateLimitPerIp { get; init; } = 20;

    /// <summary>Gets the requests per second of one API key (design §14.7).</summary>
    public int RateLimitPerKey { get; init; } = 50;

    /// <summary>Gets how long audit rows are kept, or <see langword="null"/> to keep them (design §18.2, the default).</summary>
    public TimeSpan? AuditRetention { get; init; }

    /// <summary>
    /// Reads the settings file (if any) and the environment.
    /// </summary>
    /// <param name="configPath">The settings file, or <see langword="null"/>.</param>
    /// <param name="environment">Reads an environment variable.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="ServiceStartException">A file, key, or value is not valid, or a required one is missing.</exception>
    public static ServiceSettings Load(string? configPath, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        SettingsFile file = configPath is null ? new SettingsFile() : ReadFile(configPath);
        string? Env(string name) => environment(name) is { Length: > 0 } value ? value : null;
        IReadOnlyList<string>? EnvList(string name) =>
            Env(name) is string value ? value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;

        Uri listen = ParseListen(Env("ELM_LISTEN") ?? file.Listen ?? "http://127.0.0.1:8080");
        string? tlsCertificate = Env("ELM_TLS_CERTIFICATE") ?? file.Tls?.Certificate;
        string? tlsKey = Env("ELM_TLS_KEY") ?? file.Tls?.Key;
        if (listen.Scheme == Uri.UriSchemeHttps && (tlsCertificate is null || tlsKey is null))
        {
            throw new ServiceStartException("An https listen needs tls.certificate and tls.key (PEM files).");
        }

        DatabaseProvider provider = (Env("ELM_DATABASE_PROVIDER") ?? file.Database?.Provider ?? "sqlite") switch
        {
            "sqlite" => DatabaseProvider.Sqlite,
            "postgres" => DatabaseProvider.Postgres,
            string other => throw new ServiceStartException($"database.provider must be sqlite or postgres: '{other}'."),
        };
        string connection =
            Env(ConnectionVariable)
            ?? (file.Database?.ConnectionFile is string connectionFile ? ReadSecret(connectionFile, "database.connectionFile") : null)
            ?? throw new ServiceStartException($"Set {ConnectionVariable} or database.connectionFile to the database connection string.");

        string signingKey =
            Env("ELM_SIGNING_KEY") ?? file.Signing?.Key ?? throw new ServiceStartException("Set signing.key to the signing key file (PKCS#8 PEM).");
        IReadOnlyList<string> certificates = EnvList("ELM_SIGNING_CERTIFICATES") ?? file.Signing?.Certificates ?? [];
        if (certificates.Count == 0)
        {
            throw new ServiceStartException("Set signing.certificates to the signing key's certificate files.");
        }

        AdminIdentity? owner = null;
        string? ownerIssuer = Env("ELM_BOOTSTRAPOWNER_ISS") ?? file.BootstrapOwner?.Iss;
        string? ownerSubject = Env("ELM_BOOTSTRAPOWNER_SUB") ?? file.BootstrapOwner?.Sub;
        if (ownerIssuer is not null || ownerSubject is not null)
        {
            if (ownerIssuer is not { Length: > 0 and <= MaxIdentityLength } || ownerSubject is not { Length: > 0 and <= MaxIdentityLength })
            {
                throw new ServiceStartException("bootstrapOwner needs both iss and sub, each 1 to 255 characters.");
            }

            owner = new AdminIdentity(ownerIssuer, ownerSubject);
        }

        int perIp = Env("ELM_RATELIMIT_PERIP") is string perIpText ? ParseInt(perIpText, "rateLimit.perIp") : file.RateLimit?.PerIp ?? 20;
        int perKey = Env("ELM_RATELIMIT_PERKEY") is string perKeyText ? ParseInt(perKeyText, "rateLimit.perKey") : file.RateLimit?.PerKey ?? 50;
        if (perIp is < 1 or > 10_000 || perKey is < 1 or > 10_000)
        {
            throw new ServiceStartException("rateLimit.perIp and rateLimit.perKey must be between 1 and 10000.");
        }

        int? retentionDays = Env("ELM_AUDIT_RETENTIONDAYS") is string daysText
            ? ParseInt(daysText, "audit.retentionDays")
            : file.Audit?.RetentionDays;
        if (retentionDays is < 1 or > 36_600)
        {
            throw new ServiceStartException("audit.retentionDays must be between 1 and 36600, or left out to keep every row.");
        }

        return new ServiceSettings
        {
            Listen = listen,
            TlsCertificate = tlsCertificate,
            TlsKey = tlsKey,
            Database = provider,
            ConnectionString = connection,
            SigningKeyFile = signingKey,
            SigningCertificateFiles = certificates,
            TrustRootKeyFiles = EnvList("ELM_TRUST_ROOTKEYS") ?? file.Trust?.RootKeys ?? [],
            TrustRevokedKeyIds = EnvList("ELM_TRUST_REVOKEDKEYIDS") ?? file.Trust?.RevokedKeyIds ?? [],
            BootstrapOwner = owner,
            RateLimitPerIp = perIp,
            RateLimitPerKey = perKey,
            AuditRetention = retentionDays is int days ? TimeSpan.FromDays(days) : null,
        };
    }

    /// <summary>
    /// Returns the address to listen on: the host must be an IP address, <c>localhost</c>, <c>0.0.0.0</c>, or <c>[::]</c>.
    /// </summary>
    /// <exception cref="ServiceStartException">The host is a name other than <c>localhost</c>.</exception>
    public static IPAddress ListenAddress(Uri listen)
    {
        ArgumentNullException.ThrowIfNull(listen);
        string host = listen.Host.Trim('[', ']');
        return host == "localhost" ? IPAddress.Loopback
            : IPAddress.TryParse(host, out IPAddress? parsed) ? parsed
            : throw new ServiceStartException($"listen must name an IP address, localhost, 0.0.0.0, or [::]: '{listen}'.");
    }

    private static Uri ParseListen(string text)
    {
        if (
            !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.AbsolutePath != "/"
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
        )
        {
            throw new ServiceStartException($"listen must be an http or https URL without path, query, or fragment: '{text}'.");
        }

        _ = ListenAddress(uri);
        return uri;
    }

    private static SettingsFile ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new ServiceStartException($"The settings file '{path}' does not exist.");
            }

            if (info.Length > MaxSettingsFileLength)
            {
                throw new ServiceStartException($"The settings file '{path}' is larger than 64 KiB.");
            }

            return JsonSerializer.Deserialize(File.ReadAllBytes(path), ServiceJsonContext.Default.SettingsFile)
                ?? throw new ServiceStartException($"The settings file '{path}' is empty.");
        }
        catch (JsonException e)
        {
            throw new ServiceStartException($"The settings file '{path}' is not valid: {e.Message}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ServiceStartException($"The settings file '{path}' cannot be read: {e.Message}");
        }
    }

    // A secret in a file: its text without the line break an editor leaves at the end.
    private static string ReadSecret(string path, string key)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxSecretFileLength)
            {
                throw new ServiceStartException($"{key} '{path}' does not exist or is larger than 16 KiB.");
            }

            string text = File.ReadAllText(path).TrimEnd('\r', '\n');
            return text.Length > 0 ? text : throw new ServiceStartException($"{key} '{path}' is empty.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ServiceStartException($"{key} '{path}' cannot be read: {e.Message}");
        }
    }

    private static int ParseInt(string text, string key) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new ServiceStartException($"{key} must be a whole number: '{text}'.");
}
