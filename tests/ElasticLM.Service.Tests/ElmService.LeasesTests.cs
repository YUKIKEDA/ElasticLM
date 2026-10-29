using System.Net;
using System.Text.Json;
using ElasticLM.Licensing;
using static ElasticLM.Service.Tests.ElmServiceActivationsTests;

namespace ElasticLM.Service.Tests;

/// <summary>
/// Cloud leases (design §12.7.6, M4d1): acquisition, renewal, release, borrows and their early return, and the
/// administration API of leases, on SQLite and on PostgreSQL.
/// </summary>
public sealed class ElmServiceLeasesTests
{
    private const string Cloud = "\"floating\":\"cloud\"";
    private const string TwoSeats = """[{"code":"core","seats":2}]""";

    /// <summary>
    /// A cloud-floating license lends a seat as a lease token the client verifies with the service's certificates; the
    /// token follows the policy and is cut at the license's expiry; acquiring writes no audit row.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product app with feature core; a cloud-floating policy with leaseDuration 600 and clientInfoStrategy hostAndUser; a
    ///   license of it with 2 seats of core, one that expires 300 seconds on, and one that expires 1000 seconds on whose core expires 200 seconds on;
    ///   each engine.
    ///
    /// Steps:
    /// - Acquire 1 seat with client info and app 1.2; verify the answer as a SaaS client; list the license's leases; read the
    ///   audit log; acquire from the second license and from the third.
    ///
    /// Expected:
    /// - 201 with a fresh nonce; the lease is for app, the license, core, 1 seat, the device's key, seq 1, not a borrow, and
    ///   expires 600 seconds on; its ID has 22 characters; the list shows it with the client and app; no audit row names
    ///   it; the second lease expires 300 seconds on, the third 200 seconds on.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Acquire_CloudLicense_IssuesAVerifiedLease(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, string owner) = await StartAsync(test);
        string policy = await CloudPolicyAsync(admin, "\"clientInfoStrategy\":\"hostAndUser\"");
        (string licenseId, string key) = await LicenseAsync(admin, policy, TwoSeats);
        (_, string shortKey) = await LicenseAsync(admin, policy, TwoSeats, $",\"expiresAt\":\"{Iso(300)}\"");
        (_, string shortFeature) = await LicenseAsync(
            admin,
            policy,
            $$"""[{"code":"core","seats":2,"expiresAt":"{{Iso(200)}}"}]""",
            $",\"expiresAt\":\"{Iso(1000)}\""
        );
        using var device = TestDevice.Create(test, service);

        DeviceAnswer answer = await device.AcquireAsync(key, more: ""","client":{"host":"pc","user":"u"},"app":"1.2" """);
        Assert.True(answer.Status == HttpStatusCode.Created, answer.Text);
        Assert.NotNull(answer.Nonce);
        VerifiedLease lease = device.VerifyLease(answer);
        Assert.Equal(
            ("app", licenseId, "core", 1, device.KeyId, 1L, false, Time(600), 22),
            (
                lease.ProductId,
                lease.LicenseId,
                lease.FeatureId,
                lease.Seats,
                lease.InstallationKeyThumbprint,
                lease.Sequence,
                lease.IsBorrow,
                lease.ExpiresAt,
                lease.LeaseId.Length
            )
        );

        JsonElement item = (await TestService.GetAsync(admin, $"v1/admin/licenses/{licenseId}/leases"))
            .GetProperty("items")
            .EnumerateArray()
            .Single();
        Assert.Equal(
            (lease.LeaseId, "core", 1L, device.KeyId, 1L, Iso(600), false, false, "pc", "1.2"),
            (
                item.GetProperty("id").GetString(),
                item.GetProperty("feature").GetString(),
                item.GetProperty("seats").GetInt64(),
                item.GetProperty("keyId").GetString(),
                item.GetProperty("seq").GetInt64(),
                item.GetProperty("expiresAt").GetString(),
                item.GetProperty("borrow").GetBoolean(),
                item.GetProperty("stopped").GetBoolean(),
                item.GetProperty("client").GetProperty("host").GetString(),
                item.GetProperty("app").GetString()
            )
        );
        Assert.DoesNotContain(await TestService.AuditRowsAsync(test, service, owner), r => r.GetProperty("target").GetString() == lease.LeaseId);

