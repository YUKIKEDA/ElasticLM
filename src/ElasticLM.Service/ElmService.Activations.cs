using ElasticLM.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ElasticLM.Service;

/// <content>
/// The administration API of M4b1 (design §14.5): activations and trials. An administrator's deactivation does not count
/// against <c>DeactivationLimit</c> (Round 1 of the M4b Grill).
/// </content>
internal sealed partial class ElmService
{
    private const string NoTrial = "No trial has this ID.";

    private void MapActivations()
    {
        void Map(string method, string path, string permission, string action, Func<HttpContext, Caller, Task<AdminOutcome>> operation) =>
            _app.MapMethods(path, [method], (RequestDelegate)(c => AdminAsync(c, permission, action, operation)));

        Map("GET", "/v1/admin/licenses/{id}/activations", Permissions.ActivationRead, AuditActions.ActivationList, ListActivationsAsync);
        Map("GET", "/v1/admin/activations/{id}", Permissions.ActivationRead, AuditActions.ActivationGet, GetActivationAsync);
        Map(
            "POST",
            "/v1/admin/activations/{id}/deactivate",
            Permissions.ActivationDeactivate,
            AuditActions.ActivationDeactivate,
            AdminDeactivateAsync
        );
        Map("GET", "/v1/admin/trials", Permissions.TrialRead, AuditActions.TrialList, ListTrialsAsync);
        Map("DELETE", "/v1/admin/trials/{id}", Permissions.TrialDelete, AuditActions.TrialDelete, DeleteTrialAsync);
    }

    private async Task<AdminOutcome> ListActivationsAsync(HttpContext context, Caller caller)
    {
        if (await FindLicenseAsync(context).ConfigureAwait(false) is not LicenseRow license)
        {
            return await RefuseAsync(context, ErrorCode.LicenseNotFound, NoLicense, RouteText(context, "id")).ConfigureAwait(false);
        }

        if (await ReadPageAsync(context).ConfigureAwait(false) is not (int limit, var after))
        {
            return new AdminOutcome(ErrorCodes.ToWireName(ErrorCode.InvalidArgument), license.Id.ToString("D"));
        }

        IReadOnlyList<ActivationRow> rows = await Database
            .ListActivationsAsync(license.Id, after, limit + 1, context.RequestAborted)
            .ConfigureAwait(false);
        ActivationRow[] page = [.. rows.Take(limit)];
        var answer = new ActivationPage([.. page.Select(ToItem)], rows.Count > limit ? page[^1].Id.ToString("D") : null);
        await WriteJsonAsync(context, StatusCodes.Status200OK, answer, ServiceJsonContext.Default.ActivationPage).ConfigureAwait(false);
        return new AdminOutcome("ELM_OK", license.Id.ToString("D"));
    }

    private async Task<AdminOutcome> GetActivationAsync(HttpContext context, Caller caller)
    {
        if (await FindActivationAsync(context).ConfigureAwait(false) is not ActivationRow activation)
        {
            return await RefuseAsync(context, ErrorCode.ActivationNotFound, "No activation has this ID.", RouteText(context, "id"))
                .ConfigureAwait(false);
        }

        await WriteJsonAsync(context, StatusCodes.Status200OK, ToItem(activation), ServiceJsonContext.Default.ActivationItem).ConfigureAwait(false);
        return new AdminOutcome("ELM_OK", activation.Id.ToString("D"));
    }

