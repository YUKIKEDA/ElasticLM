using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ElasticLM.Server;

/// <summary>
/// A settings error: the server does not start.
/// </summary>
internal sealed class ServerStartException(string message) : Exception(message);

/// <summary>
/// The server's settings (design §13.8): a JSON file, then environment variables named <c>ELM_</c> + the key path in upper
/// case with <c>.</c> as <c>_</c> (for example <c>ELM_RATELIMIT_PERIP</c>). Lists in variables are separated by <c>;</c>.
/// </summary>
internal sealed partial class ServerSettings
{
    /// <summary>The variable that holds the vendor's root keys as PEM text, instead of <c>trust.rootKeys</c> files.</summary>
    public const string TrustRootKeysVariable = "ELM_TRUST_ROOT_KEYS";

    /// <summary>The variable that holds the password of a PKCS#12 <c>tls.certificate</c>.</summary>
    public const string TlsPasswordVariable = "ELM_TLS_PASSWORD";

    private const int MaxSettingsFileLength = 64 * 1024;

    public required string Product { get; init; }

    public Uri Listen { get; init; } = new("https://0.0.0.0:7070");

    /// <summary>Gets the configured public URLs, or an empty list to build them from <see cref="Listen"/>.</summary>
    public IReadOnlyList<Uri> PublicUrls { get; init; } = [];

    public int RateLimitPerIp { get; init; } = 20;

    public required string LicensePath { get; init; }

    public IReadOnlyList<string> TrustRootKeyFiles { get; init; } = [];

    public IReadOnlyList<string> TrustRevokedKeyIds { get; init; } = [];

    public string? TlsCertificate { get; init; }

    public string? TlsKey { get; init; }

    public AccessRules Access { get; init; } = AccessRules.Empty;

    public string? DataDirectory { get; init; }

    /// <summary>Gets the operator's borrow limit (<c>borrow.maxDuration</c>), or <see langword="null"/> for the license's.</summary>
    public TimeSpan? BorrowMaxDuration { get; init; }

    /// <summary>Gets the seats of each feature kept from borrowing (<c>borrow.reserve</c>).</summary>
    public int BorrowReserve { get; init; }

    /// <summary>Gets the usage log (<c>log.usage</c>): a file name whose folder and name, without extension, name the daily
    /// files. A relative path is under the data folder.</summary>
    public string UsageLog { get; init; } = "usage.jsonl";

    /// <summary>Gets where the local administration API listens (<c>admin.listen</c>, design §13.7): http on a loopback
    /// address only.</summary>
    public Uri AdminListen { get; init; } = new("http://127.0.0.1:7071");

    /// <summary>
    /// Reads the settings file (if any) and the environment.
    /// </summary>
    /// <param name="configPath">The settings file, or <see langword="null"/>.</param>
    /// <param name="environment">Reads an environment variable.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="ServerStartException">A file, key, or value is not valid.</exception>
    public static ServerSettings Load(string? configPath, Func<string, string?> environment)
    {
        SettingsFile file = configPath is null ? new SettingsFile() : ReadFile(configPath);
        string? Env(string name) => environment(name) is { Length: > 0 } value ? value : null;
        IReadOnlyList<string>? EnvList(string name) =>
            Env(name) is string value ? value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;

        string product = Env("ELM_PRODUCT") ?? file.Product ?? throw new ServerStartException("Set product to the product ID of the server license.");
        if (!ProductIdPattern().IsMatch(product))
        {
            throw new ServerStartException("product must be lowercase letters, digits, and '-', at most 64 characters.");
        }

        Uri listen = ParseHttps(Env("ELM_LISTEN") ?? file.Listen ?? "https://0.0.0.0:7070", "listen");
        _ = ListenAddress(listen);
        IReadOnlyList<Uri> publicUrls = [.. (EnvList("ELM_PUBLICURLS") ?? file.PublicUrls ?? []).Select(u => ParseHttps(u, "publicUrls"))];
        int perIp = Env("ELM_RATELIMIT_PERIP") is string perIpText ? ParseInt(perIpText, "rateLimit.perIp") : file.RateLimit?.PerIp ?? 20;
        if (perIp is < 1 or > 10_000)
        {
            throw new ServerStartException("rateLimit.perIp must be between 1 and 10000.");
        }

        TimeSpan? borrowMax = (Env("ELM_BORROW_MAXDURATION") ?? file.Borrow?.MaxDuration) is string maxText
            ? ParseDuration(maxText, "borrow.maxDuration")
            : null;
        int reserve = Env("ELM_BORROW_RESERVE") is string reserveText ? ParseInt(reserveText, "borrow.reserve") : file.Borrow?.Reserve ?? 0;
        if (reserve is < 0 or > 1_000_000)
        {
            throw new ServerStartException("borrow.reserve must be between 0 and 1000000.");
        }

        Uri admin = ParseAdmin(Env("ELM_ADMIN_LISTEN") ?? file.Admin?.Listen ?? "http://127.0.0.1:7071");
        string usage = Env("ELM_LOG_USAGE") ?? file.Log?.Usage ?? "usage.jsonl";
        if (Path.GetFileNameWithoutExtension(usage).Length == 0)
        {
            throw new ServerStartException($"log.usage must name a file: '{usage}'.");
        }

        return new ServerSettings
        {
            BorrowMaxDuration = borrowMax,
            BorrowReserve = reserve,
            AdminListen = admin,
            UsageLog = usage,
            Product = product,
            Listen = listen,
            PublicUrls = publicUrls,
            RateLimitPerIp = perIp,
            LicensePath = Env("ELM_LICENSE") ?? file.License ?? Path.Combine(AppContext.BaseDirectory, "server.elmlic"),
            TrustRootKeyFiles = EnvList("ELM_TRUST_ROOTKEYS") ?? file.Trust?.RootKeys ?? [],
            TrustRevokedKeyIds = EnvList("ELM_TRUST_REVOKEDKEYIDS") ?? file.Trust?.RevokedKeyIds ?? [],
            TlsCertificate = Env("ELM_TLS_CERTIFICATE") ?? file.Tls?.Certificate,
            TlsKey = Env("ELM_TLS_KEY") ?? file.Tls?.Key,
            Access = AccessRules.Parse(EnvList("ELM_ALLOW") ?? file.Allow ?? [], EnvList("ELM_DENY") ?? file.Deny ?? []),
            DataDirectory = Env("ELM_DATADIRECTORY") ?? file.DataDirectory,
        };
    }

