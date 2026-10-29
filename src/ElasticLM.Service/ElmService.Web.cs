using System.Buffers.Text;
using System.Data.Common;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ElasticLM.Licensing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ElasticLM.Service;

/// <content>
/// The admin UI's sign-in through the service (design §15.3, decision 0.99, M5b): the OpenIdConnect handler runs the
/// authorization code flow with PKCE and checks the ID token; the service makes its own session in the database and the
/// cookie <c>__Host-elm-session</c>, checks that cookie and the CSRF header on the administration API, and keeps the Data
/// Protection keys of the sign-in state.
/// </content>
internal sealed partial class ElmService
{
    /// <summary>The session cookie's name (design §15.3).</summary>
    public const string SessionCookie = "__Host-elm-session";

    /// <summary>The CSRF header (design §15.3).</summary>
    public const string CsrfHeader = "X-ELM-CSRF";

    /// <summary>The most live sessions of one administrator (design §15.3).</summary>
    public const int MaxSessionsPerAdmin = 50;

    private const string WebScheme = "elm-web";
    private const string LoginPath = "/auth/login";
    private const string CallbackPath = "/auth/callback";
    private const string LogoutPath = "/auth/logout";
    private const int SessionValueLength = 43;

    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LastSeenInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SessionSweepInterval = TimeSpan.FromHours(1);

    // design §15.3: the UI is served at the first public URL, or the listen URL when there is none.
    private Uri PublicUrl =>
        _publicUrls.Count > 0 ? _publicUrls[0]
        : _settings.PublicUrls.Count > 0 ? _settings.PublicUrls[0]
        : Url;

    private string CallbackUrl => new Uri(PublicUrl, CallbackPath).ToString();

