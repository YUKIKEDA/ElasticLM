using System.Buffers.Text;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ElasticLM.Service;

/// <summary>
/// What checking an OIDC access token found (design §14.6, M5a).
/// </summary>
/// <param name="Caller">The caller when the token is accepted.</param>
/// <param name="Forbidden">Whether the token is valid but its claims map to no role (<c>403</c>).</param>
/// <param name="Identity">The token's issuer and subject when they were read.</param>
internal sealed record OidcResult(Caller? Caller, bool Forbidden = false, AdminIdentity? Identity = null);

/// <content>
/// OIDC access tokens on the administration API (design §14.6, decision 0.98): checked by JwtBearer with every guard set
/// here, not left to the library's defaults.
/// </content>
internal sealed partial class ElmService
{
    /// <summary>The longest Bearer value read as an access token (design §14.6).</summary>
    public const int MaxAccessTokenLength = 8192;

    /// <summary>The most role values a token may hold (design §14.6).</summary>
    public const int MaxRoleValues = 100;

    private const string OidcScheme = "elm-oidc";
    private const int MaxIdentifierLength = 255;
    private const int MaxDiscoveryLength = 256 * 1024;

    private static readonly TimeSpan OidcClockSkew = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OidcRefresh = TimeSpan.FromHours(12);
    private static readonly TimeSpan OidcKeyRetry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OidcFetchTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] AccessTokenTypes = ["at+jwt", "application/at+jwt", "JWT"];

    // design §14.6, §15.3: the signature algorithms of the access token and the ID token.
    private static readonly string[] SignatureAlgorithms =
    [
        SecurityAlgorithms.RsaSha256,
        SecurityAlgorithms.RsaSsaPssSha256,
        SecurityAlgorithms.EcdsaSha256,
    ];

    // design §14.6: the discovery URL is the issuer without a terminating "/" and the well-known suffix (OpenID Connect
    // Discovery 1.0 §4).
    internal static string DiscoveryUrl(string issuer) => issuer.TrimEnd('/') + "/.well-known/openid-configuration";

    private static void AddOidc(
        WebApplicationBuilder builder,
        OidcSettings oidc,
        WebLoginSettings? web,
        Database database,
        TimeProvider time,
        WebHooks hooks
    )
    {
        bool https = new Uri(oidc.Issuer).Scheme == Uri.UriSchemeHttps;

        // The identity provider's answers are input from outside: no redirects, 30 seconds, 256 KiB. The web sign-in's token
        // requests use the same client (design §15.3), where a 5xx answer counts as an identity provider out of reach.
        var http = new HttpClient(new ServerErrorHandler(new SocketsHttpHandler { AllowAutoRedirect = false }))
        {
            Timeout = OidcFetchTimeout,
            MaxResponseContentBufferSize = MaxDiscoveryLength,
        };
        var manager = new ConfigurationManager<OpenIdConnectConfiguration>(
            DiscoveryUrl(oidc.Issuer),
            new CheckedDiscovery(oidc.Issuer, web is not null),
            new HttpDocumentRetriever(http) { RequireHttps = https }
        )
        {
            AutomaticRefreshInterval = OidcRefresh,
            RefreshInterval = OidcKeyRetry,
        };
        builder.Services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(manager);

        builder
            .Services.AddAuthentication()
            .AddJwtBearer(
                OidcScheme,
                options =>
                {
                    options.ConfigurationManager = manager;
                    options.RequireHttpsMetadata = https;
                    options.RefreshOnIssuerKeyNotFound = true;
                    options.AutomaticRefreshInterval = OidcRefresh;
                    options.RefreshInterval = OidcKeyRetry;
                    options.MapInboundClaims = false;
                    options.IncludeErrorDetails = false;
                    options.SaveToken = false;
                    options.TimeProvider = time;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = oidc.Issuer,
                        ValidateIssuer = true,
                        ValidAudience = oidc.Audience,
                        ValidateAudience = true,
                        ValidAlgorithms = SignatureAlgorithms,
                        RequireSignedTokens = true,
                        RequireExpirationTime = true,
                        ValidateLifetime = true,
                        ClockSkew = OidcClockSkew,
                        TryAllIssuerSigningKeys = true,
                        LifetimeValidator = (notBefore, expires, _, _) => IsWithinLifetime(notBefore, expires, time.GetUtcNow()),
                        TypeValidator = (type, _, _) =>
                            type is not null && AccessTokenTypes.Contains(type, StringComparer.OrdinalIgnoreCase)
                                ? type
                                : throw new SecurityTokenInvalidTypeException("The token's typ is not at+jwt or JWT."),
                    };
                }
            );
        if (web is not null)
        {
            hooks.KeyStore = new DataProtectionKeyStore(database, web.Kek, time);
            AddWebLogin(builder, oidc, web, manager, http, hooks.KeyStore, hooks, time);
        }
    }

    // design §14.6: exp is required; exp and nbf are read against the service's clock with 60 seconds of skew.
    internal static bool IsWithinLifetime(DateTime? notBefore, DateTime? expires, DateTimeOffset now) =>
        expires is DateTime end
        && now.UtcDateTime <= end.ToUniversalTime() + OidcClockSkew
        && (notBefore is not DateTime start || now.UtcDateTime >= start.ToUniversalTime() - OidcClockSkew);

    // design §14.6 steps 3 to 9.
    private async Task<OidcResult> AuthenticateOidcAsync(HttpContext context, OidcSettings oidc, string presented)
    {
        if (presented.Length > MaxAccessTokenLength)
        {
            return new OidcResult(null);
        }

        AuthenticateResult result = await context.AuthenticateAsync(OidcScheme).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            if (result.Failure is InvalidOperationException unavailable)
            {
                ServiceLog.IdentityProviderUnavailable(_log, unavailable.Message);
            }
            else
            {
                ServiceLog.TokenRefused(_log, result.Failure?.Message ?? "no token");
            }

            return new OidcResult(null);
        }

        ClaimsPrincipal principal = result.Principal;
        string? issuer = principal.FindFirst("iss")?.Value;
        if (!IsIdentifier(issuer))
        {
            ServiceLog.TokenRefused(_log, "iss is missing or not 1 to 255 characters without control characters");
            return new OidcResult(null);
        }

        // JwtBearer checked the value after "Bearer " without its surrounding white space.
        string? subject = IdentityOf(presented.Trim(), oidc.SubjectClaim);
        if (subject is null)
        {
            string reason = IdentityRefusal(oidc.SubjectClaim);
            ServiceLog.TokenRefused(_log, reason);
            return new OidcResult(null);
        }

        string[] values = [.. principal.FindAll(oidc.RoleClaim).Where(c => c.ValueType == ClaimValueTypes.String).Select(c => c.Value)];
        if (values.Length > MaxRoleValues)
        {
            ServiceLog.TokenRefused(_log, "the role claim holds more than 100 values");
            return new OidcResult(null);
        }

        var identity = new AdminIdentity(issuer!, subject!);
        AdminRow? admin = await Database.FindAdminAsync(identity, context.RequestAborted).ConfigureAwait(false);
        if (admin is { Enabled: false })
        {
            return new OidcResult(null, Identity: identity);
        }

        bool bootstrap = _settings.BootstrapOwner is AdminIdentity owner && owner.Issuer == issuer && owner.Subject == subject;
        string? role = bootstrap ? Permissions.RoleOwner : RoleOf(values, oidc.RoleMap);
        DateTimeOffset now = Time.GetUtcNow();
        string? ip = Ip(context);
        if (role is null)
        {
            if (admin is not null)
            {
                await Database.RemoveAdminRoleAsync(admin, ip, now, context.RequestAborted).ConfigureAwait(false);
            }

            return new OidcResult(null, Forbidden: true, Identity: identity) with
            {
                Caller = admin is null ? null : OidcCaller(admin with { Role = Permissions.RoleNone }),
            };
        }

        AdminRow signedIn = await Database.SignInAdminAsync(identity, role, ip, now, context.RequestAborted).ConfigureAwait(false);
        return signedIn.Enabled ? new OidcResult(OidcCaller(signedIn), Identity: identity) : new OidcResult(null, Identity: identity);
    }

    // The strongest role the values map to, or null (design §14.6).
    internal static string? RoleOf(IEnumerable<string> values, IReadOnlyDictionary<string, string> map) =>
        values.Select(v => map.TryGetValue(v, out string? role) ? role : null).OfType<string>().OrderByDescending(Roles.Rank).FirstOrDefault();

    private static Caller OidcCaller(AdminRow admin) =>
        new(admin.Id, Permissions.OfRole(admin.Role), AuditActors.Oidc, admin.Id.ToString("D"), admin.Id);

    private static bool IsIdentifier(string? value) => value is { Length: >= 1 and <= MaxIdentifierLength } && !value.Any(char.IsControl);

    // The log reason of a refused identity, naming the claim (design §20.7, V1c15).
    internal static string IdentityRefusal(string claim) =>
        $"{claim} is missing, given more than once, or not one string of 1 to 255 characters without control characters";

    /// <summary>
    /// Reads the administrator's identity from a checked token (design §14.6, §15.3): the member that <c>oidc.subjectClaim</c>
    /// names, when the payload holds it once, as a JSON string of 1 to 255 characters without control characters.
    /// </summary>
    /// <param name="token">The token in its compact form, already checked by the handler.</param>
    /// <param name="claim">The claim's name.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The payload's JSON is read here instead of the claims that the handlers make, because those claims hide the JSON type
    /// (design §20.7, V1c15 review R5). Microsoft.IdentityModel.JsonWebTokens 8.19.2, which both handlers use, shows it.
    /// JsonWebToken.PayloadClaimSet.cs (ReadPayloadValue) reads <c>sub</c> with
    /// <c>JsonSerializerPrimitives.ReadStringOrNumberAsString</c> (Microsoft.IdentityModel.Tokens, Json/JsonSerializerPrimitives.cs):
    /// a number becomes a string claim, and an array or an object makes the library refuse the token while it parses it.
    /// Any other name, such as <c>oid</c>, goes through Json/JsonClaimSet.cs (CreateClaimFromObject,
    /// CreateClaimFromJsonElement): an array becomes one claim per element, so <c>["x"]</c> gives the same string claim as
    /// <c>"x"</c>, a number gives an Integer or Double claim, and an object a Json claim. In both, a member written twice
    /// keeps only its last value. Here a number, an array of any length, an object, null, and a member written twice are all
    /// refused.
    /// </remarks>
    internal static string? IdentityOf(string token, string claim)
    {
        string[] parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            using JsonDocument payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            string? value = OnlyString(payload.RootElement, claim);
            return IsIdentifier(value) ? value : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks that a token is signed (design §15.3, §20.7 V1c16): it has three parts, the signature part is not empty, and
    /// the header holds <c>alg</c> once, as the JSON string RS256, PS256, or ES256.
    /// </summary>
    /// <param name="token">The token in its compact form, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the token is signed with one of the three algorithms.</returns>
    /// <remarks>
    /// The OpenIdConnect handler does not require a signature on the code flow's ID token (ASP.NET Core 10.0.14,
    /// OpenIdConnectHandler.cs, line 839, sets RequireSignedTokens to false). Microsoft.IdentityModel.JsonWebTokens 8.19.2
    /// takes a token as signed when anything follows its second dot (JsonWebToken.cs, IsSigned), and then checks the
    /// signature with the keys and ValidAlgorithms. So the empty signature part is what lets an unsigned token through; the
    /// part count and the algorithm are checked here too, so that this check does not rest on the library's internals.
    /// </remarks>
    internal static bool IsSigned(string? token)
    {
        string[] parts = token?.Split('.') ?? [];
        if (parts.Length != 3 || parts[2].Length == 0)
        {
            return false;
        }

        try
        {
            using JsonDocument header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0]));
            return OnlyString(header.RootElement, "alg") is string alg && SignatureAlgorithms.Contains(alg, StringComparer.Ordinal);
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    // The value of a member that a JSON object holds exactly once, as a string; null for anything else. JsonDocument lists a
    // member written twice twice, which the library's readers would collapse to the last value.
    private static string? OnlyString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int found = 0;
        string? value = null;
        foreach (JsonProperty member in element.EnumerateObject())
        {
            if (member.NameEquals(name))
            {
                found++;
                value = member.Value.ValueKind == JsonValueKind.String ? member.Value.GetString() : null;
            }
        }

        return found == 1 ? value : null;
    }

    /// <summary>
    /// Reads the discovery document and checks it (design §14.6): its <c>issuer</c> equals the configured issuer as text, and
    /// its <c>jwks_uri</c> is https (http only on a loopback host). With the web sign-in (design §15.3, M5b), the
    /// <c>authorization_endpoint</c>, the <c>token_endpoint</c> where the client's secret goes, and an
    /// <c>end_session_endpoint</c> must be so too. A document that fails is treated as one that cannot be read.
    /// </summary>
    private sealed class CheckedDiscovery(string issuer, bool web) : IConfigurationRetriever<OpenIdConnectConfiguration>
    {
        public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel)
        {
            OpenIdConnectConfiguration configuration = await OpenIdConnectConfigurationRetriever
                .GetAsync(address, retriever, cancel)
                .ConfigureAwait(false);
            if (configuration.Issuer != issuer)
            {
                throw new InvalidOperationException($"The discovery document names the issuer '{configuration.Issuer}', not '{issuer}'.");
            }

            if (!Uri.TryCreate(configuration.JwksUri, UriKind.Absolute, out Uri? jwks) || !OidcSettings.IsIssuerUrl(jwks))
            {
                throw new InvalidOperationException("The discovery document's jwks_uri is not an https URL.");
            }

            if (
                web
                && (
                    !IsEndpoint(configuration.AuthorizationEndpoint)
                    || !IsEndpoint(configuration.TokenEndpoint)
                    || (!string.IsNullOrEmpty(configuration.EndSessionEndpoint) && !IsEndpoint(configuration.EndSessionEndpoint))
                )
            )
            {
                throw new InvalidOperationException(
                    "The discovery document's authorization_endpoint, token_endpoint, or end_session_endpoint is not an https URL."
                );
            }

            return configuration;
        }

        private static bool IsEndpoint(string? text) =>
            Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            && uri.Fragment.Length == 0
            && uri.UserInfo.Length == 0;
    }
}
