using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ElasticLM.Service.Tests;

/// <summary>
/// A minimal OIDC provider for tests (design §15.3, decision 0.97): discovery, JWKS, the device authorization flow (RFC 8628),
/// refresh tokens, revocation, and the authorization code flow with PKCE of the admin UI's sign-in (M5b), on loopback http.
/// Its signing key is made by the test, and it mints access tokens and ID tokens with any claims the test asks for. The
/// authorization endpoint signs in <see cref="Subject"/> at once, without a page.
/// </summary>
internal sealed class FakeIdp : IAsyncDisposable
{
    public const string Realm = "/realms/test";
    public const string Audience = "elm-service";
    public const string CliClientId = "elm-cli";
    public const string WebClientId = "elm-web";
    public const string WebClientSecret = "web-secret-test-only";

    private static readonly Dictionary<string, string> DefaultRoleMap = new(StringComparer.Ordinal)
    {
        ["elm-owners"] = "owner",
        ["elm-admins"] = "admin",
        ["elm-support"] = "support",
        ["elm-viewers"] = "viewer",
    };

    private readonly WebApplication _app;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, string> _refreshTokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CodeGrant> _codes = new(StringComparer.Ordinal);
    private ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private FakeIdp(WebApplication app, TimeProvider time)
    {
        _app = app;
        _time = time;
    }

    /// <summary>Gets the issuer: the server's URL and <see cref="Realm"/>.</summary>
    public string Issuer { get; private set; } = string.Empty;

    /// <summary>Gets or sets the signing key's ID.</summary>
    public string KeyId { get; set; } = "k1";

    /// <summary>Gets or sets whether the provider answers; when false, every request gets <c>503</c>.</summary>
    public bool Available { get; set; } = true;

    /// <summary>Gets or sets the issuer the discovery document names, or <see langword="null"/> for <see cref="Issuer"/>.</summary>
    public string? DiscoveryIssuer { get; set; }

    /// <summary>Gets or sets whether the discovery document names a revocation endpoint.</summary>
    public bool Revocation { get; set; } = true;

    /// <summary>Gets or sets the subject the device flow and refreshes sign in.</summary>
    public string Subject { get; set; } = "alice";

    /// <summary>Gets or sets the groups the device flow and refreshes put in the tokens.</summary>
    public string[] Groups { get; set; } = ["elm-owners"];

    /// <summary>Gets or sets the access tokens' lifetime.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets the token_type of the token answers.</summary>
    public string TokenType { get; set; } = "Bearer";

    /// <summary>Gets or sets whether the token answers leave out expires_in.</summary>
    public bool OmitExpiresIn { get; set; }

    /// <summary>Gets or sets the device authorization's interval in seconds.</summary>
    public int DeviceInterval { get; set; } = 1;

    /// <summary>Gets or sets the device authorization's expires_in in seconds.</summary>
    public int DeviceExpiresIn { get; set; } = 600;

    /// <summary>Gets or sets whether a refresh gives a new refresh token (rotation).</summary>
    public bool Rotate { get; set; } = true;

    /// <summary>
    /// Gets the answers the token endpoint gives to device-code polls, in order: an error code, <c>drop</c> to close the
    /// connection without an answer, or <see langword="null"/> for tokens. When empty, a poll gets tokens.
    /// </summary>
    public ConcurrentQueue<string?> DevicePolls { get; } = new();

    /// <summary>Gets the requests the provider received, as "METHOD path".</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Gets the tokens sent to the revocation endpoint.</summary>
    public ConcurrentQueue<string> Revoked { get; } = new();

    /// <summary>Gets the query strings of the authorization requests.</summary>
    public ConcurrentQueue<string> Authorizations { get; } = new();

    /// <summary>Gets or sets an error the authorization endpoint sends back instead of a code, or <see langword="null"/>.</summary>
    public string? AuthorizeError { get; set; }

    /// <summary>Gets or sets a status the token endpoint answers a code with instead of tokens, or <see langword="null"/>.</summary>
    public int? CodeTokenStatus { get; set; }

    /// <summary>Gets or sets a change made to every ID token's payload before signing.</summary>
    public Action<Dictionary<string, object?>>? IdTokenChange { get; set; }

    /// <summary>Gets or sets whether the discovery document names an end_session_endpoint.</summary>
    public bool EndSession { get; set; } = true;

