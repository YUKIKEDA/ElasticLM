using System.Globalization;
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

        return new ServerSettings
        {
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
    public static System.Net.IPAddress ListenAddress(Uri listen)
    {
        string host = listen.Host.Trim('[', ']');
        return host == "localhost" ? System.Net.IPAddress.Loopback
            : System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? parsed) ? parsed
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

    private static int ParseInt(string text, string key) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new ServerStartException($"{key} must be a whole number: '{text}'.");

    [GeneratedRegex("^[a-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductIdPattern();
}
