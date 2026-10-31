using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using ElasticLM.Licensing;
using static ElasticLM.Service.Tests.ElmServiceActivationsTests;

namespace ElasticLM.Service.Tests;

/// <summary>
/// Offline activation, refresh, and deactivation by files (design §8.4.4, §8.4.5, §12.8, §12.8.4, M4c1), through the public
/// and the administration entry, on SQLite and on PostgreSQL.
/// </summary>
public sealed class ElmServiceOfflineTests
{
    private const long Day = 86_400;
    private const string NoActivation = "No activation has this ID; activate again.";

    /// <summary>
    /// A request file activates, the same file gets the same answer, a refresh file gives the next sequence number, and a
    /// deactivation proof frees the place; each file's token names its request, and the audit rows say offline.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product app with feature core; a default policy and a license of it; each engine.
    ///
    /// Steps:
    /// - Post an activation file; read the activation and the audit log; post the same file again; post a refresh file with
    ///   seq 1; count the kept answers; post a deactivation proof with seq 2; read the activation; post the proof again; post
    ///   the refresh file again.
    ///
    /// Expected:
    /// - 200 without an ELM-Nonce header; a standard token bound to the device, sequence 1, areq the request's nonce; the
    ///   activation has no client and no app; one activation.activate row by the installation with "channel": "offline";
    ///   the same body again, still one row, seq 1; the refresh gives sequence 2 with its own nonce; one kept answer; 204;
    ///   404 for the activation; one activation.deactivate row with "channel": "offline"; then 404 ELM_ACTIVATION_NOT_FOUND
    ///   for the proof and for the refresh file.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Offline_ActivateRefreshDeactivate_FollowTheDesign(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, string owner) = await StartAsync(test);
        string product = await ProductAsync(admin, "app", "core");
        (string licenseId, string key) = await LicenseAsync(admin, await TestService.PolicyAsync(admin, product, string.Empty));
        using var device = TestDevice.Create(test, service);
        string[] prints = TestDevice.Fingerprints(2);
        string first = TestDevice.NewNonce();
        string file = device.ActivationFile(key, prints, first);

