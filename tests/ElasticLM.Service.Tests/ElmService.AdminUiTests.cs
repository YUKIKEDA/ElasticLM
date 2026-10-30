using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The admin UI's files and its API (design §14.5, §15.2 "M5c の形", decision 0.100): index.html at /, the hashed files under
/// /assets/ from the embedded dist, the headers, the refusals, GET /v1/admin/me, and no-store on the administration API.
/// </summary>
public sealed partial class ElmServiceAdminUiTests
{
    private const string NotFound = "Not found.\n";

    private static readonly byte[] Kek = RandomNumberGenerator.GetBytes(WebLoginSettings.KekLength);

    /// <summary>
    /// With the web sign-in on, / gives the embedded index.html and /assets/ its scripts and styles, each with the CSP,
    /// nosniff, no-referrer, and the cache rule the design names.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with the web sign-in; the dist of web/admin embedded.
    ///
    /// Steps:
    /// - GET /; GET the script and the style index.html names.
    ///
    /// Expected:
    /// - 200 text/html with the app element, no-cache; 200 text/javascript and text/css, public max-age 31536000 immutable;
    ///   all three with the CSP default-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'; object-src
    ///   'none', nosniff, and Referrer-Policy no-referrer; no inline script or style in index.html.
    /// </remarks>
    [Fact]
    public async Task Ui_IsServedWithItsHeaders()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        ElmService service = await StartAsync(test, idp);
        using var browser = new WebBrowser();

        using HttpResponseMessage index = await browser.SendAsync(HttpMethod.Get, service.Url);
        string html = await index.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal("text/html; charset=utf-8", index.Content.Headers.ContentType!.ToString());
        Assert.Equal("no-cache", index.Headers.CacheControl!.ToString());
        AssertUiHeaders(index);
        Assert.Contains("<div id=\"app\"></div>", html, StringComparison.Ordinal);
        Assert.DoesNotMatch("<script>|<style|style=", html);

