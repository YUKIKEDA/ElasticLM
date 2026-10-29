using System.Net;
using System.Text.Json;

namespace ElasticLM.Cli;

/// <summary>
/// The calls <c>elm</c> makes to an identity provider and to <c>GET /v1/admin/oidc</c> (design §16): 30 seconds each, answers
/// up to 64 KiB, no redirects, and https URLs (http only on a loopback host).
/// </summary>
internal static class OidcHttp
{
    /// <summary>The largest answer read from the identity provider or <c>/v1/admin/oidc</c>.</summary>
    public const int MaxAnswerBytes = 64 * 1024;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Creates the client for these calls.</summary>
    public static HttpClient Create() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout, MaxResponseContentBufferSize = MaxAnswerBytes };

    /// <summary>Returns whether a URL may be called: https, or http on a loopback host.</summary>
    public static bool IsAllowed(Uri? url) =>
        url is { IsAbsoluteUri: true } && (url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback));

    /// <summary>Reads a URL from a document and checks it.</summary>
    /// <exception cref="CliException">The member is missing or not an allowed URL.</exception>
    public static Uri Url(JsonElement document, string member, string what) =>
        document.TryGetProperty(member, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && Uri.TryCreate(value.GetString(), UriKind.Absolute, out Uri? url)
        && IsAllowed(url)
            ? url
            : throw new CliException($"The {what} has no https {member}.", ExitCodes.VerificationFailed);

    /// <summary>Sends a request and reads its JSON answer; a status other than 2xx keeps its body for the caller to read.</summary>
    /// <exception cref="CliException">The other side cannot be reached (exit code 3), or its answer is not JSON (1).</exception>
    public static (HttpStatusCode Status, JsonDocument? Body) Send(HttpClient http, HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(http);
        try
        {
            using HttpResponseMessage response = http.Send(request);
            byte[] bytes;
            using (Stream stream = response.Content.ReadAsStream())
            using (var buffer = new MemoryStream())
            {
                byte[] chunk = new byte[8192];
                int read;
                while ((read = stream.Read(chunk)) > 0)
                {
                    if (buffer.Length + read > MaxAnswerBytes)
                    {
                        throw new CliException($"The answer of {request.RequestUri} is larger than 64 KiB.", ExitCodes.VerificationFailed);
                    }

                    buffer.Write(chunk, 0, read);
                }

                bytes = buffer.ToArray();
            }

            JsonDocument? body = null;
            try
            {
                body = bytes.Length == 0 ? null : JsonDocument.Parse(bytes);
            }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
                body = null;
            }
            catch (JsonException)
            {
                throw new CliException($"The answer of {request.RequestUri} is not JSON.", ExitCodes.VerificationFailed);
            }

            return (response.StatusCode, body);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new CliException($"{request.RequestUri} cannot be reached: {e.Message}", ExitCodes.NetworkError);
        }
    }

    /// <summary>Gets a JSON document; a status other than 2xx is an error (exit code 1).</summary>
    public static JsonDocument Get(HttpClient http, Uri url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        (HttpStatusCode status, JsonDocument? body) = Send(http, request);
        if ((int)status is < 200 or >= 300 || body is null)
        {
            body?.Dispose();
            throw new CliException($"{url} answered {(int)status}.", ExitCodes.VerificationFailed);
        }

        return body;
    }

    /// <summary>Posts a form and returns the status and the JSON answer.</summary>
    public static (HttpStatusCode Status, JsonDocument? Body) PostForm(HttpClient http, Uri url, IEnumerable<KeyValuePair<string, string>> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        return Send(http, request);
    }

    /// <summary>Returns the OAuth error code of an answer (RFC 6749 §5.2), or <see langword="null"/>.</summary>
    public static string? ErrorOf(JsonDocument? body) =>
        body?.RootElement is { ValueKind: JsonValueKind.Object } root
        && root.TryGetProperty("error", out JsonElement error)
        && error.ValueKind == JsonValueKind.String
            ? error.GetString()
            : null;

    /// <summary>
    /// Reads the discovery document of an issuer (design §14.6: the issuer without a terminating "/" and the well-known
    /// suffix), and checks that it names that issuer.
    /// </summary>
    public static JsonDocument Discovery(HttpClient http, string issuer)
    {
        if (!Uri.TryCreate(issuer.TrimEnd('/') + "/.well-known/openid-configuration", UriKind.Absolute, out Uri? url) || !IsAllowed(url))
        {
            throw new CliException($"The issuer '{issuer}' is not an https URL.", ExitCodes.VerificationFailed);
        }

        JsonDocument document = Get(http, url);
        if (
            document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("issuer", out JsonElement named)
            || named.ValueKind != JsonValueKind.String
            || named.GetString() != issuer
        )
        {
            document.Dispose();
            throw new CliException($"The discovery document of '{issuer}' names another issuer.", ExitCodes.VerificationFailed);
        }

        return document;
    }

    /// <summary>
    /// Reads a token answer (RFC 6749 §5.1): token_type Bearer and access_token are required; a missing expires_in counts as
    /// 300 seconds (design §16).
    /// </summary>
    public static (string Access, string? Refresh, long ExpiresAt) Tokens(JsonDocument? body, DateTimeOffset now)
    {
        if (
            body?.RootElement is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("token_type", out JsonElement type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("access_token", out JsonElement access)
            || access.ValueKind != JsonValueKind.String
        )
        {
            throw new CliException("The identity provider's token answer is not a Bearer token.", ExitCodes.VerificationFailed);
        }

        string? refresh = root.TryGetProperty("refresh_token", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        long lifetime =
            root.TryGetProperty("expires_in", out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long seconds)
                ? seconds
                : 300;
        return (access.GetString()!, refresh, now.ToUnixTimeSeconds() + lifetime);
    }
}

/// <summary>
/// How <c>elm admin request</c> and <c>elm offline process</c> authenticate (design §16): <c>ELM_API_KEY</c>, otherwise the
/// saved login, used only for its own origin and refreshed when it expires within 60 seconds.
/// </summary>
internal static class AdminCredentials
{
    private const long RefreshMargin = 60;

    /// <summary>Returns the API key in <c>ELM_API_KEY</c>, or <see langword="null"/>.</summary>
    public static string? ApiKey(CliHost host) => host.GetEnvironmentVariable("ELM_API_KEY") is { Length: > 0 } key ? key : null;

    /// <summary>Returns whether two URLs share scheme, host, and port.</summary>
    public static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    /// <summary>
    /// Returns the saved login's access token for a URL of the same origin, refreshing it first when it expires within 60
    /// seconds; <see langword="null"/> when there is no login or it is for another origin.
    /// </summary>
    /// <exception cref="CliException">The login expired and cannot be refreshed (exit code 1), or the provider is unreachable (3).</exception>
    public static string? AccessToken(CliHost host, Uri target)
    {
        string path = AdminLoginFile.PathOf(host);

        // Without a login nothing is created: no folder and no lock file in the user's profile.
        if (!File.Exists(path))
        {
            return null;
        }

        using IDisposable held = AdminLoginFile.Lock(host, path);
        if (
            AdminLoginFile.Read(path) is not AdminLogin login
            || !Uri.TryCreate(login.Url, UriKind.Absolute, out Uri? saved)
            || !SameOrigin(saved, target)
        )
        {
            return null;
        }

        long now = host.Time.GetUtcNow().ToUnixTimeSeconds();
        if (login.ExpiresAt - now >= RefreshMargin)
        {
            return login.AccessToken;
        }

        if (login.RefreshToken is null)
        {
            throw new CliException("The login has expired; run 'elm admin login' again.", ExitCodes.VerificationFailed);
        }

        using HttpClient http = OidcHttp.Create();
        Uri tokenEndpoint;
        using (JsonDocument discovery = OidcHttp.Discovery(http, login.Issuer))
        {
            tokenEndpoint = OidcHttp.Url(discovery.RootElement, "token_endpoint", "discovery document");
        }

        (HttpStatusCode status, JsonDocument? body) = OidcHttp.PostForm(
            http,
            tokenEndpoint,
            [new("grant_type", "refresh_token"), new("refresh_token", login.RefreshToken), new("client_id", login.ClientId)]
        );
        using (body)
        {
            if (status != HttpStatusCode.OK)
            {
                string? error = OidcHttp.ErrorOf(body);
                throw new CliException(
                    error == "invalid_grant"
                        ? "The login can no longer be refreshed; run 'elm admin login' again."
                        : $"The identity provider refused to refresh the login: {error ?? ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture)}.",
                    ExitCodes.VerificationFailed
                );
            }

            (string access, string? refresh, long expiresAt) = OidcHttp.Tokens(body, host.Time.GetUtcNow());

            // RFC 6749 §6: a new refresh token replaces the old one; without one, the old one stays.
            AdminLoginFile.Write(path, login with { AccessToken = access, RefreshToken = refresh ?? login.RefreshToken, ExpiresAt = expiresAt });
            return access;
        }
    }

    /// <summary>Returns the saved login's URL, or <see langword="null"/>.</summary>
    public static string? SavedUrl(CliHost host) => AdminLoginFile.Read(AdminLoginFile.PathOf(host))?.Url;
}