    /// <summary>Starts the provider on a free loopback port.</summary>
    public static async Task<FakeIdp> StartAsync(TimeProvider time)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        WebApplication app = builder.Build();
        var idp = new FakeIdp(app, time);
        app.Run(idp.HandleAsync);
        await app.StartAsync().ConfigureAwait(false);
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        idp.Issuer = new Uri(address).GetLeftPart(UriPartial.Authority) + Realm;
        return idp;
    }

    /// <summary>Returns the web sign-in's settings for the service (M5b) with a key encryption key.</summary>
    public static WebLoginSettings WebSettings(byte[] kek) =>
        new(WebClientId, WebClientSecret, kek, TimeSpan.FromMinutes(30), TimeSpan.FromHours(12));

    /// <summary>Returns OIDC settings for the service that trust this provider.</summary>
    public OidcSettings Settings(IReadOnlyDictionary<string, string>? roleMap = null) =>
        new(Issuer, Audience, CliClientId, OidcSettings.DefaultScopes, "groups", roleMap ?? DefaultRoleMap);

    /// <summary>Forgets every refresh token, as a provider whose sessions ended.</summary>
    public void ForgetRefreshTokens() => _refreshTokens.Clear();

    /// <summary>Replaces the signing key and its ID, as a provider that rotates its keys.</summary>
    public void RotateKey(string keyId)
    {
        _key.Dispose();
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        KeyId = keyId;
    }

    /// <summary>
    /// Mints an access token signed with the provider's key. Every claim can be changed or removed by <paramref name="change"/>.
    /// </summary>
    /// <param name="subject">The <c>sub</c>.</param>
    /// <param name="groups">The <c>groups</c> claim.</param>
    /// <param name="change">Changes the header and the payload before signing.</param>
    /// <param name="key">Signs with this key instead of the provider's.</param>
    public string Token(
        string subject = "alice",
        string[]? groups = null,
        Action<Dictionary<string, object?>, Dictionary<string, object?>>? change = null,
        ECDsa? key = null
    )
    {
        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        var header = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["alg"] = "ES256",
            ["typ"] = "at+jwt",
            ["kid"] = KeyId,
        };
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["iss"] = Issuer,
            ["sub"] = subject,
            ["aud"] = Audience,
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = now + (long)Lifetime.TotalSeconds,
            ["groups"] = groups ?? ["elm-owners"],
        };
        change?.Invoke(header, payload);
        string signingInput = Encode(header) + "." + Encode(payload);
        byte[] signature = (key ?? _key).SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return signingInput + "." + Base64Url.EncodeToString(signature);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync().ConfigureAwait(false);
        _key.Dispose();
    }

    private static string Encode(Dictionary<string, object?> members) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(members, FakeIdpJson.Default.DictionaryStringObject));

    private async Task HandleAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        Requests.Enqueue(context.Request.Method + " " + path);
        if (!Available)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        switch (path)
        {
            case Realm + "/.well-known/openid-configuration":
                await JsonAsync(context, Discovery()).ConfigureAwait(false);
                break;
            case Realm + "/jwks":
                await JsonAsync(context, Jwks()).ConfigureAwait(false);
                break;
            case Realm + "/device":
                await JsonAsync(
                        context,
                        new Dictionary<string, object?>
                        {
                            ["device_code"] = "device-1",
                            ["user_code"] = "ABCD-EFGH",
                            ["verification_uri"] = Issuer + "/device",
                            ["verification_uri_complete"] = Issuer + "/device?user_code=ABCD-EFGH",
                            ["expires_in"] = DeviceExpiresIn,
                            ["interval"] = DeviceInterval,
                        }
                    )
                    .ConfigureAwait(false);
                break;
            case Realm + "/token":
                await TokenAsync(context).ConfigureAwait(false);
                break;
            case Realm + "/auth":
                Authorize(context);
                break;
            case "/v1/admin/redirect":
                // A service that redirects: elm admin request must not follow it (design §16).
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = Issuer + "/jwks";
                break;
            case Realm + "/revoke":
                IFormCollection form = await context.Request.ReadFormAsync().ConfigureAwait(false);
                Revoked.Enqueue(form["token"].ToString());
                context.Response.StatusCode = StatusCodes.Status200OK;
                break;
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                break;
        }
    }

    private async Task TokenAsync(HttpContext context)
    {
        IFormCollection form = await context.Request.ReadFormAsync().ConfigureAwait(false);
        string grant = form["grant_type"].ToString();
        if (grant == "urn:ietf:params:oauth:grant-type:device_code")
        {
            if (DevicePolls.TryDequeue(out string? error) && error == "drop")
            {
                context.Abort();
                return;
            }

            if (error is not null)
            {
                await ErrorAsync(context, error).ConfigureAwait(false);
                return;
            }

            await TokensAsync(context, null).ConfigureAwait(false);
            return;
        }

        if (grant == "authorization_code")
        {
            await CodeAsync(context, form).ConfigureAwait(false);
            return;
        }

        if (grant == "refresh_token" && _refreshTokens.TryRemove(form["refresh_token"].ToString(), out _))
        {
            await TokensAsync(context, Rotate ? null : form["refresh_token"].ToString()).ConfigureAwait(false);
            return;
        }

        await ErrorAsync(context, "invalid_grant").ConfigureAwait(false);
    }

    private Task TokensAsync(HttpContext context, string? keepRefresh)
    {
        string refresh = keepRefresh ?? "refresh-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _refreshTokens[refresh] = Subject;
        var answer = new Dictionary<string, object?> { ["access_token"] = Token(Subject, Groups), ["token_type"] = TokenType };
        if (!OmitExpiresIn)
        {
            answer["expires_in"] = (long)Lifetime.TotalSeconds;
        }

        if (keepRefresh is null)
        {
            answer["refresh_token"] = refresh;
        }

        return JsonAsync(context, answer);
    }

    // The authorization endpoint (RFC 6749 §4.1): signs in Subject at once and sends the browser back with a code and the state.
    private void Authorize(HttpContext context)
    {
        IQueryCollection query = context.Request.Query;
        Authorizations.Enqueue(context.Request.QueryString.Value ?? string.Empty);
        string redirect = query["redirect_uri"].ToString();
        string state = Uri.EscapeDataString(query["state"].ToString());
        context.Response.StatusCode = StatusCodes.Status302Found;
        if (AuthorizeError is string error)
        {
            context.Response.Headers.Location = redirect + "?error=" + Uri.EscapeDataString(error) + "&state=" + state;
            return;
        }

        string code = "code-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _codes[code] = new CodeGrant(
            query["client_id"].ToString(),
            redirect,
            query["nonce"].ToString(),
            query["code_challenge"].ToString(),
            Subject,
            Groups
        );
        context.Response.Headers.Location = redirect + "?code=" + code + "&state=" + state;
    }

    // The token endpoint's code grant: the client and its secret in the body (client_secret_post), the same redirect_uri, and
    // the PKCE verifier whose S256 is the challenge.
    private async Task CodeAsync(HttpContext context, IFormCollection form)
    {
        if (form["client_id"] != WebClientId || form["client_secret"] != WebClientSecret)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await JsonAsync(context, new Dictionary<string, object?> { ["error"] = "invalid_client" }).ConfigureAwait(false);
            return;
        }

        string verifier = form["code_verifier"].ToString();
        string challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        if (
            !_codes.TryRemove(form["code"].ToString(), out CodeGrant? grant)
            || grant.RedirectUri != form["redirect_uri"]
            || grant.Challenge != challenge
        )
        {
            await ErrorAsync(context, "invalid_grant").ConfigureAwait(false);
            return;
        }

        if (CodeTokenStatus is int status)
        {
            context.Response.StatusCode = status;
            return;
        }

        await JsonAsync(
                context,
                new Dictionary<string, object?>
                {
                    ["access_token"] = Token(grant.Subject, grant.Groups),
                    ["id_token"] = IdToken(grant.ClientId, grant.Nonce, grant.Subject, grant.Groups),
                    ["token_type"] = "Bearer",
                    ["expires_in"] = (long)Lifetime.TotalSeconds,
                }
            )
            .ConfigureAwait(false);
    }

    /// <summary>Mints an ID token for a client, with the nonce of its authorization request.</summary>
    public string IdToken(string clientId, string nonce, string subject, string[] groups)
    {
        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        var header = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["alg"] = "ES256",
            ["typ"] = "JWT",
            ["kid"] = KeyId,
        };
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["iss"] = Issuer,
            ["sub"] = subject,
            ["aud"] = clientId,
            ["iat"] = now,
            ["exp"] = now + (long)Lifetime.TotalSeconds,
            ["nonce"] = nonce,
            ["groups"] = groups,
        };
        IdTokenChange?.Invoke(payload);
        string signingInput = Encode(header) + "." + Encode(payload);
        return signingInput + "." + Base64Url.EncodeToString(_key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256));
    }

    private static Task ErrorAsync(HttpContext context, string error)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return JsonAsync(context, new Dictionary<string, object?> { ["error"] = error });
    }

    private Dictionary<string, object?> Discovery()
    {
        var document = new Dictionary<string, object?>
        {
            ["issuer"] = DiscoveryIssuer ?? Issuer,
            ["jwks_uri"] = Issuer + "/jwks",
            ["token_endpoint"] = Issuer + "/token",
            ["device_authorization_endpoint"] = Issuer + "/device",
            ["authorization_endpoint"] = Issuer + "/auth",
            ["id_token_signing_alg_values_supported"] = new[] { "ES256" },
        };
        if (EndSession)
        {
            document["end_session_endpoint"] = Issuer + "/logout";
        }

        if (Revocation)
        {
            document["revocation_endpoint"] = Issuer + "/revoke";
        }

        return document;
    }

    private Dictionary<string, object?> Jwks()
    {
        ECParameters p = _key.ExportParameters(false);
        return new Dictionary<string, object?>
        {
            ["keys"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["kty"] = "EC",
                    ["crv"] = "P-256",
                    ["kid"] = KeyId,
                    ["use"] = "sig",
                    ["alg"] = "ES256",
                    ["x"] = Base64Url.EncodeToString(p.Q.X),
                    ["y"] = Base64Url.EncodeToString(p.Q.Y),
                },
            },
        };
    }

    private static Task JsonAsync(HttpContext context, Dictionary<string, object?> body)
    {
        context.Response.ContentType = "application/json";
        return context.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(body, FakeIdpJson.Default.DictionaryStringObject)).AsTask();
    }
}

/// <summary>An authorization code waiting to be redeemed.</summary>
internal sealed record CodeGrant(string ClientId, string RedirectUri, string Nonce, string Challenge, string Subject, string[] Groups);

/// <summary>The JSON the fake provider writes.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object?>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object?>[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(long))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
[System.Text.Json.Serialization.JsonSerializable(typeof(bool))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class FakeIdpJson : System.Text.Json.Serialization.JsonSerializerContext;
