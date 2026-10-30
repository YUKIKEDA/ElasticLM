using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The admin UI's sign-in through the service (design §15.3, decision 0.99, M5b), against <see cref="FakeIdp"/>: the
/// authorization code flow, the session and its cookie, CSRF, logout, the session API, and the Data Protection keys.
/// </summary>
public sealed class ElmServiceWebTests
{
    private const string SessionEnded = "The session is missing, ended, or its administrator is disabled.";
    private const string CsrfRefused = "The request lacks the CSRF header or comes from another site.";
    private const string SignInFailed = "The sign-in failed. Start it again.\n";

    // A key encryption key made by the test, never a stored secret.
    private static readonly byte[] Kek = RandomNumberGenerator.GetBytes(WebLoginSettings.KekLength);

    /// <summary>
    /// A sign-in runs the authorization code flow with PKCE (design §15.3): the service sends the browser to the provider
    /// with the settings the design names and the sign-in's __Host- cookies, and the callback makes a session, its cookie,
    /// and a session.create row; the cookie then signs in to the administration API.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with the web sign-in that trusts the fake provider; alice in elm-owners; an owner key from setup.
    ///
    /// Steps:
    /// - GET /auth/login; follow the provider's redirect; follow the callback; list administrators with the cookie.
    ///
    /// Expected:
    /// - 302 to the provider's authorization endpoint with response_type code, no response_mode (query is the default),
    ///   scope openid, S256, the web client, and the redirect_uri of the service's URL; the correlation and nonce cookies with __Host- names, Path=/,
    ///   Secure, HttpOnly, SameSite=Lax.
    /// - The callback answers 302 to / with __Host-elm-session: 43 characters, Path=/, Secure, HttpOnly, SameSite=Lax, no
    ///   expiry; the sign-in's cookies are removed; one session row, found by the value's SHA-256.
    /// - 200 with alice as owner; a session.create row by alice's ID with the session as target and replaced and evicted null.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task SignIn_MakesASessionAndItsCookie(string provider)
    {
        await using TestService test = TestService.Open(provider);
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        ElmService service = await StartAsync(test, idp);
        using var browser = new WebBrowser();

        using HttpResponseMessage login = await browser.SendAsync(HttpMethod.Get, new Uri(service.Url, "auth/login"));
        Assert.Equal(HttpStatusCode.Found, login.StatusCode);
        Uri authorize = login.Headers.Location!;
        Assert.StartsWith(idp.Issuer + "/auth?", authorize.ToString(), StringComparison.Ordinal);
        Dictionary<string, string> query = Query(authorize);
        Assert.Equal(
            ("code", "openid", "S256", FakeIdp.WebClientId, new Uri(service.Url, "auth/callback").ToString()),
            (query["response_type"], query["scope"], query["code_challenge_method"], query["client_id"], query["redirect_uri"])
        );

        // response_mode query is the code flow's default, which the handler leaves out (OAuth 2.0 Multiple Response Types §2.1).
        Assert.False(query.ContainsKey("response_mode"));
        Assert.True(query.ContainsKey("code_challenge") && query.ContainsKey("nonce") && query.ContainsKey("state"));
        foreach (string prefix in new[] { "__Host-elm-correlation.", "__Host-elm-nonce." })
        {
            string cookie = Assert.Single(browser.LastSetCookies, c => c.StartsWith(prefix, StringComparison.Ordinal));
            AssertCookieAttributes(cookie);
        }

        using HttpResponseMessage back = await browser.FollowAsync(login);
        using HttpResponseMessage callback = await browser.FollowAsync(back);
        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal("/", callback.Headers.Location!.OriginalString);
        string set = Assert.Single(browser.LastSetCookies, c => c.StartsWith(ElmService.SessionCookie + "=", StringComparison.Ordinal));
        AssertCookieAttributes(set);
        Assert.DoesNotContain("expires", set, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("max-age", set, StringComparison.OrdinalIgnoreCase);
        Dictionary<string, string> jar = browser.Cookies(service.Url);
        Assert.DoesNotContain(
            jar.Keys,
            k => k.StartsWith("__Host-elm-correlation.", StringComparison.Ordinal) || k.StartsWith("__Host-elm-nonce.", StringComparison.Ordinal)
        );
        string value = jar[ElmService.SessionCookie];
        Assert.Equal(43, value.Length);
        Assert.Equal(1L, await test.ScalarAsync("SELECT COUNT(*) FROM admin_sessions"));
        SessionHolder session = (await service.Database.FindSessionAsync(ApiKeys.HashOf(value), TestContext.Current.CancellationToken))!;

        using HttpResponseMessage admins = await browser.SendAsync(HttpMethod.Get, new Uri(service.Url, "v1/admin/admins"));
        string text = await admins.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(admins.StatusCode == HttpStatusCode.OK, text);
        using JsonDocument page = JsonDocument.Parse(text);
        JsonElement alice = page.RootElement.GetProperty("items").EnumerateArray().Single(a => a.GetProperty("subject").GetString() == "alice");
        Assert.Equal("owner", alice.GetProperty("role").GetString());
        Assert.Equal(session.AdminId.ToString("D"), alice.GetProperty("id").GetString());

        JsonElement created = (await TestService.AuditRowsAsync(test, service, owner)).Single(r =>
            r.GetProperty("action").GetString() == "session.create"
        );
        Assert.Equal(
            ("oidc", session.AdminId.ToString("D"), session.Id.ToString("D"), "ELM_OK", "{\"replaced\":null,\"evicted\":null}"),
            (
                created.GetProperty("actorType").GetString(),
                created.GetProperty("actorId").GetString(),
                created.GetProperty("target").GetString(),
                created.GetProperty("result").GetString(),
                created.GetProperty("detail").GetString()
            )
        );
    }

    /// <summary>
    /// A cookie request that changes state needs X-ELM-CSRF: 1 and, when sent, an Origin of the public URL (design §15.3); a
    /// refusal is 403 with an audit row; GET needs neither.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A signed-in browser; an owner key from setup to read the audit log.
    ///
    /// Steps:
    /// - POST /v1/admin/audit/verify: without the header, with X-ELM-CSRF: 2, with the header and another Origin, with the
    ///   header and Origin null; then with the header alone and with the header and the service's Origin; GET the audit log
    ///   without the header.
    ///
    /// Expected:
    /// - 403 ELM_FORBIDDEN for the first four, each with an audit.verify row ELM_FORBIDDEN by alice with detail
    ///   {"reason":"csrf"}; 200 for the last three.
    /// </remarks>
    [Fact]
    public async Task Session_StateChange_NeedsTheCsrfHeaderAndOrigin()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        ElmService service = await StartAsync(test, idp);
        using var browser = new WebBrowser();
        await SignInAsync(browser, service);
        var verify = new Uri(service.Url, "v1/admin/audit/verify");
        string origin = service.Url.GetLeftPart(UriPartial.Authority);

        Action<HttpRequestMessage>[] refused =
        [
            _ => { },
            r => r.Headers.Add(ElmService.CsrfHeader, "2"),
            r => Csrf(r, "https://evil.example"),
            r => Csrf(r, "null"),
        ];
        foreach (Action<HttpRequestMessage> change in refused)
        {
            await TestService.AssertProblemAsync(
                await browser.SendAsync(HttpMethod.Post, verify, change),
                HttpStatusCode.Forbidden,
                "ELM_FORBIDDEN",
                CsrfRefused
            );
        }

        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(HttpMethod.Post, verify, r => Csrf(r, null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(HttpMethod.Post, verify, r => Csrf(r, origin))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(HttpMethod.Get, new Uri(service.Url, "v1/admin/admins"))).StatusCode);

        JsonElement[] rows = await TestService.AuditRowsAsync(test, service, owner);
        JsonElement[] csrf =
        [
            .. rows.Where(r => r.GetProperty("action").GetString() == "audit.verify" && r.GetProperty("result").GetString() == "ELM_FORBIDDEN"),
        ];
        Assert.Equal(4, csrf.Length);
        Assert.All(
            csrf,
            r => Assert.Equal(("oidc", "{\"reason\":\"csrf\"}"), (r.GetProperty("actorType").GetString(), r.GetProperty("detail").GetString()))
        );
    }

