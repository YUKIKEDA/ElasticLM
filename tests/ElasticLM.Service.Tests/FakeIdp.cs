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
/// refresh tokens, and revocation, on loopback http. Its signing key is made by the test, and it mints access tokens with any
/// claims the test asks for.
/// </summary>
internal sealed class FakeIdp : IAsyncDisposable
{
    public const string Realm = "/realms/test";
    public const string Audience = "elm-service";
    public const string CliClientId = "elm-cli";

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

    /// <summary>Gets or sets whether a refresh gives a new refresh token (rotation).</summary>
    public bool Rotate { get; set; } = true;

    /// <summary>
    /// Gets the answers the token endpoint gives to device-code polls, in order: an error code, or <see langword="null"/> for
    /// tokens. When empty, a poll gets tokens.
    /// </summary>
    public ConcurrentQueue<string?> DevicePolls { get; } = new();

    /// <summary>Gets the requests the provider received, as "METHOD path".</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Gets the tokens sent to the revocation endpoint.</summary>
    public ConcurrentQueue<string> Revoked { get; } = new();

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

    /// <summary>Returns OIDC settings for the service that trust this provider.</summary>
    public OidcSettings Settings(IReadOnlyDictionary<string, string>? roleMap = null) =>
        new(Issuer, Audience, CliClientId, OidcSettings.DefaultScopes, "groups", roleMap ?? DefaultRoleMap);

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
                            ["expires_in"] = 600,
                            ["interval"] = 1,
                        }
                    )
                    .ConfigureAwait(false);
                break;
            case Realm + "/token":
                await TokenAsync(context).ConfigureAwait(false);
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
            if (DevicePolls.TryDequeue(out string? error) && error is not null)
            {
                await ErrorAsync(context, error).ConfigureAwait(false);
                return;
            }

            await TokensAsync(context, null).ConfigureAwait(false);
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
        string refresh = keepRefresh ?? "refresh-" + Guid.NewGuid().ToString("N");
        _refreshTokens[refresh] = Subject;
        var answer = new Dictionary<string, object?>
        {
            ["access_token"] = Token(Subject, Groups),
            ["token_type"] = "Bearer",
            ["expires_in"] = (long)Lifetime.TotalSeconds,
        };
        if (keepRefresh is null)
        {
            answer["refresh_token"] = refresh;
        }

        return JsonAsync(context, answer);
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
            ["id_token_signing_alg_values_supported"] = new[] { "ES256" },
        };
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

/// <summary>The JSON the fake provider writes.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object?>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object?>[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(long))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
[System.Text.Json.Serialization.JsonSerializable(typeof(bool))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class FakeIdpJson : System.Text.Json.Serialization.JsonSerializerContext;
