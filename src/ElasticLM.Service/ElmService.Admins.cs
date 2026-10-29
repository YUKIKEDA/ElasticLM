using ElasticLM.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ElasticLM.Service;

/// <content>
/// Administrators and the OIDC settings for <c>elm admin login</c> (design §14.5, M5a).
/// </content>
internal sealed partial class ElmService
{
    private void MapAdmins()
    {
        _app.MapGet("/v1/admin/oidc", (RequestDelegate)OidcInfoAsync);
        _app.MapGet("/v1/admin/admins", (RequestDelegate)(c => AdminAsync(c, Permissions.AdminRead, AuditActions.AdminList, ListAdminsAsync)));
        _app.MapPost(
            "/v1/admin/admins/{id}/disable",
            (RequestDelegate)(c => AdminAsync(c, Permissions.AdminUpdate, AuditActions.AdminDisable, SetEnabled(false)))
        );
        _app.MapPost(
            "/v1/admin/admins/{id}/enable",
            (RequestDelegate)(c => AdminAsync(c, Permissions.AdminUpdate, AuditActions.AdminEnable, SetEnabled(true)))
        );
    }

    // design §14.5: no authentication and no audit row; it returns no secret.
    private Task OidcInfoAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return _settings.Oidc is OidcSettings oidc
            ? WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                new OidcInfo(oidc.Issuer, oidc.CliClientId, [.. oidc.CliScopes]),
                ServiceJsonContext.Default.OidcInfo
            )
            : ProblemAsync(context, ErrorCode.NotFound, "The service does not accept OIDC sign-in.");
    }

    private async Task<AdminOutcome> ListAdminsAsync(HttpContext context, Caller caller)
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
                return await RefuseAsync(context, ErrorCode.InvalidArgument, "after must be an administrator ID.").ConfigureAwait(false);
            }

            after = parsed;
        }

        IReadOnlyList<AdminRow> admins = await Database.ListAdminsAsync(after, limit + 1, context.RequestAborted).ConfigureAwait(false);
        AdminRow[] page = [.. admins.Take(limit)];
        string? next = admins.Count > limit ? page[^1].Id.ToString("D") : null;
        var answer = new AdminPage([.. page.Select(ToItem)], next);
        await WriteJsonAsync(context, StatusCodes.Status200OK, answer, ServiceJsonContext.Default.AdminPage).ConfigureAwait(false);
        return new AdminOutcome("ELM_OK");
    }

    // A method that returns the operation, so the analyzer does not read the inner lambda as a request delegate (ASP0016).
    private Func<HttpContext, Caller, Task<AdminOutcome>> SetEnabled(bool enabled) => (context, caller) => SetEnabledAsync(context, caller, enabled);

    private async Task<AdminOutcome> SetEnabledAsync(HttpContext context, Caller caller, bool enabled)
    {
        string text = context.Request.RouteValues["id"] as string ?? string.Empty;
        if (!Guid.TryParseExact(text, "D", out Guid id))
        {
            return await RefuseAsync(context, ErrorCode.NotFound, "No administrator has this ID.").ConfigureAwait(false);
        }

        string action = enabled ? AuditActions.AdminEnable : AuditActions.AdminDisable;
        string target = id.ToString("D");
        AdminChange change = await Database
            .SetAdminEnabledAsync(
                id,
                enabled,
                caller.AdminId,
                _settings.BootstrapOwner,
                unchanged => new AuditEntry(
                    caller.ActorType,
                    caller.ActorId,
                    action,
                    target,
                    "ELM_OK",
                    Ip(context),
                    unchanged ? AuditLog.Detail(w => w.WriteBoolean("unchanged", true)) : null
                ),
                Time.GetUtcNow(),
                context.RequestAborted
            )
            .ConfigureAwait(false);
        switch (change)
        {
            case AdminChange.NotFound:
                return await RefuseAsync(context, ErrorCode.NotFound, "No administrator has this ID.", target).ConfigureAwait(false);
            case AdminChange.Refused:
                return await RefuseAsync(
                        context,
                        ErrorCode.Conflict,
                        "The caller, the bootstrap owner, and the last enabled owner cannot be disabled.",
                        target
                    )
                    .ConfigureAwait(false);
            default:
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return new AdminOutcome("ELM_OK", target, Recorded: true);
        }
    }

    private static AdminItem ToItem(AdminRow row) =>
        new(
            row.Id.ToString("D"),
            row.Issuer,
            row.Subject,
            row.Role,
            row.Enabled,
            AdminApi.Time(row.CreatedAt),
            row.LastLoginAt is long last ? AdminApi.Time(last) : null
        );
}
