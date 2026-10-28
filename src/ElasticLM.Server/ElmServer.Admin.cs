using System.Security.Cryptography;
using System.Text;
using ElasticLM.Leasing;
using ElasticLM.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ElasticLM.Server;

/// <content>
/// The local administration API (design §13.7): status, leases, a forced release, a license reload, and signing the usage
/// log now. It answers only on the loopback http listener (<c>admin.listen</c>) and only with the administrator token; every
/// authorized operation is written to the usage log as <c>admin.action</c>, and requests without the token are counted each
/// minute as <c>admin.denied</c>.
/// </content>
internal sealed partial class ElmServer
{
    private void MapAdmin()
    {
        _app.MapGet("/admin/v1/status", (RequestDelegate)(context => AdminAsync(context, "status", null, StatusAsync)));
        _app.MapGet("/admin/v1/leases", (RequestDelegate)(context => AdminAsync(context, "leases", null, LeasesAsync)));
        _app.MapDelete(
            "/admin/v1/leases/{id}",
            (RequestDelegate)(context => AdminAsync(context, "release", context.Request.RouteValues["id"] as string, ForceReleaseAsync))
        );
        _app.MapPost("/admin/v1/license/reload", (RequestDelegate)(context => AdminAsync(context, "reload", null, ReloadAsync)));
        _app.MapPost("/admin/v1/log/seal", (RequestDelegate)(context => AdminAsync(context, "seal", null, SealAsync)));
    }

    // The bearer token is compared in constant time; a missing or wrong token is 401 ELM_UNAUTHORIZED and is counted
    // (design §13.7), not written line by line, so local requests cannot fill the disk.
    private async Task AdminAsync(HttpContext context, string action, string? target, Func<HttpContext, Task<string>> operation)
    {
        string header = context.Request.Headers.Authorization.ToString();
        byte[] presented = header.StartsWith("Bearer ", StringComparison.Ordinal) ? Encoding.ASCII.GetBytes(header["Bearer ".Length..]) : [];
        if (!CryptographicOperations.FixedTimeEquals(presented, _adminToken))
        {
            _usage.AdminDenied();
            await ProblemAsync(context, ErrorCode.Unauthorized, "The administrator token is missing or wrong.").ConfigureAwait(false);
            return;
        }

        string result = await operation(context).ConfigureAwait(false);
        WriteAdminAction(action, target, result);
    }

    private void WriteAdminAction(string action, string? target, string result)
    {
        ServerLog.AdminAction(_log, action, target, result);
        _usage.Write(
            "admin.action",
            w =>
            {
                w.WriteString("action", action);
                if (target is not null)
                {
                    w.WriteString("target", target);
                }

                w.WriteString("result", result);
            }
        );
    }

    private async Task<string> StatusAsync(HttpContext context)
    {
        VerifiedLicense license = Engine.Server.License;
        var answer = new StatusAnswer(
            _settings.Product,
            license.LicenseId,
            license.TokenId,
            license.ExpiresAt?.ToUnixTimeSeconds(),
            KeyId,
            Engine.IsRecovering(),
            Engine.RecoveryEndsAt.ToUnixTimeSeconds(),
            Engine.LeaseCount,
            [.. Engine.Usage().Select(u => new FeatureItem(u.FeatureId, u.Seats, u.Allowed, u.InUse, u.Borrowed))],
            AutoRefresh
        );
        await WriteJsonAsync(context, StatusCodes.Status200OK, answer, ServerJsonContext.Default.StatusAnswer).ConfigureAwait(false);
        return "ELM_OK";
    }

    private async Task<string> LeasesAsync(HttpContext context)
    {
        var answer = new LeasesAnswer([
            .. Engine
                .Leases()
                .Select(l => new LeaseItem(l.LeaseId, l.FeatureId, l.Seats, l.KeyId, l.ExpiresAt.ToUnixTimeSeconds(), l.IsBorrow, l.IsStopped)),
        ]);
        await WriteJsonAsync(context, StatusCodes.Status200OK, answer, ServerJsonContext.Default.LeasesAnswer).ConfigureAwait(false);
        return "ELM_OK";
    }

    private async Task<string> ForceReleaseAsync(HttpContext context)
    {
        string id = context.Request.RouteValues["id"] as string ?? string.Empty;
        LeaseInfo? released = Engine.ForceRelease(id);
        if (released is null)
        {
            await ProblemAsync(context, ErrorCode.UnknownLease, "The server does not know the lease.").ConfigureAwait(false);
            return "ELM_UNKNOWN_LEASE";
        }

        if (released.IsBorrow)
        {
            // design §12.7.4: a borrow ends only by the client's early return or at its expiry.
            await ProblemAsync(context, ErrorCode.ReturnNotAllowed, "A borrow is not released by the administrator; it ends at its expiry.")
                .ConfigureAwait(false);
            return "ELM_RETURN_NOT_ALLOWED";
        }

        _usage.Lease("lease.released", released, Engine.Server.License.LicenseId);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return "ELM_OK";
    }

    private async Task<string> ReloadAsync(HttpContext context)
    {
        LicenseError? error = ReloadLicense();
        if (error is not null)
        {
            // The old license stays; 409 whatever the reason, because the request itself was right.
            await ProblemAsync(context, error.Code, error.Message, status: StatusCodes.Status409Conflict).ConfigureAwait(false);
            return ErrorCodes.ToWireName(error.Code);
        }

        // design §13.7: after elm server activate or import, the server's client reads the kept activation again.
        await RestartActivationAsync().ConfigureAwait(false);
        VerifiedLicense license = Engine.Server.License;
        await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                new ReloadAnswer(license.LicenseId, license.TokenId),
                ServerJsonContext.Default.ReloadAnswer
            )
            .ConfigureAwait(false);
        return "ELM_OK";
    }

    private Task<string> SealAsync(HttpContext context)
    {
        _usage.Seal();
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.FromResult("ELM_OK");
    }
}
