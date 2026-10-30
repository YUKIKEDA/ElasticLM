using System.Net;
using System.Text;
using System.Text.Json;
using ElasticLM.Service;
using ElasticLM.Service.Tests;

namespace ElasticLM.Native.Tests;

/// <summary>
/// Tests of the activation functions of libelasticlm (design §17.5) against a real elm-service on loopback, called through
/// unmanaged function pointers as C calls them (<see cref="ActivationApi"/>). The service and the activations share one
/// fake clock.
/// </summary>
[Collection(NativeClock.Name)]
public sealed class ActivationTests : IAsyncLifetime
{
    private const string Product = "app";
    private const string UnknownKey = "ELM-AAAAA-AAAAA-AAAAA-AAAAA";
    private readonly NativeApi _api = new();
    private TestService _test = null!;
    private ElmService _service = null!;
    private HttpClient _admin = null!;
    private string _productId = null!;
    private string _root = null!;

    public ValueTask InitializeAsync() => OpenAsync(null);

    // A service on loopback with product app and feature core; the activations take its fake clock.
    private async ValueTask OpenAsync(Func<ServiceSettings, ServiceSettings>? change)
    {
        _test = new TestService();
        string owner = await _test.SetupAsync();
        _service = await _test.StartAsync(change);
        _admin = _test.Http(_service, owner);
        _productId = (await TestService.CreatedAsync(_admin, "v1/admin/products", $$"""{"code":"{{Product}}","name":"App"}"""))
            .GetProperty("id")
            .GetString()!;
        await TestService.CreatedAsync(_admin, $"v1/admin/products/{_productId}/features", """{"code":"core","name":"Core"}""");
        _root = File.ReadAllText(_test.FilePath("root.pub.pem"));
        Exports.Time = _test.Time;
        StateCallbacks.Reset();
    }

    public async ValueTask DisposeAsync()
    {
        Exports.Time = TimeProvider.System;
        StateCallbacks.Reset();
        _api.Dispose();
        await _test.DisposeAsync();
    }

    /// <summary>
    /// Online activation, check, ID, state, and deactivation, with the callbacks they cause.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license of a policy with 3 activations; an activation with the service URL and the state callback (user 42).
    ///
    /// Steps:
    /// - Read the state and the ID; activate; read the state and the ID (size 0, then enough); check feature core;
    ///   deactivate; read the state and the ID.
    ///
    /// Expected:
    /// - State 0 and ID "" (needed 1); activate 0 with a standard license not in grace and retry 0; state 1; for size 0,
    ///   ELM_INVALID_ARGUMENT with the ID's byte count plus 1 in needed, then the ID; check 0; deactivate 0; state 0 and ID "".
    ///   The callbacks 0→1 and 1→0 with user 42, error 0, and an empty message, each seen before its function returned.
    /// </remarks>
    [Fact]
    public async Task Activate_Check_Deactivate_WithCallbacks()
    {
        string key = (await LicenseAsync("\"maxActivations\":3")).Key;
        nint activation = Create("a");
        Assert.Equal(0, ActivationApi.State(activation));
        Assert.Equal((0, string.Empty, (nuint)1), (ActivationApi.Id(activation, 64, out string none, out nuint noneNeeded), none, noneNeeded));

        Assert.Equal(0, ActivationApi.Activate(_api, activation, key, out nint license, out long retry));
        Assert.Equal((0, 0, 0L), (ActivationApi.Kind(license), ActivationApi.InGrace(license), retry));
        Assert.Equal([(0, 1, 0, string.Empty, (nint)42)], StateCallbacks.Changes());
        Assert.Equal(1, ActivationApi.State(activation));

        Assert.Equal(1, ActivationApi.Id(activation, 0, out _, out nuint needed));
        Assert.Equal(0, ActivationApi.Id(activation, 128, out string id, out _));
        Assert.Equal((nuint)Encoding.UTF8.GetByteCount(id) + 1, needed);
        Assert.NotEqual(string.Empty, id);

        Assert.Equal(0, ActivationApi.Check(_api, activation, "core", out nint checkedLicense));
        Assert.Equal(0, ActivationApi.Deactivate(activation));
        Assert.Equal((0, 0, string.Empty), (ActivationApi.State(activation), ActivationApi.Id(activation, 64, out string after, out _), after));
        Assert.Equal([(0, 1, 0, string.Empty, (nint)42), (1, 0, 0, string.Empty, (nint)42)], StateCallbacks.Changes());

        NativeApi.FreeLicense(license);
        NativeApi.FreeLicense(checkedLicense);
        ActivationApi.Destroy(activation);
    }

