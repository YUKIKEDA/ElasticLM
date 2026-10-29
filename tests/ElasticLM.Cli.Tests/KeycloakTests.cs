using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ElasticLM.Service;
using ElasticLM.Service.Tests;

namespace ElasticLM.Cli.Tests;

/// <summary>
/// The OIDC sign-in end to end against Keycloak (design §15.3, decision 0.97, M5a): <c>elm admin login</c> runs the device
/// flow, the device code is approved through Keycloak's own pages over HTTP (no browser), and <c>elm-service</c> accepts the
/// access token with JwtBearer. Runs where <c>ELM_TEST_KEYCLOAK</c> names the test realm (the Linux CI job).
/// </summary>
public sealed partial class KeycloakTests
{
    /// <summary>The variable that names the Keycloak realm of tests/keycloak/elm-realm.json.</summary>
    public const string KeycloakVariable = "ELM_TEST_KEYCLOAK";

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
    /// - Run elm admin login; at its first wait, open the printed verification URL and post Keycloak's login form as alice;
    ///   run elm admin request GET /v1/admin/admins; run elm admin logout.
    ///
    /// Expected:
    /// - login 0; request 0 with one owner of Keycloak's issuer, enabled, with a last login; logout 0 "Signed out.".
    /// </remarks>
    [Fact]
    public async Task DeviceFlow_SignsInToTheService()
    {
        string? realm = Environment.GetEnvironmentVariable(KeycloakVariable);
        Assert.SkipUnless(realm is { Length: > 0 }, $"Set {KeycloakVariable} to the Keycloak realm of tests/keycloak to run this test.");
        await using var test = new TestService();
        test.Time.SetUtcNow(TimeProvider.System.GetUtcNow());
        var oidc = new OidcSettings(
            realm,
            "elm-service",
            "elm-cli",
            OidcSettings.DefaultScopes,
            "groups",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["elm-owners"] = "owner" }
        );
        ElmService service = await test.StartAsync(s => s with { Oidc = oidc });
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

    // Approves a device code through Keycloak's pages: each page's first form is posted with the login, the user code, or the
    // consent filled in, until the page says the device is signed in.
    private static async Task ApproveAsync(string verification)
    {
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string html = await http.GetStringAsync(new Uri(verification), cancel);
        for (int step = 0; step < 6; step++)
        {
            if (html.Contains("kc-page-title", StringComparison.Ordinal) && Regex.IsMatch(html, "(?i)device.{0,40}(success|signed in|logged in)"))
            {
                return;
            }

            Match form = FormAction().Match(html);
            Assert.True(form.Success, "Keycloak's page has no form: " + html[..Math.Min(html.Length, 2000)]);
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match input in HiddenInput().Matches(html))
            {
                fields[WebUtility.HtmlDecode(input.Groups["name"].Value)] = WebUtility.HtmlDecode(input.Groups["value"].Value);
            }

            if (html.Contains("name=\"username\"", StringComparison.Ordinal))
            {
                fields["username"] = "alice";
                fields["password"] = "alice-test-only";
            }
            else if (html.Contains("name=\"accept\"", StringComparison.Ordinal))
            {
                fields["accept"] = "Yes";
            }
            else if (!html.Contains("name=\"device_user_code\"", StringComparison.Ordinal))
            {
                return;
            }

            using var content = new FormUrlEncodedContent(fields);
            using HttpResponseMessage answer = await http.PostAsync(new Uri(WebUtility.HtmlDecode(form.Groups["action"].Value)), content, cancel);
            html = await answer.Content.ReadAsStringAsync(cancel);
        }
    }

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
