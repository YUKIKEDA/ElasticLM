using System.CommandLine;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ElasticLM.Cli;

/// <summary>
/// <c>elm admin login</c>, <c>logout</c>, and <c>request</c> (design §16, decision 0.98, M5a).
/// </summary>
internal static class AdminCommands
{
    /// <summary>The longest path <c>request</c> sends.</summary>
    public const int MaxPathLength = 2048;

    private const int MaxBodyBytes = 64 * 1024;
    private const int MaxErrorBytes = 64 * 1024;
    private const int DefaultInterval = 5;
    private const int MaxInterval = 60;
    private const int MaxDeviceLifetime = 1800;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(100);
    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    public static Command Create(CliHost host)
    {
        var loginUrl = new Option<string>("--url") { Description = "The elm-service URL (https; http only on a loopback address).", Required = true };
        var login = new Command("login", "Sign in to an elm-service with your identity provider (OIDC device flow).") { loginUrl };
        login.SetAction(parse => CommonOptions.Run(host, () => Login(host, parse.GetValue(loginUrl)!)));

        var logout = new Command("logout", "Remove the saved login and revoke its refresh token.");
        logout.SetAction(_ => CommonOptions.Run(host, () => Logout(host)));

        var method = new Argument<string>("METHOD") { Description = "GET, POST, PUT, PATCH, or DELETE." };
        var path = new Argument<string>("path") { Description = "The path, starting with /v1/admin/; it may hold a query." };
        var body = new Option<string?>("--body") { Description = "A JSON file to send, up to 64 KiB." };
        var url = new Option<string?>("--url") { Description = "The elm-service URL; by default ELM_SERVICE_URL, then the saved login's." };
        var request = new Command("request", "Call the administration API once and write the answer's body.") { method, path, body, url };
        request.SetAction(parse =>
            CommonOptions.Run(host, () => Request(host, parse.GetValue(method)!, parse.GetValue(path)!, parse.GetValue(body), parse.GetValue(url)))
        );

        var admin = new Command("admin", "Sign in to elm-service and call its administration API.");
        admin.Subcommands.Add(login);
        admin.Subcommands.Add(logout);
        admin.Subcommands.Add(request);
        return admin;
    }