        Assert.Equal(Time(300), device.VerifyLease(await device.AcquireAsync(shortKey)).ExpiresAt);
        Assert.Equal(Time(200), device.VerifyLease(await device.AcquireAsync(shortFeature)).ExpiresAt);
    }

    /// <summary>
    /// Seats are counted under the license's lock: acquisitions at once never pass floor(seats × (100 + overage) / 100), a
    /// count above the seats is refused even within the overage, and a release frees the seats.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy with overage 50; a license with 2 seats of core (3 may be held); six devices; each engine.
    ///
    /// Steps:
    /// - Acquire 1 seat from five devices at once; acquire 3 seats from the sixth; release one lease; acquire 1 seat from the
    ///   sixth.
    ///
    /// Expected:
    /// - Three 201 and two 409 ELM_NO_SEATS "No seat of the feature is free."; 400 ELM_INVALID_ARGUMENT "The seat count must
    ///   be at least 1 and at most the feature's seats."; 204; 201.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Acquire_AtOnce_StopsAtTheSeatsWithOverage(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        (_, string key) = await LicenseAsync(admin, await CloudPolicyAsync(admin, "\"overage\":50"), TwoSeats);
        TestDevice[] devices = [.. Enumerable.Range(0, 6).Select(_ => TestDevice.Create(test, service))];

        DeviceAnswer[] answers = await Task.WhenAll(devices[..5].Select(d => d.AcquireAsync(key)));
        Assert.Equal(3, answers.Count(a => a.Status == HttpStatusCode.Created));
        Assert.All(
            answers.Where(a => a.Status != HttpStatusCode.Created),
            a => AssertProblem(a, HttpStatusCode.Conflict, "ELM_NO_SEATS", "No seat of the feature is free.")
        );
        AssertProblem(
            await devices[5].AcquireAsync(key, count: 3),
            HttpStatusCode.BadRequest,
            "ELM_INVALID_ARGUMENT",
            "The seat count must be at least 1 and at most the feature's seats."
        );

        int first = Array.FindIndex(answers, a => a.Status == HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent, (await devices[first].ReleaseAsync(devices[first].VerifyLease(answers[first]).LeaseId)).Status);
        Assert.Equal(HttpStatusCode.Created, (await devices[5].AcquireAsync(key)).Status);
        foreach (TestDevice device in devices)
        {
            device.Dispose();
        }
    }

    /// <summary>
    /// Each branch of design §10.4 step 3 on a renewal, under Deny and under Flag: the current sequence number gives the next
    /// token, a retry naming the lost request the last one again; a clone is recorded as lease.clone, and Deny stops the
    /// lease, which then holds its seat until it expires and refuses renewals and releases.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy with cloneStrategy deny, and one with flag; a license of each with 2 seats of core.
    ///
    /// Steps:
    /// - Acquire; renew with seq 1 after 100 seconds; renew with seq 1 again naming that request; renew with seq 0 (a clone);
    ///   read the lease and the audit row; under Deny renew with seq 2, release, and acquire 2 seats from another device.
    ///
    /// Expected:
    /// - 201; 200 with seq 2 expiring 700 seconds on; 200 with the same token. Under Deny the clone gives 409
    ///   ELM_CLONE_DETECTED, the lease is stopped, the audit row is ELM_CLONE_DETECTED with seq 0, lastSeq 2, and the
    ///   strategy; the renewal and the release give 409 ELM_CLONE_DETECTED; the 2 seats give 409 ELM_NO_SEATS. Under Flag
    ///   the clone gives 200 with seq 3, the lease is not stopped, and the audit row is ELM_OK.
    /// </remarks>
    [Theory]
    [InlineData("deny")]
    [InlineData("flag")]
    public async Task Renew_EachBranch_IsJudged(string strategy)
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, string owner) = await StartAsync(test);
        (string licenseId, string key) = await LicenseAsync(admin, await CloudPolicyAsync(admin, $"\"cloneStrategy\":\"{strategy}\""), TwoSeats);
        using var device = TestDevice.Create(test, service);

        DeviceAnswer acquired = await device.AcquireAsync(key);
        string id = device.VerifyLease(acquired).LeaseId;
        test.Time.Advance(TimeSpan.FromSeconds(100));
        DeviceAnswer renewed = await device.RenewAsync(id, 1);
        Assert.Equal(HttpStatusCode.OK, renewed.Status);
        Assert.Equal((2L, Time(700)), (device.VerifyLease(renewed).Sequence, device.VerifyLease(renewed).ExpiresAt));
        DeviceAnswer retried = await device.RenewAsync(id, 1, ["unrelated", renewed.Jti]);
        Assert.Equal((HttpStatusCode.OK, renewed.Lease), (retried.Status, retried.Lease));

        DeviceAnswer clone = await device.RenewAsync(id, 0);
        JsonElement lease = (await TestService.GetAsync(admin, $"v1/admin/licenses/{licenseId}/leases")).GetProperty("items")[0];
        JsonElement row = (await TestService.AuditRowsAsync(test, service, owner)).Single(r => r.GetProperty("action").GetString() == "lease.clone");
        bool deny = strategy == "deny";
        Assert.Equal(
            (
                "installation",
                device.KeyId,
                id,
                deny ? "ELM_CLONE_DETECTED" : "ELM_OK",
                $"{{\"licenseId\":\"{licenseId}\",\"feature\":\"core\",\"seq\":0,\"lastSeq\":2,\"strategy\":\"{strategy}\"}}",
                deny
            ),
            (
                row.GetProperty("actorType").GetString(),
                row.GetProperty("actorId").GetString(),
                row.GetProperty("target").GetString(),
                row.GetProperty("result").GetString(),
                row.GetProperty("detail").GetString(),
                lease.GetProperty("stopped").GetBoolean()
            )
        );
        if (!deny)
        {
            Assert.Equal((HttpStatusCode.OK, 3L), (clone.Status, device.VerifyLease(clone).Sequence));
            return;
        }

        const string Stopped = "The lease was stopped because a clone of the installation was detected.";
        AssertProblem(
            clone,
            HttpStatusCode.Conflict,
            "ELM_CLONE_DETECTED",
            "Another copy of the installation went ahead; the lease is stopped until it expires."
        );
        AssertProblem(await device.RenewAsync(id, 2), HttpStatusCode.Conflict, "ELM_CLONE_DETECTED", Stopped);
        AssertProblem(await device.ReleaseAsync(id), HttpStatusCode.Conflict, "ELM_CLONE_DETECTED", Stopped);
        using var other = TestDevice.Create(test, service);
        AssertProblem(await other.AcquireAsync(key, count: 2), HttpStatusCode.Conflict, "ELM_NO_SEATS", "No seat of the feature is free.");
    }

    /// <summary>
    /// Only the lease's installation key renews or releases it; a lease that expired, or an ID that cannot be a lease, is
    /// unknown to a renewal and released already to a release; the sweep removes expired rows.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy (lease 600 seconds); a license with 2 seats of core; two devices; each engine.
    ///
    /// Steps:
    /// - Acquire from the first; renew and release it with a proof of the second device's key carrying that key; renew and release "x" and an unknown
    ///   22-character ID; set the lease's expiry to now in the database; renew the lease, renew it with the second key's
    ///   proof, and release it; move the clock a minute and wait for the sweep.
    ///
    /// Expected:
    /// - 401 ELM_PROOF_INVALID "The proof is not made with the lease's installation key." twice; 404 ELM_UNKNOWN_LEASE "No
    ///   lease has this ID; acquire again." and 204 for each ID; after the expiry 404 twice (before the proof) and 204, and the list is empty; the
    ///   leases table becomes empty.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Renew_OtherKeyOrUnknownLease_IsRefused(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        (string licenseId, string key) = await LicenseAsync(admin, await CloudPolicyAsync(admin, string.Empty), TwoSeats);
        using var device = TestDevice.Create(test, service);
        using var other = TestDevice.Create(test, service);
        string id = device.VerifyLease(await device.AcquireAsync(key)).LeaseId;

        const string WrongKey = "The proof is not made with the lease's installation key.";
        const string Unknown = "No lease has this ID; acquire again.";
        AssertProblem(
            await device.PostAsync($"v1/leases/{id}/renew", "{\"seq\":1}", signer: other.Key),
            HttpStatusCode.Unauthorized,
            "ELM_PROOF_INVALID",
            WrongKey
        );
        AssertProblem(
            await device.SendAsync(HttpMethod.Delete, $"v1/leases/{id}", string.Empty, signer: other.Key),
            HttpStatusCode.Unauthorized,
            "ELM_PROOF_INVALID",
            WrongKey
        );
        foreach (string unknown in new[] { "x", "AAAAAAAAAAAAAAAAAAAAAA" })
        {
            AssertProblem(await device.RenewAsync(unknown, 1), HttpStatusCode.NotFound, "ELM_UNKNOWN_LEASE", Unknown);
            Assert.Equal(HttpStatusCode.NoContent, (await device.ReleaseAsync(unknown)).Status);
        }

        // The row is ended in the database, not by the clock, so the sweep does not remove it before the requests.
        await test.ExecuteAsync($"UPDATE leases SET expires_at = {TestService.Start.ToUnixTimeSeconds()}");
        AssertProblem(await device.RenewAsync(id, 1), HttpStatusCode.NotFound, "ELM_UNKNOWN_LEASE", Unknown);
        AssertProblem(
            await device.PostAsync($"v1/leases/{id}/renew", "{\"seq\":1}", signer: other.Key),
            HttpStatusCode.NotFound,
            "ELM_UNKNOWN_LEASE",
            Unknown
        );
        Assert.Equal(HttpStatusCode.NoContent, (await device.ReleaseAsync(id)).Status);
        test.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, (await TestService.GetAsync(admin, $"v1/admin/licenses/{licenseId}/leases")).GetProperty("items").GetArrayLength());
        await test.WaitWithClockAsync(async () => await test.ScalarAsync("SELECT COUNT(*) FROM leases") == 0);
    }

    /// <summary>
    /// A renewal follows the license as it is now: fewer seats than are held refuse renewals until the held seats fit
    /// again, a feature taken away is not licensed, and a policy no longer cloud-floating does not lend.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy; a license with 2 seats of core and a feature extra; two devices holding a seat each.
    ///
    /// Steps:
    /// - Set the license's core to 1 seat; renew the first; release the second; renew the first; set the features to extra
    ///   alone; renew; give core back; set the policy's floating to none; renew.
    ///
    /// Expected:
    /// - 409 ELM_NO_SEATS "The seats held are more than the license allows now."; 204; 200; 403 ELM_FEATURE_NOT_LICENSED
    ///   "The license does not grant the feature."; 409 ELM_CONFLICT "Only a license of a cloud-floating policy lends seats.".
    /// </remarks>
    [Fact]
    public async Task Renew_LicenseChanged_FollowsTheLicense()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        string product = await ProductAsync(admin, "app", "core", "extra");
        string policy = await TestService.PolicyAsync(admin, product, Cloud);
        (string licenseId, string key) = await LicenseAsync(admin, policy, """[{"code":"core","seats":2},{"code":"extra","seats":1}]""");
        using var first = TestDevice.Create(test, service);
        using var second = TestDevice.Create(test, service);
        string a = first.VerifyLease(await first.AcquireAsync(key)).LeaseId;
        string b = second.VerifyLease(await second.AcquireAsync(key)).LeaseId;

        await TestService.PatchAsync(
            admin,
            $"v1/admin/licenses/{licenseId}",
            """{"features":[{"code":"core","seats":1},{"code":"extra","seats":1}]}"""
        );
        AssertProblem(await first.RenewAsync(a, 1), HttpStatusCode.Conflict, "ELM_NO_SEATS", "The seats held are more than the license allows now.");
        Assert.Equal(HttpStatusCode.NoContent, (await second.ReleaseAsync(b)).Status);
        Assert.Equal(HttpStatusCode.OK, (await first.RenewAsync(a, 1)).Status);

        await TestService.PatchAsync(admin, $"v1/admin/licenses/{licenseId}", """{"features":[{"code":"extra","seats":1}]}""");
        AssertProblem(await first.RenewAsync(a, 2), HttpStatusCode.Forbidden, "ELM_FEATURE_NOT_LICENSED", "The license does not grant the feature.");
        await TestService.PatchAsync(admin, $"v1/admin/licenses/{licenseId}", """{"features":[{"code":"core","seats":1}]}""");
        await TestService.PatchAsync(admin, $"v1/admin/policies/{policy}", "{\"floating\":\"none\"}");
        AssertProblem(
            await first.RenewAsync(a, 2),
            HttpStatusCode.Conflict,
            "ELM_CONFLICT",
            "Only a license of a cloud-floating policy lends seats."
        );
    }

    /// <summary>
    /// A borrow is lent up to the policy's BorrowLimit with fingerprints, holds its seat for its period, is not renewed, and
    /// is returned early only while the key and each fingerprint stay under BorrowReturnLimit in 30 days and the license has
    /// not lowered the feature's seats.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy with borrowLimit 86400 and borrowReturnLimit 1; a license with 2 seats of core; a policy
    ///   without borrowLimit and a license of it; a second license of the first policy; devices A, B (A's fingerprint), and
    ///   C; each engine.
    ///
    /// Steps:
    /// - A borrows 86401 seconds; A borrows without fingerprints; A borrows from the second license; A borrows 3600 seconds;
    ///   renew it; release it; A borrows and releases; A borrows from the second license and releases; B borrows with A's fingerprint and releases; C borrows; move the clock
    ///   3601 seconds; C borrows; set core to 1 seat; C releases; set core to 2 seats; move the clock 30 days; A borrows and
    ///   releases.
    ///
    /// Expected:
    /// - 403 ELM_BORROW_NOT_ALLOWED "The borrow period is longer than allowed."; 400 ELM_FINGERPRINT_UNAVAILABLE; 403
    ///   "Borrows are not lent."; 201 with a borrow expiring 3600 seconds on; 400 ELM_INVALID_ARGUMENT "A borrowed lease is
    ///   not renewed."; 204; 409 ELM_RETURN_NOT_ALLOWED; 204 (returns count per license); 409 ELM_RETURN_NOT_ALLOWED (the
    ///   fingerprint used the one return); C's
    ///   first borrow 409 ELM_NO_SEATS (A and B hold both seats); C's release 409 ELM_RETURN_NOT_ALLOWED (fewer seats); after
    ///   the window 204.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Borrow_EarlyReturn_FollowsTheLimits(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        string product = await ProductAsync(admin, "app", "core");
        string policy = await TestService.PolicyAsync(admin, product, Cloud + ",\"borrowLimit\":86400,\"borrowReturnLimit\":1");
        (string licenseId, string key) = await LicenseAsync(admin, policy, TwoSeats);
        (_, string otherKey) = await LicenseAsync(admin, policy, TwoSeats);
        (_, string noBorrow) = await LicenseAsync(admin, await TestService.PolicyAsync(admin, product, Cloud), TwoSeats);
        using var a = TestDevice.Create(test, service);
        using var b = TestDevice.Create(test, service);
        using var c = TestDevice.Create(test, service);
        string[] prints = TestDevice.Fingerprints();
        const string NotNow = "The borrow cannot be returned early now; it ends at its expiry.";

        AssertProblem(
            await a.BorrowAsync(key, 86_401, prints),
            HttpStatusCode.Forbidden,
            "ELM_BORROW_NOT_ALLOWED",
            "The borrow period is longer than allowed."
        );
        AssertProblem(
            await a.AcquireAsync(key, more: ",\"borrow\":3600"),
            HttpStatusCode.BadRequest,
            "ELM_FINGERPRINT_UNAVAILABLE",
            "A borrow needs the machine fingerprints of the client."
        );
        AssertProblem(await a.BorrowAsync(noBorrow, 3600, prints), HttpStatusCode.Forbidden, "ELM_BORROW_NOT_ALLOWED", "Borrows are not lent.");

        DeviceAnswer borrowed = await a.BorrowAsync(key, 3600, prints);
        Assert.Equal(HttpStatusCode.Created, borrowed.Status);
        VerifiedLease lease = a.VerifyLease(borrowed);
        Assert.Equal((true, Time(3600)), (lease.IsBorrow, lease.ExpiresAt));
        AssertProblem(await a.RenewAsync(lease.LeaseId, 1), HttpStatusCode.BadRequest, "ELM_INVALID_ARGUMENT", "A borrowed lease is not renewed.");
        Assert.Equal(HttpStatusCode.NoContent, (await a.ReleaseAsync(lease.LeaseId)).Status);

        string again = a.VerifyLease(await a.BorrowAsync(key, 3600, prints)).LeaseId;
        AssertProblem(await a.ReleaseAsync(again), HttpStatusCode.Conflict, "ELM_RETURN_NOT_ALLOWED", NotNow);
        string elsewhere = a.VerifyLease(await a.BorrowAsync(otherKey, 3600, prints)).LeaseId;
        Assert.Equal(HttpStatusCode.NoContent, (await a.ReleaseAsync(elsewhere)).Status);
        string bLease = b.VerifyLease(await b.BorrowAsync(key, 3600, prints)).LeaseId;
        AssertProblem(await b.ReleaseAsync(bLease), HttpStatusCode.Conflict, "ELM_RETURN_NOT_ALLOWED", NotNow);
        AssertProblem(
            await c.BorrowAsync(key, 3600, TestDevice.Fingerprints()),
            HttpStatusCode.Conflict,
            "ELM_NO_SEATS",
            "No seat of the feature is free."
        );

        test.Time.Advance(TimeSpan.FromSeconds(3601));
        string cLease = c.VerifyLease(await c.BorrowAsync(key, 3600, TestDevice.Fingerprints())).LeaseId;
        await TestService.PatchAsync(admin, $"v1/admin/licenses/{licenseId}", """{"features":[{"code":"core","seats":1}]}""");
        AssertProblem(await c.ReleaseAsync(cLease), HttpStatusCode.Conflict, "ELM_RETURN_NOT_ALLOWED", NotNow);

        await TestService.PatchAsync(admin, $"v1/admin/licenses/{licenseId}", """{"features":[{"code":"core","seats":2}]}""");
        test.Time.Advance(TimeSpan.FromDays(30));
        string late = a.VerifyLease(await a.BorrowAsync(key, 3600, prints)).LeaseId;
        Assert.Equal(HttpStatusCode.NoContent, (await a.ReleaseAsync(late)).Status);
    }

    /// <summary>
    /// The refusals of acquisition steps 2 to 5 (design §12.7.6), and the refusal of an activation of a cloud-floating
    /// license.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Products app (features core, extra) and other; a cloud-floating policy; licenses with 2 seats of core: plain, with
    ///   ver ">=2.0 &lt;3.0", with core expiring 100 seconds on, and one suspended; a policy not cloud-floating and a license
    ///   of it with core.
    ///
    /// Steps:
    /// - Acquire: an unknown key; product other; the non-floating license; feature extra; version 3.0 and 2.5 of the ranged
    ///   one; a body without count; the suspended one; move the clock 101 seconds and acquire the expiring one; activate
    ///   the plain license.
    ///
    /// Expected:
    /// - 404 ELM_LICENSE_NOT_FOUND; 400 ELM_WRONG_PRODUCT; 409 ELM_CONFLICT; 403 ELM_FEATURE_NOT_LICENSED; 403
    ///   ELM_FEATURE_VERSION_NOT_LICENSED and 201; 400 ELM_MALFORMED; 403 ELM_LICENSE_SUSPENDED; 403 ELM_FEATURE_EXPIRED;
    ///   409 ELM_CONFLICT "A license of a cloud-floating policy is leased, not activated.".
    /// </remarks>
    [Fact]
    public async Task Acquire_EachRefusal_IsAnswered()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string product = await ProductAsync(admin, "app", "core", "extra");
        await ProductAsync(admin, "other");
        string policy = await TestService.PolicyAsync(admin, product, Cloud);
        (_, string plain) = await LicenseAsync(admin, policy, TwoSeats);
        (_, string ranged) = await LicenseAsync(admin, policy, """[{"code":"core","seats":2,"ver":">=2.0 <3.0"}]""");
        (_, string expiring) = await LicenseAsync(admin, policy, $$"""[{"code":"core","seats":2,"expiresAt":"{{Iso(100)}}"}]""");
        (string suspendedId, string suspended) = await LicenseAsync(admin, policy, TwoSeats);
        (await admin.PostAsync($"v1/admin/licenses/{suspendedId}/suspend", null, cancel)).Dispose();
        (_, string fixedSeats) = await LicenseAsync(admin, await TestService.PolicyAsync(admin, product, string.Empty));
        using var device = TestDevice.Create(test, service);

        AssertProblem(
            await device.AcquireAsync("ELM-AAAA-AAAA-AAAA-AAAA"),
            HttpStatusCode.NotFound,
            "ELM_LICENSE_NOT_FOUND",
            "No license has this key."
        );
        AssertProblem(
            await device.AcquireAsync(plain, product: "other"),
            HttpStatusCode.BadRequest,
            "ELM_WRONG_PRODUCT",
            "The license key is for another product."
        );
        AssertProblem(
            await device.AcquireAsync(fixedSeats),
            HttpStatusCode.Conflict,
            "ELM_CONFLICT",
            "Only a license of a cloud-floating policy lends seats."
        );
        AssertProblem(
            await device.AcquireAsync(plain, feature: "extra"),
            HttpStatusCode.Forbidden,
            "ELM_FEATURE_NOT_LICENSED",
            "The license does not grant the feature."
        );
        AssertProblem(
            await device.AcquireAsync(ranged, more: ",\"version\":\"3.0\""),
            HttpStatusCode.Forbidden,
            "ELM_FEATURE_VERSION_NOT_LICENSED",
            "The feature version is outside the licensed range."
        );
        Assert.Equal(HttpStatusCode.Created, (await device.AcquireAsync(ranged, more: ",\"version\":\"2.5\"")).Status);
        AssertProblem(
            await device.PostAsync("v1/leases", $$"""{"licenseKey":"{{plain}}","product":"app","feature":"core"}"""),
            HttpStatusCode.BadRequest,
            "ELM_MALFORMED",
            "The request body is not a valid lease request."
        );
        AssertProblem(await device.AcquireAsync(suspended), HttpStatusCode.Forbidden, "ELM_LICENSE_SUSPENDED", "The license is suspended.");
        test.Time.Advance(TimeSpan.FromSeconds(101));
        AssertProblem(await device.AcquireAsync(expiring), HttpStatusCode.Forbidden, "ELM_FEATURE_EXPIRED", "The feature has expired.");
        AssertProblem(
            await device.ActivateAsync(plain, TestDevice.Fingerprints()),
            HttpStatusCode.Conflict,
            "ELM_CONFLICT",
            "A license of a cloud-floating policy is leased, not activated."
        );
    }

    /// <summary>
    /// Under FromFirstActivation the first lease fixes the license's expiry, and the lease is cut at it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy with expirationBasis fromFirstActivation, duration 86400, and leaseDuration 3600; a license
    ///   of it with 2 seats of core and no expiry.
    ///
    /// Steps:
    /// - Move the clock 100 seconds; acquire; read the license; move the clock to 300 seconds before the expiry; acquire.
    ///
    /// Expected:
    /// - The license expires 86500 seconds after the start; the second lease expires then, 300 seconds on.
    /// </remarks>
    [Fact]
    public async Task Acquire_FromFirstActivation_FixesTheExpiry()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        string policy = await CloudPolicyAsync(admin, "\"expirationBasis\":\"fromFirstActivation\",\"duration\":86400,\"leaseDuration\":3600");
        (string licenseId, string key) = await LicenseAsync(admin, policy, TwoSeats);
        using var device = TestDevice.Create(test, service);

        test.Time.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(HttpStatusCode.Created, (await device.AcquireAsync(key)).Status);
        Assert.Equal(Iso(86_500), (await TestService.GetAsync(admin, $"v1/admin/licenses/{licenseId}")).GetProperty("expiresAt").GetString());
        test.Time.Advance(TimeSpan.FromSeconds(86_100));
        Assert.Equal(Time(86_500), device.VerifyLease(await device.AcquireAsync(key)).ExpiresAt);
    }

    /// <summary>
    /// The administration API lists a license's leases page by page, stopped ones too, with lease.read, and releases one,
    /// a borrow too, with lease.release in the same transaction as its audit row; support has lease.release, viewer not.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy with borrowLimit 3600; a license with 2 seats of core; one device holding a lease and
    ///   another a borrow; keys with lease.read alone and with lease.release alone; each engine.
    ///
    /// Steps:
    /// - List with limit 1, then after the first page's next; list an unknown license and with after "x"; release with the
    ///   reader; release the borrow with the remover; release it again and release "x"; read the audit row.
    ///
    /// Expected:
    /// - One item each, the first page's next is its item and the second has no next; 404 ELM_LICENSE_NOT_FOUND, 400 ELM_INVALID_ARGUMENT "after must be
    ///   a lease ID."; 403 ELM_FORBIDDEN; 204 and the list has one item; 404 ELM_UNKNOWN_LEASE "No lease has this ID." twice;
    ///   the lease.release row targets the borrow with its license, feature, and seats.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Admin_ListAndRelease_AreAudited(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, string owner) = await StartAsync(test);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        (string licenseId, string key) = await LicenseAsync(admin, await CloudPolicyAsync(admin, "\"borrowLimit\":3600"), TwoSeats);
        using var device = TestDevice.Create(test, service);
        using var borrower = TestDevice.Create(test, service);
        _ = device.VerifyLease(await device.AcquireAsync(key));
        string borrow = borrower.VerifyLease(await borrower.BorrowAsync(key, 3600, TestDevice.Fingerprints())).LeaseId;
        HttpClient reader = test.Http(service, await TestService.CreateKeyAsync(admin, "\"lease.read\""));
        HttpClient remover = test.Http(service, await TestService.CreateKeyAsync(admin, "\"lease.release\""));

        JsonElement first = await TestService.GetAsync(reader, $"v1/admin/licenses/{licenseId}/leases?limit=1");
        string next = first.GetProperty("next").GetString()!;
        Assert.Equal(next, first.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetString());
        JsonElement second = await TestService.GetAsync(reader, $"v1/admin/licenses/{licenseId}/leases?limit=1&after={next}");
        Assert.Equal((1, false), (second.GetProperty("items").GetArrayLength(), second.TryGetProperty("next", out _)));
        await TestService.AssertProblemAsync(
            await reader.GetAsync($"v1/admin/licenses/{Guid.CreateVersion7():D}/leases", cancel),
            HttpStatusCode.NotFound,
            "ELM_LICENSE_NOT_FOUND",
            "No license has this ID."
        );
        await TestService.AssertProblemAsync(
            await reader.GetAsync($"v1/admin/licenses/{licenseId}/leases?after=x", cancel),
            HttpStatusCode.BadRequest,
            "ELM_INVALID_ARGUMENT",
            "after must be a lease ID."
        );
        await TestService.AssertProblemAsync(
            await reader.DeleteAsync($"v1/admin/leases/{borrow}", cancel),
            HttpStatusCode.Forbidden,
            "ELM_FORBIDDEN",
            "The API key does not have the permission lease.release."
        );

        using (HttpResponseMessage released = await remover.DeleteAsync($"v1/admin/leases/{borrow}", cancel))
        {
            Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        }

        Assert.Equal(1, (await TestService.GetAsync(admin, $"v1/admin/licenses/{licenseId}/leases")).GetProperty("items").GetArrayLength());
        foreach (string gone in new[] { borrow, "x" })
        {
            await TestService.AssertProblemAsync(
                await remover.DeleteAsync($"v1/admin/leases/{gone}", cancel),
                HttpStatusCode.NotFound,
                "ELM_UNKNOWN_LEASE",
                "No lease has this ID."
            );
        }

        JsonElement row = (await TestService.AuditRowsAsync(test, service, owner)).Single(r =>
            r.GetProperty("action").GetString() == "lease.release" && r.GetProperty("result").GetString() == "ELM_OK"
        );
        Assert.Equal(
            (borrow, $"{{\"licenseId\":\"{licenseId}\",\"feature\":\"core\",\"seats\":1}}"),
            (row.GetProperty("target").GetString(), row.GetProperty("detail").GetString())
        );
        Assert.Contains("lease.release", Permissions.OfRole(Permissions.RoleSupport));
        Assert.Contains("lease.read", Permissions.OfRole(Permissions.RoleViewer));
        Assert.DoesNotContain("lease.release", Permissions.OfRole(Permissions.RoleViewer));
    }

    /// <summary>
    /// The lease paths are limited per source in the devices' bucket before anything else, so an ID that cannot be a lease
    /// is still counted.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - RateLimitClientPerIp 1.
    ///
    /// Steps:
    /// - Release "x" twice without a proof; list the API keys as the owner.
    ///
    /// Expected:
    /// - 204, then 429 ELM_RATE_LIMITED "Too many requests from this address."; the list gives 200.
    /// </remarks>
    [Fact]
    public async Task Release_PerSource_IsLimited()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync(s => s with { RateLimitClientPerIp = 1 });
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using var anonymous = new HttpClient { BaseAddress = service.Url };

        using (HttpResponseMessage released = await anonymous.DeleteAsync("v1/leases/x", cancel))
        {
            Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        }

        await TestService.AssertProblemAsync(
            await anonymous.DeleteAsync("v1/leases/x", cancel),
            HttpStatusCode.TooManyRequests,
            "ELM_RATE_LIMITED",
            "Too many requests from this address."
        );
        using HttpResponseMessage list = await test.Http(service, owner).GetAsync("v1/admin/api-keys", cancel);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    /// <summary>
    /// When no certificate of the signing key covers the time any more, an acquisition answers ELM_INTERNAL_ERROR and
    /// writes nothing.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The service's only certificate ends 9 days after the start; a cloud-floating license with 2 seats of core.
    ///
    /// Steps:
    /// - Move the clock 10 days; acquire; list the leases.
    ///
    /// Expected:
    /// - 500 ELM_INTERNAL_ERROR "The service cannot sign a lease now."; no lease.
    /// </remarks>
    [Fact]
    public async Task Acquire_CertificateRanOut_AnswersInternalError()
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
        (string licenseId, string key) = await LicenseAsync(admin, await CloudPolicyAsync(admin, string.Empty), TwoSeats);
        using var device = TestDevice.Create(test, service);

        test.Time.Advance(TimeSpan.FromDays(10));
        AssertProblem(
            await device.AcquireAsync(key),
            HttpStatusCode.InternalServerError,
            "ELM_INTERNAL_ERROR",
            "The service cannot sign a lease now."
        );
        Assert.Equal(0, (await TestService.GetAsync(admin, $"v1/admin/licenses/{licenseId}/leases")).GetProperty("items").GetArrayLength());
    }

    // Product app with feature core, and a cloud-floating policy of it with the members given.
    private static async Task<string> CloudPolicyAsync(HttpClient admin, string members)
    {
        string product = await ProductAsync(admin, "app", "core");
        return await TestService.PolicyAsync(admin, product, members.Length > 0 ? Cloud + "," + members : Cloud);
    }
}