        DeviceAnswer activated = await device.OfflineAsync(file);
        Assert.True(activated.Status == HttpStatusCode.OK, activated.Text);
        Assert.Null(activated.Nonce);
        VerifiedLicense license = device.Verify(activated.File, prints);
        Assert.Equal((LicenseKind.Standard, 1L, first), (license.Kind, license.Sequence!.Value, license.OfflineRequestNonce));
        JsonElement activation = await TestService.GetAsync(admin, $"v1/admin/activations/{activated.ActivationId}");
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (activation.GetProperty("client").ValueKind, activation.GetProperty("app").ValueKind));
        JsonElement row = (await TestService.AuditRowsAsync(test, service, owner)).Single(r =>
            r.GetProperty("action").GetString() == "activation.activate"
        );
        Assert.Equal(
            ("installation", device.KeyId, $"{{\"licenseId\":\"{licenseId}\",\"seq\":1,\"reactivated\":false,\"channel\":\"offline\"}}"),
            (row.GetProperty("actorType").GetString(), row.GetProperty("actorId").GetString(), row.GetProperty("detail").GetString())
        );

        DeviceAnswer again = await device.OfflineAsync(file);
        Assert.Equal((HttpStatusCode.OK, activated.Text), (again.Status, again.Text));
        Assert.Single(await TestService.AuditRowsAsync(test, service, owner), r => r.GetProperty("action").GetString() == "activation.activate");
        Assert.Equal(1L, (await TestService.GetAsync(admin, $"v1/admin/activations/{activated.ActivationId}")).GetProperty("seq").GetInt64());

        string second = TestDevice.NewNonce();
        string refreshFile = device.RefreshFile(activated.ActivationId, 1, nonce: second);
        DeviceAnswer refreshed = await device.OfflineAsync(refreshFile);
        Assert.True(refreshed.Status == HttpStatusCode.OK, refreshed.Text);
        VerifiedLicense next = device.Verify(refreshed.File, prints);
        Assert.Equal((2L, second), (next.Sequence!.Value, next.OfflineRequestNonce));
        Assert.Equal(1L, await test.ScalarAsync("SELECT COUNT(*) FROM offline_requests"));

        DeviceAnswer deactivated = await device.OfflineAsync(device.DeactivationFile(activated.ActivationId, 2));
        Assert.True(deactivated.Status == HttpStatusCode.NoContent, deactivated.Text);
        using (
            HttpResponseMessage gone = await admin.GetAsync($"v1/admin/activations/{activated.ActivationId}", TestContext.Current.CancellationToken)
        )
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        JsonElement deactivation = (await TestService.AuditRowsAsync(test, service, owner)).Single(r =>
            r.GetProperty("action").GetString() == "activation.deactivate"
        );
        Assert.Equal(
            $"{{\"licenseId\":\"{licenseId}\",\"deactivations\":1,\"channel\":\"offline\"}}",
            deactivation.GetProperty("detail").GetString()
        );
        AssertProblem(
            await device.OfflineAsync(device.DeactivationFile(activated.ActivationId, 2)),
            HttpStatusCode.NotFound,
            "ELM_ACTIVATION_NOT_FOUND",
            NoActivation
        );
        AssertProblem(await device.OfflineAsync(refreshFile), HttpStatusCode.NotFound, "ELM_ACTIVATION_NOT_FOUND", NoActivation);
    }

    /// <summary>
    /// Each check of an offline file refuses only what it should, with the design's status and code, at both sides of its
    /// bounds.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product app with core, and product other; two licenses of app; devices A and B; each engine.
    ///
    /// Steps:
    /// - Post: text that is no JWS; a proof's type; a payload with another file's signature; ver 2; exp one second past 7
    ///   days; iat 301 seconds ahead, then 300; iat 7 days back (exp now), then a second less; product other; an unknown key;
    ///   another request with A's nonce; B's request with A's nonce; a refresh of an unknown ID; a refresh of A's activation
    ///   signed by B; a deactivation of A's activation signed by B; A's deactivation with its seq changed after signing; a
    ///   deactivation 301 seconds ahead; a deactivation whose act is not a UUID; a body of 65 KiB.
    ///
    /// Expected:
    /// - 400 ELM_MALFORMED twice; 400 ELM_PROOF_INVALID; 400 ELM_MALFORMED twice; 400 ELM_MALFORMED "issued in the future",
    ///   then 200; 400 ELM_REQUEST_EXPIRED, then 200; 400 ELM_WRONG_PRODUCT; 404 ELM_LICENSE_NOT_FOUND; 409 ELM_REPLAYED;
    ///   200 for B; 404 ELM_ACTIVATION_NOT_FOUND; 400 ELM_PROOF_INVALID three times; 400 ELM_MALFORMED twice; 413.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Offline_EachRefusal_IsAsDesigned(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        string product = await ProductAsync(admin, "app", "core");
        await ProductAsync(admin, "other");
        string policy = await TestService.PolicyAsync(admin, product, "\"maxActivations\":10");
        (_, string key) = await LicenseAsync(admin, policy);
        (_, string otherKey) = await LicenseAsync(admin, policy);
        using var a = TestDevice.Create(test, service);
        using var b = TestDevice.Create(test, service);
        string[] prints = TestDevice.Fingerprints();
        const string Malformed = "The file is not a valid offline request.";
        const string NotAFile = "The file is not an elm-actreq+jws or elm-deact+jws.";

        AssertProblem(await a.OfflineAsync("not a file"), HttpStatusCode.BadRequest, "ELM_MALFORMED", NotAFile);
        string proofType = a.Sign(TokenTypes.Proof, Payload(a.ActivationFile(key, prints)));
        AssertProblem(await a.OfflineAsync(proofType), HttpStatusCode.BadRequest, "ELM_MALFORMED", NotAFile);
        string[] one = a.ActivationFile(key, prints).Split('.');
        string[] two = a.ActivationFile(key, prints).Split('.');
        AssertProblem(
            await a.OfflineAsync(one[0] + "." + one[1] + "." + two[2]),
            HttpStatusCode.BadRequest,
            "ELM_PROOF_INVALID",
            "The request's signature does not verify with its key."
        );
        AssertProblem(
            await a.OfflineAsync(a.ActivationFile(key, prints, change: p => p.Replace("\"ver\":1", "\"ver\":2", StringComparison.Ordinal))),
            HttpStatusCode.BadRequest,
            "ELM_MALFORMED",
            Malformed
        );
        AssertProblem(await a.OfflineAsync(a.ActivationFile(key, prints, exp: 604_801)), HttpStatusCode.BadRequest, "ELM_MALFORMED", Malformed);
        AssertProblem(
            await a.OfflineAsync(a.ActivationFile(key, prints, iat: 301)),
            HttpStatusCode.BadRequest,
            "ELM_MALFORMED",
            "The request is issued in the future."
        );
        Assert.Equal(HttpStatusCode.OK, (await a.OfflineAsync(a.ActivationFile(key, prints, iat: 300))).Status);
        AssertProblem(
            await a.OfflineAsync(a.ActivationFile(key, prints, iat: -604_800)),
            HttpStatusCode.BadRequest,
            "ELM_REQUEST_EXPIRED",
            "The request has expired; make a new one."
        );

        // A's activation is there now: the next activation is a reactivation and needs its seq (design §10.4 step 4).
        DeviceAnswer kept = await a.OfflineAsync(a.ActivationFile(key, prints, iat: -604_799, more: ",\"seq\":1"));
        Assert.True(kept.Status == HttpStatusCode.OK, kept.Text);
        AssertProblem(
            await a.OfflineAsync(a.ActivationFile(key, prints, product: "other")),
            HttpStatusCode.BadRequest,
            "ELM_WRONG_PRODUCT",
            "The license key is for another product."
        );
        AssertProblem(
            await a.OfflineAsync(a.ActivationFile("ELM-AAAA-AAAA-AAAA-AAAA", prints)),
            HttpStatusCode.NotFound,
            "ELM_LICENSE_NOT_FOUND",
            "No license has this key."
        );

        string nonce = TestDevice.NewNonce();
        Assert.Equal(HttpStatusCode.OK, (await a.OfflineAsync(a.ActivationFile(otherKey, prints, nonce))).Status);
        AssertProblem(
            await a.OfflineAsync(a.ActivationFile(otherKey, TestDevice.Fingerprints(), nonce)),
            HttpStatusCode.Conflict,
            "ELM_REPLAYED",
            "Another request was made with this request's nonce."
        );
        Assert.Equal(HttpStatusCode.OK, (await b.OfflineAsync(b.ActivationFile(otherKey, prints, nonce))).Status);

        AssertProblem(
            await a.OfflineAsync(a.RefreshFile(Guid.CreateVersion7().ToString("D"), 1)),
            HttpStatusCode.NotFound,
            "ELM_ACTIVATION_NOT_FOUND",
            NoActivation
        );
        AssertProblem(
            await b.OfflineAsync(b.RefreshFile(kept.ActivationId, 2)),
            HttpStatusCode.BadRequest,
            "ELM_PROOF_INVALID",
            "The request is not made with the activation's installation key."
        );
        const string NotSigned = "The proof is not signed with the activation's installation key.";
        AssertProblem(
            await a.OfflineAsync(a.DeactivationFile(kept.ActivationId, 2, signer: b.Key)),
            HttpStatusCode.BadRequest,
            "ELM_PROOF_INVALID",
            NotSigned
        );
        string[] proof = a.DeactivationFile(kept.ActivationId, 2).Split('.');
        string changed = Base64Url.EncodeToString(
            Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(Base64Url.DecodeFromChars(proof[1])).Replace("\"seq\":2", "\"seq\":3", StringComparison.Ordinal)
            )
        );
        AssertProblem(await a.OfflineAsync(proof[0] + "." + changed + "." + proof[2]), HttpStatusCode.BadRequest, "ELM_PROOF_INVALID", NotSigned);
        AssertProblem(
            await a.OfflineAsync(a.DeactivationFile(kept.ActivationId, 2, iat: 301)),
            HttpStatusCode.BadRequest,
            "ELM_MALFORMED",
            "The request is issued in the future."
        );
        AssertProblem(
            await a.OfflineAsync(a.Sign(TokenTypes.DeactivationProof, """{"ver":1,"act":"x","seq":2,"iat":0}""")),
            HttpStatusCode.BadRequest,
            "ELM_MALFORMED",
            Malformed
        );
        AssertProblem(
            await a.OfflineAsync(new string('a', 65 * 1024)),
            HttpStatusCode.RequestEntityTooLarge,
            "ELM_PAYLOAD_TOO_LARGE",
            "The request body is larger than 64 KiB."
        );
    }

    /// <summary>
    /// A kept answer is sent again only while the license and the activation still allow it: not after a revocation, an
    /// administrator's deactivation, or a stop as a clone.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product app; three licenses of a default (Deny) policy, each activated offline by its own device; each engine.
    ///
    /// Steps:
    /// - Revoke the first license and post its activation file again; deactivate the second activation through the
    ///   administration API and post its file again; post a refresh of the third with seq 0 (a clone), then its activation
    ///   file again.
    ///
    /// Expected:
    /// - 403 ELM_LICENSE_REVOKED; 404 ELM_ACTIVATION_NOT_FOUND; 409 ELM_CLONE_DETECTED twice, the second "… was stopped …".
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Offline_KeptAnswer_FollowsTheState(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string product = await ProductAsync(admin, "app", "core");
        string policy = await TestService.PolicyAsync(admin, product, string.Empty);
        string[] prints = TestDevice.Fingerprints();
        var files = new List<(TestDevice Device, string LicenseId, string File, DeviceAnswer Answer)>();
        for (int i = 0; i < 3; i++)
        {
            (string id, string key) = await LicenseAsync(admin, policy);
            var device = TestDevice.Create(test, service);
            string file = device.ActivationFile(key, prints);
            files.Add((device, id, file, await device.OfflineAsync(file)));
            Assert.Equal(HttpStatusCode.OK, files[^1].Answer.Status);
        }

        (await admin.PostAsync($"v1/admin/licenses/{files[0].LicenseId}/revoke", null, cancel)).Dispose();
        AssertProblem(await files[0].Device.OfflineAsync(files[0].File), HttpStatusCode.Forbidden, "ELM_LICENSE_REVOKED", "The license is revoked.");
        (await admin.PostAsync($"v1/admin/activations/{files[1].Answer.ActivationId}/deactivate", null, cancel)).Dispose();
        AssertProblem(await files[1].Device.OfflineAsync(files[1].File), HttpStatusCode.NotFound, "ELM_ACTIVATION_NOT_FOUND", NoActivation);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await files[2].Device.OfflineAsync(files[2].Device.RefreshFile(files[2].Answer.ActivationId, 0))).Status
        );
        AssertProblem(
            await files[2].Device.OfflineAsync(files[2].File),
            HttpStatusCode.Conflict,
            "ELM_CLONE_DETECTED",
            "The activation was stopped because a clone of the installation was detected."
        );
        foreach ((TestDevice device, _, _, _) in files)
        {
            device.Dispose();
        }
    }

    /// <summary>
    /// An offline request that names the request whose answer was lost gets the next file, not the last one, and a copy
    /// that got the lost file is a clone after it; a new request after an expired one goes through with the old nonce.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product app; a default (Deny) policy with two licenses; device A activated online on the first; device B; each
    ///   engine.
    ///
    /// Steps:
    /// - A refreshes online (the answer is lost) and then sends an offline refresh with seq 1 and retry naming the lost
    ///   request; A refreshes online with seq 2 (as a copy that got the lost file would). B activates the second license
    ///   offline (the answer is lost), waits 8 days, and sends a new activation with seq 0 and retry naming the first nonce.
    ///
    /// Expected:
    /// - The offline refresh gives sequence 3 with its own nonce; the online one 409 ELM_CLONE_DETECTED. B's new request
    ///   gives sequence 2 with the new nonce, and no activation.clone row is written.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Offline_RetryOfALostAnswer_GetsTheNextFile(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, string owner) = await StartAsync(test);
        string product = await ProductAsync(admin, "app", "core");
        string policy = await TestService.PolicyAsync(admin, product, string.Empty);
        (_, string key) = await LicenseAsync(admin, policy);
        (_, string secondKey) = await LicenseAsync(admin, policy);
        using var a = TestDevice.Create(test, service);
        using var b = TestDevice.Create(test, service);
        string[] prints = TestDevice.Fingerprints();

        DeviceAnswer online = await a.ActivateAsync(key, prints);
        DeviceAnswer lost = await a.RefreshAsync(online.ActivationId, 1);
        Assert.Equal(HttpStatusCode.OK, lost.Status);
        string nonce = TestDevice.NewNonce();
        DeviceAnswer offline = await a.OfflineAsync(a.RefreshFile(online.ActivationId, 1, [lost.Jti], nonce));
        Assert.True(offline.Status == HttpStatusCode.OK, offline.Text);
        VerifiedLicense next = a.Verify(offline.File, prints);
        Assert.Equal((3L, nonce), (next.Sequence!.Value, next.OfflineRequestNonce));
        Assert.Equal(HttpStatusCode.Conflict, (await a.RefreshAsync(online.ActivationId, 2)).Status);

        string first = TestDevice.NewNonce();
        Assert.Equal(HttpStatusCode.OK, (await b.OfflineAsync(b.ActivationFile(secondKey, prints, first))).Status);
        test.Time.Advance(TimeSpan.FromDays(8));
        string second = TestDevice.NewNonce();
        DeviceAnswer again = await b.OfflineAsync(
            b.ActivationFile(secondKey, prints, second, iat: 8 * Day, more: $",\"seq\":0,\"retry\":[\"{first}\"]")
        );
        Assert.True(again.Status == HttpStatusCode.OK, again.Text);
        VerifiedLicense renewed = b.Verify(again.File, prints);
        Assert.Equal((2L, second), (renewed.Sequence!.Value, renewed.OfflineRequestNonce));
        Assert.Single(await TestService.AuditRowsAsync(test, service, owner), r => r.GetProperty("action").GetString() == "activation.clone");
    }

    /// <summary>
    /// The administration entry needs activation.offline and audits every request, refused ones too; its activation rows
    /// name the API key.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product app; a license; a key with activation.offline and one with activation.read only; each engine.
    ///
    /// Steps:
    /// - With the first key, post an activation file, an expired one, and a text that is no file; with the second, post a
    ///   file; read the audit log.
    ///
    /// Expected:
    /// - support holds activation.offline and viewer does not.
    /// - 200, 400 ELM_REQUEST_EXPIRED, 400 ELM_MALFORMED, 403 ELM_FORBIDDEN; an activation.activate row by the API key
    ///   with "channel": "offline"; activation.offline rows: ELM_OK with the activation and {"operation":"activate",...},
    ///   ELM_REQUEST_EXPIRED with operation activate, ELM_MALFORMED with operation null and no target, and ELM_FORBIDDEN.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Offline_AdminEntry_AuditsEveryRequest(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin, string owner) = await StartAsync(test);
        string product = await ProductAsync(admin, "app", "core");
        (_, string key) = await LicenseAsync(admin, await TestService.PolicyAsync(admin, product, string.Empty));
        Assert.Contains("activation.offline", Permissions.OfRole(Permissions.RoleSupport));
        Assert.DoesNotContain("activation.offline", Permissions.OfRole(Permissions.RoleViewer));
        string processor = await TestService.CreateKeyAsync(admin, "\"activation.offline\"");
        string reader = await TestService.CreateKeyAsync(admin, "\"activation.read\"");
        using var device = TestDevice.Create(test, service);
        string[] prints = TestDevice.Fingerprints();

        DeviceAnswer done = await device.OfflineAsync(device.ActivationFile(key, prints), processor);
        Assert.True(done.Status == HttpStatusCode.OK, done.Text);
        AssertProblem(
            await device.OfflineAsync(device.ActivationFile(key, prints, iat: -604_800), processor),
            HttpStatusCode.BadRequest,
            "ELM_REQUEST_EXPIRED",
            "The request has expired; make a new one."
        );
        Assert.Equal(HttpStatusCode.BadRequest, (await device.OfflineAsync("x", processor)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await device.OfflineAsync(device.ActivationFile(key, prints), reader)).Status);

        JsonElement[] rows = await TestService.AuditRowsAsync(test, service, owner);
        JsonElement activated = rows.Single(r => r.GetProperty("action").GetString() == "activation.activate");
        Assert.Equal("apikey", activated.GetProperty("actorType").GetString());
        Assert.Contains("\"channel\":\"offline\"", activated.GetProperty("detail").GetString(), StringComparison.Ordinal);
        (string? Result, string? Target, string? Detail)[] offline =
        [
            .. rows.Where(r => r.GetProperty("action").GetString() == "activation.offline")
                .Select(r =>
                    (
                        r.GetProperty("result").GetString(),
                        r.GetProperty("target").GetString(),
                        r.TryGetProperty("detail", out JsonElement d) ? d.GetString() : null
                    )
                ),
        ];
        Assert.Equal(
            [
                ("ELM_OK", done.ActivationId, "{\"operation\":\"activate\",\"channel\":\"offline\"}"),
                ("ELM_REQUEST_EXPIRED", null, "{\"operation\":\"activate\",\"channel\":\"offline\"}"),
                ("ELM_MALFORMED", null, "{\"operation\":null,\"channel\":\"offline\"}"),
                ("ELM_FORBIDDEN", null, "{\"permission\":\"activation.offline\"}"),
            ],
            offline
        );
    }

    /// <summary>
    /// The same request posted twice at once is processed once; the public entry is limited per installation key; kept
    /// answers are removed once their request has expired.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with rateLimit.clientPerKey 2; product app; a license activated offline; each engine.
    ///
    /// Steps:
    /// - After a second, post one refresh file twice at once; post another refresh file at once; move the clock 7 days and a
    ///   minute.
    ///
    /// Expected:
    /// - Both answers are 200 with the same body, and the activation's seq is 2; the other file 429 ELM_RATE_LIMITED (the
    ///   two before used the second's 2 requests); no kept answer after the expiry.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Offline_AtOnce_ProcessedOnceAndRemovedAfterExpiry(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync(s => s with { RateLimitClientPerKey = 2 });
        HttpClient admin = test.Http(service, owner);
        string product = await ProductAsync(admin, "app", "core");
        (_, string key) = await LicenseAsync(admin, await TestService.PolicyAsync(admin, product, string.Empty));
        using var device = TestDevice.Create(test, service);
        string[] prints = TestDevice.Fingerprints();
        DeviceAnswer activated = await device.OfflineAsync(device.ActivationFile(key, prints));
        Assert.Equal(HttpStatusCode.OK, activated.Status);

        test.Time.Advance(TimeSpan.FromSeconds(1));
        string file = device.RefreshFile(activated.ActivationId, 1, iat: 1);
        DeviceAnswer[] answers = await Task.WhenAll(device.OfflineAsync(file), device.OfflineAsync(file));
        Assert.All(answers, a => Assert.True(a.Status == HttpStatusCode.OK, a.Text));
        Assert.Equal(answers[0].Text, answers[1].Text);
        Assert.Equal(2L, (await TestService.GetAsync(admin, $"v1/admin/activations/{activated.ActivationId}")).GetProperty("seq").GetInt64());
        AssertProblem(
            await device.OfflineAsync(device.RefreshFile(activated.ActivationId, 2, iat: 1)),
            HttpStatusCode.TooManyRequests,
            "ELM_RATE_LIMITED",
            "Too many requests with this installation key."
        );

        Assert.Equal(1L, await test.ScalarAsync("SELECT COUNT(*) FROM offline_requests"));
        test.Time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        await test.WaitWithClockAsync(async () => await test.ScalarAsync("SELECT COUNT(*) FROM offline_requests") == 0);
    }

    /// <summary>
    /// The public entry is limited per address in the devices' bucket, apart from the administration API's.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with rateLimit.clientPerIp 2 and the administration API's per-address limit at its default (SQLite: the
    ///   limit is in the HTTP layer).
    ///
    /// Steps:
    /// - Post three texts to /v1/offline at once; list licenses through the administration API.
    ///
    /// Expected:
    /// - Two 400 ELM_MALFORMED and one 429 ELM_RATE_LIMITED "Too many requests from this address."; the list is 200.
    /// </remarks>
    [Fact]
    public async Task Offline_PublicEntry_SharesTheDevicesLimit()
    {
        await using var test = new TestService();
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync(s => s with { RateLimitClientPerIp = 2 });
        HttpClient admin = test.Http(service, owner);
        using var device = TestDevice.Create(test, service);

        DeviceAnswer[] answers = await Task.WhenAll(device.OfflineAsync("x"), device.OfflineAsync("x"), device.OfflineAsync("x"));
        Assert.Equal(2, answers.Count(a => a.Status == HttpStatusCode.BadRequest));
        AssertProblem(
            answers.Single(a => a.Status != HttpStatusCode.BadRequest),
            HttpStatusCode.TooManyRequests,
            "ELM_RATE_LIMITED",
            "Too many requests from this address."
        );
        using HttpResponseMessage list = await admin.GetAsync("v1/admin/licenses", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    /// <summary>
    /// A server license is activated offline as online: its token carries the lease members and names the request.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product app; a server policy (lease 600 s, borrow 1 h, overage 10, borrow returns 3, clone flag) and a license of it
    ///   with core at 5 seats; a device; each engine.
    ///
    /// Steps:
    /// - Post an activation file; verify the file as a server license.
    ///
    /// Expected:
    /// - 200; kind Server, 5 seats, lease 600 s, borrow 1 h, overage 10, borrow returns 3, clone Flag, areq the request's
    ///   nonce.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Offline_ServerLicense_CarriesTheLeaseMembers(string provider)
    {
        await using TestService test = TestService.Open(provider);
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
        string nonce = TestDevice.NewNonce();

        DeviceAnswer answer = await device.OfflineAsync(device.ActivationFile(key, prints, nonce));
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Text);
        VerifiedLicense license = device.Verify(answer.File, prints, LicenseKind.Server);
        Assert.Equal(
            (LicenseKind.Server, 5, TimeSpan.FromSeconds(600), TimeSpan.FromHours(1), 10, 3, CloneStrategy.Flag, nonce),
            (
                license.Kind,
                license.Features.Single().Seats!.Value,
                license.LeaseDuration!.Value,
                license.BorrowLimit!.Value,
                license.OveragePercent!.Value,
                license.BorrowReturnLimit!.Value,
                license.CloneStrategy!.Value,
                license.OfflineRequestNonce
            )
        );
    }

    private static string Payload(string file) => Encoding.UTF8.GetString(Base64Url.DecodeFromChars(file.Split('.')[1]));
}
