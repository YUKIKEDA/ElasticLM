using System.Net;
using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.Service.Tests;

/// <summary>
/// Online activation, refresh, and deactivation (design §10.4, §12.2, §12.4 to §12.6, M4b1), with the administration API of
/// activations, on SQLite and on PostgreSQL.
/// </summary>
public sealed class ElmServiceActivationsTests
{
    private const long Day = 86_400;

    /// <summary>
    /// A new installation gets a license file bound to its key and fingerprints, whose token follows the policy; the
    /// activation, its audit row, and a token cut at the license's expiry are as designed.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product app with feature core; a policy for 365 days (token 30 days, refresh 20 days, grace 7 days, fingerprints
    ///   any, client info none); a license of it, and one whose expiry is 10 days on; each engine.
    ///
    /// Steps:
    /// - Activate the first with two fingerprints, client info, and app 1.2; verify the file as the app would; read the
    ///   activation and the audit log; activate the second from another device and verify.
    ///
    /// Expected:
    /// - 201 with an ID and a fresh nonce header; the token is standard, for app, the license, core, expiring 30 days on
    ///   with refresh 20 days, grace 7 days, the key's thumbprint, both fingerprints, and sequence 1; the activation shows
    ///   both fingerprints, no client (the policy does not keep it), app 1.2, seq 1, not blocked; the audit row is by the
    ///   installation with the license and seq 1; the second token expires 10 days on.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Activate_NewInstallation_IssuesABoundFile(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, string owner) = await StartAsync(test);
        string product = await ProductAsync(admin, "app", "core");
        string policy = await TestService.PolicyAsync(admin, product, "\"duration\":31536000");
        (string licenseId, string key) = await LicenseAsync(admin, policy);
        (string _, string shortKey) = await LicenseAsync(admin, policy, more: $",\"expiresAt\":\"{Iso(10 * Day)}\"");
        using var device = TestDevice.Create(test, service);
        string[] prints = TestDevice.Fingerprints(2);

        DeviceAnswer answer = await device.ActivateAsync(key, prints, more: ""","client":{"host":"pc","user":"u"},"app":"1.2" """);
        Assert.Equal(HttpStatusCode.Created, answer.Status);
        Assert.NotNull(answer.Nonce);
        VerifiedLicense license = device.Verify(answer.File, prints);
        Assert.Equal(
            (licenseId, "app", LicenseKind.Standard, "core", Time(30 * Day), TimeSpan.FromDays(20), TimeSpan.FromDays(7), device.KeyId, 1L),
            (
                license.LicenseId,
                license.ProductId,
                license.Kind,
                license.Features.Single().Id,
                license.ExpiresAt,
                license.RefreshWindow!.Value,
                license.GracePeriod,
                license.InstallationKeyThumbprint,
                license.Sequence!.Value
            )
        );
        Assert.Equal(prints.Order(), license.Fingerprints.Order());

        JsonElement activation = await TestService.GetAsync(admin, $"v1/admin/activations/{answer.ActivationId}");
        Assert.Equal(
            (licenseId, device.KeyId, JsonValueKind.Null, "1.2", 1L, JsonValueKind.Null),
            (
                activation.GetProperty("licenseId").GetString(),
                activation.GetProperty("keyId").GetString(),
                activation.GetProperty("client").ValueKind,
                activation.GetProperty("app").GetString(),
                activation.GetProperty("seq").GetInt64(),
                activation.GetProperty("blockedAt").ValueKind
            )
        );
        Assert.Equal(prints.Order(), activation.GetProperty("fingerprints").EnumerateArray().Select(f => f.GetString()!).Order());

        JsonElement row = (await TestService.AuditRowsAsync(test, service, owner)).Single(r =>
            r.GetProperty("action").GetString() == "activation.activate"
        );
        Assert.Equal(
            ("installation", device.KeyId, answer.ActivationId, "ELM_OK", $"{{\"licenseId\":\"{licenseId}\",\"seq\":1,\"reactivated\":false}}"),
            (
                row.GetProperty("actorType").GetString(),
                row.GetProperty("actorId").GetString(),
                row.GetProperty("target").GetString(),
                row.GetProperty("result").GetString(),
                row.GetProperty("detail").GetString()
            )
        );

