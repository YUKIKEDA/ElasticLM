using System.Net;
using System.Text.Json;
using ElasticLM.Licensing;
using ElasticLM.Service;
using ElasticLM.Service.Tests;

namespace ElasticLM.Client.Tests;

/// <summary>
/// <see cref="ActivationClient"/> (design §12.6.2) against a real <c>elm-service</c> on loopback, on SQLite: activation,
/// refresh, deactivation, trials, lost answers, the lock file, refusals, and the kept file.
/// </summary>
public sealed class ActivationClientTests
{
    private const string Product = "app";

    /// <summary>
    /// A license key activates the installation; the kept file checks offline; a refresh moves the sequence number; a
    /// deactivation removes the file and the key, and a later activation makes a new key.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product app with feature core; a license of 2 activations; a client of a fresh folder.
    ///
    /// Steps:
    /// - Check before activating; activate; check core; refresh; deactivate; check; look for the key file; activate again.
    ///
    /// Expected:
    /// - ELM_LICENSE_NOT_FOUND; Activated with sequence 1, state Active, the activation ID; Valid; sequence 2; null, state
    ///   None, ID null; ELM_LICENSE_NOT_FOUND; no key file; Activated with another key thumbprint.
    /// </remarks>
    [Fact]
    public async Task Activate_RefreshDeactivate_FollowTheDesign()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync("\"maxActivations\":2");
        await using ActivationClient client = vendor.Client("a");

        Assert.Equal(ErrorCode.LicenseNotFound, Invalid(client.Check("core", null)).Code);
        VerifiedLicense license = Activated(await client.ActivateAsync(key, Cancel));
        Assert.Equal((1L, ActivationState.Active), (license.Sequence, client.State));
        Assert.NotNull(client.ActivationId);
        string firstKey = license.InstallationKeyThumbprint!;
        Assert.IsType<LicenseVerificationResult.Valid>(client.Check("core", null));
        Assert.Equal(2L, Activated(await client.RefreshAsync(Cancel)).Sequence);

