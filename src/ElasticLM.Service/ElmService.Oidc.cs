using System.Security.Claims;
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

    // design §14.6: the discovery URL is the issuer without a terminating "/" and the well-known suffix (OpenID Connect
    // Discovery 1.0 §4).
    internal static string DiscoveryUrl(string issuer) => issuer.TrimEnd('/') + "/.well-known/openid-configuration";

    private static void AddOidc(WebApplicationBuilder builder, OidcSettings oidc, TimeProvider time)
    {
        bool https = new Uri(oidc.Issuer).Scheme == Uri.UriSchemeHttps;

        // The identity provider's documents are input from outside: no redirects, 30 seconds, 256 KiB.
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = OidcFetchTimeout,
            MaxResponseContentBufferSize = MaxDiscoveryLength,
        };
        var manager = new ConfigurationManager<OpenIdConnectConfiguration>(
            DiscoveryUrl(oidc.Issuer),
            new CheckedDiscovery(oidc.Issuer),
            new HttpDocumentRetriever(http) { RequireHttps = https }
        )
        {
            AutomaticRefreshInterval = OidcRefresh,
            RefreshInterval = OidcKeyRetry,
        };

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
                        ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.EcdsaSha256],
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
        string? subject = principal.FindFirst("sub")?.Value;
        if (!IsIdentifier(issuer) || !IsIdentifier(subject))
        {
            ServiceLog.TokenRefused(_log, "iss or sub is missing or not 1 to 255 characters without control characters");
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

    /// <summary>
    /// Reads the discovery document and checks it (design §14.6): its <c>issuer</c> equals the configured issuer as text, and
    /// its <c>jwks_uri</c> is https (http only on a loopback host). A document that fails is treated as one that cannot be read.
    /// </summary>
    private sealed class CheckedDiscovery(string issuer) : IConfigurationRetriever<OpenIdConnectConfiguration>
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

            return configuration;
        }
    }
}
