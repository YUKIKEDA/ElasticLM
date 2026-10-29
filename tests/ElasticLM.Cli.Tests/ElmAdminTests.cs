using System.Text;
using System.Text.Json;
using ElasticLM.Service;
using ElasticLM.Service.Tests;

namespace ElasticLM.Cli.Tests;

/// <summary>
/// <c>elm admin login</c>, <c>logout</c>, and <c>request</c> (design §16, decision 0.98, M5a) against a real <c>elm-service</c>
/// that trusts a <see cref="FakeIdp"/>, with the waits of the device flow recorded and the fake clock moved by them.
/// </summary>
public sealed class ElmAdminTests
{
    private readonly Dictionary<string, string> _environment = [];
    private readonly List<TimeSpan> _waits = [];
    private Microsoft.Extensions.Time.Testing.FakeTimeProvider _time = new();

    /// <summary>
    /// The device flow (RFC 8628 §3.4, §3.5): the service URL and issuer are shown before polling, pending waits the interval,
    /// slow_down adds 5 seconds, and the tokens are kept in the protected login file; request then calls the API with them.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service that trusts the fake provider; the provider answers authorization_pending, then slow_down, then tokens;
    ///   ELM_ADMIN_LOGIN points into the test folder.
    ///
    /// Steps:
    /// - Run elm admin login --url; read the login file's bytes; run elm admin request GET /v1/admin/admins.
    ///
    /// Expected:
    /// - 0 "Signed in to …"; the error output names the service and issuer before the code ABCD-EFGH; waits of 1, 1, and 6
    ///   seconds; the file does not hold the access token as text on Windows and has mode 0600 elsewhere; the request is 0
    ///   and its output is the list with alice.
    /// </remarks>
    [Fact]
    public async Task Login_DeviceFlow_SavesTheLogin()
    {
        await using var test = new TestService();
        _time = test.Time;
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        ElmService service = await StartAsync(test, idp);
        string login = SetLoginFile(test);
        idp.DevicePolls.Enqueue("authorization_pending");
        idp.DevicePolls.Enqueue("slow_down");

        (int code, string output, string error) = Run("admin", "login", "--url", service.Url.ToString());
        Assert.True(code == 0, error);
        Assert.Equal($"Signed in to {service.Url}", output.Trim().TrimEnd('.'));
        int named = error.IndexOf($"Signing in to {service.Url} with the identity provider {idp.Issuer}.", StringComparison.Ordinal);
        Assert.InRange(named, 0, error.IndexOf("ABCD-EFGH", StringComparison.Ordinal));
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(6)], _waits);

        byte[] stored = File.ReadAllBytes(login);
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("accessToken", Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(login));
        }

        (code, output, error) = Run("admin", "request", "GET", "/v1/admin/admins");
        Assert.True(code == 0, error);
        using JsonDocument page = JsonDocument.Parse(output);
        Assert.Contains(page.RootElement.GetProperty("items").EnumerateArray(), a => a.GetProperty("subject").GetString() == "alice");
    }

    /// <summary>
    /// The device flow stops on any other error and when the code expires; a network failure doubles the interval; a service
    /// without OIDC is told (design §16).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service that trusts the fake provider and one without OIDC; ELM_ADMIN_LOGIN in the test folder.
    ///
    /// Steps:
    /// - Log in while the provider answers access_denied; expired_token; authorization_pending until past the code's 600
    ///   seconds; log in to the service without OIDC.
    ///
    /// Expected:
    /// - 1 with "ended the sign-in: access_denied."; 1 with "expired_token."; 1 "The device code expired …"; 1 "The service does
    ///   not accept OIDC sign-in; use ELM_API_KEY."; no login file in any case.
    /// </remarks>
    [Fact]
    public async Task Login_Refusals_EndTheFlow()
    {
        await using var test = new TestService();
        _time = test.Time;
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        ElmService service = await StartAsync(test, idp);
        ElmService plain = await test.StartAsync();
        string login = SetLoginFile(test);
        string url = service.Url.ToString();

        idp.DevicePolls.Enqueue("access_denied");
        Assert.Equal((1, "error: The identity provider ended the sign-in: access_denied."), Last(Run("admin", "login", "--url", url)));
        idp.DevicePolls.Enqueue("expired_token");
        Assert.Equal((1, "error: The identity provider ended the sign-in: expired_token."), Last(Run("admin", "login", "--url", url)));
        for (int i = 0; i < 700; i++)
        {
            idp.DevicePolls.Enqueue("authorization_pending");
        }

        Assert.Equal((1, "error: The device code expired before the sign-in was approved."), Last(Run("admin", "login", "--url", url)));
        Assert.Equal(
            (1, "error: The service does not accept OIDC sign-in; use ELM_API_KEY."),
            Last(Run("admin", "login", "--url", plain.Url.ToString()))
        );
        Assert.False(File.Exists(login));
    }

    /// <summary>
    /// A login that expires within 60 seconds is refreshed under the lock: a new refresh token replaces the old one, a missing
    /// one keeps it; invalid_grant asks for a new login (design §16, RFC 6749 §6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A signed-in login of five minutes at a service that trusts the fake provider.
    ///
    /// Steps:
    /// - Move the clock 4 minutes 1 second and request; move 5 minutes with rotation off and request twice; make the provider
    ///   forget its refresh tokens, move 5 minutes, and request.
    ///
    /// Expected:
    /// - 0 with one refresh grant and a new refresh token in the file; 0 twice with the same refresh token kept; 1 "The login
    ///   can no longer be refreshed; run 'elm admin login' again."
    /// </remarks>
    [Fact]
    public async Task Request_ExpiringLogin_IsRefreshed()
    {
        await using var test = new TestService();
        _time = test.Time;
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        ElmService service = await StartAsync(test, idp);
        string login = SetLoginFile(test);
        Assert.Equal(0, Run("admin", "login", "--url", service.Url.ToString()).Code);
        string? first = AdminLoginFile.Read(login)!.RefreshToken;

        test.Time.Advance(new TimeSpan(0, 4, 1));
        Assert.Equal(0, Run("admin", "request", "GET", "/v1/admin/admins").Code);
        string? second = AdminLoginFile.Read(login)!.RefreshToken;
        Assert.NotEqual(first, second);

        idp.Rotate = false;
        test.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, Run("admin", "request", "GET", "/v1/admin/admins").Code);
        test.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, Run("admin", "request", "GET", "/v1/admin/admins").Code);
        Assert.Equal(second, AdminLoginFile.Read(login)!.RefreshToken);

        idp.ForgetRefreshTokens();
        test.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(
            (1, "error: The login can no longer be refreshed; run 'elm admin login' again."),
            Last(Run("admin", "request", "GET", "/v1/admin/admins"))
        );
    }

    /// <summary>
    /// logout revokes the refresh token when the provider offers revocation and removes the file; without a login it says so
    /// (design §16).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A signed-in login at a service that trusts the fake provider.
    ///
    /// Steps:
    /// - Run elm admin logout twice.
    ///
    /// Expected:
    /// - 0 "Signed out." with the refresh token at the provider's revocation endpoint and no file; 0 "Not signed in.".
    /// </remarks>
    [Fact]
    public async Task Logout_RevokesAndRemoves()
    {
        await using var test = new TestService();
        _time = test.Time;
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        ElmService service = await StartAsync(test, idp);
        string login = SetLoginFile(test);
        Assert.Equal(0, Run("admin", "login", "--url", service.Url.ToString()).Code);
        string refresh = AdminLoginFile.Read(login)!.RefreshToken!;

        Assert.Equal((0, "Signed out."), Last(Run("admin", "logout"), output: true));
        Assert.Equal([refresh], idp.Revoked);
        Assert.False(File.Exists(login));
        Assert.Equal((0, "Not signed in."), Last(Run("admin", "logout"), output: true));
    }

    /// <summary>
    /// request checks its arguments before sending, sends the saved login only to its own origin, writes a refusal's body to
    /// the error output with exit code 1, uses ELM_API_KEY first, and is exit code 3 when the service cannot be reached
    /// (design §16).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A signed-in login at a service that trusts the fake provider, and an owner key from setup.
    ///
    /// Steps:
    /// - Request with method TRACE; paths /v1/other, /v1/admin/../x, /v1/admin/%2E%2E/x, /v1/admin/a#b, /v1/admin/a\b; a 64 KiB
    ///   + 1 body; --url of another port; a 403 (GET /v1/admin/admins as a viewer); with ELM_API_KEY; to a closed port with
    ///   ELM_API_KEY.
    ///
    /// Expected:
    /// - 2 for each argument; 2 naming the login's service; 1 with the problem body in the error output; 0 with the key's
    ///   answer; 3.
    /// </remarks>
    [Fact]
    public async Task Request_ChecksAndAnswers()
    {
        await using var test = new TestService();
        _time = test.Time;
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        ElmService service = await StartAsync(test, idp);
        SetLoginFile(test);
        idp.Groups = ["elm-viewers"];
        Assert.Equal(0, Run("admin", "login", "--url", service.Url.ToString()).Code);

        Assert.Equal(2, Run("admin", "request", "TRACE", "/v1/admin/admins").Code);
        foreach (string path in new[] { "/v1/other", "/v1/admin/../x", "/v1/admin/%2E%2E/x", "/v1/admin/a#b", "/v1/admin/a\\b" })
        {
            Assert.StartsWith("error: path must start with /v1/admin/", Run("admin", "request", "GET", path).Error, StringComparison.Ordinal);
        }

        string large = test.FilePath("large.json");
        await File.WriteAllBytesAsync(large, new byte[(64 * 1024) + 1], TestContext.Current.CancellationToken);
        Assert.Equal(2, Run("admin", "request", "POST", "/v1/admin/licenses", "--body", large).Code);

        string other = new UriBuilder(service.Url) { Port = service.Url.Port + 1 }.Uri.ToString();
        Assert.Equal(
            (2, $"error: Set ELM_API_KEY, or sign in to {other} with 'elm admin login'."),
            Last(Run("admin", "request", "GET", "/v1/admin/admins", "--url", other))
        );

        (int code, _, string error) = Run("admin", "request", "GET", "/v1/admin/admins");
        Assert.Equal(1, code);
        Assert.Contains("the service answered 403", error, StringComparison.Ordinal);
        Assert.Contains("ELM_FORBIDDEN", error, StringComparison.Ordinal);

        _environment["ELM_API_KEY"] = owner;
        (code, string output, _) = Run("admin", "request", "GET", "/v1/admin/api-keys");
        Assert.Equal(0, code);
        Assert.Contains("\"items\"", output, StringComparison.Ordinal);
        Assert.Equal(3, Run("admin", "request", "GET", "/v1/admin/admins", "--url", "http://127.0.0.1:1/").Code);
    }

    /// <summary>
    /// elm offline process uses the saved login for the administration entry when its origin is the --url's (design §12.8.4,
    /// §16): the audit row is by the OIDC administrator.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A signed-in owner login at a service that trusts the fake provider; a license and a device's activation file; an
    ///   owner key from setup to read the audit log.
    ///
    /// Steps:
    /// - Run elm offline process with the --url of the login, without ELM_API_KEY; read the audit log.
    ///
    /// Expected:
    /// - 0, and an activation.offline row with actor type oidc.
    /// </remarks>
    [Fact]
    public async Task OfflineProcess_UsesTheSavedLogin()
    {
        await using var test = new TestService();
        _time = test.Time;
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        ElmService service = await StartAsync(test, idp);
        HttpClient admin = test.Http(service, owner);
        SetLoginFile(test);
        Assert.Equal(0, Run("admin", "login", "--url", service.Url.ToString()).Code);

        string product = (await TestService.CreatedAsync(admin, "v1/admin/products", """{"code":"app","name":"app"}"""))
            .GetProperty("id")
            .GetString()!;
        await TestService.CreatedAsync(admin, $"v1/admin/products/{product}/features", """{"code":"core","name":"core"}""");
        string policy = await TestService.PolicyAsync(admin, product, string.Empty);
        JsonElement license = await TestService.CreatedAsync(
            admin,
            "v1/admin/licenses",
            $$"""{"policyId":"{{policy}}","name":"n","features":[{"code":"core"}]}"""
        );
        using var device = TestDevice.Create(test, service);
        string request = test.FilePath("a.elmreq");
        await File.WriteAllTextAsync(
            request,
            device.ActivationFile(license.GetProperty("key").GetString()!, TestDevice.Fingerprints()),
            TestContext.Current.CancellationToken
        );

        (int code, _, string error) = Run(
            "offline",
            "process",
            "--url",
            service.Url.ToString(),
            "--in",
            request,
            "--out",
            test.FilePath("a.elmresp")
        );
        Assert.True(code == 0, error);
        Assert.Contains(
            await TestService.AuditRowsAsync(test, service, owner),
            r => r.GetProperty("action").GetString() == "activation.offline" && r.GetProperty("actorType").GetString() == "oidc"
        );
    }

    private static Task<ElmService> StartAsync(TestService test, FakeIdp idp) => test.StartAsync(s => s with { Oidc = idp.Settings() });

    private static (int Code, string Text) Last((int Code, string Out, string Error) result, bool output = false) =>
        (result.Code, (output ? result.Out : result.Error).Trim().Split('\n')[^1].Trim());

    private string SetLoginFile(TestService test)
    {
        string path = test.FilePath(Path.Combine("home", "login"));
        _environment[AdminLoginFile.PathVariable] = path;
        return path;
    }

    private (int Code, string Out, string Error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var host = new CliHost
        {
            Out = output,
            Error = error,
            Time = _time,
            GetEnvironmentVariable = name => _environment.GetValueOrDefault(name),
            ReadSecret = null,
            Wait = wait =>
            {
                _waits.Add(wait);
                _time.Advance(wait);
            },
        };
        int code = ElmApp.Run(args, host);
        return (code, output.ToString(), error.ToString());
    }
}
