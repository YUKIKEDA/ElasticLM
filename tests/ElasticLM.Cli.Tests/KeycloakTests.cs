using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ElasticLM.Service;
using ElasticLM.Service.Tests;

namespace ElasticLM.Cli.Tests;

/// <summary>
/// The OIDC sign-in end to end against Keycloak (design §15.3, decision 0.97, M5a): <c>elm admin login</c> runs the device
/// flow, the device code is approved through Keycloak's own pages over HTTP (no browser), and <c>elm-service</c> accepts the
/// access token with JwtBearer. Runs where <c>ELM_TEST_KEYCLOAK</c> names the test realm: the Linux CI job, or a machine that
/// starts tests/keycloak's realm in a WSL container (<c>wslc run -p 8180:8080 -v …/tests/keycloak:/opt/keycloak/data/import</c>
/// with the image and arguments of the CI step) and sets the variable to <c>http://127.0.0.1:8180/realms/elm</c>.
/// </summary>
public sealed partial class KeycloakTests
{
    /// <summary>The variable that names the Keycloak realm of tests/keycloak/elm-realm.json.</summary>
    public const string KeycloakVariable = "ELM_TEST_KEYCLOAK";

    private static readonly Dictionary<string, string> Roles = new(StringComparer.Ordinal) { ["elm-owners"] = "owner" };

    private readonly Dictionary<string, string> _environment = [];

