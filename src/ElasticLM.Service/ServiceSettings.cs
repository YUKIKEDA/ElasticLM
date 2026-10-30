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

    /// <summary>PostgreSQL 15 to 18 (decision 0.62).</summary>
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

    /// <summary>The variable that holds the nonces' HMAC secret in base64, instead of <c>nonce.secretFile</c>.</summary>
    public const string NonceSecretVariable = "ELM_NONCE_SECRET";

    private const int MaxSettingsFileLength = 64 * 1024;
    private const int MaxSecretFileLength = 16 * 1024;
    private const int MaxIdentityLength = 255;

    /// <summary>Gets where the service listens: http, or https with <see cref="TlsCertificate"/> and <see cref="TlsKey"/>.</summary>
    public Uri Listen { get; init; } = new("http://127.0.0.1:8080");

    public string? TlsCertificate { get; init; }

    public string? TlsKey { get; init; }

    public DatabaseProvider Database { get; init; } = DatabaseProvider.Sqlite;

    public required string ConnectionString { get; init; }

    /// <summary>Gets the signing key file, which the service needs and setup does not (<see langword="null"/> only for setup).</summary>
    public string? SigningKeyFile { get; init; }

    public IReadOnlyList<string> SigningCertificateFiles { get; init; } = [];

    public IReadOnlyList<string> TrustRootKeyFiles { get; init; } = [];

    public IReadOnlyList<string> TrustRevokedKeyIds { get; init; } = [];

    /// <summary>Gets the first owner (design §14.6), or <see langword="null"/> when it is not set.</summary>
    public AdminIdentity? BootstrapOwner { get; init; }

    /// <summary>Gets the requests per second from one source before authentication (design §14.7).</summary>
    public int RateLimitPerIp { get; init; } = 20;

    /// <summary>Gets the requests per second of one API key (design §14.7).</summary>
    public int RateLimitPerKey { get; init; } = 10;

    /// <summary>Gets how long audit rows are kept, or <see langword="null"/> to keep them (design §18.2, the default).</summary>
    public TimeSpan? AuditRetention { get; init; }

    /// <summary>
    /// Gets the URLs devices reach the service under (design §12.1), which a proof's <c>htu</c> is checked against; empty
    /// means the listen URL.
    /// </summary>
    public IReadOnlyList<Uri> PublicUrls { get; init; } = [];

    /// <summary>Gets the nonces' HMAC secret, at least 32 bytes (design §12.2, §14.8); empty only for setup.</summary>
    public byte[] NonceSecret { get; init; } = [];

    /// <summary>Gets the requests per second from one source to the device endpoints (design §14.7).</summary>
    public int RateLimitClientPerIp { get; init; } = 20;

    /// <summary>Gets the requests per second of one installation key (design §14.7).</summary>
    public int RateLimitClientPerKey { get; init; } = 2;

    /// <summary>Gets the folder of the usage log (design §18.1), or <see langword="null"/> to write none.</summary>
    public string? UsageLogDirectory { get; init; }

    /// <summary>
    /// Gets this node's name: 1 to 64 characters of <c>[A-Za-z0-9-]</c>, part of the usage log's file names, so that nodes
    /// sharing a folder keep separate chains (decision 0.95). By default the host name, its other characters made <c>-</c>.
    /// </summary>
    public string NodeName { get; init; } = DefaultNodeName();

    /// <summary>Gets the identity provider whose access tokens the administration API accepts, or <see langword="null"/> for API keys only (M5a).</summary>
    public OidcSettings? Oidc { get; init; }

    /// <summary>
    /// Gets the days without a login after which an administrator's API keys stop (decision 0.66, M5a); 0 turns the rule off.
    /// </summary>
    public int InactiveAdminDays { get; init; } = 90;

    /// <summary>Gets the admin UI's sign-in through the service, or <see langword="null"/> when it is off (design §15.3, M5b).</summary>
    public WebLoginSettings? Web { get; init; }

    /// <summary>
    /// Reads the settings file (if any) and the environment.
    /// </summary>
    /// <param name="configPath">The settings file, or <see langword="null"/>.</param>
    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="requireSigning">Whether the signing key, its certificates, and the nonce secret are required: the service
    /// needs them, setup does not.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="ServiceStartException">A file, key, or value is not valid, or a required one is missing.</exception>
    public static ServiceSettings Load(string? configPath, Func<string, string?> environment, bool requireSigning = true)
    {
        ArgumentNullException.ThrowIfNull(environment);
        SettingsFile file = configPath is null ? new SettingsFile() : Check(ReadFile(configPath));
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

        string? signingKey = Env("ELM_SIGNING_KEY") ?? file.Signing?.Key;
        IReadOnlyList<string> certificates = EnvList("ELM_SIGNING_CERTIFICATES") ?? file.Signing?.Certificates ?? [];
        if (requireSigning && signingKey is null)
        {
            throw new ServiceStartException("Set signing.key to the signing key file (PKCS#8 PEM).");
        }

        if (requireSigning && certificates.Count == 0)
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

        int Rate(string variable, string key, int? inFile, int fallback) =>
            (Env(variable) is string text ? ParseInt(text, key) : inFile ?? fallback) is int rate and >= 1 and <= 10_000
                ? rate
                : throw new ServiceStartException($"{key} must be between 1 and 10000.");
        int perIp = Rate("ELM_RATELIMIT_PERIP", "rateLimit.perIp", file.RateLimit?.PerIp, 20);
        int perKey = Rate("ELM_RATELIMIT_PERKEY", "rateLimit.perKey", file.RateLimit?.PerKey, 10);
        int clientPerIp = Rate("ELM_RATELIMIT_CLIENTPERIP", "rateLimit.clientPerIp", file.RateLimit?.ClientPerIp, 20);
        int clientPerKey = Rate("ELM_RATELIMIT_CLIENTPERKEY", "rateLimit.clientPerKey", file.RateLimit?.ClientPerKey, 2);

        IReadOnlyList<Uri> publicUrls = [.. (EnvList("ELM_PUBLICURLS") ?? file.PublicUrls ?? []).Select(ParsePublicUrl)];
        if (publicUrls.Count == 0 && !(listen.IsLoopback || listen.Host == "localhost"))
        {
            // design §14.8: devices sign the URL they reach; the listen address is not one they reach unless it is loopback.
            throw new ServiceStartException("Set publicUrls to the https URLs devices reach the service under; listen is not a loopback address.");
        }

        // design §14.8: no default secret, so that nodes never disagree about the nonces they accept.
        byte[] nonceSecret = [];
        string? nonceText =
            Env(NonceSecretVariable) ?? (file.Nonce?.SecretFile is string secretFile ? ReadSecret(secretFile, "nonce.secretFile") : null);
        if (nonceText is not null || requireSigning)
        {
            nonceSecret = DecodeBase64(nonceText) is { Length: >= Issuing.NonceService.MinSecretLength } secret
                ? secret
                : throw new ServiceStartException(
                    $"Set {NonceSecretVariable} or nonce.secretFile to the nonces' secret: at least 32 random bytes in base64."
                );
        }

        string nodeName = Env("ELM_NODE_NAME") ?? file.Node?.Name ?? DefaultNodeName();
        if (!IsNodeName(nodeName))
        {
            throw new ServiceStartException($"node.name must be 1 to 64 characters of A-Z, a-z, 0-9, and '-': '{nodeName}'.");
        }

        int? retentionDays = Env("ELM_AUDIT_RETENTIONDAYS") is string daysText
            ? ParseInt(daysText, "audit.retentionDays")
            : file.Audit?.RetentionDays;
        if (retentionDays is < 1 or > 36_600)
        {
            throw new ServiceStartException("audit.retentionDays must be between 1 and 36600, or left out to keep every row.");
        }

        int inactiveDays = Env("ELM_APIKEYS_INACTIVEADMINDAYS") is string inactiveText
            ? ParseInt(inactiveText, "apiKeys.inactiveAdminDays")
            : file.ApiKeys?.InactiveAdminDays ?? 90;
        if (inactiveDays is < 0 or > 3_650)
        {
            throw new ServiceStartException("apiKeys.inactiveAdminDays must be between 0 and 3650; 0 turns the rule off.");
        }

        OidcSettings? oidc = OidcSettings.Load(file.Oidc, Env);
        if (oidc is not null && owner is not null && owner.Issuer != oidc.Issuer)
        {
            // design §14.6: otherwise the bootstrap owner could never sign in, and the 90-day rule would stop its keys.
            throw new ServiceStartException("bootstrapOwner.iss must equal oidc.issuer.");
        }

        WebLoginSettings? web = WebLoginSettings.Load(file, Env, ReadSecret, oidc, listen, publicUrls);

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
            PublicUrls = publicUrls,
            NonceSecret = nonceSecret,
            RateLimitClientPerIp = clientPerIp,
            RateLimitClientPerKey = clientPerKey,
            UsageLogDirectory = Env("ELM_USAGELOG_DIRECTORY") ?? file.UsageLog?.Directory,
            NodeName = nodeName,
            Oidc = oidc,
            InactiveAdminDays = inactiveDays,
            Web = web,
        };
    }

    /// <summary>Returns whether a text is a node name: 1 to 64 characters of <c>[A-Za-z0-9-]</c>.</summary>
    public static bool IsNodeName(string? name) =>
        name is { Length: >= 1 and <= 64 } && name.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>
    /// Returns the node name made from a host name (design §14.8): characters a node name does not allow become <c>-</c>,
    /// cut to 64; <c>node</c> when nothing is left.
    /// </summary>
    public static string NodeNameOf(string hostName)
    {
        ArgumentNullException.ThrowIfNull(hostName);
        string name = new([.. hostName.Select(c => IsNodeName(c.ToString()) ? c : '-').Take(64)]);
        return name.Length > 0 ? name : "node";
    }

    private static string DefaultNodeName() => NodeNameOf(Environment.MachineName);

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

    // design §12.1: an https URL without query or fragment, as elm-server's publicUrls.
    private static Uri ParsePublicUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? uri
            : throw new ServiceStartException($"publicUrls must be https URLs without query or fragment: '{text}'.");

    // A value written in the file may be left out but not empty: "" or null in a list is a mistake, not "use the default".
    private static SettingsFile Check(SettingsFile file)
    {
        static void Text(string? value, string key)
        {
            if (value is { Length: 0 })
            {
                throw new ServiceStartException($"{key} must not be empty; leave it out instead.");
            }
        }

        static void List(List<string>? values, string key)
        {
            if (values is not null && values.Any(string.IsNullOrEmpty))
            {
                throw new ServiceStartException($"{key} must not hold an empty or null entry.");
            }
        }

        Text(file.Listen, "listen");
        Text(file.Tls?.Certificate, "tls.certificate");
        Text(file.Tls?.Key, "tls.key");
        Text(file.Database?.Provider, "database.provider");
        Text(file.Database?.ConnectionFile, "database.connectionFile");
        Text(file.Signing?.Key, "signing.key");
        List(file.Signing?.Certificates, "signing.certificates");
        List(file.Trust?.RootKeys, "trust.rootKeys");
        List(file.Trust?.RevokedKeyIds, "trust.revokedKeyIds");
        List(file.PublicUrls, "publicUrls");
        Text(file.Nonce?.SecretFile, "nonce.secretFile");
        Text(file.UsageLog?.Directory, "usageLog.directory");
        Text(file.Node?.Name, "node.name");
        Text(file.Oidc?.Issuer, "oidc.issuer");
        Text(file.Oidc?.Audience, "oidc.audience");
        Text(file.Oidc?.CliClientId, "oidc.cliClientId");
        List(file.Oidc?.CliScopes, "oidc.cliScopes");
        Text(file.Oidc?.RoleClaim, "oidc.roleClaim");
        Text(file.Oidc?.WebClientId, "oidc.webClientId");
        Text(file.Oidc?.WebClientSecretFile, "oidc.webClientSecretFile");
        Text(file.DataProtection?.KekFile, "dataProtection.kekFile");
        return file;
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

    private static byte[]? DecodeBase64(string? text)
    {
        try
        {
            return text is null ? null : Convert.FromBase64String(text.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static int ParseInt(string text, string key) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new ServiceStartException($"{key} must be a whole number: '{text}'.");
}