    /// <summary>
    /// A refused activation gives the service's number, null, and no retry; the service's rate limit gives its Retry-After.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service that takes 2 requests a second from one installation key (rateLimit.clientPerKey); an activation with
    ///   the service URL; a license key of 3 activations.
    ///
    /// Steps:
    /// - Activate with an unknown key (retry given, then retry NULL); activate with a null out; move the clock a second;
    ///   then, in that second, activate the real key and refresh until the service limits the installation's requests.
    ///
    /// Expected:
    /// - ELM_LICENSE_NOT_FOUND (30) with out null and retry 0, twice; ELM_INVALID_ARGUMENT (1); then ELM_RATE_LIMITED (52)
    ///   with retry 1 (the service's Retry-After) and out null.
    /// </remarks>
    [Fact]
    public async Task Failures_GiveTheNumberNullAndRetryAfter()
    {
        await _test.DisposeAsync();
        await OpenAsync(settings => settings with { RateLimitClientPerKey = 2 });
        string key = (await LicenseAsync("\"maxActivations\":3")).Key;
        nint activation = Create("a");
        Assert.Equal((30, (nint)0, 0L), (ActivationApi.Activate(_api, activation, UnknownKey, out nint license, out long retry), license, retry));
        Assert.NotEqual(string.Empty, NativeApi.LastError);
        Assert.Equal(30, ActivationApi.Activate(_api, activation, UnknownKey, out _, out _, nullRetry: true));
        Assert.Equal(1, ActivationApi.Activate(_api, activation, key, out _, out _, nullOut: true));

        // A new second fills the installation's bucket again.
        _test.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, ActivationApi.Activate(_api, activation, key, out license, out _));
        NativeApi.FreeLicense(license);
        int code = 0;
        for (int i = 0; i < 6 && code != 52; i++)
        {
            code = ActivationApi.Refresh(activation, out license, out retry);
            NativeApi.FreeLicense(license);
        }

