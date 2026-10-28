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
    /// A license key activates the installation; the kept file checks offline; a refresh moves the sequence number; another
    /// activation is refused while one is kept; a deactivation removes the file and keeps the key (design §12.6.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product app with feature core; a license of 2 activations; a client of a fresh folder.
    ///
    /// Steps:
    /// - Look for the key file; check before activating; activate; check core; refresh; activate again; deactivate; check;
    ///   look for the key file; activate again.
    ///
    /// Expected:
    /// - No key file (it is made by the first activation); ELM_LICENSE_NOT_FOUND; Activated with sequence 1, state Active,
    ///   the activation ID; Valid; sequence 2; ELM_INVALID_STATE without sending; null, state None, ID null;
    ///   ELM_LICENSE_NOT_FOUND; the key file is there; Activated with the same key thumbprint.
    /// </remarks>
    [Fact]
    public async Task Activate_RefreshDeactivate_FollowTheDesign()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync("\"maxActivations\":2");
        await using ActivationClient client = vendor.Client("a");
        string keyFile = Path.Combine(vendor.Folder("a"), "installation.key");

        Assert.False(File.Exists(keyFile));
        Assert.Equal(ErrorCode.LicenseNotFound, Invalid(client.Check("core", null)).Code);
        VerifiedLicense license = Activated(await client.ActivateAsync(key, Cancel));
        Assert.Equal((1L, ActivationState.Active), (license.Sequence, client.State));
        Assert.NotNull(client.ActivationId);
        Assert.IsType<LicenseVerificationResult.Valid>(client.Check("core", null));
        Assert.Equal(2L, Activated(await client.RefreshAsync(Cancel)).Sequence);
        LicenseError again = Failed(await client.ActivateAsync(key, Cancel));
        Assert.Equal(
            (ErrorCode.InvalidState, "An activation is kept; deactivate it before activating another license."),
            (again.Code, again.Message)
        );

        Assert.Null(await client.DeactivateAsync(Cancel));
        Assert.Equal((ActivationState.None, (string?)null), (client.State, client.ActivationId));
        Assert.Equal(ErrorCode.LicenseNotFound, Invalid(client.Check("core", null)).Code);
        Assert.True(File.Exists(keyFile));
        Assert.Equal(license.InstallationKeyThumbprint, Activated(await client.ActivateAsync(key, Cancel)).InstallationKeyThumbprint);
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
    /// - Advance 12 hours and wait for the refresh; suspend the license; advance 12 hours and wait for the state; check;
    ///   resume the license; advance an hour and wait for the state; add a handler that disposes the client, revoke, advance a
    ///   day.
    ///
    /// Expected:
    /// - The service's activation reaches sequence 2; the state becomes Refused with ELM_LICENSE_SUSPENDED in the event; the
    ///   check is still Valid; the refresh tried again an hour later succeeds and the state is Active. A handler that disposes
    ///   the client and waits for it on the next refusal (revoked, a day later) sees the disposal end.
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

        (await vendor.Admin.PostAsync($"v1/admin/licenses/{licenseId}/resume", null, TestContext.Current.CancellationToken)).Dispose();
        vendor.Test.Time.Advance(TimeSpan.FromHours(1));
        await TestService.WaitForAsync(() => Task.FromResult(client.State == ActivationState.Active));

        // A handler on the timer's thread may dispose the client and wait for it: the disposal does not wait for the refresh
        // that raised the event (design §12.6.2).
        bool? disposed = null;
        client.StateChanged += (_, _) =>
        {
            Task disposing = client.DisposeAsync().AsTask();
#pragma warning disable xUnit1031 // The handler blocks on purpose, as an app's synchronous handler would.
            disposed ??= disposing.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
#pragma warning restore xUnit1031
        };
        (await vendor.Admin.PostAsync($"v1/admin/licenses/{licenseId}/revoke", null, TestContext.Current.CancellationToken)).Dispose();
        vendor.Test.Time.Advance(TimeSpan.FromDays(1));
        await TestService.WaitForAsync(() => Task.FromResult(disposed is not null));
        Assert.True(disposed);
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
    /// - The network error, then the same activation ID and file; the error and state RefreshFailing, then sequence 2 with the
    ///   same file as the service issued and state Active (the lost request's jti kept between, none after); the same for the trial; the error, then null. The service has one activation of the license at each step.
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
        Assert.Equal((ActivationState.RefreshFailing, 1), (client.State, vendor.Kept("a").Retry.Count));
        Assert.Equal(2L, Activated(await client.RefreshAsync(Cancel)).Sequence);
        Assert.Equal((ActivationState.Active, 0), (client.State, vendor.Kept("a").Retry.Count));
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
    /// - Refresh with both at once, moving the clock while they wait; read the audit log. Then let both timers come due at
    ///   the same instant and move the clock while they run.
    ///
    /// Expected:
    /// - Both Activated, with sequence numbers 2 and 3; no activation.clone row. Only one of the two timers sends: the
    ///   service's activation is at sequence 4.
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

        // The second timer reads the file the first refreshed and finds the refresh not due (design §12.6.2).
        vendor.Test.Time.Advance(TimeSpan.FromDays(20));
        await vendor.AdvanceForAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(4L, (await vendor.ActivationAsync(one.ActivationId!)).GetProperty("seq").GetInt64());
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
    ///   state None, ID null, check ELM_LICENSE_NOT_FOUND. D Failed ELM_LICENSE_REVOKED, state Refused, no jti kept (the
    ///   answer came), check Valid.
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
        Assert.Equal((ActivationState.Refused, 0), (d.State, vendor.Kept("d").Retry.Count));
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
    /// - With a client that does not take trials as standard: start a trial; start another; lose the answer of the license
    ///   key's activation; activate it again; check as Standard.
    ///
    /// Expected:
    /// - Activated trial; Failed ELM_INVALID_STATE; the network error with the state still Active; Activated with the
    ///   license's ID; Valid with that ID.
    /// </remarks>
    [Fact]
    public async Task Trial_ThenPurchase_ReplacesTheTrial()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        (string licenseId, string key) = await vendor.LicenseWithIdAsync(string.Empty);
        await vendor.TrialPolicyAsync();
        var lose = new LoseNextAnswer();
        await using ActivationClient client = vendor.Client("a", lose, verifier: new LicenseVerifierOptions { AcceptTrialAsStandard = false });

        Assert.Equal(LicenseKind.Trial, Activated(await client.StartTrialAsync(Cancel)).Kind);
        Assert.Equal(ErrorCode.InvalidState, Failed(await client.StartTrialAsync(Cancel)).Code);
        lose.Armed = true;
        Assert.Equal(ErrorCode.NetworkError, Failed(await client.ActivateAsync(key, Cancel)).Code);
        Assert.Equal(ActivationState.Active, client.State);
        Assert.Equal(licenseId, Activated(await client.ActivateAsync(key, Cancel)).LicenseId);
        Assert.Equal(licenseId, Assert.IsType<LicenseVerificationResult.Valid>(client.Check("core", null)).License.LicenseId);
    }

    /// <summary>
    /// A token whose refresh is already due when it is saved (its exp is the license's expiry) is refreshed again only
    /// after the retry interval, not at once.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product app with a trial policy, whose token ends with the trial and is within the refresh window at once.
    ///
    /// Steps:
    /// - Start a trial; move the clock 59 minutes; move it 2 minutes more.
    ///
    /// Expected:
    /// - The kept seq stays 1 for the 59 minutes; it is 2 after the hour.
    /// </remarks>
    [Fact]
    public async Task Refresh_AlreadyDue_WaitsTheRetryInterval()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        await vendor.TrialPolicyAsync();
        await using ActivationClient client = vendor.Client("a");

        Assert.Equal(LicenseKind.Trial, Activated(await client.StartTrialAsync(Cancel)).Kind);
        await vendor.AdvanceForAsync(TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(1));
        Assert.Equal(1L, vendor.Kept("a").Seq);
        await vendor.AdvanceForAsync(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1));
        for (int i = 0; i < 500 && vendor.Kept("a").Seq != 2; i++)
        {
            await Task.Delay(10, Cancel);
        }

        Assert.Equal(2L, vendor.Kept("a").Seq);
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
    ///   activate; pad the kept file with spaces to 256 KiB and check; to 256 KiB + 1 and check.
    ///
    /// Expected:
    /// - Failed with the verifier's error (ELM_UNTRUSTED_KEY); ELM_LICENSE_NOT_FOUND. ELM_MALFORMED; Activated; Valid;
    ///   ELM_MALFORMED.
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
        Directory.CreateDirectory(vendor.Folder("a"));
        File.WriteAllText(kept, "not json");
        Assert.Equal(ErrorCode.Malformed, Invalid(client.Check("core", null)).Code);
        Activated(await client.ActivateAsync(key, Cancel));
        string text = File.ReadAllText(kept);
        File.WriteAllText(kept, text + new string(' ', ActivationStore.MaxLength - System.Text.Encoding.UTF8.GetByteCount(text)));
        Assert.IsType<LicenseVerificationResult.Valid>(client.Check("core", null));
        File.AppendAllText(kept, " ");
        Assert.Equal(ErrorCode.Malformed, Invalid(client.Check("core", null)).Code);
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

    /// <summary>
    /// A key of another product is refused by the service before an activation is made (design §12.6.1); 16 requests without
    /// an answer stop further sends (design §12.6.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product other with a license; product app with a license; a client of app.
    ///
    /// Steps:
    /// - Activate other's key; count other's activations. Activate app's key; write the kept file with 16 jti in its retry
    ///   list; refresh.
    ///
    /// Expected:
    /// - Failed ELM_WRONG_PRODUCT, no activation of other. Activated; Failed ELM_INVALID_STATE "16 requests got no answer;
    ///   ...", and nothing sent (the service's activation stays at sequence 1).
    /// </remarks>
    [Fact]
    public async Task OtherProduct_AndUnansweredRequests_AreRefused()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string other = (await TestService.CreatedAsync(vendor.Admin, "v1/admin/products", """{"code":"other","name":"Other"}"""))
            .GetProperty("id")
            .GetString()!;
        await TestService.CreatedAsync(vendor.Admin, $"v1/admin/products/{other}/features", """{"code":"core","name":"Core"}""");
        string otherPolicy = await TestService.PolicyAsync(vendor.Admin, other, string.Empty);
        JsonElement otherLicense = await TestService.CreatedAsync(
            vendor.Admin,
            "v1/admin/licenses",
            $$"""{"policyId":"{{otherPolicy}}","name":"n","features":[{"code":"core"}]}"""
        );
        string key = await vendor.LicenseAsync(string.Empty);
        await using ActivationClient client = vendor.Client("a");

        Assert.Equal(ErrorCode.WrongProduct, Failed(await client.ActivateAsync(otherLicense.GetProperty("key").GetString()!, Cancel)).Code);
        Assert.Equal(0, await vendor.ActivationCountAsync(otherLicense.GetProperty("id").GetString()!));

        Activated(await client.ActivateAsync(key, Cancel));
        StoredActivation kept = vendor.Kept("a");
        new ActivationStore(vendor.Folder("a"), vendor.Test.Time).Write(
            kept with
            {
                Retry = [.. Enumerable.Range(0, StoredActivation.MaxRetry).Select(i => "lost" + i)],
            }
        );
        LicenseError stopped = Failed(await client.RefreshAsync(Cancel));
        Assert.Equal(
            (ErrorCode.InvalidState, "16 requests got no answer; an administrator deactivates this installation before it activates again."),
            (stopped.Code, stopped.Message)
        );
        Assert.Equal(1L, (await vendor.ActivationAsync(client.ActivationId!)).GetProperty("seq").GetInt64());
    }

    /// <summary>
    /// A device whose clock was set forward and back recovers with an online refresh: the received file is checked without
    /// the clock guard, and its iat replaces the guard's last time (design §11.4 step 4, §12.6.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An activation of a policy with a refresh window of a day; a second client on the same folder whose clock is 10 days
    ///   ahead.
    ///
    /// Steps:
    /// - Check with the client ahead; check with the first client (its clock is now behind the guard's state); refresh with the
    ///   first client; check again.
    ///
    /// Expected:
    /// - Valid; ELM_CLOCK_TAMPERED; Activated; Valid.
    /// </remarks>
    [Fact]
    public async Task ClockSetBack_RecoversWithARefresh()
    {
        await using Vendor vendor = await Vendor.StartAsync();

        // A refresh window of a day: the client ahead is not due for a refresh, so its timer stays quiet.
        string key = await vendor.LicenseAsync("\"refreshWindow\":86400");
        await using ActivationClient client = vendor.Client("a");
        Activated(await client.ActivateAsync(key, Cancel));
        await using ActivationClient ahead = vendor.Client(
            "a",
            time: new Microsoft.Extensions.Time.Testing.FakeTimeProvider(vendor.Test.Time.GetUtcNow().AddDays(10))
        );

        Assert.IsType<LicenseVerificationResult.Valid>(ahead.Check("core", null));
        Assert.Equal(ErrorCode.ClockTampered, Invalid(client.Check("core", null)).Code);
        Activated(await client.RefreshAsync(Cancel));
        Assert.IsType<LicenseVerificationResult.Valid>(client.Check("core", null));
    }

    /// <summary>
    /// A successful answer whose file this installation cannot keep leaves its request named in the retry list, so the next
    /// refresh gets the same file again instead of being judged a clone (design §10.4, §12.6.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Client A activated; client W on the same folder whose trust store has another root key (an app before a key
    ///   rotation reached it).
    ///
    /// Steps:
    /// - W refreshes; read W's state and the kept retry list; A refreshes; read the audit log.
    ///
    /// Expected:
    /// - W Failed ELM_UNTRUSTED_KEY, state RefreshFailing, its jti kept (W's own timer, due at once since W cannot check the
    ///   kept file, may have tried too); A Activated with sequence 2 (the file W could not keep); no activation.clone row.
    /// </remarks>
    [Fact]
    public async Task UnkeptFile_IsAskedForAgain()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync(string.Empty);
        await using ActivationClient a = vendor.Client("a");
        Activated(await a.ActivateAsync(key, Cancel));
        using var otherRoot = Issuing.KeyFiles.CreateKey();
        await using ActivationClient w = vendor.Client("a", trust: TrustStore.Create([Issuing.KeyFiles.ExportPublic(otherRoot)], []));

        // W's own timer comes at once and may hold the lock file; the lock waits on the client's clock, so move it.
        Task<ActivationResult> refreshW = w.RefreshAsync(Cancel);
        await vendor.AdvanceUntilAsync(refreshW);
        Assert.Equal(ErrorCode.UntrustedKey, Failed(await refreshW).Code);
        Assert.Equal(ActivationState.RefreshFailing, w.State);
        Assert.NotEmpty(vendor.Kept("a").Retry);
        Task<ActivationResult> refreshA = a.RefreshAsync(Cancel);
        await vendor.AdvanceUntilAsync(refreshA);
        Assert.Equal(2L, Activated(await refreshA).Sequence);
        Assert.DoesNotContain(await vendor.AuditAsync(), r => r.GetProperty("action").GetString() == "activation.clone");
    }

    /// <summary>
    /// The automatic refresh stops when the key is not the activation's (ELM_PROOF_INVALID), and waits for a Retry-After
    /// longer than an hour (design §12.6.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A policy with tokens of a day and a refresh window of 12 hours; client A activated; client B on a copy of A's
    ///   folder whose key file is replaced by another key; a handler on A that counts POSTs and can answer one with 429 and
    ///   Retry-After 7200.
    ///
    /// Steps:
    /// - B refreshes; let B's own first timer end; count B's POSTs, advance 2 hours, count again. Arm the 429 on A; advance 12 hours and wait for A's
    ///   refresh to fail; advance an hour; advance another hour and wait for the refresh.
    ///
    /// Expected:
    /// - B Failed ELM_PROOF_INVALID, state RefreshFailing, no POST in the 2 hours. A: state RefreshFailing after the 429;
    ///   no POST an hour later; the service's activation at sequence 2 after the second hour.
    /// </remarks>
    [Fact]
    public async Task Refresh_StopsOnAnotherKey_AndWaitsForRetryAfter()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync("\"tokenLifetime\":86400,\"refreshWindow\":43200,\"maxActivations\":2");
        var countA = new ScriptedHandler();
        await using ActivationClient a = vendor.Client("a", countA);
        Activated(await a.ActivateAsync(key, Cancel));
        CopyFolder(vendor.Folder("a"), vendor.Folder("b"));
        string other = Path.Combine(vendor.Test.Directory, "other-key");
        Assert.True(new InstallationKeyStore("app", KeyScope.User, other).TryLoadOrCreate(out InstallationKey? made, out _));
        made!.Dispose();
        File.Copy(Path.Combine(other, "installation.key"), Path.Combine(vendor.Folder("b"), "installation.key"), overwrite: true);
        var countB = new ScriptedHandler();
        await using ActivationClient b = vendor.Client("b", countB);

        Task<ActivationResult> refreshB = b.RefreshAsync(Cancel);
        await vendor.AdvanceUntilAsync(refreshB);
        Assert.Equal(ErrorCode.ProofInvalid, Failed(await refreshB).Code);
        Assert.Equal(ActivationState.RefreshFailing, b.State);

        // B's own timer came at once (B cannot check the kept file) and may still be sending: let it end first.
        await vendor.AdvanceForAsync(TimeSpan.FromSeconds(10));
        int posts = countB.Posts;
        await vendor.AdvanceForAsync(TimeSpan.FromHours(2), step: TimeSpan.FromMinutes(10));
        Assert.Equal(posts, countB.Posts);

        countA.TooManyNext = TimeSpan.FromSeconds(7200);
        vendor.Test.Time.Advance(TimeSpan.FromHours(10));
        await TestService.WaitForAsync(() => Task.FromResult(a.State == ActivationState.RefreshFailing));
        posts = countA.Posts;
        vendor.Test.Time.Advance(TimeSpan.FromHours(1));
        await vendor.AdvanceForAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(posts, countA.Posts);
        vendor.Test.Time.Advance(TimeSpan.FromHours(1));
        await TestService.WaitForAsync(async () => (await vendor.ActivationAsync(a.ActivationId!)).GetProperty("seq").GetInt64() == 2);
    }

    /// <summary>
    /// A timer that comes while the last automatic refresh still runs is not lost: the refresh schedules again from the
    /// kept file when it ends (design §12.6.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A policy with tokens of a day and a refresh window of 12 hours; an activated client whose refresh failed (a lost
    ///   answer), so the next success raises an event; a handler that holds the refresh on that event.
    ///
    /// Steps:
    /// - Advance 12 hours (the timer's refresh succeeds and the handler holds it); advance 13 hours (the next timer comes
    ///   while the refresh is held); let the handler go; move the clock and wait.
    ///
    /// Expected:
    /// - The service's activation goes from sequence 2 to 3 after the handler lets go.
    /// </remarks>
    [Fact]
    public async Task TimerDuringARefresh_IsNotLost()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync("\"tokenLifetime\":86400,\"refreshWindow\":43200");
        var lose = new LoseNextAnswer();
        await using ActivationClient client = vendor.Client("a", lose);
        Activated(await client.ActivateAsync(key, Cancel));
        lose.Armed = true;
        Assert.Equal(ErrorCode.NetworkError, Failed(await client.RefreshAsync(Cancel)).Code);

        using var held = new ManualResetEventSlim();
        using var hold = new ManualResetEventSlim();
        client.StateChanged += (_, e) =>
        {
            if (e.Current == ActivationState.Active && !held.IsSet)
            {
                held.Set();
#pragma warning disable xUnit1031 // The handler holds the refresh on purpose.
                hold.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
#pragma warning restore xUnit1031
            }
        };

        vendor.Test.Time.Advance(TimeSpan.FromHours(12));
        await TestService.WaitForAsync(() => Task.FromResult(held.IsSet));
        Assert.Equal(2L, (await vendor.ActivationAsync(client.ActivationId!)).GetProperty("seq").GetInt64());
        vendor.Test.Time.Advance(TimeSpan.FromHours(13));
        hold.Set();
        await vendor.AdvanceForAsync(TimeSpan.FromSeconds(5));
        await TestService.WaitForAsync(async () => (await vendor.ActivationAsync(client.ActivationId!)).GetProperty("seq").GetInt64() == 3);
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

    // Counts POSTs, and answers the next one with 429 and a Retry-After without sending it, when armed.
    private sealed class ScriptedHandler : DelegatingHandler
    {
        private int _posts;

        public TimeSpan? TooManyNext { get; set; }

        public int Posts => Volatile.Read(ref _posts);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post)
            {
                return base.SendAsync(request, cancellationToken);
            }

            Interlocked.Increment(ref _posts);
            if (TooManyNext is TimeSpan wait)
            {
                TooManyNext = null;
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent(
                        """{"type":"about:blank","title":"ELM_RATE_LIMITED","status":429,"detail":"Slow down.","code":"ELM_RATE_LIMITED"}"""
                    ),
                };
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
                return Task.FromResult(response);
            }

            return base.SendAsync(request, cancellationToken);
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

        public StoredActivation Kept(string name) => new ActivationStore(Folder(name), Test.Time).Read(out _)!;

        public ActivationClient Client(
            string folder,
            DelegatingHandler? wrap = null,
            TrustStore? trust = null,
            Uri? url = null,
            string? app = null,
            LicenseVerifierOptions? verifier = null,
            TimeProvider? time = null
        )
        {
            var options = new ActivationClientOptions
            {
                ProductId = Product,
                ServiceUrl = url ?? Service.Url,
                Trust = trust ?? TrustStore.Create([File.ReadAllText(Test.FilePath("root.pub.pem"))], []),
                DataDirectory = Folder(folder),
                AppVersion = app,
                VerifierOptions = verifier,
                TimeProvider = time ?? Test.Time,
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

        // Moves the clock in small steps, giving the timers' work time to run between them.
        public async Task AdvanceForAsync(TimeSpan total, TimeSpan? step = null)
        {
            TimeSpan by = step ?? TimeSpan.FromMilliseconds(50);
            for (TimeSpan moved = TimeSpan.Zero; moved < total; moved += by)
            {
                Test.Time.Advance(by);
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

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