    /// <summary>
    /// A person signs in with elm admin login against Keycloak, approves the code on Keycloak's pages, and calls the service as
    /// an owner; logout revokes the login.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Keycloak with the test realm (alice in elm-owners; the elm-cli client with the elm-service audience and a top-level
    ///   groups claim); a service on the real clock that trusts the realm.
    ///
    /// Steps:
    /// - Run elm admin login; at its first wait, open the printed verification URL with the code, post Keycloak's login form
    ///   as alice, and post the consent; run elm admin request GET /v1/admin/admins; run elm admin logout.
    ///
    /// Expected:
    /// - login 0; request 0 with one owner of Keycloak's issuer, enabled, with a last login; logout 0 "Signed out.".
    /// </remarks>
    [Fact]
    public async Task DeviceFlow_SignsInToTheService()
    {
        string realm = Realm();
        await using var test = new TestService();
        test.Time.SetUtcNow(TimeProvider.System.GetUtcNow());
        ElmService service = await test.StartAsync(s => s with { Oidc = Settings(realm, "elm-service") });
        _environment[AdminLoginFile.PathVariable] = test.FilePath(Path.Combine("home", "login"));

        (int code, string output, string error) = await Task.Run(() => Run("admin", "login", "--url", service.Url.ToString()));
        Assert.True(code == 0, error);

        (code, output, error) = Run("admin", "request", "GET", "/v1/admin/admins");
        Assert.True(code == 0, error);
        using JsonDocument page = JsonDocument.Parse(output);
        JsonElement admin = Assert.Single(page.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(
            (realm, "owner", true),
            (admin.GetProperty("issuer").GetString(), admin.GetProperty("role").GetString(), admin.GetProperty("enabled").GetBoolean())
        );
        Assert.Equal(JsonValueKind.String, admin.GetProperty("lastLoginAt").ValueKind);

        (code, output, _) = Run("admin", "logout");
        Assert.Equal((0, "Signed out."), (code, output.Trim()));
    }

    /// <summary>
    /// A Keycloak access token is refused by a service that wants another audience (design §14.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Keycloak with the test realm; a service on the real clock that trusts the realm with the audience other-service.
    ///
    /// Steps:
    /// - Take an access token for alice from the test-only client elm-test-direct (password grant); list administrators.
    ///
    /// Expected:
    /// - 401.
    /// </remarks>
    [Fact]
    public async Task AccessToken_OfAnotherAudience_IsRefused()
    {
        string realm = Realm();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await using var test = new TestService();
        test.Time.SetUtcNow(TimeProvider.System.GetUtcNow());
        ElmService other = await test.StartAsync(s => s with { Oidc = Settings(realm, "other-service") });

        using var http = new HttpClient();
        using var form = new FormUrlEncodedContent([
            new("grant_type", "password"),
            new("client_id", "elm-test-direct"),
            new("username", "alice"),
            new("password", "alice-test-only"),
            new("scope", "openid"),
        ]);
        using HttpResponseMessage issued = await http.PostAsync(new Uri(realm + "/protocol/openid-connect/token"), form, cancel);
        string body = await issued.Content.ReadAsStringAsync(cancel);
        Assert.True(issued.IsSuccessStatusCode, body);
        using JsonDocument tokens = JsonDocument.Parse(body);
        string access = tokens.RootElement.GetProperty("access_token").GetString()!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await test.Http(other, access).GetAsync("v1/admin/admins", cancel)).StatusCode);
    }

    /// <summary>
    /// The admin UI's sign-in end to end against Keycloak (design §15.3, M5b): the authorization code flow with PKCE and the
    /// confidential client elm-web, through Keycloak's own login page over HTTP (no browser); the session cookie then signs
    /// in to the administration API, and logout gives Keycloak's end of session.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Keycloak with the test realm (alice in elm-owners; the elm-web client with the secret elm-web-test-only, the redirect
    ///   URI http://127.0.0.1:18089/auth/callback, and the groups claim in the ID token); a service on the real clock that
    ///   listens on 127.0.0.1:18089 with the web sign-in.
    ///
    /// Steps:
    /// - GET /auth/login and follow it to Keycloak's login page; post the login form as alice and follow the redirects back
    ///   through the callback to /; list administrators with the session cookie; POST /auth/logout with X-ELM-CSRF; follow
    ///   the endSession URL.
    ///
    /// Expected:
    /// - The callback leads to / (200, the placeholder) with __Host-elm-session; the list holds alice as owner of Keycloak's
    ///   issuer; logout answers 200 with endSession of the realm's logout endpoint with client_id elm-web and the
    ///   post_logout_redirect_uri http://127.0.0.1:18089/; following it answers 200.
    /// </remarks>
    [Fact]
    public async Task CodeFlow_SignsInToTheAdminUi()
    {
        string realm = Realm();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await using var test = new TestService();
        test.Time.SetUtcNow(TimeProvider.System.GetUtcNow());
        var web = new WebLoginSettings(
            "elm-web",
            "elm-web-test-only",
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(WebLoginSettings.KekLength),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(12)
        );
        ElmService service = await test.StartAsync(s =>
            s with
            {
                Listen = new Uri("http://127.0.0.1:18089"),
                Oidc = Settings(realm, "elm-service"),
                Web = web,
            }
        );

        using var http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        var trace = new List<string>();
        (Uri page, HttpStatusCode status, string html) = await BrowseAsync(
            http,
            cookies,
            trace,
            HttpMethod.Get,
            new Uri(service.Url, "auth/login"),
            null
        );
        for (int step = 0; step < 3 && page.Port != service.Url.Port; step++)
        {
            Assert.True((int)status is >= 200 and < 300, $"Keycloak answered {(int)status}: {Excerpt(html)}{Steps(trace)}");
            Match form = FormAction().Match(html);
            Assert.True(
                form.Success && html.Contains("name=\"username\"", StringComparison.Ordinal),
                $"Not a login page: {Excerpt(html)}{Steps(trace)}"
            );
            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["username"] = "alice",
                ["password"] = "alice-test-only",
                ["credentialId"] = string.Empty,
            };
            (page, status, html) = await BrowseAsync(
                http,
                cookies,
                trace,
                HttpMethod.Post,
                new Uri(page, WebUtility.HtmlDecode(form.Groups["action"].Value)),
                fields
            );
        }

        Assert.True(status == HttpStatusCode.OK && page.AbsolutePath == "/", $"{(int)status} {page}: {Excerpt(html)}{Steps(trace)}");
        Assert.Equal("The admin UI is not built yet.", html.Trim());
        string session = cookies[ElmService.SessionCookie];

        using var admins = new HttpRequestMessage(HttpMethod.Get, new Uri(service.Url, "v1/admin/admins"));
        admins.Headers.Add("Cookie", ElmService.SessionCookie + "=" + session);
        using HttpResponseMessage listed = await http.SendAsync(admins, cancel);
        string text = await listed.Content.ReadAsStringAsync(cancel);
        Assert.True(listed.StatusCode == HttpStatusCode.OK, text);
        using (JsonDocument document = JsonDocument.Parse(text))
        {
            JsonElement alice = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal((realm, "owner"), (alice.GetProperty("issuer").GetString(), alice.GetProperty("role").GetString()));
        }

        using var logout = new HttpRequestMessage(HttpMethod.Post, new Uri(service.Url, "auth/logout"));
        logout.Headers.Add("Cookie", ElmService.SessionCookie + "=" + session);
        logout.Headers.Add(ElmService.CsrfHeader, "1");
        using HttpResponseMessage ended = await http.SendAsync(logout, cancel);
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        using JsonDocument answer = JsonDocument.Parse(await ended.Content.ReadAsStringAsync(cancel));
        string endSession = answer.RootElement.GetProperty("endSession").GetString()!;
        Assert.Equal(
            realm + "/protocol/openid-connect/logout?client_id=elm-web&post_logout_redirect_uri=" + Uri.EscapeDataString("http://127.0.0.1:18089/"),
            endSession
        );
        (_, status, html) = await BrowseAsync(http, cookies, trace, HttpMethod.Get, new Uri(endSession), null);
        Assert.True(status == HttpStatusCode.OK, $"{(int)status}: {Excerpt(html)}{Steps(trace)}");
    }

    private static string Realm()
    {
        string? realm = Environment.GetEnvironmentVariable(KeycloakVariable);
        Assert.SkipUnless(realm is { Length: > 0 }, $"Set {KeycloakVariable} to the Keycloak realm of tests/keycloak to run this test.");
        return realm;
    }

    private static OidcSettings Settings(string realm, string audience) =>
        new(realm, audience, "elm-cli", OidcSettings.DefaultScopes, "groups", Roles);

    // Approves a device code on Keycloak's pages, as a person would: the verification URL with the code leads to the login
    // page, the login to the consent page (Keycloak asks for consent in the device flow), and the consent ends the approval;
    // Keycloak then redirects to /device/status, which answers 404.
    //
    // Keycloak marks its session cookies Secure also on http; a browser sends them to 127.0.0.1 all the same, but .NET's
    // CookieContainer does not send Secure cookies over http. The cookies and the redirects are kept here, as a browser would.
    private static async Task ApproveAsync(string verification)
    {
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        var trace = new List<string>();
        (Uri page, HttpStatusCode status, string html) = await BrowseAsync(http, cookies, trace, HttpMethod.Get, new Uri(verification), null);
        for (int step = 0; step < 4; step++)
        {
            Assert.True((int)status is >= 200 and < 300, $"Keycloak answered {(int)status}: {Excerpt(html)}{Steps(trace)}");
            Match form = FormAction().Match(html);
            Assert.True(form.Success, $"Keycloak's page has no form: {Excerpt(html)}{Steps(trace)}");
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match input in HiddenInput().Matches(html))
            {
                fields[WebUtility.HtmlDecode(input.Groups["name"].Value)] = WebUtility.HtmlDecode(input.Groups["value"].Value);
            }

            bool consent = html.Contains("name=\"accept\"", StringComparison.Ordinal);
            if (consent)
            {
                fields["accept"] = "Yes";
            }
            else if (html.Contains("name=\"username\"", StringComparison.Ordinal))
            {
                fields["username"] = "alice";
                fields["password"] = "alice-test-only";
                fields["credentialId"] = string.Empty;
            }
            else
            {
                Assert.Fail($"Keycloak showed a page the test does not know: {Excerpt(html)}{Steps(trace)}");
            }

            // A form action may be relative to the page (/realms/...); it is resolved against the page's URL.
            var action = new Uri(page, WebUtility.HtmlDecode(form.Groups["action"].Value));
            (page, status, html) = await BrowseAsync(http, cookies, trace, HttpMethod.Post, action, fields);
            if (consent)
            {
                return;
            }
        }

        Assert.Fail("Keycloak did not reach the consent in 4 pages." + Steps(trace));
    }

    // Sends a request with the kept cookies, keeps the cookies of each answer, and follows up to 5 redirects with GET.
    private static async Task<(Uri Page, HttpStatusCode Status, string Html)> BrowseAsync(
        HttpClient http,
        Dictionary<string, string> cookies,
        List<string> trace,
        HttpMethod method,
        Uri url,
        Dictionary<string, string>? form
    )
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        for (int hop = 0; hop < 6; hop++)
        {
            using var request = new HttpRequestMessage(method, url);
            if (form is not null)
            {
                request.Content = new FormUrlEncodedContent(form);
            }

            if (cookies.Count > 0)
            {
                request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => c.Key + "=" + c.Value)));
            }

            using HttpResponseMessage answer = await http.SendAsync(request, cancel);
            if (answer.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? set))
            {
                foreach (string cookie in set)
                {
                    string pair = cookie.Split(';', 2)[0];
                    int equals = pair.IndexOf('=', StringComparison.Ordinal);
                    cookies[pair[..equals]] = pair[(equals + 1)..];
                }
            }

            trace.Add($"{method} {url.AbsolutePath} {(int)answer.StatusCode}");
            if ((int)answer.StatusCode is >= 300 and < 400 && answer.Headers.Location is Uri next)
            {
                url = new Uri(url, next);
                method = HttpMethod.Get;
                form = null;
                continue;
            }

            return (url, answer.StatusCode, await answer.Content.ReadAsStringAsync(cancel));
        }

        Assert.Fail("Keycloak redirected more than 5 times." + Steps(trace));
        return default;
    }

    private static string Excerpt(string html) => html.Length <= 3000 ? html : html[..3000];

    private static string Steps(List<string> trace) => Environment.NewLine + "Steps:" + Environment.NewLine + string.Join(Environment.NewLine, trace);

    [GeneratedRegex("<form[^>]*action=\"(?<action>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex FormAction();

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*name=\"(?<name>[^\"]+)\"[^>]*value=\"(?<value>[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenInput();

    [GeneratedRegex("Or open (?<url>\\S+)")]
    private static partial Regex Verification();

    private (int Code, string Out, string Error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        bool approved = false;
        var host = new CliHost
        {
            Out = output,
            Error = error,
            Time = TimeProvider.System,
            GetEnvironmentVariable = name => _environment.GetValueOrDefault(name),
            ReadSecret = null,
            Wait = wait =>
            {
                if (!approved && Verification().Match(error.ToString()) is { Success: true } url)
                {
                    approved = true;
                    ApproveAsync(url.Groups["url"].Value).GetAwaiter().GetResult();
                }

                Task.Delay(wait, TimeProvider.System).GetAwaiter().GetResult();
            },
        };
        int code = ElmApp.Run(args, host);
        return (code, output.ToString(), error.ToString());
    }
}