    /// <summary>
    /// A session ends after session.idleMinutes without a request, at session.maxHours whatever the use, and when its
    /// administrator is disabled; the last request is written at most once a minute (design §15.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with the web sign-in; idle 30 minutes and lifetime 12 hours (the defaults); an owner key from setup.
    ///
    /// Steps:
    /// - Sign in; a request at 30 seconds and at 61 seconds, reading last_seen_at; then 31 minutes without one, before the
    ///   hourly sweep.
    /// - Sign in again; a request 29 and 58 minutes later.
    /// - Sign in again; a request every 20 minutes up to 11 hours 40 minutes; then one at 12 hours.
    /// - Sign in again; disable alice with the owner key; a request with the cookie.
    ///
    /// Expected:
    /// - last_seen_at unchanged at 30 seconds and written at 61 seconds; 401 with the session message after 31 idle minutes,
    ///   and its row is removed.
    /// - 200 for both, since each request starts the idle time again.
    /// - 200 up to 11 hours 40 minutes; 401 at 12 hours.
    /// - The disable row's detail is {"sessions":1}; then 401.
    /// </remarks>
    [Fact]
    public async Task Session_Ends_AsDesigned()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        ElmService service = await StartAsync(test, idp);
        var admins = new Uri(service.Url, "v1/admin/admins");