    private static SettingsFile ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new ServerStartException($"The settings file '{path}' does not exist.");
            }

            if (info.Length > MaxSettingsFileLength)
            {
                throw new ServerStartException($"The settings file '{path}' is larger than 64 KiB.");
            }

            return JsonSerializer.Deserialize(File.ReadAllBytes(path), ServerJsonContext.Default.SettingsFile)
                ?? throw new ServerStartException($"The settings file '{path}' is empty.");
        }
        catch (JsonException e)
        {
            throw new ServerStartException($"The settings file '{path}' is not valid: {e.Message}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ServerStartException($"The settings file '{path}' cannot be read: {e.Message}");
        }
    }

    /// <summary>
    /// Returns the address to listen on: the host of <c>listen</c> must be an IP address, <c>localhost</c>, <c>0.0.0.0</c>,
    /// or <c>[::]</c> (design §13.8).
    /// </summary>
    /// <exception cref="ServerStartException">The host is a name other than <c>localhost</c>.</exception>
    public static IPAddress ListenAddress(Uri listen)
    {
        string host = listen.Host.Trim('[', ']');
        return host == "localhost" ? IPAddress.Loopback
            : IPAddress.TryParse(host, out IPAddress? parsed) ? parsed
            : throw new ServerStartException($"listen must name an IP address, localhost, 0.0.0.0, or [::]: '{listen}'.");
    }

    private static Uri ParseHttps(string text, string key)
    {
        if (
            !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
        )
        {
            throw new ServerStartException($"{key} must be an https URL without query or fragment: '{text}'.");
        }

        return uri;
    }

    // design §13.8: a duration in the invariant form d.hh:mm:ss, at least one second and at most 100 years.
    private static TimeSpan ParseDuration(string text, string key) =>
        TimeSpan.TryParseExact(text, "c", CultureInfo.InvariantCulture, out TimeSpan value)
        && value >= TimeSpan.FromSeconds(1)
        && value <= TimeSpan.FromDays(36_600)
            ? value
            : throw new ServerStartException($"{key} must be a duration such as 7.00:00:00, from one second to 100 years: '{text}'.");

    // design §13.7: the administration API answers only on this machine, so it listens on a loopback address over http.
    private static Uri ParseAdmin(string text)
    {
        if (
            !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || uri.AbsolutePath != "/"
            || uri.Query.Length > 0
            || !(
                uri.Host == "localhost"
                || (
                    System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out System.Net.IPAddress? address)
                    && System.Net.IPAddress.IsLoopback(address)
                )
            )
        )
        {
            throw new ServerStartException($"admin.listen must be an http URL on a loopback address, such as http://127.0.0.1:7071: '{text}'.");
        }

        return uri;
    }

    private static int ParseInt(string text, string key) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new ServerStartException($"{key} must be a whole number: '{text}'.");

    [GeneratedRegex("^[a-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductIdPattern();
}