    /// <summary>Returns the service URL with a trailing "/", or throws when it is not https (http only on a loopback host).</summary>
    public static Uri ServiceUrl(string text, string what) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? url) && OidcHttp.IsAllowed(url) && url.Query.Length == 0 && url.Fragment.Length == 0
            ? (url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/"))
            : throw new CliException($"{what} must be an https URL, or http on a loopback address: '{text}'.");

    private static int Login(CliHost host, string urlText)
    {
        Uri service = ServiceUrl(urlText, "--url");
        using HttpClient http = OidcHttp.Create();
        using var infoRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(service, "v1/admin/oidc"));
        (HttpStatusCode infoStatus, JsonDocument? infoBody) = OidcHttp.Send(http, infoRequest);
        string issuer;
        string clientId;
        string scopes;
        using (infoBody)
        {
            if (infoStatus == HttpStatusCode.NotFound)
            {
                throw new CliException("The service does not accept OIDC sign-in; use ELM_API_KEY.", ExitCodes.VerificationFailed);
            }

            if (infoStatus != HttpStatusCode.OK || infoBody?.RootElement is not { ValueKind: JsonValueKind.Object } info)
            {
                throw new CliException($"The service answered {(int)infoStatus} to /v1/admin/oidc.", ExitCodes.VerificationFailed);
            }

            issuer = Text(info, "issuer", "service's OIDC settings");
            clientId = Text(info, "clientId", "service's OIDC settings");
            scopes =
                info.TryGetProperty("scopes", out JsonElement list) && list.ValueKind == JsonValueKind.Array
                    ? string.Join(' ', list.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()))
                    : throw new CliException("The service's OIDC settings have no scopes.", ExitCodes.VerificationFailed);
        }

        Uri deviceEndpoint;
        Uri tokenEndpoint;
        using (JsonDocument discovery = OidcHttp.Discovery(http, issuer))
        {
            deviceEndpoint = OidcHttp.Url(discovery.RootElement, "device_authorization_endpoint", "discovery document");
            tokenEndpoint = OidcHttp.Url(discovery.RootElement, "token_endpoint", "discovery document");
        }

        // design §5.4: the person can check where they sign in before they approve.
        host.Error.WriteLine($"Signing in to {service} with the identity provider {issuer}.");

        (HttpStatusCode deviceStatus, JsonDocument? deviceBody) = OidcHttp.PostForm(
            http,
            deviceEndpoint,
            [new("client_id", clientId), new("scope", scopes)]
        );
        string deviceCode;
        int interval;
        int lifetime;
        using (deviceBody)
        {
            if (deviceStatus != HttpStatusCode.OK || deviceBody?.RootElement is not { ValueKind: JsonValueKind.Object } device)
            {
                throw new CliException(
                    $"The identity provider refused the device authorization: {OidcHttp.ErrorOf(deviceBody) ?? ((int)deviceStatus).ToString(CultureInfo.InvariantCulture)}.",
                    ExitCodes.VerificationFailed
                );
            }

            deviceCode = Text(device, "device_code", "device authorization");
            string userCode = Text(device, "user_code", "device authorization");
            string verification = Text(device, "verification_uri", "device authorization");
            int? expiresIn = Number(device, "expires_in");
            if (expiresIn is not int seconds || seconds < 1)
            {
                throw new CliException("The device authorization has no expires_in.", ExitCodes.VerificationFailed);
            }

            lifetime = Math.Min(seconds, MaxDeviceLifetime);
            interval = Math.Clamp(Number(device, "interval") ?? DefaultInterval, 1, MaxInterval);
            host.Error.WriteLine($"Open {verification} and enter the code {userCode}.");
            if (device.TryGetProperty("verification_uri_complete", out JsonElement complete) && complete.ValueKind == JsonValueKind.String)
            {
                host.Error.WriteLine($"Or open {complete.GetString()}");
            }
        }

        // RFC 8628 §3.4 and §3.5.
        long start = host.Time.GetTimestamp();
        while (true)
        {
            host.Pause(TimeSpan.FromSeconds(interval));
            if (host.Time.GetElapsedTime(start) > TimeSpan.FromSeconds(lifetime))
            {
                throw new CliException("The device code expired before the sign-in was approved.", ExitCodes.VerificationFailed);
            }

            HttpStatusCode status;
            JsonDocument? body;
            try
            {
                (status, body) = OidcHttp.PostForm(
                    http,
                    tokenEndpoint,
                    [new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"), new("device_code", deviceCode), new("client_id", clientId)]
                );
            }
            catch (CliException e) when (e.ExitCode == ExitCodes.NetworkError)
            {
                interval = Math.Min(interval * 2, MaxInterval);
                continue;
            }

            using (body)
            {
                if (status == HttpStatusCode.OK)
                {
                    (string access, string? refresh, long expiresAt) = OidcHttp.Tokens(body, host.Time.GetUtcNow());
                    string path = AdminLoginFile.PathOf(host);
                    using (AdminLoginFile.Lock(host, path))
                    {
                        AdminLoginFile.Write(path, new AdminLogin(service.AbsoluteUri, issuer, clientId, access, refresh, expiresAt));
                    }

                    host.Out.WriteLine($"Signed in to {service}.");
                    return ExitCodes.Success;
                }

                switch (OidcHttp.ErrorOf(body))
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval = Math.Min(interval + 5, MaxInterval);
                        continue;
                    case string error:
                        throw new CliException($"The identity provider ended the sign-in: {error}.", ExitCodes.VerificationFailed);
                    default:
                        throw new CliException($"The identity provider answered {(int)status} to the token request.", ExitCodes.VerificationFailed);
                }
            }
        }
    }

    private static int Logout(CliHost host)
    {
        string path = AdminLoginFile.PathOf(host);
        using IDisposable held = AdminLoginFile.Lock(host, path);
        if (AdminLoginFile.Read(path) is not AdminLogin login)
        {
            AdminLoginFile.Delete(path);
            host.Out.WriteLine("Not signed in.");
            return ExitCodes.Success;
        }

        if (login.RefreshToken is not null)
        {
            try
            {
                using HttpClient http = OidcHttp.Create();
                using JsonDocument discovery = OidcHttp.Discovery(http, login.Issuer);
                if (discovery.RootElement.TryGetProperty("revocation_endpoint", out _))
                {
                    Uri revocation = OidcHttp.Url(discovery.RootElement, "revocation_endpoint", "discovery document");
                    (HttpStatusCode status, JsonDocument? body) = OidcHttp.PostForm(
                        http,
                        revocation,
                        [new("token", login.RefreshToken), new("token_type_hint", "refresh_token"), new("client_id", login.ClientId)]
                    );
                    body?.Dispose();
                    if (status != HttpStatusCode.OK)
                    {
                        host.Error.WriteLine($"The identity provider answered {(int)status} to the revocation; the login is removed all the same.");
                    }
                }
            }
            catch (CliException e)
            {
                host.Error.WriteLine("The refresh token was not revoked: " + e.Message);
            }
        }

        AdminLoginFile.Delete(path);
        host.Out.WriteLine("Signed out.");
        return ExitCodes.Success;
    }

    private static int Request(CliHost host, string method, string path, string? bodyFile, string? urlText)
    {
        string verb = method.ToUpperInvariant();
        if (!Methods.Contains(verb, StringComparer.Ordinal))
        {
            throw new CliException($"METHOD must be GET, POST, PUT, PATCH, or DELETE: '{method}'.");
        }

        CheckPath(path);
        byte[]? body = bodyFile is null ? null : CliFiles.ReadBytes(bodyFile, MaxBodyBytes);
        string? chosen =
            urlText ?? (host.GetEnvironmentVariable("ELM_SERVICE_URL") is { Length: > 0 } env ? env : null) ?? AdminCredentials.SavedUrl(host);
        if (chosen is null)
        {
            throw new CliException("Give --url, set ELM_SERVICE_URL, or sign in with 'elm admin login'.");
        }

        Uri service = ServiceUrl(chosen, "The service URL");
        string bearer =
            AdminCredentials.ApiKey(host)
            ?? AdminCredentials.AccessToken(host, service)
            ?? throw new CliException($"Set ELM_API_KEY, or sign in to {service} with 'elm admin login'.");
        var target = new Uri(service.AbsoluteUri.TrimEnd('/') + path);

        // design §16: no redirects, so the Bearer value goes nowhere else (§12.1).
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = RequestTimeout };
        using var request = new HttpRequestMessage(new HttpMethod(verb), target);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        try
        {
            using HttpResponseMessage response = http.Send(request, HttpCompletionOption.ResponseHeadersRead);
            using Stream stream = response.Content.ReadAsStream();
            if (response.IsSuccessStatusCode)
            {
                // The body is written as it arrives, whatever its size: a usage log file can be large.
                using var reader = new StreamReader(stream, Encoding.UTF8);
                char[] chunk = new char[8192];
                int read;
                while ((read = reader.Read(chunk)) > 0)
                {
                    host.Out.Write(chunk, 0, read);
                }

                return ExitCodes.Success;
            }

            byte[] error = new byte[MaxErrorBytes];
            int length = 0;
            int got;
            while (length < error.Length && (got = stream.Read(error, length, error.Length - length)) > 0)
            {
                length += got;
            }

            host.Error.WriteLine($"error: the service answered {(int)response.StatusCode}.");
            host.Error.WriteLine(Encoding.UTF8.GetString(error, 0, length));
            return ExitCodes.VerificationFailed;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new CliException("The service cannot be reached: " + e.Message, ExitCodes.NetworkError);
        }
    }

    // design §16: forms that System.Uri would normalize are refused before sending.
    private static void CheckPath(string path)
    {
        string route = path.Split('?', 2)[0];
        bool bad =
            path.Length > MaxPathLength
            || !path.StartsWith("/v1/admin/", StringComparison.Ordinal)
            || path.Contains('#', StringComparison.Ordinal)
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains("%2e", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%5c", StringComparison.OrdinalIgnoreCase)
            || route.Split('/').Any(segment => segment is "." or "..");
        if (bad)
        {
            throw new CliException(
                $"path must start with /v1/admin/, be at most {MaxPathLength} characters, and hold no fragment, backslash, '.' or '..' segment, or %2e, %2f, %5c: '{path}'."
            );
        }
    }

    private static string Text(JsonElement document, string member, string what) =>
        document.TryGetProperty(member, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new CliException($"The {what} has no {member}.", ExitCodes.VerificationFailed);

    private static int? Number(JsonElement document, string member) =>
        document.TryGetProperty(member, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : null;
}
