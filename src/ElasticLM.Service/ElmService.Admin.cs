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
/// <param name="Recorded">Whether the operation already wrote its audit row in its own transaction.</param>
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
    }

    // design §14.7: every request is limited per source before authentication, devices and administrators in separate
    // buckets; an authenticated one also per API key or installation key.
    private async Task GuardAsync(HttpContext context, RequestDelegate next)
    {
        PathString path = context.Request.Path;
        bool device =
            path.StartsWithSegments("/v1/activations")
            || path.StartsWithSegments("/v1/trials")
            || path.StartsWithSegments("/v1/nonce")
            || path.StartsWithSegments("/v1/offline");
        RateLimiter<IPAddress> perIp = device ? _clientPerIp : _perIp;
        if (context.Connection.RemoteIpAddress is IPAddress address && !perIp.TryAcquire(RateLimitSource.Of(address)))
        {
            await ProblemAsync(context, ErrorCode.RateLimited, "Too many requests from this address.").ConfigureAwait(false);
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

    private async Task AdminAsync(HttpContext context, string permission, string action, Func<HttpContext, Caller, Task<AdminOutcome>> operation)
    {
        string? ip = context.Connection.RemoteIpAddress?.ToString();
        try
        {
            string header = context.Request.Headers.Authorization.ToString();
            string? presented = header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) ? header[BearerPrefix.Length..] : null;
            Caller? caller = await ApiKeys.AuthenticateAsync(Database, presented, Time.GetUtcNow(), context.RequestAborted).ConfigureAwait(false);
            if (caller is null)
            {
                CountDenial();
                await ProblemAsync(context, ErrorCode.Unauthorized, "The API key is missing, unknown, stopped, or expired.").ConfigureAwait(false);
                return;
            }

            if (!_perKey.TryAcquire(caller.Key.Id))
            {
                await ProblemAsync(context, ErrorCode.RateLimited, "Too many requests with this API key.").ConfigureAwait(false);
                return;
            }

            AdminOutcome outcome;
            if (!caller.Permissions.Contains(permission))
            {
                outcome = new AdminOutcome("ELM_FORBIDDEN", Detail: AuditLog.Detail(w => w.WriteString("permission", permission)));
                await ProblemAsync(context, ErrorCode.Forbidden, $"The API key does not have the permission {permission}.").ConfigureAwait(false);
            }
            else
            {
                outcome = await operation(context, caller).ConfigureAwait(false);
            }

            if (!outcome.Recorded)
            {
                var entry = new AuditEntry("apikey", caller.Key.Id.ToString("D"), action, outcome.Target, outcome.Result, ip, outcome.Detail);
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
                        "apikey",
                        caller.Key.Id.ToString("D"),
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
            "apikey",
            caller.Key.Id.ToString("D"),
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

        long after = 0;
        if (context.Request.Query["after"] is { Count: > 0 } afterText)
        {
            if (afterText.Count != 1 || !long.TryParse(afterText[0], NumberStyles.None, CultureInfo.InvariantCulture, out after))
            {
                return await RefuseAsync(context, ErrorCode.InvalidArgument, "after must be a row number.").ConfigureAwait(false);
            }
        }

        IReadOnlyList<AuditRow> rows = await Database.ListAuditAsync(after, limit + 1, context.RequestAborted).ConfigureAwait(false);
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
            ErrorCode.Unauthorized or ErrorCode.ProofInvalid or ErrorCode.NonceRequired or ErrorCode.Replayed => StatusCodes.Status401Unauthorized,
            ErrorCode.Forbidden or ErrorCode.LicenseSuspended or ErrorCode.LicenseRevoked or ErrorCode.Expired => StatusCodes.Status403Forbidden,
            ErrorCode.NotFound or ErrorCode.LicenseNotFound or ErrorCode.ActivationNotFound or ErrorCode.TrialNotOffered =>
                StatusCodes.Status404NotFound,
            ErrorCode.Conflict or ErrorCode.ActivationLimit or ErrorCode.DeactivationLimit or ErrorCode.TrialUsed or ErrorCode.CloneDetected =>
                StatusCodes.Status409Conflict,
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