        string[] assets = [.. AssetPath().Matches(html).Select(m => m.Value)];
        Assert.Equal(2, assets.Length);
        foreach (string asset in assets)
        {
            using HttpResponseMessage file = await browser.SendAsync(HttpMethod.Get, new Uri(service.Url, asset));
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Equal(
                asset.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript" : "text/css",
                file.Content.Headers.ContentType!.MediaType
            );
            Assert.Equal("public, max-age=31536000, immutable", file.Headers.CacheControl!.ToString());
            AssertUiHeaders(file);
        }
    }

    /// <summary>
    /// The UI's paths answer only GET, and nothing while the web sign-in is off; a missing file or another kind of file is the
    /// 404 page (design §15.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with the web sign-in and one with OIDC only.
    ///
    /// Steps:
    /// - With the sign-in: HEAD /, HEAD and POST the script, GET /assets/missing.js, /assets/missing.txt, and /assets/; without
    ///   it: GET / and the script.
    ///
    /// Expected:
    /// - The 404 page (text/plain, "Not found.") for each.
    /// </remarks>
    [Fact]
    public async Task Ui_OtherMethodsAndFiles_AreNotFound()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        ElmService on = await StartAsync(test, idp);
        ElmService off = await test.StartAsync(s => s with { Oidc = idp.Settings() });
        using var browser = new WebBrowser();
        using HttpResponseMessage index = await browser.SendAsync(HttpMethod.Get, on.Url);
        string script = AssetPath()
            .Matches(await index.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .First(m => m.Value.EndsWith(".js", StringComparison.Ordinal))
            .Value;

        (HttpMethod, Uri)[] refused =
        [
            (HttpMethod.Head, on.Url),
            (HttpMethod.Head, new Uri(on.Url, script)),
            (HttpMethod.Post, new Uri(on.Url, script)),
            (HttpMethod.Get, new Uri(on.Url, "assets/missing.js")),
            (HttpMethod.Get, new Uri(on.Url, "assets/missing.txt")),
            (HttpMethod.Get, new Uri(on.Url, "assets/")),
            (HttpMethod.Get, off.Url),
            (HttpMethod.Get, new Uri(off.Url, script)),
        ];
        foreach ((HttpMethod method, Uri url) in refused)
        {
            using HttpResponseMessage answer = await browser.SendAsync(method, url);
            Assert.True(answer.StatusCode == HttpStatusCode.NotFound, $"{method} {url}: {(int)answer.StatusCode}");
            Assert.Equal("text/plain; charset=utf-8", answer.Content.Headers.ContentType!.ToString());
            if (method != HttpMethod.Head)
            {
                Assert.Equal(NotFound, await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            }
        }
    }

    /// <summary>
    /// GET /v1/admin/me gives the caller's administrator and this request's permissions, writes no audit row for its success,
    /// and writes the refusal of an unmapped token as admin.me (design §14.5, M5c); every administration answer is no-store.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with OIDC; the setup owner's key.
    ///
    /// Steps:
    /// - GET /v1/admin/me with the owner key, with a token of carol in elm-viewers, and with a token of dave of no mapped
    ///   group; GET it without credentials; read the audit log.
    ///
    /// Expected:
    /// - 200 for the owner with role owner and every permission in the design's order; 200 for carol with role viewer and the
    ///   read permissions other than admin.read and session.read; 403 for dave; 401 without credentials; every answer with
    ///   Cache-Control no-store; no admin.me row with ELM_OK, one with ELM_FORBIDDEN.
    /// </remarks>
    [Fact]
    public async Task Me_AnswersTheCaller()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync(s => s with { Oidc = idp.Settings() });
        CancellationToken cancel = TestContext.Current.CancellationToken;

        using HttpResponseMessage mine = await test.Http(service, owner).GetAsync("v1/admin/me", cancel);
        Assert.Equal("no-store", mine.Headers.CacheControl!.ToString());
        using (JsonDocument me = JsonDocument.Parse(await mine.Content.ReadAsStringAsync(cancel)))
        {
            Assert.Equal(("owner", "owner-1"), (me.RootElement.GetProperty("role").GetString(), me.RootElement.GetProperty("subject").GetString()));
            Assert.Equal(Permissions.All, me.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        }

        using HttpResponseMessage viewer = await test.Http(service, idp.Token("carol", ["elm-viewers"])).GetAsync("v1/admin/me", cancel);
        using (JsonDocument me = JsonDocument.Parse(await viewer.Content.ReadAsStringAsync(cancel)))
        {
            Assert.Equal("viewer", me.RootElement.GetProperty("role").GetString());
            string[] permissions = [.. me.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!)];
            Assert.All(permissions, p => Assert.EndsWith(".read", p, StringComparison.Ordinal));
            Assert.DoesNotContain("admin.read", permissions);
            Assert.DoesNotContain("session.read", permissions);
        }

        using HttpResponseMessage dave = await test.Http(service, idp.Token("dave", ["nobody"])).GetAsync("v1/admin/me", cancel);
        Assert.Equal(HttpStatusCode.Forbidden, dave.StatusCode);
        using HttpResponseMessage none = await test.Http(service).GetAsync("v1/admin/me", cancel);
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.All(new[] { viewer, dave, none }, a => Assert.Equal("no-store", a.Headers.CacheControl!.ToString()));

        JsonElement[] rows =
        [
            .. (await TestService.AuditRowsAsync(test, service, owner)).Where(r => r.GetProperty("action").GetString() == "admin.me"),
        ];
        Assert.Equal(["ELM_FORBIDDEN"], rows.Select(r => r.GetProperty("result").GetString()));
    }

    private static Task<ElmService> StartAsync(TestService test, FakeIdp idp) =>
        test.StartAsync(s => s with { Oidc = idp.Settings(), Web = FakeIdp.WebSettings(Kek) });

    private static void AssertUiHeaders(HttpResponseMessage answer)
    {
        Assert.Equal(ElmService.AdminUiPolicy, answer.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", answer.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", answer.Headers.GetValues("Referrer-Policy").Single());
    }

    [GeneratedRegex("/assets/[A-Za-z0-9_.-]+\\.(js|css)")]
    private static partial Regex AssetPath();
}