    // design §12.6: an administrator frees an installation's place without its proof, and without counting it.
    private async Task<AdminOutcome> AdminDeactivateAsync(HttpContext context, Caller caller)
    {
        const string None = "No activation has this ID.";
        if (await FindActivationAsync(context).ConfigureAwait(false) is not ActivationRow found)
        {
            return await RefuseAsync(context, ErrorCode.ActivationNotFound, None, RouteText(context, "id")).ConfigureAwait(false);
        }

        DateTimeOffset now = Time.GetUtcNow();
        string detail = AuditLog.Detail(w =>
        {
            w.WriteString("licenseId", found.LicenseId.ToString("D"));
            w.WriteString("keyId", found.KeyId);
        });
        bool removed = await Database
            .WithLicenseAsync(
                found.LicenseId,
                async scope =>
                {
                    if (scope is null || await scope.FindActivationAsync(found.Id, context.RequestAborted).ConfigureAwait(false) is null)
                    {
                        return false;
                    }

                    await scope.DeleteActivationAsync(found.Id, context.RequestAborted).ConfigureAwait(false);
                    await scope
                        .AppendAuditAsync(Entry(context, caller, AuditActions.ActivationDeactivate, found.Id, detail), context.RequestAborted)
                        .ConfigureAwait(false);
                    return true;
                },
                now,
                context.RequestAborted
            )
            .ConfigureAwait(false);
        if (!removed)
        {
            return await RefuseAsync(context, ErrorCode.ActivationNotFound, None, found.Id.ToString("D")).ConfigureAwait(false);
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return new AdminOutcome("ELM_OK", found.Id.ToString("D"), detail, Recorded: true);
    }

    private async Task<AdminOutcome> ListTrialsAsync(HttpContext context, Caller caller)
    {
        Guid? productId = null;
        if (context.Request.Query["productId"] is { Count: > 0 } text)
        {
            if (text.Count != 1 || !Guid.TryParseExact(text[0], "D", out Guid parsed))
            {
                return await RefuseAsync(context, ErrorCode.InvalidArgument, "productId must be a product ID.").ConfigureAwait(false);
            }

            productId = parsed;
        }

        if (await ReadPageAsync(context).ConfigureAwait(false) is not (int limit, var after))
        {
            return new AdminOutcome(ErrorCodes.ToWireName(ErrorCode.InvalidArgument));
        }

        IReadOnlyList<TrialRecord> rows = await Database.ListTrialsAsync(productId, after, limit + 1, context.RequestAborted).ConfigureAwait(false);
        TrialRecord[] page = [.. rows.Take(limit)];
        var answer = new TrialPage(
            [
                .. page.Select(t => new TrialItem(
                    t.Trial.LicenseId.ToString("D"),
                    t.Trial.ProductId.ToString("D"),
                    [.. t.Fingerprints],
                    AdminApi.Time(t.Trial.CreatedAt)
                )),
            ],
            rows.Count > limit ? page[^1].Trial.LicenseId.ToString("D") : null
        );
        await WriteJsonAsync(context, StatusCodes.Status200OK, answer, ServiceJsonContext.Default.TrialPage).ConfigureAwait(false);
        return new AdminOutcome("ELM_OK");
    }

    // design §12.9: removes the record of a trial, so its fingerprints may have one again; the trial's license stays.
    private async Task<AdminOutcome> DeleteTrialAsync(HttpContext context, Caller caller)
    {
        if (!TryRouteId(context, "id", out Guid id))
        {
            return await RefuseAsync(context, ErrorCode.NotFound, NoTrial, RouteText(context, "id")).ConfigureAwait(false);
        }

        var entry = new AuditEntry("apikey", caller.Key.Id.ToString("D"), AuditActions.TrialDelete, id.ToString("D"), "ELM_OK", Ip(context), null);
        if (!await Database.DeleteTrialAsync(id, entry, Time.GetUtcNow(), context.RequestAborted).ConfigureAwait(false))
        {
            return await RefuseAsync(context, ErrorCode.NotFound, NoTrial, id.ToString("D")).ConfigureAwait(false);
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return new AdminOutcome("ELM_OK", id.ToString("D"), Recorded: true);
    }

    private static ActivationItem ToItem(ActivationRow a) =>
        new(
            a.Id.ToString("D"),
            a.LicenseId.ToString("D"),
            a.KeyId,
            Activations.Load(a.Fingerprints),
            a.ClientHost is string host && a.ClientUser is string user ? new ClientItem(host, user) : null,
            a.App,
            a.Seq,
            a.BlockedAt is long blocked ? AdminApi.Time(blocked) : null,
            AdminApi.Time(a.CreatedAt),
            AdminApi.Time(a.UpdatedAt)
        );
}