        using var other = TestDevice.Create(test, service);
        DeviceAnswer cut = await other.ActivateAsync(shortKey, prints);
        Assert.Equal(Time(10 * Day), other.Verify(cut.File, prints).ExpiresAt);
    }

    /// <summary>
    /// Activations of one license are counted under its row's lock, so installations at once never pass the limit; the
    /// license's own limit overrides the policy's, and lowering it keeps the activations there are.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A policy of 1 activation; a license with maxActivations 3; five devices; each engine.
    ///
    /// Steps:
    /// - Activate the five at once; set maxActivations 1; refresh one that got in; activate a sixth device.
    ///
    /// Expected:
    /// - Three 201 and two 409 ELM_ACTIVATION_LIMIT "… limit of 3 activations."; the refresh gives 200; the sixth 409 with
    ///   "… limit of 1 activations.".
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Activate_AtOnce_StopsAtTheLimit(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        string product = await ProductAsync(admin, "app", "core");
        string policy = await TestService.PolicyAsync(admin, product, string.Empty);
        (string licenseId, string key) = await LicenseAsync(admin, policy, more: ",\"maxActivations\":3");
        TestDevice[] devices = [.. Enumerable.Range(0, 5).Select(_ => TestDevice.Create(test, service))];
        string[] prints = TestDevice.Fingerprints();

        DeviceAnswer[] answers = await Task.WhenAll(devices.Select(d => d.ActivateAsync(key, prints)));
        Assert.Equal(3, answers.Count(a => a.Status == HttpStatusCode.Created));
        Assert.All(
            answers.Where(a => a.Status != HttpStatusCode.Created),
            a => AssertProblem(a, HttpStatusCode.Conflict, "ELM_ACTIVATION_LIMIT", "The license has reached its limit of 3 activations.")
        );

        await TestService.PatchAsync(admin, $"v1/admin/licenses/{licenseId}", "{\"maxActivations\":1}");
        int first = Array.FindIndex(answers, a => a.Status == HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.OK, (await devices[first].RefreshAsync(answers[first].ActivationId, 1)).Status);
        using var sixth = TestDevice.Create(test, service);
        AssertProblem(
            await sixth.ActivateAsync(key, prints),
            HttpStatusCode.Conflict,
            "ELM_ACTIVATION_LIMIT",
            "The license has reached its limit of 1 activations."
        );
        foreach (TestDevice device in devices)
        {
            device.Dispose();
        }
    }

    /// <summary>
    /// Each branch of design §10.4 step 3 on a refresh and a reactivation, under Deny and under Flag: the current sequence
    /// number and a retry naming the lost request pass, on a deactivation too; one behind without it, two behind, ahead, and a
    /// reactivation without one are clones, which Deny stops for every later request but a deactivation with the last
    /// sequence number, and Flag records and lets through.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A policy of 20 activations with cloneStrategy deny, and one with flag; a license of each.
    ///
    /// Steps:
    /// - For each strategy: activate; activate again with seq 0 naming the first request, as after a lost first answer;
    ///   refresh with seq 1, refresh with seq 1 again naming the second request, reactivate with
    ///   seq 2 and a new fingerprint; deactivate with seq 2 naming the reactivation, as after a lost answer. Then for each clone case (seq 1 without
    ///   retry, seq 0, seq 9, a reactivation without seq) take a new device to seq 2 and send it; read the activation and the
    ///   audit row; under Deny refresh it with seq 2, deactivate it with seq 0, then with seq 2.
    ///
    /// Expected:
    /// - 201, 200 with the same ID and file, 200 with seq 2, 200 with the same file, 200 with the same ID, seq 3, and only the new fingerprint, 204 with no clone row. Under Deny each
    ///   clone gives 409 ELM_CLONE_DETECTED, the activation is blocked, its audit row says ELM_CLONE_DETECTED with the
    ///   strategy; the later refresh gives 409; the deactivation with seq 0 gives 409, with seq 2 gives 204. Under Flag each
    ///   gives 200 with seq 3, the activation is not blocked, the audit row says ELM_OK.
    /// </remarks>
    [Theory]
    [InlineData("deny")]
    [InlineData("flag")]
    public async Task Sequence_EachBranch_IsJudged(string strategy)
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, string owner) = await StartAsync(test);
        string product = await ProductAsync(admin, "app", "core");
        string policy = await TestService.PolicyAsync(admin, product, $"\"maxActivations\":20,\"cloneStrategy\":\"{strategy}\"");
        (_, string key) = await LicenseAsync(admin, policy);
        string[] prints = TestDevice.Fingerprints();

        using (var device = TestDevice.Create(test, service))
        {
            DeviceAnswer activated = await device.ActivateAsync(key, prints);
            Assert.Equal(HttpStatusCode.Created, activated.Status);
            DeviceAnswer lost = await device.ActivateAsync(key, prints, seq: 0, retry: [activated.Jti]);
            Assert.Equal((HttpStatusCode.OK, activated.ActivationId), (lost.Status, lost.ActivationId));
            Assert.Equal(activated.File, lost.File);
            DeviceAnswer second = await device.RefreshAsync(activated.ActivationId, 1);
            Assert.Equal(2L, device.Verify(second.File, prints).Sequence);
            DeviceAnswer retried = await device.RefreshAsync(activated.ActivationId, 1, ["unrelated", second.Jti]);
            Assert.Equal(HttpStatusCode.OK, retried.Status);
            Assert.Equal(second.File, retried.File);
            string[] moved = TestDevice.Fingerprints();
            DeviceAnswer again = await device.ActivateAsync(key, moved, seq: 2);
            Assert.Equal((HttpStatusCode.OK, activated.ActivationId), (again.Status, again.ActivationId));
            Assert.Equal((3L, moved[0]), (device.Verify(again.File, moved).Sequence, device.Verify(again.File, moved).Fingerprints.Single()));
            Assert.Equal(HttpStatusCode.NoContent, (await device.DeactivateAsync(activated.ActivationId, 2, [again.Jti])).Status);
            Assert.DoesNotContain(
                await TestService.AuditRowsAsync(test, service, owner),
                r => r.GetProperty("action").GetString() == "activation.clone" && r.GetProperty("target").GetString() == activated.ActivationId
            );
        }

        var cases = new (string Name, Func<TestDevice, string, Task<DeviceAnswer>> Send)[]
        {
            ("one behind without retry", (d, id) => d.RefreshAsync(id, 1)),
            ("two behind", (d, id) => d.RefreshAsync(id, 0)),
            ("ahead", (d, id) => d.RefreshAsync(id, 9)),
            ("reactivation without seq", (d, _) => d.ActivateAsync(key, prints)),
        };
        foreach ((string name, Func<TestDevice, string, Task<DeviceAnswer>> send) in cases)
        {
            using var device = TestDevice.Create(test, service);
            string id = (await device.ActivateAsync(key, prints)).ActivationId;
            Assert.Equal(HttpStatusCode.OK, (await device.RefreshAsync(id, 1)).Status);
            DeviceAnswer clone = await send(device, id);
            JsonElement activation = await TestService.GetAsync(admin, $"v1/admin/activations/{id}");
            JsonElement row = (await TestService.AuditRowsAsync(test, service, owner)).Last(r =>
                r.GetProperty("action").GetString() == "activation.clone" && r.GetProperty("target").GetString() == id
            );
            Assert.Contains($"\"lastSeq\":2,\"strategy\":\"{strategy}\"", row.GetProperty("detail").GetString(), StringComparison.Ordinal);
            if (strategy == "deny")
            {
                AssertProblem(
                    clone,
                    HttpStatusCode.Conflict,
                    "ELM_CLONE_DETECTED",
                    "Another copy of the installation went ahead; the activation is stopped. Deactivate it and activate again."
                );
                Assert.True(activation.GetProperty("blockedAt").ValueKind == JsonValueKind.String, name);
                Assert.Equal("ELM_CLONE_DETECTED", row.GetProperty("result").GetString());
                AssertProblem(
                    await device.RefreshAsync(id, 2),
                    HttpStatusCode.Conflict,
                    "ELM_CLONE_DETECTED",
                    "The activation was stopped because a clone of the installation was detected."
                );
                AssertProblem(
                    await device.DeactivateAsync(id, 0),
                    HttpStatusCode.Conflict,
                    "ELM_CLONE_DETECTED",
                    "The activation was stopped as a clone; only the copy with the last sequence number, or an administrator, deactivates it."
                );
                Assert.Equal(HttpStatusCode.NoContent, (await device.DeactivateAsync(id, 2)).Status);
            }
            else
            {
                Assert.True(clone.Status == HttpStatusCode.OK, name + ": " + clone.Text);
                Assert.Equal(3L, device.Verify(clone.File, prints).Sequence);
                Assert.Equal((JsonValueKind.Null, 3L), (activation.GetProperty("blockedAt").ValueKind, activation.GetProperty("seq").GetInt64()));
                Assert.Equal("ELM_OK", row.GetProperty("result").GetString());
            }
        }
    }

    /// <summary>
    /// A device's deactivation frees its place and counts against DeactivationLimit; an administrator's frees it without
    /// counting; a deactivated activation is no longer known.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A policy of 1 activation and a deactivation limit of 1; a license; each engine.
    ///
    /// Steps:
    /// - Activate; deactivate it as an administrator; activate again; deactivate it from the device; refresh it; activate
    ///   again; deactivate it from the device; read the audit rows of activation.deactivate.
    ///
    /// Expected:
    /// - 201, 204, 201, 204, 404 ELM_ACTIVATION_NOT_FOUND, 201, 409 ELM_DEACTIVATION_LIMIT "… limit of 1 deactivations.";
    ///   one row by the API key and one by the installation with deactivations 1; the license shows deactivations 1.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Deactivate_CountsOnlyTheDevicesOwn(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, string owner) = await StartAsync(test);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string product = await ProductAsync(admin, "app", "core");
        string policy = await TestService.PolicyAsync(admin, product, "\"deactivationLimit\":1");
        (string licenseId, string key) = await LicenseAsync(admin, policy);
        using var device = TestDevice.Create(test, service);
        string[] prints = TestDevice.Fingerprints();

        string first = (await device.ActivateAsync(key, prints)).ActivationId;
        using (HttpResponseMessage removed = await admin.PostAsync($"v1/admin/activations/{first}/deactivate", null, cancel))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        DeviceAnswer second = await device.ActivateAsync(key, prints);
        Assert.Equal(HttpStatusCode.Created, second.Status);
        Assert.Equal(HttpStatusCode.NoContent, (await device.DeactivateAsync(second.ActivationId, 1)).Status);
        AssertProblem(
            await device.RefreshAsync(second.ActivationId, 1),
            HttpStatusCode.NotFound,
            "ELM_ACTIVATION_NOT_FOUND",
            "No activation has this ID; activate again."
        );
        DeviceAnswer third = await device.ActivateAsync(key, prints);
        Assert.Equal(HttpStatusCode.Created, third.Status);
        AssertProblem(
            await device.DeactivateAsync(third.ActivationId, 1),
            HttpStatusCode.Conflict,
            "ELM_DEACTIVATION_LIMIT",
            "The license has reached its limit of 1 deactivations."
        );

        JsonElement[] rows =
        [
            .. (await TestService.AuditRowsAsync(test, service, owner)).Where(r => r.GetProperty("action").GetString() == "activation.deactivate"),
        ];
        Assert.Equal(["apikey", "installation"], rows.Select(r => r.GetProperty("actorType").GetString()));
        Assert.Equal($"{{\"licenseId\":\"{licenseId}\",\"deactivations\":1}}", rows[1].GetProperty("detail").GetString());
        Assert.Equal(1L, (await TestService.GetAsync(admin, $"v1/admin/licenses/{licenseId}")).GetProperty("deactivations").GetInt64());
    }

    /// <summary>
    /// A license whose expiry starts at the first activation gets it then, and no later activation moves it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A fromFirstActivation policy of 30 days and 2 activations; a license of it (no expiry).
    ///
    /// Steps:
    /// - Activate at the start; read the license; a day later activate a second device; read it again.
    ///
    /// Expected:
    /// - The expiry is 30 days after the start both times, and the first token ends then.
    /// </remarks>
    [Fact]
    public async Task Activate_FromFirstActivation_FixesTheExpiryOnce()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        string product = await ProductAsync(admin, "app", "core");
        string policy = await TestService.PolicyAsync(
            admin,
            product,
            "\"expirationBasis\":\"fromFirstActivation\",\"duration\":2592000,\"maxActivations\":2"
        );
        (string licenseId, string key) = await LicenseAsync(admin, policy);
        using var first = TestDevice.Create(test, service);
        using var second = TestDevice.Create(test, service);
        string[] prints = TestDevice.Fingerprints();

        Assert.Equal(JsonValueKind.Null, (await TestService.GetAsync(admin, $"v1/admin/licenses/{licenseId}")).GetProperty("expiresAt").ValueKind);
        DeviceAnswer answer = await first.ActivateAsync(key, prints);
        Assert.Equal(Time(30 * Day), first.Verify(answer.File, prints).ExpiresAt);
        Assert.Equal(Iso(30 * Day), (await TestService.GetAsync(admin, $"v1/admin/licenses/{licenseId}")).GetProperty("expiresAt").GetString());
        test.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(HttpStatusCode.Created, (await second.ActivateAsync(key, prints)).Status);
        Assert.Equal(Iso(30 * Day), (await TestService.GetAsync(admin, $"v1/admin/licenses/{licenseId}")).GetProperty("expiresAt").GetString());
    }

    /// <summary>
    /// A suspended, revoked, or expired license refuses activations and refreshes with its own code; a deactivation is
    /// taken in any state.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A policy of 5 activations; a license with an activation; a second license expiring a day on, with an activation.
    ///
    /// Steps:
    /// - Suspend the first: activate a new device, refresh; resume and revoke: the same, then deactivate. A day later
    ///   activate and refresh against the second.
    ///
    /// Expected:
    /// - 403 ELM_LICENSE_SUSPENDED twice; 403 ELM_LICENSE_REVOKED twice and 204; 403 ELM_EXPIRED twice.
    /// </remarks>
    [Fact]
    public async Task States_RefuseAsDesigned()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string product = await ProductAsync(admin, "app", "core");
        string policy = await TestService.PolicyAsync(admin, product, "\"maxActivations\":5");
        (string licenseId, string key) = await LicenseAsync(admin, policy);
        (_, string ending) = await LicenseAsync(admin, policy, more: $",\"expiresAt\":\"{Iso(Day)}\"");
        using var device = TestDevice.Create(test, service);
        using var late = TestDevice.Create(test, service);
        string[] prints = TestDevice.Fingerprints();
        string id = (await device.ActivateAsync(key, prints)).ActivationId;
        string endingId = (await device.ActivateAsync(ending, prints)).ActivationId;

        async Task Refused(string code, string detail, string licenseKey, string activation, long seq)
        {
            AssertProblem(await late.ActivateAsync(licenseKey, prints), HttpStatusCode.Forbidden, code, detail);
            AssertProblem(await device.RefreshAsync(activation, seq), HttpStatusCode.Forbidden, code, detail);
        }

        (await admin.PostAsync($"v1/admin/licenses/{licenseId}/suspend", null, cancel)).Dispose();
        await Refused("ELM_LICENSE_SUSPENDED", "The license is suspended.", key, id, 1);
        (await admin.PostAsync($"v1/admin/licenses/{licenseId}/resume", null, cancel)).Dispose();
        (await admin.PostAsync($"v1/admin/licenses/{licenseId}/revoke", null, cancel)).Dispose();
        await Refused("ELM_LICENSE_REVOKED", "The license is revoked.", key, id, 1);
        Assert.Equal(HttpStatusCode.NoContent, (await device.DeactivateAsync(id, 1)).Status);
        test.Time.Advance(TimeSpan.FromDays(1));
        await Refused("ELM_EXPIRED", "The license has expired.", ending, endingId, 1);
    }

    /// <summary>
    /// Each rule of the proof (design §12.2) is enforced on the device requests, every answer carries a fresh nonce, and only
    /// the activation's own installation key refreshes or deactivates it (T19).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license with an activation of device A; device B.
    ///
    /// Steps:
    /// - Activate without a proof; with a proof signed for another path; for another body; with an unknown nonce; with a
    ///   nonce 301 seconds old; send one proof twice. Refresh and deactivate A's activation with B's proof carrying its key;
    ///   refresh an unknown activation and one whose ID is not a UUID.
    ///
    /// Expected:
    /// - 401 ELM_PROOF_INVALID, ELM_PROOF_INVALID, ELM_PROOF_INVALID, ELM_NONCE_REQUIRED twice, then 201 and 401 ELM_REPLAYED;
    ///   401 ELM_PROOF_INVALID "The proof is not made with the activation's installation key." twice; 404
    ///   ELM_ACTIVATION_NOT_FOUND twice; every answer with an ELM-Nonce header.
    /// </remarks>
    [Fact]
    public async Task Proof_EachRule_IsEnforced()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        string product = await ProductAsync(admin, "app", "core");
        string policy = await TestService.PolicyAsync(admin, product, "\"maxActivations\":5");
        (_, string key) = await LicenseAsync(admin, policy);
        using var a = TestDevice.Create(test, service);
        using var b = TestDevice.Create(test, service);
        string[] prints = TestDevice.Fingerprints();
        string body = $$"""{"licenseKey":"{{key}}","product":"app","fingerprints":["{{prints[0]}}"]}""";
        var answers = new List<DeviceAnswer>();
        async Task Expect(Task<DeviceAnswer> send, HttpStatusCode status, string code, string detail)
        {
            DeviceAnswer answer = await send;
            answers.Add(answer);
            AssertProblem(answer, status, code, detail);
        }

        const string Missing = "The request proof is missing or not a valid elm-proof+jws.";
        await Expect(a.PostAsync("v1/activations", body, proof: _ => null), HttpStatusCode.Unauthorized, "ELM_PROOF_INVALID", Missing);
        await Expect(
            a.PostAsync("v1/activations", body, signedPath: "v1/trials"),
            HttpStatusCode.Unauthorized,
            "ELM_PROOF_INVALID",
            "The request proof is for another method or URI."
        );
        await Expect(
            a.PostAsync("v1/activations", body, signedBody: body + " "),
            HttpStatusCode.Unauthorized,
            "ELM_PROOF_INVALID",
            "The request proof is for another body."
        );
        const string Stale = "The nonce is missing or expired; get a new one.";
        await Expect(a.PostAsync("v1/activations", body, nonce: "nonce"), HttpStatusCode.Unauthorized, "ELM_NONCE_REQUIRED", Stale);
        string old = await a.NonceAsync();
        test.Time.Advance(TimeSpan.FromSeconds(301));
        await Expect(a.PostAsync("v1/activations", body, nonce: old), HttpStatusCode.Unauthorized, "ELM_NONCE_REQUIRED", Stale);

        string? sent = null;
        DeviceAnswer activated = await a.PostAsync("v1/activations", body, proof: p => sent = p);
        Assert.Equal(HttpStatusCode.Created, activated.Status);
        answers.Add(activated);
        await Expect(
            a.PostAsync("v1/activations", body, proof: _ => sent),
            HttpStatusCode.Unauthorized,
            "ELM_REPLAYED",
            "The request proof was already used."
        );

        const string Other = "The proof is not made with the activation's installation key.";
        string id = activated.ActivationId;
        await Expect(
            a.PostAsync($"v1/activations/{id}/refresh", "{\"seq\":1}", signer: b.Key),
            HttpStatusCode.Unauthorized,
            "ELM_PROOF_INVALID",
            Other
        );
        await Expect(
            a.PostAsync($"v1/activations/{id}/deactivate", "{\"seq\":1}", signer: b.Key),
            HttpStatusCode.Unauthorized,
            "ELM_PROOF_INVALID",
            Other
        );
        const string None = "No activation has this ID; activate again.";
        await Expect(a.RefreshAsync(Guid.CreateVersion7().ToString("D"), 1), HttpStatusCode.NotFound, "ELM_ACTIVATION_NOT_FOUND", None);
        await Expect(a.RefreshAsync("x", 1), HttpStatusCode.NotFound, "ELM_ACTIVATION_NOT_FOUND", None);
        Assert.All(answers, x => Assert.False(string.IsNullOrEmpty(x.Nonce)));
    }

    /// <summary>
    /// The request is checked against the license and its policy before anything is written: an unknown key, an unbound
    /// license, and a missing fingerprint where the policy compares them are refused; a policy that ignores fingerprints
    /// takes none and binds the token to the key alone; client info is kept only under hostAndUser.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Policies: the defaults; unbound; fingerprints ignored with client info hostAndUser and 2 activations; a license of
    ///   each.
    ///
    /// Steps:
    /// - Activate with a key no license has, and with text that is not a key; a key of app sent for product other; the
    ///   unbound license; the default one without
    ///   fingerprints; the ignoring one with fingerprints and client info; read that activation; the ignoring one from
    ///   another device without fingerprints.
    ///
    /// Expected:
    /// - 404 ELM_LICENSE_NOT_FOUND twice; 400 ELM_WRONG_PRODUCT; 409 ELM_CONFLICT; 400 ELM_FINGERPRINT_UNAVAILABLE; 201 with a token without
    ///   fingerprints although some were sent, and the activation's client is the one sent; 201.
    /// </remarks>
    [Fact]
    public async Task Activate_LicenseAndPolicy_AreChecked()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        string product = await ProductAsync(admin, "app", "core");
        (_, string standard) = await LicenseAsync(admin, await TestService.PolicyAsync(admin, product, string.Empty));
        (_, string unbound) = await LicenseAsync(admin, await TestService.PolicyAsync(admin, product, "\"binding\":\"unbound\""));
        const string Ignoring = "\"fingerprintMatching\":\"ignore\",\"clientInfoStrategy\":\"hostAndUser\",\"maxActivations\":2";
        (_, string ignoring) = await LicenseAsync(admin, await TestService.PolicyAsync(admin, product, Ignoring));
        using var device = TestDevice.Create(test, service);
        string[] prints = TestDevice.Fingerprints();

        AssertProblem(
            await device.ActivateAsync("ELM-00000-00000-00000-00000", prints),
            HttpStatusCode.NotFound,
            "ELM_LICENSE_NOT_FOUND",
            "No license has this key."
        );
        AssertProblem(await device.ActivateAsync("not a key", prints), HttpStatusCode.NotFound, "ELM_LICENSE_NOT_FOUND", "No license has this key.");
        AssertProblem(
            await device.ActivateAsync(standard, prints, product: "other"),
            HttpStatusCode.BadRequest,
            "ELM_WRONG_PRODUCT",
            "The license key is for another product."
        );
        AssertProblem(
            await device.ActivateAsync(unbound, prints),
            HttpStatusCode.Conflict,
            "ELM_CONFLICT",
            "A license of an unbound policy is not activated; its file comes from the vendor."
        );
        AssertProblem(
            await device.ActivateAsync(standard, []),
            HttpStatusCode.BadRequest,
            "ELM_FINGERPRINT_UNAVAILABLE",
            "The policy compares fingerprints, and the request has none."
        );
        DeviceAnswer bound = await device.ActivateAsync(ignoring, prints, more: ""","client":{"host":"pc","user":"u"}""");
        Assert.Equal(HttpStatusCode.Created, bound.Status);
        Assert.Empty(device.Verify(bound.File, prints).Fingerprints);
        JsonElement client = (await TestService.GetAsync(admin, $"v1/admin/activations/{bound.ActivationId}")).GetProperty("client");
        Assert.Equal(("pc", "u"), (client.GetProperty("host").GetString(), client.GetProperty("user").GetString()));
        using var bare = TestDevice.Create(test, service);
        Assert.Equal(HttpStatusCode.Created, (await bare.ActivateAsync(ignoring, [])).Status);
    }

    /// <summary>
    /// A server license (design §9.6) is activated by the same request, its token carrying the seats of the license and the
    /// lease members of the policy.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server policy: lease 600 s, borrow up to an hour, overage 10 %, 3 early returns, clone flag; a license with core
    ///   of 5 seats.
    ///
    /// Steps:
    /// - Activate and verify as a server license.
    ///
    /// Expected:
    /// - Kind server, core with 5 seats, lease 600 s, borrow an hour, overage 10, 3 returns, Flag.
    /// </remarks>
    [Fact]
    public async Task Activate_ServerLicense_CarriesTheLeaseMembers()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        string product = await ProductAsync(admin, "app", "core");
        string policy = await TestService.PolicyAsync(
            admin,
            product,
            "\"licenseKind\":\"server\",\"leaseDuration\":600,\"borrowLimit\":3600,\"overage\":10,\"borrowReturnLimit\":3,\"cloneStrategy\":\"flag\""
        );
        (_, string key) = await LicenseAsync(admin, policy, features: """[{"code":"core","seats":5}]""");
        using var device = TestDevice.Create(test, service);
        string[] prints = TestDevice.Fingerprints();

        VerifiedLicense license = device.Verify((await device.ActivateAsync(key, prints)).File, prints, LicenseKind.Server);
        Assert.Equal(
            (LicenseKind.Server, 5, TimeSpan.FromSeconds(600), TimeSpan.FromHours(1), 10, 3, CloneStrategy.Flag),
            (
                license.Kind,
                license.Features.Single().Seats!.Value,
                license.LeaseDuration!.Value,
                license.BorrowLimit!.Value,
                license.OveragePercent!.Value,
                license.BorrowReturnLimit!.Value,
                license.CloneStrategy!.Value
            )
        );
    }

    /// <summary>
    /// The device endpoints have their own per-source bucket, apart from the administration API's, and a per-installation-key
    /// one (design §14.7); the jti records are removed once their nonce has expired (design §14.4).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - clientPerIp 4 and clientPerKey 1 a second; a license of 5 activations.
    ///
    /// Steps:
    /// - With the clock still: activate with device A, activate again with A (four device requests from the address, each
    ///   activation a nonce and a POST); read the nonce; list licenses as the owner; count the jti records; advance 7 minutes (the record is kept 6)
    ///   and wait for the sweep.
    ///
    /// Expected:
    /// - 201; 429 "Too many requests with this installation key."; 429 "Too many requests from this address."; 200; one
    ///   record (the refused request's jti is not recorded), then none.
    /// </remarks>
    [Fact]
    public async Task Limits_AndJtiSweep_FollowTheDesign()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync(s => s with { RateLimitClientPerIp = 4, RateLimitClientPerKey = 1 });
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string product = await ProductAsync(admin, "app", "core");
        (_, string key) = await LicenseAsync(admin, await TestService.PolicyAsync(admin, product, "\"maxActivations\":5"));
        using var device = TestDevice.Create(test, service);
        string[] prints = TestDevice.Fingerprints();
        using HttpClient anonymous = test.Http(service);

        // Each activation is two device requests: GET /v1/nonce and the POST.
        Assert.Equal(HttpStatusCode.Created, (await device.ActivateAsync(key, prints)).Status);
        AssertProblem(
            await device.ActivateAsync(key, prints),
            HttpStatusCode.TooManyRequests,
            "ELM_RATE_LIMITED",
            "Too many requests with this installation key."
        );
        await TestService.AssertProblemAsync(
            await anonymous.GetAsync("v1/nonce", cancel),
            HttpStatusCode.TooManyRequests,
            "ELM_RATE_LIMITED",
            "Too many requests from this address."
        );
        using (HttpResponseMessage list = await admin.GetAsync("v1/admin/licenses", cancel))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        }

        Assert.Equal(1L, await test.ScalarAsync("SELECT COUNT(*) FROM proof_jtis"));
        test.Time.Advance(TimeSpan.FromMinutes(7));
        await TestService.WaitForAsync(async () => await test.ScalarAsync("SELECT COUNT(*) FROM proof_jtis") == 0);
    }

    /// <summary>
    /// The administration API lists and reads activations with activation.read and deactivates with
    /// activation.deactivate; support has both, viewer only the first.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license with two activations; keys with license.read, with activation.read, with activation.deactivate.
    ///
    /// Steps:
    /// - List the license's activations with limit 1 and follow next, with each key; read an unknown activation; deactivate
    ///   with the read key and with the deactivate key; list for an unknown license; read the roles' permissions.
    ///
    /// Expected:
    /// - 403 naming activation.read; two pages of one that hold both activations; 404 ELM_ACTIVATION_NOT_FOUND; 403 naming activation.deactivate, then
    ///   204; 404 ELM_LICENSE_NOT_FOUND; support has activation.deactivate, viewer does not.
    /// </remarks>
    [Fact]
    public async Task Admin_ActivationsApi_AnswersAsDesigned()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string product = await ProductAsync(admin, "app", "core");
        (string licenseId, string key) = await LicenseAsync(admin, await TestService.PolicyAsync(admin, product, "\"maxActivations\":5"));
        using var a = TestDevice.Create(test, service);
        using var b = TestDevice.Create(test, service);
        string[] prints = TestDevice.Fingerprints();
        string first = (await a.ActivateAsync(key, prints)).ActivationId;
        string second = (await b.ActivateAsync(key, prints)).ActivationId;
        HttpClient licenseReader = test.Http(service, await TestService.CreateKeyAsync(admin, "\"license.read\""));
        HttpClient reader = test.Http(service, await TestService.CreateKeyAsync(admin, "\"activation.read\""));
        HttpClient remover = test.Http(service, await TestService.CreateKeyAsync(admin, "\"activation.deactivate\""));

        await TestService.AssertProblemAsync(
            await licenseReader.GetAsync($"v1/admin/licenses/{licenseId}/activations", cancel),
            HttpStatusCode.Forbidden,
            "ELM_FORBIDDEN",
            "The API key does not have the permission activation.read."
        );
        JsonElement page = await TestService.GetAsync(reader, $"v1/admin/licenses/{licenseId}/activations?limit=1");
        JsonElement next = await TestService.GetAsync(
            reader,
            $"v1/admin/licenses/{licenseId}/activations?limit=1&after={page.GetProperty("next").GetString()}"
        );
        Assert.Equal((1, false), (next.GetProperty("items").GetArrayLength(), next.TryGetProperty("next", out _)));

        // Two IDs made in the same millisecond of the fake clock have no set order (UUID version 7), so the pages are compared as a set.
        Assert.Equal(
            new[] { first, second }.Order(),
            new[] { page, next }.Select(p => p.GetProperty("items")[0].GetProperty("id").GetString()!).Order()
        );
        await TestService.AssertProblemAsync(
            await reader.GetAsync($"v1/admin/activations/{Guid.CreateVersion7():D}", cancel),
            HttpStatusCode.NotFound,
            "ELM_ACTIVATION_NOT_FOUND",
            "No activation has this ID."
        );
        await TestService.AssertProblemAsync(
            await reader.PostAsync($"v1/admin/activations/{first}/deactivate", null, cancel),
            HttpStatusCode.Forbidden,
            "ELM_FORBIDDEN",
            "The API key does not have the permission activation.deactivate."
        );
        using (HttpResponseMessage removed = await remover.PostAsync($"v1/admin/activations/{first}/deactivate", null, cancel))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        await TestService.AssertProblemAsync(
            await reader.GetAsync($"v1/admin/licenses/{Guid.CreateVersion7():D}/activations", cancel),
            HttpStatusCode.NotFound,
            "ELM_LICENSE_NOT_FOUND",
            "No license has this ID."
        );
        Assert.Contains("activation.deactivate", Permissions.OfRole(Permissions.RoleSupport));
        Assert.DoesNotContain("activation.deactivate", Permissions.OfRole(Permissions.RoleViewer));
        Assert.Contains("activation.read", Permissions.OfRole(Permissions.RoleViewer));
    }

    /// <summary>
    /// When no certificate of the signing key covers the time any more (it ran out after the start), a device activation and
    /// an unbound file answer ELM_INTERNAL_ERROR, and the activation writes nothing.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The service started with a certificate valid from a day before the start for 10 days; a node-locked and an unbound
    ///   license.
    ///
    /// Steps:
    /// - Advance 10 days; activate the first; ask for the second's file; list the first's activations.
    ///
    /// Expected:
    /// - 500 ELM_INTERNAL_ERROR "The service cannot sign a license file now." twice; no activation.
    /// </remarks>
    [Fact]
    public async Task Issue_CertificateRanOut_AnswersInternalError()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        string certificate = test.WriteCertificate(
            "short.cert",
            Issuing.KeyUses.License | Issuing.KeyUses.Lease,
            TestService.Start.AddDays(-1),
            TimeSpan.FromDays(10)
        );
        ElmService service = await test.StartAsync(s => s with { SigningCertificateFiles = [certificate] });
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string product = await ProductAsync(admin, "app", "core");
        (string bound, string key) = await LicenseAsync(admin, await TestService.PolicyAsync(admin, product, string.Empty));
        (string unbound, _) = await LicenseAsync(admin, await TestService.PolicyAsync(admin, product, "\"binding\":\"unbound\""));
        using var device = TestDevice.Create(test, service);
        const string Answer = "The service cannot sign a license file now.";

        test.Time.Advance(TimeSpan.FromDays(10));
        AssertProblem(await device.ActivateAsync(key, TestDevice.Fingerprints()), HttpStatusCode.InternalServerError, "ELM_INTERNAL_ERROR", Answer);
        await TestService.AssertProblemAsync(
            await admin.PostAsync($"v1/admin/licenses/{unbound}/file", null, cancel),
            HttpStatusCode.InternalServerError,
            "ELM_INTERNAL_ERROR",
            Answer
        );
        Assert.Equal(0, (await TestService.GetAsync(admin, $"v1/admin/licenses/{bound}/activations")).GetProperty("items").GetArrayLength());
    }

    /// <summary>Asserts a problem answer to a device request.</summary>
    internal static void AssertProblem(DeviceAnswer answer, HttpStatusCode status, string code, string detail)
    {
        Assert.True(answer.Status == status, answer.Text);
        Assert.Equal((code, detail), (answer.Code, answer.Json.GetProperty("detail").GetString()));
    }

    internal static async Task<(ElmService Service, HttpClient Admin, string Owner)> StartAsync(TestService test)
    {
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        return (service, test.Http(service, owner), owner);
    }

    internal static async Task<string> ProductAsync(HttpClient admin, string code, params string[] features)
    {
        string product = (await TestService.CreatedAsync(admin, "v1/admin/products", $$"""{"code":"{{code}}","name":"{{code}}"}"""))
            .GetProperty("id")
            .GetString()!;
        foreach (string feature in features)
        {
            await TestService.CreatedAsync(admin, $"v1/admin/products/{product}/features", $$"""{"code":"{{feature}}","name":"{{feature}}"}""");
        }

        return product;
    }

    internal static async Task<(string Id, string Key)> LicenseAsync(
        HttpClient admin,
        string policy,
        string features = """[{"code":"core"}]""",
        string more = ""
    )
    {
        JsonElement created = await TestService.CreatedAsync(
            admin,
            "v1/admin/licenses",
            $$"""{"policyId":"{{policy}}","name":"n","features":{{features}}{{more}}}"""
        );
        return (created.GetProperty("id").GetString()!, created.GetProperty("key").GetString()!);
    }

    internal static DateTimeOffset Time(long seconds) => TestService.Start.AddSeconds(seconds);

    internal static string Iso(long seconds) => AdminApi.Time(TestService.Start.ToUnixTimeSeconds() + seconds);
}