        using (var browser = new WebBrowser())
        {
            await SignInAsync(browser, service);
            long first = await test.ScalarAsync("SELECT last_seen_at FROM admin_sessions");
            test.Time.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(HttpMethod.Get, admins)).StatusCode);
            Assert.Equal(first, await test.ScalarAsync("SELECT last_seen_at FROM admin_sessions"));
            test.Time.Advance(TimeSpan.FromSeconds(31));
            Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(HttpMethod.Get, admins)).StatusCode);
            Assert.Equal(first + 61, await test.ScalarAsync("SELECT last_seen_at FROM admin_sessions"));

            // Before the hourly sweep, so that the 401 comes from the request's own idle check, not from a swept row.
            test.Time.Advance(TimeSpan.FromMinutes(31));
            await TestService.AssertProblemAsync(
                await browser.SendAsync(HttpMethod.Get, admins),
                HttpStatusCode.Unauthorized,
                "ELM_UNAUTHORIZED",
                SessionEnded
            );
            Assert.Equal(0L, await test.ScalarAsync("SELECT COUNT(*) FROM admin_sessions"));
        }

        using (var browser = new WebBrowser())
        {
            await SignInAsync(browser, service);
            for (int i = 0; i < 2; i++)
            {
                test.Time.Advance(TimeSpan.FromMinutes(29));
                Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(HttpMethod.Get, admins)).StatusCode);
            }
        }

        using (var browser = new WebBrowser())
        {
            await SignInAsync(browser, service);
            for (int i = 0; i < 35; i++)
            {
                test.Time.Advance(TimeSpan.FromMinutes(20));
                Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(HttpMethod.Get, admins)).StatusCode);
            }

            test.Time.Advance(TimeSpan.FromMinutes(20));
            await TestService.AssertProblemAsync(
                await browser.SendAsync(HttpMethod.Get, admins),
                HttpStatusCode.Unauthorized,
                "ELM_UNAUTHORIZED",
                SessionEnded
            );
        }

        using (var browser = new WebBrowser())
        {
            SessionHolder session = await SignInAsync(browser, service);
            using HttpResponseMessage disabled = await test.Http(service, owner)
                .PostAsync($"v1/admin/admins/{session.AdminId:D}/disable", null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
            JsonElement row = (await TestService.AuditRowsAsync(test, service, owner)).Last(r =>
                r.GetProperty("action").GetString() == "admin.disable"
            );
            Assert.Equal("{\"sessions\":1}", row.GetProperty("detail").GetString());
            await TestService.AssertProblemAsync(
                await browser.SendAsync(HttpMethod.Get, admins),
                HttpStatusCode.Unauthorized,
                "ELM_UNAUTHORIZED",
                SessionEnded
            );
        }
    }

    /// <summary>
    /// A session whose administrator is disabled is refused and removed even when its row is still there (design §15.3,
    /// check step 6): the disable endpoint removes the rows, and this check covers a row left by a disable that raced it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A signed-in browser.
    ///
    /// Steps:
    /// - Set alice's enabled to false in the database, leaving the session row; list administrators with the cookie.
    ///
    /// Expected:
    /// - 401 with the session message; no session row.
    /// </remarks>
    [Fact]
    public async Task Session_OfADisabledAdministrator_IsRefused()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        ElmService service = await StartAsync(test, idp);
        using var browser = new WebBrowser();
        await SignInAsync(browser, service);

        await test.ExecuteAsync("UPDATE admins SET enabled = false WHERE subject = 'alice'");
        await TestService.AssertProblemAsync(
            await browser.SendAsync(HttpMethod.Get, new Uri(service.Url, "v1/admin/admins")),
            HttpStatusCode.Unauthorized,
            "ELM_UNAUTHORIZED",
            SessionEnded
        );
        Assert.Equal(0L, await test.ScalarAsync("SELECT COUNT(*) FROM admin_sessions"));
    }

    /// <summary>
    /// The redirect_uri and the Origin check use the first public URL, not the host the request named (design §15.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with the web sign-in and the public URLs https://elm.example/ and https://other.example/.
    ///
    /// Steps:
    /// - GET /auth/login on the loopback address; POST /auth/logout with X-ELM-CSRF and the Origin of the loopback address,
    ///   then with the Origin https://elm.example.
    ///
    /// Expected:
    /// - The redirect_uri is https://elm.example/auth/callback; the logout with the loopback Origin is 403 and the one with
    ///   https://elm.example is 200.
    /// </remarks>
    [Fact]
    public async Task PublicUrl_GivesTheRedirectUriAndTheOrigin()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        ElmService service = await StartAsync(
            test,
            idp,
            s => s with { PublicUrls = [new Uri("https://elm.example/"), new Uri("https://other.example/")] }
        );
        using var browser = new WebBrowser();

        using HttpResponseMessage login = await browser.SendAsync(HttpMethod.Get, new Uri(service.Url, "auth/login"));
        Assert.Equal("https://elm.example/auth/callback", Query(login.Headers.Location!)["redirect_uri"]);
        var logout = new Uri(service.Url, "auth/logout");
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await browser.SendAsync(HttpMethod.Post, logout, r => Csrf(r, service.Url.GetLeftPart(UriPartial.Authority)))).StatusCode
        );
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(HttpMethod.Post, logout, r => Csrf(r, "https://elm.example"))).StatusCode);
    }

    /// <summary>
    /// The session cookie is checked before the database (design §15.3, check step 4): two cookies or a value that is not 43
    /// base64url characters get 401; an Authorization header takes the API key's path and ignores the cookie.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A signed-in browser; an owner key from setup.
    ///
    /// Steps:
    /// - List administrators with: the cookie twice; a value of 42 characters; a value of 43 characters with '+'; an unknown
    ///   value of 43 characters; the cookie and the owner key's Authorization header.
    ///
    /// Expected:
    /// - 401 with the session message for the first four; 200 for the last.
    /// </remarks>
    [Fact]
    public async Task Session_CookieForms_AreChecked()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        ElmService service = await StartAsync(test, idp);
        using var browser = new WebBrowser();
        await SignInAsync(browser, service);
        string value = browser.Cookies(service.Url)[ElmService.SessionCookie];
        var admins = new Uri(service.Url, "v1/admin/admins");
        using var plain = new WebBrowser();

        string[] headers =
        [
            $"{ElmService.SessionCookie}={value}; {ElmService.SessionCookie}={value}",
            $"{ElmService.SessionCookie}={value[..42]}",
            $"{ElmService.SessionCookie}=+{value[1..]}",
            $"{ElmService.SessionCookie}={new string('A', 43)}",
        ];
        foreach (string header in headers)
        {
            await TestService.AssertProblemAsync(
                await plain.SendAsync(HttpMethod.Get, admins, r => r.Headers.Add("Cookie", header)),
                HttpStatusCode.Unauthorized,
                "ELM_UNAUTHORIZED",
                SessionEnded
            );
        }

        using HttpResponseMessage key = await browser.SendAsync(HttpMethod.Get, admins, r => r.Headers.Authorization = new("Bearer", owner));
        Assert.Equal(HttpStatusCode.OK, key.StatusCode);
    }

    /// <summary>
    /// A callback the design refuses gets the 400 page, counted as an authentication failure, and makes no session (design
    /// §15.3): a state from another browser, a sign-in older than 15 minutes, an error from the provider, and ID tokens that
    /// fail a check.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with the web sign-in; an owner key from setup.
    ///
    /// Steps:
    /// - Start a sign-in in one browser and finish it in another; start one and come back after 16 minutes; let the provider
    ///   answer access_denied; let it put in the ID token: another audience, azp of another client, two audiences without azp,
    ///   another nonce, no sub, 101 groups; then wait a minute for the failure count.
    ///
    /// Expected:
    /// - 400 with the fixed text, text/plain; charset=utf-8, no-store, nosniff, and the CSP default-src 'none'; frame-ancestors
    ///   'none' for each; no session row; auth.failed rows whose counts add up to the ten.
    /// </remarks>
    [Fact]
    public async Task Callback_Refusals_AreBadRequests()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        ElmService service = await StartAsync(test, idp);

        using (var starter = new WebBrowser())
        using (var other = new WebBrowser())
        {
            using HttpResponseMessage login = await starter.SendAsync(HttpMethod.Get, new Uri(service.Url, "auth/login"));
            using HttpResponseMessage back = await other.FollowAsync(login);
            await AssertPageAsync(await other.FollowAsync(back), HttpStatusCode.BadRequest, SignInFailed);
        }

        using (var browser = new WebBrowser())
        {
            using HttpResponseMessage login = await browser.SendAsync(HttpMethod.Get, new Uri(service.Url, "auth/login"));
            using HttpResponseMessage back = await browser.FollowAsync(login);
            test.Time.Advance(TimeSpan.FromMinutes(16));
            await AssertPageAsync(await browser.FollowAsync(back), HttpStatusCode.BadRequest, SignInFailed);
        }

        idp.AuthorizeError = "access_denied";
        await AssertPageAsync(await TrySignInAsync(service), HttpStatusCode.BadRequest, SignInFailed);
        idp.AuthorizeError = "server_error";
        await AssertPageAsync(await TrySignInAsync(service), HttpStatusCode.BadRequest, SignInFailed);
        idp.AuthorizeError = null;

        Action<Dictionary<string, object?>>[] tokens =
        [
            p => p["aud"] = "other-client",
            p => p["azp"] = "other-client",
            p => p["aud"] = new[] { FakeIdp.WebClientId, "other" },
            p => p["nonce"] = "another-nonce",
            p => p.Remove("sub"),
            p => p["groups"] = Enumerable.Range(0, ElmService.MaxRoleValues + 1).Select(i => "g" + i).ToArray(),
        ];
        foreach (Action<Dictionary<string, object?>> change in tokens)
        {
            idp.IdTokenChange = change;
            await AssertPageAsync(await TrySignInAsync(service), HttpStatusCode.BadRequest, SignInFailed);
        }

        Assert.Equal(0L, await test.ScalarAsync("SELECT COUNT(*) FROM admin_sessions"));
        await test.WaitWithClockAsync(async () => await FailuresAsync(test, service, owner) == 10);
    }

    /// <summary>
    /// A provider out of reach gives the 503 page at the login and at the callback (design §15.3), and a callback for a
    /// disabled administrator or a user without a mapped role gives the 403 page, the latter with a session.create refusal.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with the web sign-in; an owner key from setup.
    ///
    /// Steps:
    /// - GET /auth/login while the provider answers 503; sign in while its token endpoint answers 500; sign in as bob of no
    ///   mapped group; sign in as carol, disable carol with the owner key, and sign in as carol again, then again with no
    ///   mapped group.
    ///
    /// Expected:
    /// - 503 with the unreachable text twice; 403 with the refusal text three times and no session; a session.create row
    ///   ELM_FORBIDDEN without an actor ID and with bob's issuer and subject; carol keeps the role admin.
    /// </remarks>
    [Fact]
    public async Task Callback_ProviderOutOfReach_AndRefusedAccounts()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        ElmService service = await StartAsync(test, idp);
        const string Unreachable = "The identity provider cannot be reached.\n";
        const string Refused = "This account may not use the admin UI.\n";

        idp.Available = false;
        using (var browser = new WebBrowser())
        {
            await AssertPageAsync(
                await browser.SendAsync(HttpMethod.Get, new Uri(service.Url, "auth/login")),
                HttpStatusCode.ServiceUnavailable,
                Unreachable
            );
        }

        idp.Available = true;
        idp.CodeTokenStatus = 500;
        await AssertPageAsync(await TrySignInAsync(service), HttpStatusCode.ServiceUnavailable, Unreachable);
        idp.CodeTokenStatus = null;

        idp.Subject = "bob";
        idp.Groups = ["nobody"];
        await AssertPageAsync(await TrySignInAsync(service), HttpStatusCode.Forbidden, Refused);
        JsonElement bob = (await TestService.AuditRowsAsync(test, service, owner)).Single(r =>
            r.GetProperty("action").GetString() == "session.create" && r.GetProperty("result").GetString() == "ELM_FORBIDDEN"
        );
        Assert.Equal(JsonValueKind.Null, bob.GetProperty("actorId").ValueKind);
        Assert.Equal($"{{\"issuer\":\"{idp.Issuer}\",\"subject\":\"bob\"}}", bob.GetProperty("detail").GetString());

        idp.Subject = "carol";
        idp.Groups = ["elm-admins"];
        SessionHolder carol;
        using (var browser = new WebBrowser())
        {
            carol = await SignInAsync(browser, service);
        }

        using HttpResponseMessage disabled = await test.Http(service, owner)
            .PostAsync($"v1/admin/admins/{carol.AdminId:D}/disable", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        await AssertPageAsync(await TrySignInAsync(service), HttpStatusCode.Forbidden, Refused);

        // design §14.6 step 6 before 7: a disabled administrator is refused before its claims are read, so its role stays.
        idp.Groups = ["nobody"];
        await AssertPageAsync(await TrySignInAsync(service), HttpStatusCode.Forbidden, Refused);
        AdminRow row = (await service.Database.FindAdminAsync(new AdminIdentity(idp.Issuer, "carol"), TestContext.Current.CancellationToken))!;
        Assert.Equal("admin", row.Role);
        Assert.Equal(0L, await test.ScalarAsync("SELECT COUNT(*) FROM admin_sessions"));
    }

    /// <summary>
    /// POST /auth/logout needs the CSRF header and Origin even without a session, removes the session with a session.logout
    /// row, clears the cookie, and gives the provider's end of session (design §15.3); without a cookie it still answers 200.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A signed-in browser; an owner key from setup.
    ///
    /// Steps:
    /// - POST /auth/logout without the header; with it; again with it and no cookie; with a provider that names no
    ///   end_session_endpoint.
    ///
    /// Expected:
    /// - 403 with the CSRF text and the session kept; then 200 with endSession of the provider's end_session_endpoint with
    ///   client_id and post_logout_redirect_uri of the service's origin and "/", the cookie removed, no session row, and a
    ///   session.logout row by alice with the session as target; then 200 again; then endSession null.
    /// </remarks>
    [Fact]
    public async Task Logout_EndsTheSession()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        ElmService service = await StartAsync(test, idp);
        using var browser = new WebBrowser();
        SessionHolder session = await SignInAsync(browser, service);
        var logout = new Uri(service.Url, "auth/logout");

        await AssertPageAsync(await browser.SendAsync(HttpMethod.Post, logout), HttpStatusCode.Forbidden, CsrfRefused + "\n");
        Assert.Equal(1L, await test.ScalarAsync("SELECT COUNT(*) FROM admin_sessions"));

        using HttpResponseMessage answer = await browser.SendAsync(HttpMethod.Post, logout, r => Csrf(r, null));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        string back = service.Url.GetLeftPart(UriPartial.Authority) + "/";
        using (JsonDocument body = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            Assert.Equal(
                $"{idp.Issuer}/logout?client_id={FakeIdp.WebClientId}&post_logout_redirect_uri={Uri.EscapeDataString(back)}",
                body.RootElement.GetProperty("endSession").GetString()
            );
        }

        Assert.False(browser.Cookies(service.Url).ContainsKey(ElmService.SessionCookie));
        Assert.Equal(0L, await test.ScalarAsync("SELECT COUNT(*) FROM admin_sessions"));
        JsonElement row = (await TestService.AuditRowsAsync(test, service, owner)).Single(r =>
            r.GetProperty("action").GetString() == "session.logout"
        );
        Assert.Equal(
            (session.AdminId.ToString("D"), session.Id.ToString("D")),
            (row.GetProperty("actorId").GetString(), row.GetProperty("target").GetString())
        );

        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(HttpMethod.Post, logout, r => Csrf(r, null))).StatusCode);
        idp.EndSession = false;
        ElmService later = await StartAsync(test, idp);
        using HttpResponseMessage plain = await browser.SendAsync(HttpMethod.Post, new Uri(later.Url, "auth/logout"), r => Csrf(r, null));
        Assert.Equal("{\"endSession\":null}", await plain.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The session API lists live sessions and revokes one, for owners only (design §14.5): an adminId filter, a cursor,
    /// 400 for a bad adminId, 404 for an unknown or malformed ID, and a session.revoke row; an admin gets 403.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two browsers signed in as alice; an owner key from setup.
    ///
    /// Steps:
    /// - Sign in the second browser a second after the first. List sessions; list with limit 1 and its next; with alice's adminId; with an unknown adminId; with adminId x; revoke
    ///   the first; revoke it again; revoke "x"; list with carol's admin token; let 31 minutes pass and list.
    ///
    /// Expected:
    /// - Two sessions with alice's ID, the times, and ip 127.0.0.1, no hash; the pages give both in ID order; the filter two
    ///   and zero; 400 ELM_INVALID_ARGUMENT; 204 and the first browser's next request 401; a session.revoke row with the
    ///   session as target and detail {"adminId"}; 404 ELM_NOT_FOUND twice; 403 for carol; an empty list after 31 minutes.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task SessionsApi_ListsAndRevokes(string provider)
    {
        await using TestService test = TestService.Open(provider);
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        ElmService service = await StartAsync(test, idp);
        using var first = new WebBrowser();
        using var second = new WebBrowser();
        SessionHolder one = await SignInAsync(first, service);
        test.Time.Advance(TimeSpan.FromSeconds(1));
        SessionHolder two = await SignInAsync(second, service);
        HttpClient http = test.Http(service, owner);
        string alice = one.AdminId.ToString("D");

        JsonElement listed = await TestService.GetAsync(http, "v1/admin/sessions");
        JsonElement[] items = [.. listed.GetProperty("items").EnumerateArray()];
        Assert.Equal([one.Id.ToString("D"), two.Id.ToString("D")], items.Select(i => i.GetProperty("id").GetString()));
        Assert.All(items, i => Assert.Equal((alice, "127.0.0.1"), (i.GetProperty("adminId").GetString(), i.GetProperty("ip").GetString())));
        Assert.Equal(
            ["adminId", "createdAt", "expiresAt", "id", "ip", "lastSeenAt"],
            items[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
        );
        Assert.Equal("2026-10-01T12:00:00Z", items[0].GetProperty("expiresAt").GetString());
        Assert.Equal("2026-10-01T12:00:01Z", items[1].GetProperty("expiresAt").GetString());

        JsonElement firstPage = await TestService.GetAsync(http, "v1/admin/sessions?limit=1");
        string next = firstPage.GetProperty("next").GetString()!;
        Assert.Equal(one.Id.ToString("D"), next);
        JsonElement secondPage = await TestService.GetAsync(http, "v1/admin/sessions?limit=1&after=" + next);
        Assert.Equal(two.Id.ToString("D"), secondPage.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(2, (await TestService.GetAsync(http, "v1/admin/sessions?adminId=" + alice)).GetProperty("items").GetArrayLength());
        Assert.Equal(
            0,
            (await TestService.GetAsync(http, "v1/admin/sessions?adminId=" + Guid.CreateVersion7().ToString("D")))
                .GetProperty("items")
                .GetArrayLength()
        );
        await TestService.AssertProblemAsync(
            await http.GetAsync("v1/admin/sessions?adminId=x", TestContext.Current.CancellationToken),
            HttpStatusCode.BadRequest,
            "ELM_INVALID_ARGUMENT",
            "adminId must be an administrator ID."
        );

        CancellationToken cancel = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync("v1/admin/sessions/" + one.Id.ToString("D"), cancel)).StatusCode);
        await TestService.AssertProblemAsync(
            await first.SendAsync(HttpMethod.Get, new Uri(service.Url, "v1/admin/admins")),
            HttpStatusCode.Unauthorized,
            "ELM_UNAUTHORIZED",
            SessionEnded
        );
        JsonElement revoked = (await TestService.AuditRowsAsync(test, service, owner)).Single(r =>
            r.GetProperty("action").GetString() == "session.revoke"
        );
        Assert.Equal(
            (one.Id.ToString("D"), $"{{\"adminId\":\"{alice}\"}}"),
            (revoked.GetProperty("target").GetString(), revoked.GetProperty("detail").GetString())
        );
        foreach (string id in new[] { one.Id.ToString("D"), "x" })
        {
            await TestService.AssertProblemAsync(
                await http.DeleteAsync("v1/admin/sessions/" + id, cancel),
                HttpStatusCode.NotFound,
                "ELM_NOT_FOUND",
                "No live session has this ID."
            );
        }

        using HttpResponseMessage carol = await test.Http(service, idp.Token("carol", ["elm-admins"])).GetAsync("v1/admin/sessions", cancel);
        Assert.Equal(HttpStatusCode.Forbidden, carol.StatusCode);
        test.Time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(0, (await TestService.GetAsync(http, "v1/admin/sessions")).GetProperty("items").GetArrayLength());
    }

    /// <summary>
    /// One administrator keeps at most 50 live sessions; the 51st sign-in removes the oldest and names it in session.create
    /// (design §15.3), and a browser that sends its old cookie at sign-in has that session replaced.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with the web sign-in; an owner key from setup.
    ///
    /// Steps:
    /// - Sign in 51 times in new browsers, a second apart; then sign in again in the last browser, which still has its
    ///   cookie.
    ///
    /// Expected:
    /// - 50 sessions; the 51st session.create names the first session as evicted, and the first browser gets 401; the last
    ///   sign-in names the last browser's session as replaced, and the count stays 50.
    /// </remarks>
    [Fact]
    public async Task Sessions_PerAdmin_AreLimited()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        ElmService service = await StartAsync(test, idp);
        var browsers = new List<WebBrowser>();
        try
        {
            var sessions = new List<SessionHolder>();
            for (int i = 0; i <= ElmService.MaxSessionsPerAdmin; i++)
            {
                var browser = new WebBrowser();
                browsers.Add(browser);
                sessions.Add(await SignInAsync(browser, service));
                test.Time.Advance(TimeSpan.FromSeconds(1));
            }

            Assert.Equal(50L, await test.ScalarAsync("SELECT COUNT(*) FROM admin_sessions"));
            JsonElement[] created =
            [
                .. (await TestService.AuditRowsAsync(test, service, owner)).Where(r => r.GetProperty("action").GetString() == "session.create"),
            ];
            Assert.Equal($"{{\"replaced\":null,\"evicted\":\"{sessions[0].Id:D}\"}}", created[^1].GetProperty("detail").GetString());
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await browsers[0].SendAsync(HttpMethod.Get, new Uri(service.Url, "v1/admin/admins"))).StatusCode
            );

            _ = await SignInAsync(browsers[^1], service);
            JsonElement last = (await TestService.AuditRowsAsync(test, service, owner)).Last(r =>
                r.GetProperty("action").GetString() == "session.create"
            );
            Assert.Equal($"{{\"replaced\":\"{sessions[^1].Id:D}\",\"evicted\":null}}", last.GetProperty("detail").GetString());
            Assert.Equal(50L, await test.ScalarAsync("SELECT COUNT(*) FROM admin_sessions"));
        }
        finally
        {
            browsers.ForEach(b => b.Dispose());
        }
    }

    /// <summary>
    /// The hourly sweep removes sessions past their idle time (design §15.3, §14.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A signed-in browser.
    ///
    /// Steps:
    /// - Let an hour pass, which fires the sweep.
    ///
    /// Expected:
    /// - No session row.
    /// </remarks>
    [Fact]
    public async Task Sweep_RemovesEndedSessions()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        ElmService service = await StartAsync(test, idp);
        using var browser = new WebBrowser();
        await SignInAsync(browser, service);

        test.Time.Advance(TimeSpan.FromHours(1));
        await TestService.WaitForAsync(async () => await test.ScalarAsync("SELECT COUNT(*) FROM admin_sessions") == 0);
    }

    /// <summary>
    /// The sign-in's paths answer only their method, and only with the web sign-in on; the handler's sign-out paths do not
    /// answer (design §15.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with OIDC but no web sign-in; a service with the web sign-in.
    ///
    /// Steps:
    /// - Without the web sign-in: GET /auth/login, GET /auth/callback, POST /auth/logout, GET /.
    /// - With it: POST /auth/login, POST /auth/callback with a form, GET /auth/logout, GET /signout-oidc, GET
    ///   /signout-callback-oidc, GET /.
    ///
    /// Expected:
    /// - 404 with the text page for every one but the last; 200 with the admin UI's index.html (text/html) for GET / (M5c).
    /// </remarks>
    [Fact]
    public async Task WebPaths_AnswerOnlyAsDesigned()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        ElmService off = await test.StartAsync(s => s with { Oidc = idp.Settings() });
        ElmService on = await StartAsync(test, idp);
        using var browser = new WebBrowser();
        const string NotFound = "Not found.\n";

        foreach (
            (HttpMethod method, string path) in new[]
            {
                (HttpMethod.Get, "auth/login"),
                (HttpMethod.Get, "auth/callback"),
                (HttpMethod.Post, "auth/logout"),
                (HttpMethod.Get, string.Empty),
            }
        )
        {
            await AssertPageAsync(await browser.SendAsync(method, new Uri(off.Url, path)), HttpStatusCode.NotFound, NotFound);
        }

        foreach (
            (HttpMethod method, string path) in new[]
            {
                (HttpMethod.Post, "auth/login"),
                (HttpMethod.Post, "auth/callback"),
                (HttpMethod.Get, "auth/logout"),
            }
        )
        {
            await AssertPageAsync(
                await browser.SendAsync(
                    method,
                    new Uri(on.Url, path),
                    r => r.Content = new FormUrlEncodedContent([new("code", "c"), new("state", "s")])
                ),
                HttpStatusCode.NotFound,
                NotFound
            );
        }

        foreach (string path in new[] { "signout-oidc", "signout-callback-oidc" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await browser.SendAsync(HttpMethod.Get, new Uri(on.Url, path))).StatusCode);
        }

        using HttpResponseMessage index = await browser.SendAsync(HttpMethod.Get, on.Url);
        Assert.Equal((HttpStatusCode.OK, "text/html"), (index.StatusCode, index.Content.Headers.ContentType!.MediaType));
    }

    /// <summary>
    /// The Data Protection keys are made by the service, encrypted with the key encryption key, shared by nodes, and checked
    /// at the start (design §15.3): a sign-in started on one node finishes on another; a start with another key encryption
    /// key is refused; the next key is made when the last one ends within 7 days.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A SQLite database shared by two services with the same key encryption key.
    ///
    /// Steps:
    /// - Start node A; read the key rows; start node B; start a sign-in on A and finish it on B; start a third service with
    ///   another key encryption key; make keys with a clock 84 days later, then again.
    ///
    /// Expected:
    /// - One row after A and after B, whose ciphertext holds no key XML; the sign-in on B makes a session; the third start
    ///   throws ServiceStartException with the key encryption key message; 84 days later one key is made, then none, and a
    ///   key manager over the table reads both.
    /// </remarks>
    [Fact]
    public async Task DataProtection_Keys_AreSharedAndChecked()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        ElmService a = await StartAsync(test, idp);
        Assert.Equal(1L, await test.ScalarAsync("SELECT COUNT(*) FROM dataprotection_keys"));
        DataProtectionKeyRow row = Assert.Single(await a.Database.ListDataProtectionKeysAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("descriptor", System.Text.Encoding.UTF8.GetString(row.Ciphertext), StringComparison.Ordinal);
        ElmService b = await StartAsync(test, idp);
        Assert.Equal(1L, await test.ScalarAsync("SELECT COUNT(*) FROM dataprotection_keys"));

        using (var browser = new WebBrowser())
        {
            using HttpResponseMessage login = await browser.SendAsync(HttpMethod.Get, new Uri(a.Url, "auth/login"));
            using HttpResponseMessage back = await browser.FollowAsync(login);
            Uri onB = new UriBuilder(back.Headers.Location!) { Port = b.Url.Port }.Uri;
            foreach ((string name, string value) in browser.Cookies(a.Url))
            {
                browser.Cookies(b.Url)[name] = value;
            }

            using HttpResponseMessage callback = await browser.SendAsync(HttpMethod.Get, onB);
            Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
            Assert.Equal(1L, await test.ScalarAsync("SELECT COUNT(*) FROM admin_sessions"));
        }

        ServiceStartException refused = await Assert.ThrowsAsync<ServiceStartException>(() =>
            StartAsync(test, idp, kek: RandomNumberGenerator.GetBytes(WebLoginSettings.KekLength))
        );
        Assert.Equal("No Data Protection key decrypts with the key encryption key; every node needs the same key encryption key.", refused.Message);

        var later = new FakeTimeProvider(test.Time.GetUtcNow() + TimeSpan.FromDays(84));
        var store = new DataProtectionKeyStore(a.Database, Kek, later);
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("ElasticLM.Service").DisableAutomaticKeyGeneration();
        services.Configure<KeyManagementOptions>(o => o.XmlRepository = store);
        await using ServiceProvider provider = services.BuildServiceProvider();
        IKeyManager keys = provider.GetRequiredService<IKeyManager>();
        Assert.Equal(1, await store.EnsureKeyAsync(keys, atStart: false, TestContext.Current.CancellationToken));
        Assert.Equal(0, await store.EnsureKeyAsync(keys, atStart: false, TestContext.Current.CancellationToken));
        Assert.Equal(2L, await test.ScalarAsync("SELECT COUNT(*) FROM dataprotection_keys"));
        Assert.Equal(2, keys.GetAllKeys().Count);
    }

    private static async Task<long> FailuresAsync(TestService test, ElmService service, string owner) =>
        (await TestService.AuditRowsAsync(test, service, owner))
            .Where(r => r.GetProperty("action").GetString() == "auth.failed")
            .Sum(r =>
            {
                using JsonDocument detail = JsonDocument.Parse(r.GetProperty("detail").GetString()!);
                return detail.RootElement.GetProperty("count").GetInt64();
            });

    private static Task<ElmService> StartAsync(
        TestService test,
        FakeIdp idp,
        Func<ServiceSettings, ServiceSettings>? change = null,
        byte[]? kek = null
    ) =>
        test.StartAsync(s =>
        {
            ServiceSettings web = s with { Oidc = idp.Settings(), Web = FakeIdp.WebSettings(kek ?? Kek) };
            return change?.Invoke(web) ?? web;
        });

    private static async Task<SessionHolder> SignInAsync(WebBrowser browser, ElmService service)
    {
        using HttpResponseMessage login = await browser.SendAsync(HttpMethod.Get, new Uri(service.Url, "auth/login"));
        Assert.Equal(HttpStatusCode.Found, login.StatusCode);
        using HttpResponseMessage back = await browser.FollowAsync(login);
        using HttpResponseMessage callback = await browser.FollowAsync(back);
        string text = await callback.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(callback.StatusCode == HttpStatusCode.Found, text);
        string value = browser.Cookies(service.Url)[ElmService.SessionCookie];
        return (await service.Database.FindSessionAsync(ApiKeys.HashOf(value), TestContext.Current.CancellationToken))!;
    }

    // A sign-in in a new browser, up to the callback's answer.
    private static async Task<HttpResponseMessage> TrySignInAsync(ElmService service)
    {
        using var browser = new WebBrowser();
        using HttpResponseMessage login = await browser.SendAsync(HttpMethod.Get, new Uri(service.Url, "auth/login"));
        using HttpResponseMessage back = await browser.FollowAsync(login);
        return await browser.FollowAsync(back);
    }

    private static void Csrf(HttpRequestMessage request, string? origin)
    {
        request.Headers.Add(ElmService.CsrfHeader, "1");
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }
    }

    private static void AssertCookieAttributes(string cookie)
    {
        foreach (string attribute in new[] { "path=/", "secure", "httponly", "samesite=lax" })
        {
            Assert.Contains("; " + attribute, cookie, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertPageAsync(HttpResponseMessage answer, HttpStatusCode status, string text)
    {
        using (answer)
        {
            Assert.Equal(status, answer.StatusCode);
            Assert.Equal(text, await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("text/plain; charset=utf-8", answer.Content.Headers.ContentType!.ToString());
            Assert.Equal("no-store", answer.Headers.CacheControl!.ToString());
            Assert.Equal("nosniff", answer.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("default-src 'none'; frame-ancestors 'none'", answer.Headers.GetValues("Content-Security-Policy").Single());
        }
    }

    private static Dictionary<string, string> Query(Uri uri) =>
        uri
            .Query.TrimStart('?')
            .Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]), StringComparer.Ordinal);
}
