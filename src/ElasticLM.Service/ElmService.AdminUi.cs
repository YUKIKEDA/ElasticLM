using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace ElasticLM.Service;

/// <content>
/// The admin UI's files (design §15.2 "M5c の形", decision 0.100): <c>index.html</c> at <c>/</c> and Vite's hashed files under
/// <c>/assets/</c>, from the resources embedded in the executable, with the headers the design names. They are served only
/// while the web sign-in is on.
/// </content>
internal sealed partial class ElmService
{
    /// <summary>The Content-Security-Policy of the admin UI's answers (design §15.2).</summary>
    public const string AdminUiPolicy = "default-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'; object-src 'none'";

    private const string AssetsPath = "/assets";

    // The five kinds of file the UI's build makes; a file of any other kind is not served (design §15.2).
    private static readonly FileExtensionContentTypeProvider AssetTypes = MakeAssetTypes();

    private static FileExtensionContentTypeProvider MakeAssetTypes()
    {
        var types = new FileExtensionContentTypeProvider();
        types.Mappings.Clear();
        types.Mappings[".js"] = "text/javascript";
        types.Mappings[".css"] = "text/css";
        types.Mappings[".svg"] = "image/svg+xml";
        types.Mappings[".woff2"] = "font/woff2";
        types.Mappings[".png"] = "image/png";
        return types;
    }

    // After the guard and the authentication: /assets/* from the embedded dist/assets, then the 404 page for what it did not
    // answer; / gives index.html.
    private void MapAdminUi()
    {
        var files = new ManifestEmbeddedFileProvider(typeof(ElmService).Assembly, "admin");
        _app.UseStaticFiles(
            new StaticFileOptions
            {
                FileProvider = new ManifestEmbeddedFileProvider(typeof(ElmService).Assembly, "admin/assets"),
                RequestPath = AssetsPath,
                ContentTypeProvider = AssetTypes,
                ServeUnknownFileTypes = false,
                OnPrepareResponse = c =>
                {
                    SetUiHeaders(c.Context.Response);
                    c.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                },
            }
        );
        _app.Use(
            async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments(AssetsPath))
                {
                    await PageAsync(context, StatusCodes.Status404NotFound, "Not found.").ConfigureAwait(false);
                    return;
                }

                await next(context).ConfigureAwait(false);
            }
        );
        IFileInfo index = files.GetFileInfo("index.html");
        _app.MapGet("/", (RequestDelegate)(c => IndexAsync(c, index)));
    }

    // design §15.2: index.html needs no session; the screen moves to the sign-in on the API's 401.
    private static async Task IndexAsync(HttpContext context, IFileInfo index)
    {
        HttpResponse response = context.Response;
        SetUiHeaders(response);
        response.Headers.CacheControl = "no-cache";
        response.ContentType = "text/html; charset=utf-8";
        Stream body = index.CreateReadStream();
        await using (body.ConfigureAwait(false))
        {
            await body.CopyToAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static void SetUiHeaders(HttpResponse response)
    {
        response.Headers.ContentSecurityPolicy = AdminUiPolicy;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
