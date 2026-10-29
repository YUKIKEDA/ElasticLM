using ElasticLM.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ElasticLM.Service;

/// <content>
/// The administration API of the usage log (design §14.5, §18.1, decision 0.95): this node's files, one file's lines, and a
/// signature now. A node answers for its own files; collecting the files of every node is the operator's.
/// </content>
internal sealed partial class ElmService
{
    private const string NoUsageLog = "This node writes no usage log; set usageLog.directory.";
    private const string NoUsageFile = "No usage log file of this node has this name.";

    private void MapUsageLog()
    {
        _app.MapGet(
            "/v1/admin/usage-log/files",
            (RequestDelegate)(c => AdminAsync(c, Permissions.UsageLogRead, AuditActions.UsageLogList, ListUsageFilesAsync))
        );
        _app.MapGet(
            "/v1/admin/usage-log/files/{name}",
            (RequestDelegate)(c => AdminAsync(c, Permissions.UsageLogRead, AuditActions.UsageLogGet, GetUsageFileAsync))
        );
        _app.MapPost(
            "/v1/admin/usage-log/seal",
            (RequestDelegate)(c => AdminAsync(c, Permissions.UsageLogSeal, AuditActions.UsageLogSeal, SealUsageLogAsync))
        );
    }

    private async Task<AdminOutcome> ListUsageFilesAsync(HttpContext context, Caller caller)
    {
        if (_usage is null)
        {
            return await RefuseAsync(context, ErrorCode.Conflict, NoUsageLog).ConfigureAwait(false);
        }

        if (!TryReadLimit(context, out int limit))
        {
            return await RefuseAsync(context, ErrorCode.InvalidArgument, "limit must be between 1 and 200.").ConfigureAwait(false);
        }

        string? after = null;
        if (context.Request.Query["after"] is { Count: > 0 } afterText)
        {
            if (afterText.Count != 1 || afterText[0] is not { Length: >= 1 and <= 255 })
            {
                return await RefuseAsync(context, ErrorCode.InvalidArgument, "after must be a file name.").ConfigureAwait(false);
            }

            after = afterText[0];
        }

        // Oldest first, after the name given; the names sort in time order.
        string[] names = [.. _usage.Files().Where(n => after is null || string.CompareOrdinal(n, after) > 0)];
        var items = new List<UsageLogFileItem>();
        foreach (string name in names.Take(limit))
        {
            var info = new FileInfo(Path.Combine(_usage.Directory, name));
            if (info.Exists)
            {
                items.Add(new UsageLogFileItem(name, info.Length, AdminApi.Time(new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds())));
            }
        }

        var answer = new UsageLogFiles(_settings.NodeName, [.. items], names.Length > limit ? names[limit - 1] : null);
        await WriteJsonAsync(context, StatusCodes.Status200OK, answer, ServiceJsonContext.Default.UsageLogFiles).ConfigureAwait(false);
        return new AdminOutcome("ELM_OK", _settings.NodeName);
    }

    // The name is matched against this node's files, never joined to the folder as it came (M4d2's lesson, security.mdc).
    private async Task<AdminOutcome> GetUsageFileAsync(HttpContext context, Caller caller)
    {
        if (_usage is null)
        {
            return await RefuseAsync(context, ErrorCode.Conflict, NoUsageLog).ConfigureAwait(false);
        }

        string? name = RouteText(context, "name");
        string? file = name is null ? null : _usage.Files().FirstOrDefault(f => f == name);
        if (file is null)
        {
            return await RefuseAsync(context, ErrorCode.NotFound, NoUsageFile, name).ConfigureAwait(false);
        }

        FileStream stream;
        try
        {
            stream = new FileStream(Path.Combine(_usage.Directory, file), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return await RefuseAsync(context, ErrorCode.NotFound, NoUsageFile, file).ConfigureAwait(false);
        }

        await using (stream.ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/x-ndjson";
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }

        return new AdminOutcome("ELM_OK", file);
    }

    // design §18.1: the counted refusals and a signature over the pending lines now, as the minute's timer does.
    private async Task<AdminOutcome> SealUsageLogAsync(HttpContext context, Caller caller)
    {
        if (_usage is null)
        {
            return await RefuseAsync(context, ErrorCode.Conflict, NoUsageLog).ConfigureAwait(false);
        }

        _usage.WriteDenials();
        _usage.Seal();
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return new AdminOutcome("ELM_OK", _settings.NodeName);
    }
}
