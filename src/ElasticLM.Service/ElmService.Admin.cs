using System.Buffers.Text;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ElasticLM.Service;

/// <summary>
/// Helpers of the administration API's answers (design §14.5).
/// </summary>
internal static class AdminApi
{
    /// <summary>The largest request body (design §12.1).</summary>
    public const int MaxBodyLength = 64 * 1024;

    /// <summary>Returns a UNIX time as ISO 8601 in UTC, to the second (design §14.5).</summary>
    public static string Time(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>
/// What an administration operation did, for its audit row.
/// </summary>
/// <param name="Result">The result code.</param>
/// <param name="Target">What it acted on, or <see langword="null"/>.</param>
/// <param name="Detail">The audit detail, or <see langword="null"/>.</param>
/// <param name="Recorded">Whether the operation already wrote its audit row in its own transaction, or by design writes none
/// (the success of <c>GET /v1/admin/me</c>, design §14.5); either way no row is added after it.</param>
internal sealed record AdminOutcome(string Result, string? Target = null, string? Detail = null, bool Recorded = false);

/// <content>
/// The administration API of M4a1 (design §14.5, decision 0.79): API keys and the audit log, with authentication by API key,
/// permissions (decision 0.78), rate limits (design §14.7), and an audit row for every operation (design §18.2).
/// </content>
internal sealed partial class ElmService
{
    private const string BearerPrefix = "Bearer ";

    private void MapAdmin()
    {
        _app.Use(GuardAsync);
        if (_settings.Oidc is not null)
        {
            // After the guard, so the sign-in's callback is limited per source and by method before the handler reads it.
            _app.UseAuthentication();
        }

        if (_settings.Web is not null)
        {
            MapWeb();
            MapAdminUi();
        }

        _app.MapGet("/health", (RequestDelegate)HealthAsync);
        _app.MapPost(
            "/v1/admin/api-keys",
            (RequestDelegate)(c => AdminAsync(c, Permissions.ApiKeyCreate, AuditActions.ApiKeyCreate, CreateKeyAsync))
        );
        _app.MapGet("/v1/admin/api-keys", (RequestDelegate)(c => AdminAsync(c, Permissions.ApiKeyRead, AuditActions.ApiKeyList, ListKeysAsync)));
        _app.MapDelete(
            "/v1/admin/api-keys/{id}",
            (RequestDelegate)(c => AdminAsync(c, Permissions.ApiKeyRevoke, AuditActions.ApiKeyRevoke, RevokeKeyAsync))
        );
        _app.MapGet("/v1/admin/audit", (RequestDelegate)(c => AdminAsync(c, Permissions.AuditRead, AuditActions.AuditList, ListAuditAsync)));
        _app.MapPost(
            "/v1/admin/audit/verify",
            (RequestDelegate)(c => AdminAsync(c, Permissions.AuditRead, AuditActions.AuditVerify, VerifyAuditAsync))
        );
        MapModel();
        MapLicenses();
        MapActivations();
        MapClient();
        MapOffline();
        MapLeases();
        MapUsageLog();
        MapAdmins();
    }

    // design §14.7: every request is limited per source before authentication, devices and administrators in separate
    // buckets; an authenticated one also per API key or installation key.
    private async Task GuardAsync(HttpContext context, RequestDelegate next)
    {
        PathString path = context.Request.Path;

        // design §14.5 (M5c): no answer of the administration API is kept by a cache, the rate limit's 429 included;
        // cookie-authenticated answers exist since M5b.
        if (path.StartsWithSegments("/v1/admin"))
        {
            context.Response.Headers.CacheControl = "no-store";
        }

        bool device =
            path.StartsWithSegments("/v1/activations")
            || path.StartsWithSegments("/v1/trials")
            || path.StartsWithSegments("/v1/nonce")
            || path.StartsWithSegments("/v1/offline")
            || path.StartsWithSegments("/v1/leases");
        RateLimiter<IPAddress> perIp = device ? _clientPerIp : _perIp;
        if (context.Connection.RemoteIpAddress is IPAddress address && !perIp.TryAcquire(RateLimitSource.Of(address)))
        {
            await ProblemAsync(context, ErrorCode.RateLimited, "Too many requests from this address.").ConfigureAwait(false);
            return;
        }

        if (RefuseWebPath(context))
        {
            await PageAsync(context, StatusCodes.Status404NotFound, "Not found.").ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private async Task HealthAsync(HttpContext context)
    {
        try
        {
            await Database.PingAsync(context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status200OK;
        }
        catch (DbException e)
        {
            ServiceLog.RequestFailed(_log, e);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
    }

    // A null permission lets every authenticated caller in (GET /v1/admin/me, M5c).
    private async Task AdminAsync(HttpContext context, string? permission, string action, Func<HttpContext, Caller, Task<AdminOutcome>> operation)
    {
        string? ip = context.Connection.RemoteIpAddress?.ToString();
        try
        {
            string header = context.Request.Headers.Authorization.ToString();
            string? presented = header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) ? header[BearerPrefix.Length..] : null;
            Caller? caller;
            bool token = presented is not null && !presented.StartsWith(ApiKeys.Prefix, StringComparison.Ordinal) && _settings.Oidc is not null;

            // design §15.3: without an Authorization header, a session cookie of the admin UI, when the web sign-in is on.
            bool session = header.Length == 0 && _settings.Web is not null && SessionValue(context.Request, out _) != SessionCookieState.None;

            // design §14.6: a value that starts with elm_ is an API key; any other is an OIDC access token when OIDC is set.
            if (token)
            {
                OidcResult result = await AuthenticateOidcAsync(context, _settings.Oidc!, presented!).ConfigureAwait(false);
                if (result.Forbidden)
                {
                    CountDenial();
                    string detail = AuditLog.Detail(w =>
                    {
                        w.WriteString("issuer", result.Identity!.Issuer);
                        w.WriteString("subject", result.Identity.Subject);
                    });
                    await ProblemAsync(context, ErrorCode.Forbidden, "The access token's claims map to no role.").ConfigureAwait(false);
                    var refused = new AuditEntry(AuditActors.Oidc, result.Caller?.ActorId, action, null, "ELM_FORBIDDEN", ip, detail);
                    await Database.AppendAuditAsync(refused, Time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                caller = result.Caller;
            }
            else if (session)
            {
                (Caller? found, bool csrf) = await AuthenticateSessionAsync(context).ConfigureAwait(false);
                if (csrf)
                {
                    await ProblemAsync(context, ErrorCode.Forbidden, "The request lacks the CSRF header or comes from another site.")
                        .ConfigureAwait(false);
                    string detail = AuditLog.Detail(w => w.WriteString("reason", "csrf"));
                    var refused = new AuditEntry(found!.ActorType, found.ActorId, action, null, "ELM_FORBIDDEN", ip, detail);
                    await Database.AppendAuditAsync(refused, Time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                caller = found;
            }
            else
            {
                // design §14.6: the 90-day rule holds only when administrators can sign in with OIDC.
                caller = await ApiKeys
                    .AuthenticateAsync(
                        Database,
                        presented,
                        Time.GetUtcNow(),
                        _settings.Oidc is null ? 0 : _settings.InactiveAdminDays,
                        (key, admin) => ServiceLog.KeyInactive(_log, key, admin),
                        context.RequestAborted
                    )
                    .ConfigureAwait(false);
            }

            if (caller is null)
            {
                CountDenial();
                string why =
                    token ? "The access token is not valid, or its administrator is disabled."
                    : session ? "The session is missing, ended, or its administrator is disabled."
                    : "The API key is missing, unknown, stopped, or expired.";
                await ProblemAsync(context, ErrorCode.Unauthorized, why).ConfigureAwait(false);
                return;
            }

            if (!_perKey.TryAcquire(caller.RateKey))
            {
                string why = token || session ? "Too many requests from this administrator." : "Too many requests with this API key.";
                await ProblemAsync(context, ErrorCode.RateLimited, why).ConfigureAwait(false);
                return;
            }

            AdminOutcome outcome;
            if (permission is not null && !caller.Permissions.Contains(permission))
            {
                outcome = new AdminOutcome("ELM_FORBIDDEN", Detail: AuditLog.Detail(w => w.WriteString("permission", permission)));
                string who = token || session ? "The administrator's role" : "The API key";
                await ProblemAsync(context, ErrorCode.Forbidden, $"{who} does not have the permission {permission}.").ConfigureAwait(false);
            }
            else
            {
                outcome = await operation(context, caller).ConfigureAwait(false);
            }

            if (!outcome.Recorded)
            {
                var entry = new AuditEntry(caller.ActorType, caller.ActorId, action, outcome.Target, outcome.Result, ip, outcome.Detail);
                await Database.AppendAuditAsync(entry, Time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (DbException e)
        {
            ServiceLog.RequestFailed(_log, e);
            if (!context.Response.HasStarted)
            {
                await ProblemAsync(context, ErrorCode.InternalError, "The database failed.").ConfigureAwait(false);
            }
        }
    }

    private async Task<AdminOutcome> CreateKeyAsync(HttpContext context, Caller caller)
    {
        if (await ReadBodyAsync(context).ConfigureAwait(false) is not byte[] body)
        {
            return await RefuseAsync(context, ErrorCode.PayloadTooLarge, "The request body is larger than 64 KiB.").ConfigureAwait(false);
        }

        CreateApiKeyBody? request;
        try
        {
            request = JsonSerializer.Deserialize(body, ServiceJsonContext.Default.CreateApiKeyBody);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null || request.Permissions.Contains(null!))
        {
            return await RefuseAsync(context, ErrorCode.Malformed, "The request body is not a valid API key request.").ConfigureAwait(false);
        }

        int nameLength = request.Name.EnumerateRunes().Count();
        if (nameLength is < 1 or > ApiKeys.MaxNameLength)
        {
            return await RefuseAsync(context, ErrorCode.InvalidArgument, "name must be 1 to 64 characters.").ConfigureAwait(false);
        }

        if (request.Permissions.Count is < 1 or > ApiKeys.MaxPermissions)
        {
            return await RefuseAsync(context, ErrorCode.InvalidArgument, "permissions must hold 1 to 32 permissions.").ConfigureAwait(false);
        }

        if (request.Permissions.FirstOrDefault(p => !Permissions.IsKnown(p)) is string unknown)
        {
            return await RefuseAsync(context, ErrorCode.InvalidArgument, $"'{unknown}' is not a permission.").ConfigureAwait(false);
        }

        int days = request.ExpiresInDays ?? ApiKeys.DefaultDays;
        if (days is < 1 or > ApiKeys.MaxDays)
        {
            return await RefuseAsync(context, ErrorCode.InvalidArgument, "expiresInDays must be between 1 and 365.").ConfigureAwait(false);
        }

        if (request.Permissions.FirstOrDefault(p => !caller.Permissions.Contains(p)) is string beyond)
        {
            return await RefuseAsync(context, ErrorCode.Forbidden, $"A key cannot be given the permission {beyond}, which its creator does not have.")
                .ConfigureAwait(false);
        }

        DateTimeOffset now = Time.GetUtcNow();
        string key = ApiKeys.NewKey();
        string? detail = null;
        ApiKeyRecord record = await Database
            .CreateKeyAsync(
                caller.AdminId,
                new NewApiKey(key, request.Name, request.Permissions, days),
                created =>
                {
                    detail = AuditLog.KeyCreatedDetail(created, null);
                    return new AuditEntry(
                        caller.ActorType,
                        caller.ActorId,
                        AuditActions.ApiKeyCreate,
                        created.Id.ToString("D"),
                        "ELM_OK",
                        context.Connection.RemoteIpAddress?.ToString(),
                        detail
                    );
                },
                now,
                context.RequestAborted
            )
            .ConfigureAwait(false);

        ApiKeyItem item = ToItem(record) with { Key = key };
        await WriteJsonAsync(context, StatusCodes.Status201Created, item, ServiceJsonContext.Default.ApiKeyItem).ConfigureAwait(false);
        return new AdminOutcome("ELM_OK", record.Id.ToString("D"), detail, Recorded: true);
    }

    private async Task<AdminOutcome> ListKeysAsync(HttpContext context, Caller caller)
    {
        if (!TryReadLimit(context, out int limit))
        {
            return await RefuseAsync(context, ErrorCode.InvalidArgument, "limit must be between 1 and 200.").ConfigureAwait(false);
        }

        Guid? after = null;
        if (context.Request.Query["after"] is { Count: > 0 } afterText)
        {
            if (afterText.Count != 1 || !Guid.TryParseExact(afterText[0], "D", out Guid parsed))
            {
                return await RefuseAsync(context, ErrorCode.InvalidArgument, "after must be an API key ID.").ConfigureAwait(false);
            }

            after = parsed;
        }

        IReadOnlyList<ApiKeyRecord> keys = await Database.ListKeysAsync(after, limit + 1, context.RequestAborted).ConfigureAwait(false);
        ApiKeyRecord[] page = [.. keys.Take(limit)];
        string? next = keys.Count > limit ? page[^1].Id.ToString("D") : null;
        var answer = new ApiKeyPage([.. page.Select(ToItem)], next);
        await WriteJsonAsync(context, StatusCodes.Status200OK, answer, ServiceJsonContext.Default.ApiKeyPage).ConfigureAwait(false);
        return new AdminOutcome("ELM_OK");
    }

    private async Task<AdminOutcome> RevokeKeyAsync(HttpContext context, Caller caller)
    {
        string text = context.Request.RouteValues["id"] as string ?? string.Empty;
        if (!Guid.TryParseExact(text, "D", out Guid id))
        {
            return await RefuseAsync(context, ErrorCode.NotFound, "No API key has this ID.").ConfigureAwait(false);
        }

        DateTimeOffset now = Time.GetUtcNow();
        var entry = new AuditEntry(
            caller.ActorType,
            caller.ActorId,
            AuditActions.ApiKeyRevoke,
            id.ToString("D"),
            "ELM_OK",
            context.Connection.RemoteIpAddress?.ToString(),
            null
        );
        if (!await Database.RevokeKeyAsync(id, entry, now, context.RequestAborted).ConfigureAwait(false))
        {
            return await RefuseAsync(context, ErrorCode.NotFound, "No API key has this ID.", id.ToString("D")).ConfigureAwait(false);
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return new AdminOutcome("ELM_OK", id.ToString("D"), Recorded: true);
    }

    private async Task<AdminOutcome> ListAuditAsync(HttpContext context, Caller caller)
    {
        if (!TryReadLimit(context, out int limit))
        {
            return await RefuseAsync(context, ErrorCode.InvalidArgument, "limit must be between 1 and 200.").ConfigureAwait(false);
        }

        long? after = null;
        if (context.Request.Query["after"] is { Count: > 0 } afterText)
        {
            if (afterText.Count != 1 || !long.TryParse(afterText[0], NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
            {
                return await RefuseAsync(context, ErrorCode.InvalidArgument, "after must be a row number.").ConfigureAwait(false);
            }

            after = parsed;
        }

        // design §14.5 (M5d1): asc (the default) or desc; with desc, after gives the rows below it, newest first.
        bool descending = false;
        if (context.Request.Query["order"] is { Count: > 0 } orderText)
        {
            if (orderText.Count != 1 || orderText[0] is not ("asc" or "desc"))
            {
                return await RefuseAsync(context, ErrorCode.InvalidArgument, "order must be asc or desc.").ConfigureAwait(false);
            }

            descending = orderText[0] == "desc";
        }

        IReadOnlyList<AuditRow> rows = await Database.ListAuditAsync(after, descending, limit + 1, context.RequestAborted).ConfigureAwait(false);
        AuditRow[] page = [.. rows.Take(limit)];
        long? next = rows.Count > limit ? page[^1].N : null;
        var answer = new AuditPage([.. page.Select(ToItem)], next);
        await WriteJsonAsync(context, StatusCodes.Status200OK, answer, ServiceJsonContext.Default.AuditPage).ConfigureAwait(false);
        return new AdminOutcome("ELM_OK");
    }

    private async Task<AdminOutcome> VerifyAuditAsync(HttpContext context, Caller caller)
    {
        AuditCheck check = await Database.VerifyAuditAsync(context.RequestAborted).ConfigureAwait(false);
        var answer = new VerifyAnswer(check.FirstBroken is null, check.Checked, check.FirstBroken);
        await WriteJsonAsync(context, StatusCodes.Status200OK, answer, ServiceJsonContext.Default.VerifyAnswer).ConfigureAwait(false);
        string detail = AuditLog.Detail(w =>
        {
            w.WriteBoolean("intact", answer.Intact);
            w.WriteNumber("checked", answer.Checked);
        });
        return new AdminOutcome("ELM_OK", Detail: detail);
    }

    private static ApiKeyItem ToItem(ApiKeyRecord record) =>
        new(
            record.Id.ToString("D"),
            record.Name,
            record.Prefix,
            record.Permissions,
            record.AdminId.ToString("D"),
            AdminApi.Time(record.CreatedAt),
            AdminApi.Time(record.ExpiresAt),
            record.RevokedAt is long revoked ? AdminApi.Time(revoked) : null
        );

    private static AuditItem ToItem(AuditRow row) =>
        new(
            row.N,
            AdminApi.Time(row.Ts),
            row.Entry.ActorType,
            row.Entry.ActorId,
            row.Entry.Action,
            row.Entry.Target,
            row.Entry.Result,
            row.Entry.Ip,
            row.Entry.Detail,
            Base64Url.EncodeToString(row.Prev),
            Base64Url.EncodeToString(row.Hash)
        );

    private static bool TryReadLimit(HttpContext context, out int limit)
    {
        limit = AuditLog.DefaultLimit;
        if (context.Request.Query["limit"] is not { Count: > 0 } text)
        {
            return true;
        }

        return text.Count == 1
            && int.TryParse(text[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit)
            && limit is >= 1 and <= AuditLog.MaxLimit;
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength > AdminApi.MaxBodyLength)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > AdminApi.MaxBodyLength)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task<AdminOutcome> RefuseAsync(HttpContext context, ErrorCode code, string message, string? target = null)
    {
        await ProblemAsync(context, code, message).ConfigureAwait(false);
        return new AdminOutcome(ErrorCodes.ToWireName(code), target);
    }

    private static Task ProblemAsync(HttpContext context, ErrorCode code, string message, int? status = null) =>
        WriteProblemAsync(context, code, message, status ?? StatusOf(code));

    private static int StatusOf(ErrorCode code) =>
        code switch
        {
            ErrorCode.InvalidArgument
            or ErrorCode.Malformed
            or ErrorCode.FingerprintUnavailable
            or ErrorCode.WrongProduct
            or ErrorCode.RequestExpired => StatusCodes.Status400BadRequest,
            ErrorCode.FeatureNotLicensed or ErrorCode.FeatureVersionNotLicensed or ErrorCode.FeatureExpired or ErrorCode.BorrowNotAllowed =>
                StatusCodes.Status403Forbidden,
            ErrorCode.Unauthorized or ErrorCode.ProofInvalid or ErrorCode.NonceRequired or ErrorCode.Replayed => StatusCodes.Status401Unauthorized,
            ErrorCode.Forbidden or ErrorCode.LicenseSuspended or ErrorCode.LicenseRevoked or ErrorCode.Expired => StatusCodes.Status403Forbidden,
            ErrorCode.NotFound or ErrorCode.LicenseNotFound or ErrorCode.ActivationNotFound or ErrorCode.TrialNotOffered or ErrorCode.UnknownLease =>
                StatusCodes.Status404NotFound,
            ErrorCode.Conflict
            or ErrorCode.ActivationLimit
            or ErrorCode.DeactivationLimit
            or ErrorCode.TrialUsed
            or ErrorCode.CloneDetected
            or ErrorCode.NoSeats
            or ErrorCode.ReturnNotAllowed => StatusCodes.Status409Conflict,
            ErrorCode.PayloadTooLarge => StatusCodes.Status413PayloadTooLarge,
            ErrorCode.RateLimited => StatusCodes.Status429TooManyRequests,
            ErrorCode.ServerBusy => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };

    private static Task WriteProblemAsync(HttpContext context, ErrorCode code, string message, int status)
    {
        if (code == ErrorCode.RateLimited)
        {
            context.Response.Headers.RetryAfter = "1";
        }

        string wire = ErrorCodes.ToWireName(code);
        return WriteJsonAsync(
            context,
            status,
            new Problem("about:blank", wire, status, message, wire),
            ServiceJsonContext.Default.Problem,
            "application/problem+json"
        );
    }

    private static Task WriteJsonAsync<T>(HttpContext context, int status, T value, JsonTypeInfo<T> type, string contentType = "application/json")
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        return context.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value, type), context.RequestAborted).AsTask();
    }
}