        Assert.Null(await client.DeactivateAsync(Cancel));
        Assert.Equal((ActivationState.None, (string?)null), (client.State, client.ActivationId));
        Assert.Equal(ErrorCode.LicenseNotFound, Invalid(client.Check("core", null)).Code);
        Assert.False(File.Exists(Path.Combine(vendor.Folder("a"), "installation.key")));
        Assert.NotEqual(firstKey, Activated(await client.ActivateAsync(key, Cancel)).InstallationKeyThumbprint);
    }

    /// <summary>
    /// The client refreshes on its own when the token's refresh window starts, and tells a refusal by an event while the kept
    /// file stays usable.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A policy with tokens of a day and a refresh window of 12 hours; an activated client with a state handler.
    ///
    /// Steps:
    /// - Advance 12 hours and wait for the refresh; suspend the license; advance 12 hours and wait for the state; check.
    ///
    /// Expected:
    /// - The service's activation reaches sequence 2; the state becomes Refused with ELM_LICENSE_SUSPENDED in the event; the
    ///   check is still Valid.
    /// </remarks>
    [Fact]
    public async Task Refresh_OnItsOwn_WhenTheWindowStarts()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        (string licenseId, string key) = await vendor.LicenseWithIdAsync("\"tokenLifetime\":86400,\"refreshWindow\":43200");
        await using ActivationClient client = vendor.Client("a");
        var changes = new List<ActivationStateChangedEventArgs>();
        client.StateChanged += (_, e) =>
        {
            lock (changes)
            {
                changes.Add(e);
            }
        };
        Activated(await client.ActivateAsync(key, Cancel));

        vendor.Test.Time.Advance(TimeSpan.FromHours(12));
        await TestService.WaitForAsync(async () => (await vendor.ActivationAsync(client.ActivationId!)).GetProperty("seq").GetInt64() == 2);

        (await vendor.Admin.PostAsync($"v1/admin/licenses/{licenseId}/suspend", null, TestContext.Current.CancellationToken)).Dispose();
        vendor.Test.Time.Advance(TimeSpan.FromHours(12));
        await TestService.WaitForAsync(() => Task.FromResult(client.State == ActivationState.Refused));
        lock (changes)
        {
            Assert.Equal(ErrorCode.LicenseSuspended, changes.Last().Error!.Code);
        }

        Assert.IsType<LicenseVerificationResult.Valid>(client.Check("core", null));
    }

    /// <summary>
    /// An answer lost after the service acted is recovered by sending again with the lost request in retry (design §10.4,
    /// §12.6.1): the first activation, a refresh, and a trial get the same file; a deactivation counts as done.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license, and a product with a trial policy; clients whose HTTP handler drops the answer of the next POST.
    ///
    /// Steps:
    /// - Lose the first activation's answer and activate again; lose a refresh's answer and refresh again; lose a trial's
    ///   answer and start it again; lose a deactivation's answer and deactivate again.
    ///
    /// Expected:
    /// - The network error, then the same activation ID and file; the error, then sequence 2 with the same file as the service
    ///   issued; the same for the trial; the error, then null. The service has one activation of the license at each step.
    /// </remarks>
    [Fact]
    public async Task LostAnswers_AreRecoveredBySendingAgain()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        (string licenseId, string key) = await vendor.LicenseWithIdAsync(string.Empty);
        await vendor.TrialPolicyAsync();
        var lose = new LoseNextAnswer();
        await using ActivationClient client = vendor.Client("a", lose);

        lose.Armed = true;
        Assert.Equal(ErrorCode.NetworkError, Failed(await client.ActivateAsync(key, Cancel)).Code);
        VerifiedLicense first = Activated(await client.ActivateAsync(key, Cancel));
        Assert.Equal(1L, first.Sequence);
        Assert.Equal(1, await vendor.ActivationCountAsync(licenseId));

        lose.Armed = true;
        Assert.Equal(ErrorCode.NetworkError, Failed(await client.RefreshAsync(Cancel)).Code);
        Assert.Equal(2L, Activated(await client.RefreshAsync(Cancel)).Sequence);
        Assert.Equal(3L, Activated(await client.RefreshAsync(Cancel)).Sequence);

        lose.Armed = true;
        Assert.Equal(ErrorCode.NetworkError, await client.DeactivateAsync(Cancel) is LicenseError e ? e.Code : ErrorCode.Ok);
        Assert.Null(await client.DeactivateAsync(Cancel));
        Assert.Equal(0, await vendor.ActivationCountAsync(licenseId));

        lose.Armed = true;
        Assert.Equal(ErrorCode.NetworkError, Failed(await client.StartTrialAsync(Cancel)).Code);
        VerifiedLicense trial = Activated(await client.StartTrialAsync(Cancel));
        Assert.Equal((LicenseKind.Trial, 1L), (trial.Kind, trial.Sequence));
        Assert.Equal(1, (await vendor.GetAsync("v1/admin/trials")).GetProperty("items").GetArrayLength());
    }

    /// <summary>
    /// Two processes of one device refresh at once without being taken for clones: the lock file makes them wait for each
    /// other, and the second reads the first's file again before it sends (design §12.5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two clients on the same folder (one installation key); an activation.
    ///
    /// Steps:
    /// - Refresh with both at once, moving the clock while they wait; read the audit log.
    ///
    /// Expected:
    /// - Both Activated, with sequence numbers 2 and 3; no activation.clone row.
    /// </remarks>
    [Fact]
    public async Task Refresh_FromTwoProcesses_IsNotAClone()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync(string.Empty);
        await using ActivationClient one = vendor.Client("a");
        await using ActivationClient two = vendor.Client("a");
        Activated(await one.ActivateAsync(key, Cancel));

        Task<ActivationResult>[] both = [one.RefreshAsync(Cancel), two.RefreshAsync(Cancel)];
        await vendor.AdvanceUntilAsync(Task.WhenAll(both));
        Assert.Equal([2L, 3L], both.Select(t => Activated(t.Result).Sequence!.Value).Order());
        Assert.DoesNotContain(await vendor.AuditAsync(), r => r.GetProperty("action").GetString() == "activation.clone");
    }

    /// <summary>
    /// Refusals change the state as designed: an activation an administrator removed is forgotten; a clone and a revoked
    /// license keep the file.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A policy of 5 activations; client A activated; a copy of A's folder (a cloned machine) as client B.
    ///
    /// Steps:
    /// - B refreshes; A refreshes; A checks. Activate client C; an administrator deactivates it; C refreshes and checks.
    ///   Activate D; revoke the license; D refreshes and checks.
    ///
    /// Expected:
    /// - B Activated; A Failed ELM_CLONE_DETECTED, state CloneDetected, check Valid. C Failed ELM_ACTIVATION_NOT_FOUND,
    ///   state None, ID null, check ELM_LICENSE_NOT_FOUND. D Failed ELM_LICENSE_REVOKED, state Refused, check Valid.
    /// </remarks>
    [Fact]
    public async Task Refusals_ChangeTheStateAsDesigned()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        (string licenseId, string key) = await vendor.LicenseWithIdAsync("\"maxActivations\":5");
        await using ActivationClient a = vendor.Client("a");
        Activated(await a.ActivateAsync(key, Cancel));
        CopyFolder(vendor.Folder("a"), vendor.Folder("b"));
        await using ActivationClient b = vendor.Client("b");

        Activated(await b.RefreshAsync(Cancel));
        Assert.Equal(ErrorCode.CloneDetected, Failed(await a.RefreshAsync(Cancel)).Code);
        Assert.Equal(ActivationState.CloneDetected, a.State);
        Assert.IsType<LicenseVerificationResult.Valid>(a.Check("core", null));

        await using ActivationClient c = vendor.Client("c");
        Activated(await c.ActivateAsync(key, Cancel));
        (await vendor.Admin.PostAsync($"v1/admin/activations/{c.ActivationId}/deactivate", null, TestContext.Current.CancellationToken)).Dispose();
        Assert.Equal(ErrorCode.ActivationNotFound, Failed(await c.RefreshAsync(Cancel)).Code);
        Assert.Equal((ActivationState.None, (string?)null), (c.State, c.ActivationId));
        Assert.Equal(ErrorCode.LicenseNotFound, Invalid(c.Check("core", null)).Code);

        await using ActivationClient d = vendor.Client("d");
        Activated(await d.ActivateAsync(key, Cancel));
        (await vendor.Admin.PostAsync($"v1/admin/licenses/{licenseId}/revoke", null, TestContext.Current.CancellationToken)).Dispose();
        Assert.Equal(ErrorCode.LicenseRevoked, Failed(await d.RefreshAsync(Cancel)).Code);
        Assert.Equal(ActivationState.Refused, d.State);
        Assert.IsType<LicenseVerificationResult.Valid>(d.Check("core", null));
    }

    /// <summary>
    /// A trial is refused while an activation is kept; after a trial, a bought license activates with the same key and
    /// replaces it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product app with a trial policy and a standard license.
    ///
    /// Steps:
    /// - Start a trial; start another; activate the license key; check as Standard.
    ///
    /// Expected:
    /// - Activated trial; Failed ELM_INVALID_STATE; Activated with the license's ID; Valid with that ID.
    /// </remarks>
    [Fact]
    public async Task Trial_ThenPurchase_ReplacesTheTrial()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        (string licenseId, string key) = await vendor.LicenseWithIdAsync(string.Empty);
        await vendor.TrialPolicyAsync();
        await using ActivationClient client = vendor.Client("a");

        Assert.Equal(LicenseKind.Trial, Activated(await client.StartTrialAsync(Cancel)).Kind);
        Assert.Equal(ErrorCode.InvalidState, Failed(await client.StartTrialAsync(Cancel)).Code);
        Assert.Equal(licenseId, Activated(await client.ActivateAsync(key, Cancel)).LicenseId);
        Assert.Equal(licenseId, Assert.IsType<LicenseVerificationResult.Valid>(client.Check("core", null)).License.LicenseId);
    }

    /// <summary>
    /// A file that does not verify for this installation is not kept, and a damaged or oversized kept file is reported and
    /// replaced by the next activation.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license; a client whose trust store has another root key; a client with the right one.
    ///
    /// Steps:
    /// - Activate with the wrong trust; check. With the right client: write text that is not JSON as the kept file; check;
    ///   write a file of 256 KiB + 1; check; activate; check.
    ///
    /// Expected:
    /// - Failed with the verifier's error (ELM_UNTRUSTED_KEY); ELM_LICENSE_NOT_FOUND. ELM_MALFORMED twice; Activated; Valid.
    /// </remarks>
    [Fact]
    public async Task KeptFile_IsCheckedAndBounded()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync("\"maxActivations\":2");
        using var otherRoot = Issuing.KeyFiles.CreateKey();
        await using ActivationClient wrong = vendor.Client("w", trust: TrustStore.Create([Issuing.KeyFiles.ExportPublic(otherRoot)], []));
        Assert.Equal(ErrorCode.UntrustedKey, Failed(await wrong.ActivateAsync(key, Cancel)).Code);
        Assert.Equal(ErrorCode.LicenseNotFound, Invalid(wrong.Check("core", null)).Code);

        await using ActivationClient client = vendor.Client("a");
        string kept = Path.Combine(vendor.Folder("a"), "activation.json");
        File.WriteAllText(kept, "not json");
        Assert.Equal(ErrorCode.Malformed, Invalid(client.Check("core", null)).Code);
        File.WriteAllText(kept, new string(' ', ActivationStore.MaxLength + 1));
        Assert.Equal(ErrorCode.Malformed, Invalid(client.Check("core", null)).Code);
        Activated(await client.ActivateAsync(key, Cancel));
        Assert.IsType<LicenseVerificationResult.Valid>(client.Check("core", null));
    }

    /// <summary>
    /// The settings are checked: the service URL is https, or http on loopback only; the app version is at most 64 bytes.
    /// A request waits at most 10 seconds for another process's lock.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A running service; a license.
    ///
    /// Steps:
    /// - Create clients with http://elm.example/, a relative URL, an app version of 64 and of 65 bytes. Hold the lock file
    ///   and refresh, moving the clock 10 seconds.
    ///
    /// Expected:
    /// - ArgumentException, ArgumentException, created, ArgumentException. Failed ELM_INVALID_STATE "Another process of this
    ///   device holds the activation lock."
    /// </remarks>
    [Fact]
    public async Task Settings_AndLockWait_AreBounded()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync(string.Empty);
        Assert.Throws<ArgumentException>(() => vendor.Client("x", url: new Uri("http://elm.example/")));
        Assert.Throws<ArgumentException>(() => vendor.Client("x", url: new Uri("relative", UriKind.Relative)));
        await vendor.Client("x", app: new string('a', 64)).DisposeAsync();
        Assert.Throws<ArgumentException>(() => vendor.Client("x", app: new string('a', 65)));

        await using ActivationClient client = vendor.Client("a");
        Activated(await client.ActivateAsync(key, Cancel));
        ActivationResult result;
        using (new FileStream(Path.Combine(vendor.Folder("a"), "activation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Task<ActivationResult> refresh = client.RefreshAsync(Cancel);
            await vendor.AdvanceUntilAsync(refresh);
            result = await refresh;
        }

        LicenseError error = Failed(result);
        Assert.Equal((ErrorCode.InvalidState, "Another process of this device holds the activation lock."), (error.Code, error.Message));
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static VerifiedLicense Activated(ActivationResult result) =>
        result is ActivationResult.Activated activated ? activated.License : throw new InvalidOperationException(result.ToString());

    private static LicenseError Failed(ActivationResult result) =>
        result is ActivationResult.Failed failed ? failed.Error : throw new InvalidOperationException(result.ToString());

    private static LicenseError Invalid(LicenseVerificationResult result) => Assert.IsType<LicenseVerificationResult.Invalid>(result).Error;

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
        {
            if (!file.EndsWith(".lock", StringComparison.Ordinal))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            }
        }
    }

    // Drops the answer of the next POST after the service handled it, as a lost connection would.
    private sealed class LoseNextAnswer : DelegatingHandler
    {
        public volatile bool Armed;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Post && Armed)
            {
                Armed = false;
                response.Dispose();
                throw new HttpRequestException("The answer was lost.");
            }

            return response;
        }
    }

    // A service with an owner key and product app (feature core), and clients of folders under the test's folder.
    private sealed class Vendor : IAsyncDisposable
    {
        private readonly List<ActivationClient> _clients = [];

        private Vendor(TestService test, ElmService service, HttpClient admin, string product)
        {
            Test = test;
            Service = service;
            Admin = admin;
            ProductId = product;
        }

        public TestService Test { get; }

        public ElmService Service { get; }

        public HttpClient Admin { get; }

        public string ProductId { get; }

        public static async Task<Vendor> StartAsync()
        {
            var test = new TestService();
            string owner = await test.SetupAsync();
            ElmService service = await test.StartAsync();
            HttpClient admin = test.Http(service, owner);
            string product = (await TestService.CreatedAsync(admin, "v1/admin/products", $$"""{"code":"{{Product}}","name":"App"}"""))
                .GetProperty("id")
                .GetString()!;
            await TestService.CreatedAsync(admin, $"v1/admin/products/{product}/features", """{"code":"core","name":"Core"}""");
            return new Vendor(test, service, admin, product);
        }

        public string Folder(string name) => Path.Combine(Test.Directory, "client-" + name);

        public ActivationClient Client(string folder, DelegatingHandler? wrap = null, TrustStore? trust = null, Uri? url = null, string? app = null)
        {
            var options = new ActivationClientOptions
            {
                ProductId = Product,
                ServiceUrl = url ?? Service.Url,
                Trust = trust ?? TrustStore.Create([File.ReadAllText(Test.FilePath("root.pub.pem"))], []),
                DataDirectory = Folder(folder),
                AppVersion = app,
                TimeProvider = Test.Time,
                WrapHandler = wrap is null
                    ? null
                    : inner =>
                    {
                        wrap.InnerHandler = inner;
                        return wrap;
                    },
            };
            Assert.True(ActivationClient.TryCreate(options, out ActivationClient? client, out LicenseError? error), error?.Message);
            _clients.Add(client!);
            return client!;
        }

        public async Task<string> LicenseAsync(string policyMembers) => (await LicenseWithIdAsync(policyMembers)).Key;

        public async Task<(string Id, string Key)> LicenseWithIdAsync(string policyMembers)
        {
            string policy = await TestService.PolicyAsync(Admin, ProductId, policyMembers);
            JsonElement license = await TestService.CreatedAsync(
                Admin,
                "v1/admin/licenses",
                $$"""{"policyId":"{{policy}}","name":"n","features":[{"code":"core"}]}"""
            );
            return (license.GetProperty("id").GetString()!, license.GetProperty("key").GetString()!);
        }

        public async Task TrialPolicyAsync()
        {
            string policy = await TestService.PolicyAsync(Admin, ProductId, "\"licenseKind\":\"trial\",\"gracePeriod\":0");
            await TestService.PatchAsync(Admin, $"v1/admin/products/{ProductId}", $$"""{"trialPolicyId":"{{policy}}"}""");
        }

        public Task<JsonElement> GetAsync(string path) => TestService.GetAsync(Admin, path);

        public Task<JsonElement> ActivationAsync(string id) => GetAsync($"v1/admin/activations/{id}");

        public async Task<int> ActivationCountAsync(string licenseId) =>
            (await GetAsync($"v1/admin/licenses/{licenseId}/activations")).GetProperty("items").GetArrayLength();

        public async Task<JsonElement[]> AuditAsync() =>
            [.. (await GetAsync("v1/admin/audit?limit=200")).GetProperty("items").EnumerateArray().Select(e => e.Clone())];

        // The lock waits on the client's clock: move it in small steps until the work is done.
        public async Task AdvanceUntilAsync(Task work)
        {
            for (int i = 0; i < 1_000 && !work.IsCompleted; i++)
            {
                Test.Time.Advance(TimeSpan.FromMilliseconds(50));
                await Task.WhenAny(work, Task.Delay(10, TestContext.Current.CancellationToken));
            }

            Assert.True(work.IsCompleted, "The work did not end.");
        }

        public async ValueTask DisposeAsync()
        {
            foreach (ActivationClient client in _clients)
            {
                await client.DisposeAsync();
            }

            await Test.DisposeAsync();
        }
    }
}