        Assert.Equal((52, 1L, (nint)0), (code, retry, license));
        ActivationApi.Destroy(activation);
    }

    /// <summary>
    /// The options are checked, and an offline-only activation refuses the online operations.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None beyond the service.
    ///
    /// Steps:
    /// - Create with: an https URL (no request is sent), http on a loopback address, no URL, an app version of 64 bytes;
    ///   then http on another host, a text that is not a URL, an app version of 65 bytes, send_client_info 2,
    ///   struct_size one less; a null out. Activate with an offline-only activation.
    ///
    /// Expected:
    /// - 0 four times; ELM_INVALID_ARGUMENT (1) for the rest, with out null; the options are 112 bytes; the offline
    ///   activation's activate is ELM_INVALID_STATE (19).
    /// </remarks>
    [Fact]
    public void Options_AreChecked_AndOfflineRefusesOnline()
    {
        Assert.Equal(112, ActivationApi.SizeOfOptions);
        foreach (
            ActivationOptions good in new[]
            {
                Options("h", "https://licenses.example.com/"),
                Options("l"),
                Options("o", null),
                ActivationApi.WithAppVersion(_api, Options("v"), new string('v', 64)),
            }
        )
        {
            Assert.Equal(0, ActivationApi.Create(_api, good, out nint made));
            ActivationApi.Destroy(made);
        }

        ActivationOptions flag = Options("f");
        flag.SendClientInfo = 2;
        ActivationOptions smaller = Options("s");
        smaller.StructSize--;
        foreach (
            ActivationOptions bad in new[]
            {
                Options("x", "http://licenses.example.com/"),
                Options("y", "not a url"),
                ActivationApi.WithAppVersion(_api, Options("w"), new string('v', 65)),
                flag,
                smaller,
            }
        )
        {
            Assert.Equal((1, (nint)0), (ActivationApi.Create(_api, bad, out nint made), made));
        }

        Assert.Equal(1, ActivationApi.Create(_api, Options("z"), out _, nullOut: true));
        nint offline = Create("o", null);
        Assert.Equal(19, ActivationApi.Activate(_api, offline, UnknownKey, out _, out _));
        ActivationApi.Destroy(offline);
    }

    /// <summary>
    /// Offline files: an activation request and its renewal, the answer imported, a deactivation proof, and forgetting it;
    /// the answer's bounds.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An offline-only activation; a license key of 3 activations.
    ///
    /// Steps:
    /// - Make an activation request, make it again with renew 1, have the service process it, import the answer; make a
    ///   deactivation proof and forget it; import 262,145 bytes, 262,144 bytes of spaces, and a null response; free a null
    ///   string.
    ///
    /// Expected:
    /// - A request with replaced 0, then another with replaced 1; the import gives a license and state 1; the proof is
    ///   text; forget is 0; ELM_MALFORMED (2) for both sizes (the larger before reading); ELM_INVALID_ARGUMENT (1) for
    ///   null with out null; no crash.
    /// </remarks>
    [Fact]
    public async Task OfflineFiles_RoundTrip_AndTheAnswersBounds()
    {
        string key = (await LicenseAsync("\"maxActivations\":3")).Key;
        nint activation = Create("o", null);
        Assert.Equal(0, ActivationApi.Request(_api, activation, key, 0, out string first, out int firstReplaced));
        Assert.Equal(0, ActivationApi.Request(_api, activation, key, 1, out string second, out int secondReplaced));
        Assert.Equal((0, 1), (firstReplaced, secondReplaced));
        Assert.NotEqual(first, second);

        string answer = await ProcessAsync(second);
        Assert.Equal(0, ActivationApi.Import(_api, activation, Encoding.UTF8.GetBytes(answer), out nint license));
        Assert.Equal(1, ActivationApi.State(activation));
        NativeApi.FreeLicense(license);

        Assert.Equal(0, ActivationApi.Proof(activation, out string proof, out _));
        Assert.StartsWith("ey", proof);
        Assert.Equal(0, ActivationApi.Forget(activation));

        Assert.Equal(2, ActivationApi.Import(_api, activation, new byte[262_145], out _));
        Assert.Equal(2, ActivationApi.Import(_api, activation, Enumerable.Repeat((byte)' ', 262_144).ToArray(), out _));
        Assert.Equal((1, (nint)0), (ActivationApi.Import(_api, activation, null, out nint none), none));
        ActivationApi.FreeNullString();
        ActivationApi.Destroy(activation);
    }

    /// <summary>
    /// An answer imported after its token's expiry but inside the grace period gives a license in its grace period.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A policy with a token lifetime of 1 day, a refresh window of 1 hour, and a grace period of 2 days; an offline-only
    ///   activation.
    ///
    /// Steps:
    /// - Make a request, have it processed, move the clock 25 hours, import the answer.
    ///
    /// Expected:
    /// - 0 and elm_license_in_grace_period 1.
    /// </remarks>
    [Fact]
    public async Task Import_InsideGrace_IsInGrace()
    {
        string key = (await LicenseAsync("\"maxActivations\":3,\"tokenLifetime\":86400,\"refreshWindow\":3600,\"gracePeriod\":172800")).Key;
        nint activation = Create("o", null);
        Assert.Equal(0, ActivationApi.Request(_api, activation, key, 0, out string request, out _));
        string answer = await ProcessAsync(request);
        _test.Time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(0, ActivationApi.Import(_api, activation, Encoding.UTF8.GetBytes(answer), out nint license));
        Assert.Equal(1, ActivationApi.InGrace(license));
        NativeApi.FreeLicense(license);
        ActivationApi.Destroy(activation);
    }

    /// <summary>
    /// Inside its own callback, an activation refuses its blocking calls and destroy, allows state, ID, and check, and the
    /// caller's message survives the callback's calls.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An activation whose callback calls, on the same handle: refresh, deactivate, destroy, state, ID, and check.
    ///
    /// Steps:
    /// - Activate.
    ///
    /// Expected:
    /// - Inside: refresh and deactivate ELM_INVALID_STATE (19); destroy does nothing (the handle works afterwards); state 1;
    ///   the ID read; check 0. Outside: activate 0 with an empty message.
    /// </remarks>
    [Fact]
    public async Task InsideTheCallback_BlockingCallsAreRefused()
    {
        string key = (await LicenseAsync("\"maxActivations\":3")).Key;
        nint activation = Create("a");
        StateCallbacks.Reenter = activation;
        Assert.Equal(0, ActivationApi.Activate(_api, activation, key, out nint license, out _));
        Assert.Equal(string.Empty, NativeApi.LastError);
        StateCallbacks.Reenter = 0;
        Assert.Equal(["refresh 19", "deactivate 19", "state 1", "id ok", "check 0"], StateCallbacks.Inside.ToArray());
        Assert.Equal(1, ActivationApi.State(activation));
        NativeApi.FreeLicense(license);
        ActivationApi.Destroy(activation);
    }

    /// <summary>
    /// The automatic refresh calls back from another thread; destroy waits for a running callback; no callback comes after
    /// destroy; a refresh already due when the activation is made is not missed.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A policy with a token lifetime of 1 day and a refresh window of 1 hour; activation A activated, then the license
    ///   suspended.
    ///
    /// Steps:
    /// - Hold the callback; move the clock 23 hours and on in small steps until the callback is entered; destroy A on
    ///   another thread and see it has not returned after 300 ms; release the callback; then move the clock a day. Make
    ///   activation B on the same folder after another day (a refresh already due) and wait for its callback.
    ///
    /// Expected:
    /// - A's callback 1→3 (refused) with error 31 and a message, on a thread other than the test's; destroy returns only
    ///   after the release; no callback after destroy. B's first callback is seen.
    /// </remarks>
    [Fact]
    public async Task TimerCallbacks_Destroy_AndTheRefreshDueAtTheStart()
    {
        (string id, string key) = await LicenseAsync("\"maxActivations\":3,\"tokenLifetime\":86400,\"refreshWindow\":3600");
        nint a = Create("a");
        Assert.Equal(0, ActivationApi.Activate(_api, a, key, out nint license, out _));
        NativeApi.FreeLicense(license);
        await TestService.PostOkAsync(_admin, $"v1/admin/licenses/{id}/suspend", "{}");
        StateCallbacks.Reset();

        using var hold = new ManualResetEventSlim(false);
        StateCallbacks.Hold = hold;
        _test.Time.Advance(TimeSpan.FromHours(23));
        await _test.WaitWithClockAsync(() => Task.FromResult(StateCallbacks.Entered.IsSet));
        Task destroy = Task.Run(() => ActivationApi.Destroy(a), TestContext.Current.CancellationToken);
        Assert.NotSame(destroy, await Task.WhenAny(destroy, Task.Delay(300, TestContext.Current.CancellationToken)));
        hold.Set();
        await destroy;
        StateCallbacks.Hold = null;

        (int previous, int current, int error, string message, _) = Assert.Single(StateCallbacks.Changes());
        Assert.Equal((1, 3, 31), (previous, current, error));
        Assert.NotEqual(string.Empty, message);
        Assert.NotEqual(Environment.CurrentManagedThreadId, StateCallbacks.LastThread);
        _test.Time.Advance(TimeSpan.FromDays(1));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Single(StateCallbacks.Changes());

        StateCallbacks.Reset();
        _test.Time.Advance(TimeSpan.FromDays(1));
        nint b = Create("a");
        await _test.WaitWithClockAsync(() => Task.FromResult(!StateCallbacks.Seen.IsEmpty));
        ActivationApi.Destroy(b);
    }

    private async Task<(string Id, string Key)> LicenseAsync(string policyMembers)
    {
        string policy = await TestService.PolicyAsync(_admin, _productId, policyMembers);
        JsonElement license = await TestService.CreatedAsync(
            _admin,
            "v1/admin/licenses",
            $$"""{"policyId":"{{policy}}","name":"n","features":[{"code":"core"}]}"""
        );
        return (license.GetProperty("id").GetString()!, license.GetProperty("key").GetString()!);
    }

    private async Task<string> ProcessAsync(string file)
    {
        using var http = new HttpClient { BaseAddress = _service.Url };
        using HttpResponseMessage response = await http.PostAsync("v1/offline", new StringContent(file), TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return body;
    }

    private ActivationOptions Options(string folder, string? url = "service") =>
        ActivationApi.Options(
            _api,
            Product,
            url == "service" ? _service.Url.ToString() : url,
            _root,
            Path.Combine(_test.Directory, "client-" + folder)
        );

    private nint Create(string folder, string? url = "service")
    {
        int code = ActivationApi.Create(_api, Options(folder, url), out nint activation);
        Assert.True(code == 0, NativeApi.LastError);
        return activation;
    }
}
