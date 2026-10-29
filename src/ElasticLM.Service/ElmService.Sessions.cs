using ElasticLM.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ElasticLM.Service;

/// <content>
/// The list and revocation of the admin UI's sessions (design §14.5, decision 0.99, M5b): for owners only.
/// </content>
internal sealed partial class ElmService
{
    private void MapSessions()
    {
        _app.MapGet(
            "/v1/admin/sessions",
            (RequestDelegate)(c => AdminAsync(c, Permissions.SessionRead, AuditActions.SessionList, ListSessionsAsync))
        );
        _app.MapDelete(
            "/v1/admin/sessions/{id}",
            (RequestDelegate)(c => AdminAsync(c, Permissions.SessionRevoke, AuditActions.SessionRevoke, RevokeSessionAsync))
        );
    }

    private async Task<AdminOutcome> ListSessionsAsync(HttpContext context, Caller caller)
    {
        if (!TryReadLimit(context, out int limit))
        {
            return await RefuseAsync(context, ErrorCode.InvalidArgument, "limit must be between 1 and 200.").ConfigureAwait(false);
        }

        if (!TryReadId(context, "after", out Guid? after))
        {
            return await RefuseAsync(context, ErrorCode.InvalidArgument, "after must be a session ID.").ConfigureAwait(false);
        }

        if (!TryReadId(context, "adminId", out Guid? adminId))
        {
            return await RefuseAsync(context, ErrorCode.InvalidArgument, "adminId must be an administrator ID.").ConfigureAwait(false);
        }

        IReadOnlyList<AdminSessionRow> sessions = await Database
            .ListSessionsAsync(after, adminId, Time.GetUtcNow().ToUnixTimeSeconds(), IdleSeconds, limit + 1, context.RequestAborted)
            .ConfigureAwait(false);
        AdminSessionRow[] page = [.. sessions.Take(limit)];
        string? next = sessions.Count > limit ? page[^1].Id.ToString("D") : null;
        var answer = new SessionPage([.. page.Select(ToItem)], next);
        await WriteJsonAsync(context, StatusCodes.Status200OK, answer, ServiceJsonContext.Default.SessionPage).ConfigureAwait(false);
        string detail = AuditLog.Detail(w =>
        {
            if (adminId is Guid admin)
            {
                w.WriteString("adminId", admin.ToString("D"));
            }
            else
            {
                w.WriteNull("adminId");
            }
        });
        return new AdminOutcome("ELM_OK", Detail: detail);
    }

    private async Task<AdminOutcome> RevokeSessionAsync(HttpContext context, Caller caller)
    {
        string text = context.Request.RouteValues["id"] as string ?? string.Empty;
        if (!Guid.TryParseExact(text, "D", out Guid id))
        {
            return await RefuseAsync(context, ErrorCode.NotFound, "No live session has this ID.").ConfigureAwait(false);
        }

        string target = id.ToString("D");
        Guid? revoked = await Database
            .RevokeSessionAsync(
                id,
                IdleSeconds,
                admin => new AuditEntry(
                    caller.ActorType,
                    caller.ActorId,
                    AuditActions.SessionRevoke,
                    target,
                    "ELM_OK",
                    Ip(context),
                    AuditLog.Detail(w => w.WriteString("adminId", admin.ToString("D")))
                ),
                Time.GetUtcNow(),
                context.RequestAborted
            )
            .ConfigureAwait(false);
        if (revoked is null)
        {
            return await RefuseAsync(context, ErrorCode.NotFound, "No live session has this ID.", target).ConfigureAwait(false);
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return new AdminOutcome("ELM_OK", target, Recorded: true);
    }

    // The idle time of sessions; without the web sign-in no session is made, and the list holds what is left in the table.
    private long IdleSeconds => (long)(_settings.Web?.Idle ?? TimeSpan.FromMinutes(30)).TotalSeconds;

    private static bool TryReadId(HttpContext context, string name, out Guid? id)
    {
        id = null;
        if (context.Request.Query[name] is not { Count: > 0 } text)
        {
            return true;
        }

        if (text.Count != 1 || !Guid.TryParseExact(text[0], "D", out Guid parsed))
        {
            return false;
        }

        id = parsed;
        return true;
    }

    private static SessionItem ToItem(AdminSessionRow row) =>
        new(
            row.Id.ToString("D"),
            row.AdminId.ToString("D"),
            AdminApi.Time(row.CreatedAt),
            AdminApi.Time(row.LastSeenAt),
            AdminApi.Time(row.ExpiresAt),
            row.Ip
        );
}