    /// <summary>
    /// Adds the OpenIdConnect handler and Data Protection of the admin UI's sign-in (design §15.3), with every option set here
    /// rather than left to the library's defaults.
    /// </summary>
    private static void AddWebLogin(
        WebApplicationBuilder builder,
        OidcSettings oidc,
        WebLoginSettings web,
        IConfigurationManager<OpenIdConnectConfiguration> manager,
        HttpClient backchannel,
        DataProtectionKeyStore keyStore,
        WebHooks hooks,
        TimeProvider time
    )
    {
        bool https = new Uri(oidc.Issuer).Scheme == Uri.UriSchemeHttps;
        builder
            .Services.AddDataProtection()
            .SetApplicationName("ElasticLM.Service")
            .SetDefaultKeyLifetime(DataProtectionKeyStore.KeyLifetime)
            .DisableAutomaticKeyGeneration();
        builder.Services.Configure<KeyManagementOptions>(options =>
        {
            // The store encrypts with the key encryption key itself (AES-256-GCM); Data Protection's own encryptor is not used.
            options.XmlRepository = keyStore;
            options.XmlEncryptor = null;
        });

        builder
            .Services.AddAuthentication()
            .AddOpenIdConnect(
                WebScheme,
                options =>
                {
                    options.ClientId = web.ClientId;
                    options.ClientSecret = web.ClientSecret;
                    options.ConfigurationManager = manager;
                    options.RequireHttpsMetadata = https;
                    options.RefreshOnIssuerKeyNotFound = true;
                    options.Backchannel = backchannel;
                    options.ResponseType = OpenIdConnectResponseType.Code;
                    options.ResponseMode = OpenIdConnectResponseMode.Query;
                    options.UsePkce = true;
                    options.Scope.Clear();
                    options.Scope.Add("openid");
                    options.MapInboundClaims = false;

                    // The default claim actions remove iss, aud, azp, and others after the token is checked; the sign-in reads iss.
                    options.ClaimActions.Clear();
                    options.SaveTokens = false;
                    options.GetClaimsFromUserInfoEndpoint = false;
                    options.UseTokenLifetime = false;
                    options.DisableTelemetry = true;
                    options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
                    options.SkipUnrecognizedRequests = false;
                    options.TimeProvider = time;
                    options.CallbackPath = CallbackPath;
                    options.RemoteAuthenticationTimeout = SignInTimeout;

                    // The handler's sign-out endpoints serve a mechanism the service does not have; empty paths turn them off.
                    options.RemoteSignOutPath = PathString.Empty;
                    options.SignedOutCallbackPath = PathString.Empty;
                    SetLoginCookie(options.CorrelationCookie, "__Host-elm-correlation.");
                    SetLoginCookie(options.NonceCookie, "__Host-elm-nonce.");
                    options.ProtocolValidator = new OpenIdConnectProtocolValidator
                    {
                        RequireNonce = true,
                        RequireTimeStampInNonce = true,
                        NonceLifetime = SignInTimeout,

                        // The handler opens the state itself and checks it against the correlation cookie; it gives the
                        // validator no state, so the validator's own state check would refuse every callback.
                        RequireStateValidation = false,
                    };
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = oidc.Issuer,
                        ValidateIssuer = true,
                        ValidAudience = web.ClientId,
                        ValidateAudience = true,
                        ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.EcdsaSha256],
                        RequireSignedTokens = true,
                        RequireExpirationTime = true,
                        ValidateLifetime = true,
                        ClockSkew = OidcClockSkew,
                        TryAllIssuerSigningKeys = true,
                        LifetimeValidator = (notBefore, expires, _, _) => IsWithinLifetime(notBefore, expires, time.GetUtcNow()),
                    };
                    options.Events = new OpenIdConnectEvents
                    {
                        OnRedirectToIdentityProvider = c => hooks.Service!.OnRedirectToIdentityProvider(c),
                        OnAuthorizationCodeReceived = c => hooks.Service!.OnAuthorizationCodeReceived(c),
                        OnTokenValidated = c => hooks.Service!.OnTokenValidated(c),
                        OnTicketReceived = c => hooks.Service!.OnTicketReceivedAsync(c),
                        OnRemoteFailure = c => hooks.Service!.OnRemoteFailureAsync(c),
                        OnAccessDenied = c => hooks.Service!.OnAccessDeniedAsync(c),
                    };
                }
            );
    }

    // design §15.3: the sign-in's cookies are __Host- cookies, Lax, for the whole site, as long as the sign-in may take.
    private static void SetLoginCookie(CookieBuilder cookie, string name)
    {
        cookie.Name = name;
        cookie.Path = "/";
        cookie.HttpOnly = true;
        cookie.SecurePolicy = CookieSecurePolicy.Always;
        cookie.SameSite = SameSiteMode.Lax;
        cookie.MaxAge = SignInTimeout;
    }

    private void MapWeb()
    {
        _app.MapGet(LoginPath, (RequestDelegate)LoginAsync);
        _app.MapPost(LogoutPath, (RequestDelegate)LogoutAsync);
        _app.MapGet("/", (RequestDelegate)(c => PageAsync(c, StatusCodes.Status200OK, "The admin UI is not built yet.")));
    }

    // design §15.3: the sign-in's paths answer only their method, and nothing at all while the web sign-in is off; this runs
    // before the OpenIdConnect handler, which would read a POST body at the callback path.
    private bool RefuseWebPath(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        string? method = path switch
        {
            LoginPath or CallbackPath or "/" => HttpMethods.Get,
            LogoutPath => HttpMethods.Post,
            _ => null,
        };
        return method is not null && (_settings.Web is null || !string.Equals(context.Request.Method, method, StringComparison.Ordinal));
    }

    private async Task LoginAsync(HttpContext context)
    {
        try
        {
            await context.ChallengeAsync(WebScheme, new AuthenticationProperties { RedirectUri = "/" }).ConfigureAwait(false);
        }
        catch (Exception e) when (IsUnreachable(e, context))
        {
            ServiceLog.SignInUnavailable(_log, e.Message);
            await PageAsync(context, StatusCodes.Status503ServiceUnavailable, "The identity provider cannot be reached.").ConfigureAwait(false);
        }
    }

    private Task OnRedirectToIdentityProvider(RedirectContext context)
    {
        // design §15.3: the redirect_uri comes from the public URL, not the request's host; the start time goes into the state.
        context.ProtocolMessage.RedirectUri = CallbackUrl;
        context.Properties.IssuedUtc = Time.GetUtcNow();
        return Task.CompletedTask;
    }

    private Task OnAuthorizationCodeReceived(AuthorizationCodeReceivedContext context)
    {
        // design §15.3 callback step 2: the 15 minutes on the service's clock, before the code is redeemed.
        DateTimeOffset now = Time.GetUtcNow();
        if (context.Properties?.IssuedUtc is not DateTimeOffset issued || now - issued > SignInTimeout || issued - now > OidcClockSkew)
        {
            context.Fail("The sign-in did not come back within 15 minutes.");
        }

        return Task.CompletedTask;
    }

    private Task OnTokenValidated(TokenValidatedContext context)
    {
        ClaimsPrincipal? principal = context.Principal;
        string[] audiences = [.. principal?.FindAll("aud").Select(c => c.Value) ?? []];
        string? azp = principal?.FindFirst("azp")?.Value;
        string clientId = _settings.Web!.ClientId;

        // OpenID Connect Core 1.0 §3.1.3.7: the handler only logs several audiences without azp; the service refuses.
        if ((azp is not null && azp != clientId) || (audiences.Length > 1 && azp is null))
        {
            context.Fail("The ID token's azp does not name the web client.");
        }
        else if (!IsIdentifier(principal?.FindFirst("iss")?.Value) || !IsIdentifier(principal?.FindFirst("sub")?.Value))
        {
            context.Fail("The ID token's iss or sub is missing or not 1 to 255 characters without control characters.");
        }
        else if (principal!.FindAll(_settings.Oidc!.RoleClaim).Count() > MaxRoleValues)
        {
            context.Fail("The ID token's role claim holds more than 100 values.");
        }

        return Task.CompletedTask;
    }

    // design §15.3 callback steps 4 to 6: the steps 5 to 9 of §14.6 with the ID token, then the session.
    private async Task OnTicketReceivedAsync(TicketReceivedContext context)
    {
        context.HandleResponse();
        HttpContext http = context.HttpContext;
        try
        {
            ClaimsPrincipal principal = context.Principal!;
            OidcSettings oidc = _settings.Oidc!;
            var identity = new AdminIdentity(principal.FindFirst("iss")!.Value, principal.FindFirst("sub")!.Value);
            string[] values = [.. principal.FindAll(oidc.RoleClaim).Where(c => c.ValueType == ClaimValueTypes.String).Select(c => c.Value)];
            string? ip = Ip(http);
            AdminRow? admin = await Database.FindAdminAsync(identity, http.RequestAborted).ConfigureAwait(false);
            if (admin is { Enabled: false })
            {
                await RefuseSignInAsync(http, "the administrator is disabled").ConfigureAwait(false);
                return;
            }

            bool bootstrap = _settings.BootstrapOwner is AdminIdentity owner && owner.Issuer == identity.Issuer && owner.Subject == identity.Subject;
            string? role = bootstrap ? Permissions.RoleOwner : RoleOf(values, oidc.RoleMap);
            DateTimeOffset now = Time.GetUtcNow();
            if (role is null)
            {
                if (admin is not null)
                {
                    await Database.RemoveAdminRoleAsync(admin, ip, now, http.RequestAborted).ConfigureAwait(false);
                }

                await RefuseSignInAsync(http, "the ID token's claims map to no role").ConfigureAwait(false);
                string detail = AuditLog.Detail(w =>
                {
                    w.WriteString("issuer", identity.Issuer);
                    w.WriteString("subject", identity.Subject);
                });
                var refused = new AuditEntry(
                    AuditActors.Oidc,
                    admin?.Id.ToString("D"),
                    AuditActions.SessionCreate,
                    null,
                    "ELM_FORBIDDEN",
                    ip,
                    detail
                );
                await Database.AppendAuditAsync(refused, Time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                return;
            }

            AdminRow signedIn = await Database.SignInAdminAsync(identity, role, ip, now, http.RequestAborted).ConfigureAwait(false);
            if (!signedIn.Enabled)
            {
                await RefuseSignInAsync(http, "the administrator is disabled").ConfigureAwait(false);
                return;
            }

            string value = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
            byte[]? presented = SessionValue(http.Request, out string? sent) == SessionCookieState.One ? ApiKeys.HashOf(sent!) : null;
            string actor = signedIn.Id.ToString("D");
            WebLoginSettings web = _settings.Web!;
            await Database
                .CreateSessionAsync(
                    new NewSession(signedIn.Id, ApiKeys.HashOf(value), presented, ip, web.Lifetime, web.Idle, MaxSessionsPerAdmin),
                    created => new AuditEntry(
                        AuditActors.Oidc,
                        actor,
                        AuditActions.SessionCreate,
                        created.Id.ToString("D"),
                        "ELM_OK",
                        ip,
                        AuditLog.Detail(w =>
                        {
                            WriteIdOrNull(w, "replaced", created.Replaced);
                            WriteIdOrNull(w, "evicted", created.Evicted);
                        })
                    ),
                    Time.GetUtcNow(),
                    http.RequestAborted
                )
                .ConfigureAwait(false);
            http.Response.Cookies.Append(SessionCookie, value, SessionCookieOptions());
            http.Response.StatusCode = StatusCodes.Status302Found;
            http.Response.Headers.Location = "/";
        }
        catch (DbException e)
        {
            ServiceLog.RequestFailed(_log, e);
            await PageAsync(http, StatusCodes.Status500InternalServerError, "The sign-in failed.").ConfigureAwait(false);
        }
    }

    private async Task RefuseSignInAsync(HttpContext context, string reason)
    {
        CountDenial();
        ServiceLog.SignInRefused(_log, reason);
        await PageAsync(context, StatusCodes.Status403Forbidden, "This account may not use the admin UI.").ConfigureAwait(false);
    }

    // design §15.3: every failure of the handler comes here; one the identity provider caused is 503, any other 400.
    private async Task OnRemoteFailureAsync(RemoteFailureContext context)
    {
        context.HandleResponse();
        HttpContext http = context.HttpContext;
        if (http.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        Exception? failure = context.Failure;
        if (failure is not null && IsUnreachable(failure, http))
        {
            ServiceLog.SignInUnavailable(_log, failure.Message);
            await PageAsync(http, StatusCodes.Status503ServiceUnavailable, "The identity provider cannot be reached.").ConfigureAwait(false);
            return;
        }

        CountDenial();
        ServiceLog.SignInRefused(_log, failure?.Message ?? "the sign-in failed");
        await PageAsync(http, StatusCodes.Status400BadRequest, "The sign-in failed. Start it again.").ConfigureAwait(false);
    }

    private async Task OnAccessDeniedAsync(AccessDeniedContext context)
    {
        context.HandleResponse();
        CountDenial();
        ServiceLog.SignInRefused(_log, "the identity provider answered access_denied");
        await PageAsync(context.HttpContext, StatusCodes.Status400BadRequest, "The sign-in failed. Start it again.").ConfigureAwait(false);
    }

    // design §15.3: transport failures, timeouts that the browser did not cause, a discovery document that cannot be read,
    // and 5xx answers (turned into HttpRequestException by the back channel) mean the identity provider is out of reach.
    private static bool IsUnreachable(Exception e, HttpContext context) =>
        e is HttpRequestException or InvalidOperationException or IOException
        || (e is OperationCanceledException && !context.RequestAborted.IsCancellationRequested);

    private async Task LogoutAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!IsSameSite(context))
        {
            await PageAsync(context, StatusCodes.Status403Forbidden, "The request lacks the CSRF header or comes from another site.")
                .ConfigureAwait(false);
            return;
        }

        try
        {
            if (SessionValue(context.Request, out string? value) == SessionCookieState.One)
            {
                string? ip = Ip(context);
                await Database
                    .LogoutSessionAsync(
                        ApiKeys.HashOf(value!),
                        (session, admin) =>
                            new AuditEntry(
                                AuditActors.Oidc,
                                admin.ToString("D"),
                                AuditActions.SessionLogout,
                                session.ToString("D"),
                                "ELM_OK",
                                ip,
                                null
                            ),
                        Time.GetUtcNow(),
                        context.RequestAborted
                    )
                    .ConfigureAwait(false);
            }
        }
        catch (DbException e)
        {
            ServiceLog.RequestFailed(_log, e);
            await ProblemAsync(context, ErrorCode.InternalError, "The database failed.").ConfigureAwait(false);
            return;
        }

        context.Response.Cookies.Delete(SessionCookie, SessionCookieOptions());
        await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                new LogoutAnswer(await EndSessionAsync(context).ConfigureAwait(false)),
                ServiceJsonContext.Default.LogoutAnswer
            )
            .ConfigureAwait(false);
    }

    // design §15.3: the identity provider's end_session_endpoint with the client and the URL to come back to, or null.
    private async Task<string?> EndSessionAsync(HttpContext context)
    {
        try
        {
            IConfigurationManager<OpenIdConnectConfiguration> manager = _app.Services.GetRequiredService<
                IConfigurationManager<OpenIdConnectConfiguration>
            >();
            OpenIdConnectConfiguration configuration = await manager.GetConfigurationAsync(context.RequestAborted).ConfigureAwait(false);
            if (string.IsNullOrEmpty(configuration.EndSessionEndpoint))
            {
                return null;
            }

            string back = PublicUrl.GetLeftPart(UriPartial.Authority) + "/";
            char separator = configuration.EndSessionEndpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            return configuration.EndSessionEndpoint
                + separator
                + "client_id="
                + Uri.EscapeDataString(_settings.Web!.ClientId)
                + "&post_logout_redirect_uri="
                + Uri.EscapeDataString(back);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>What a request's session cookies are (design §15.3, check step 4).</summary>
    internal enum SessionCookieState
    {
        /// <summary>No session cookie.</summary>
        None,

        /// <summary>One well-formed session cookie.</summary>
        One,

        /// <summary>Several session cookies, or one that is not 43 base64url characters.</summary>
        Bad,
    }

    // design §15.3: the raw Cookie header is read, since HttpRequest.Cookies keeps one of several cookies of one name.
    internal static SessionCookieState SessionValue(HttpRequest request, out string? value)
    {
        value = null;
        int found = 0;
        foreach (string? header in request.Headers.Cookie)
        {
            foreach (string pair in (header ?? string.Empty).Split(';'))
            {
                string trimmed = pair.Trim();
                int equals = trimmed.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0 && trimmed.AsSpan(0, equals).Trim().SequenceEqual(SessionCookie))
                {
                    found++;
                    value = trimmed[(equals + 1)..].Trim();
                }
            }
        }

        return found == 0 ? SessionCookieState.None
            : found == 1 && value is { Length: SessionValueLength } && value.All(IsBase64Url) ? SessionCookieState.One
            : SessionCookieState.Bad;
    }

    private static bool IsBase64Url(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';

    // design §15.3: X-ELM-CSRF: 1, and an Origin, when present, of the public URL's origin; Origin: null does not match.
    private bool IsSameSite(HttpContext context)
    {
        if (context.Request.Headers[CsrfHeader] is not { Count: 1 } csrf || csrf[0] != "1")
        {
            return false;
        }

        if (context.Request.Headers.Origin is not { Count: > 0 } origin)
        {
            return true;
        }

        string expected = PublicUrl.GetLeftPart(UriPartial.Authority);
        return origin.Count == 1 && string.Equals(origin[0], expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks a request's session cookie (design §15.3, the check order's steps 4 to 8).
    /// </summary>
    /// <returns>The caller, or <see langword="null"/> with the answer written; <c>Csrf</c> tells a CSRF refusal (403).</returns>
    private async Task<(Caller? Caller, bool Csrf)> AuthenticateSessionAsync(HttpContext context)
    {
        if (SessionValue(context.Request, out string? value) != SessionCookieState.One)
        {
            return (null, false);
        }

        SessionHolder? session = await Database.FindSessionAsync(ApiKeys.HashOf(value!), context.RequestAborted).ConfigureAwait(false);
        if (session is null)
        {
            return (null, false);
        }

        WebLoginSettings web = _settings.Web!;
        long now = Time.GetUtcNow().ToUnixTimeSeconds();
        if (now >= session.ExpiresAt || now - session.LastSeenAt > (long)web.Idle.TotalSeconds || !session.Enabled)
        {
            await Database.RemoveSessionAsync(session.Id, context.RequestAborted).ConfigureAwait(false);
            return (null, false);
        }

        var caller = new Caller(session.AdminId, Permissions.OfRole(session.Role), AuditActors.Oidc, session.AdminId.ToString("D"), session.AdminId);
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !IsSameSite(context))
        {
            return (caller, true);
        }

        long interval = (long)LastSeenInterval.TotalSeconds;
        if (now - session.LastSeenAt >= interval)
        {
            await Database.TouchSessionAsync(session.Id, now, now - interval, context.RequestAborted).ConfigureAwait(false);
        }

        return (caller, false);
    }

    private static CookieOptions SessionCookieOptions() =>
        new()
        {
            Path = "/",
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
        };

    // design §15.3: the pages of the sign-in are fixed text that echoes no input.
    private static async Task PageAsync(HttpContext context, int status, string text)
    {
        HttpResponse response = context.Response;
        response.StatusCode = status;
        response.ContentType = "text/plain; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(text + "\n"), context.RequestAborted).ConfigureAwait(false);
    }

    private static void WriteIdOrNull(System.Text.Json.Utf8JsonWriter writer, string name, Guid? id)
    {
        if (id is Guid value)
        {
            writer.WriteString(name, value.ToString("D"));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    // design §15.3, §14.8: once an hour, the sessions past their end or idle time, and the next Data Protection key.
    private async Task SweepSessionsAsync()
    {
        try
        {
            long removed = await Database
                .RemoveOldSessionsAsync(Time.GetUtcNow().ToUnixTimeSeconds(), (long)_settings.Web!.Idle.TotalSeconds, CancellationToken.None)
                .ConfigureAwait(false);
            if (removed > 0)
            {
                ServiceLog.SessionsRemoved(_log, removed);
            }

            int made = await _keyStore!
                .EnsureKeyAsync(_app.Services.GetRequiredService<IKeyManager>(), atStart: false, CancellationToken.None)
                .ConfigureAwait(false);
            if (made > 0)
            {
                ServiceLog.DataProtectionKeyMade(_log, made);
            }
        }
        catch (Exception e) when (e is DbException or InvalidOperationException)
        {
            ServiceLog.PeriodicFailed(_log, e);
        }
    }

    /// <summary>Lets the handler's events, set before the service exists, reach it.</summary>
    private sealed class WebHooks
    {
        public ElmService? Service { get; set; }

        public DataProtectionKeyStore? KeyStore { get; set; }
    }

    /// <summary>
    /// Turns a 5xx answer of the identity provider into <see cref="HttpRequestException"/>, so the sign-in tells an identity
    /// provider out of reach (503) from a refused code (400) (design §15.3).
    /// </summary>
    private sealed class ServerErrorHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode >= 500)
            {
                HttpStatusCode status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"The identity provider answered {(int)status}.", null, status);
            }

            return response;
        }
    }
}
