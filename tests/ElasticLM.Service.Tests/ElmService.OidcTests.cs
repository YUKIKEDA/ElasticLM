using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

namespace ElasticLM.Service.Tests;

/// <summary>
/// OIDC access tokens on the administration API, the administrators API, and the 90-day rule (design §14.5, §14.6, decision
/// 0.98, M5a), against <see cref="FakeIdp"/>.
/// </summary>
public sealed class ElmServiceOidcTests
{
    private const string TokenRefused = "The access token is not valid, or its administrator is disabled.";

    /// <summary>
    /// A valid token of a mapped group signs in: the administrator is added with the mapped role, its last login is written,
    /// and the audit log names it by its ID, with the issuer and subject in admin.create.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service that trusts the fake provider; an owner key from setup to read the audit log.
    ///
    /// Steps:
    /// - List administrators with an owner token of alice; read the audit log.
    ///
    /// Expected:
    /// - 200 with alice as owner, enabled, lastLoginAt now; an admin.create row (actor oidc, the admin's ID, detail with
    ///   issuer, subject, role) and an admin.list row by the same actor.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Token_SignsIn_AddsTheAdministrator(string provider)
    {
        await using TestService test = TestService.Open(provider);
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        ElmService service = await StartAsync(test, idp);

        JsonElement alice = (await ListAdminsAsync(test, service, idp.Token("alice"))).Single(a => a.GetProperty("subject").GetString() == "alice");
        Assert.Equal(idp.Issuer, alice.GetProperty("issuer").GetString());
        Assert.Equal("owner", alice.GetProperty("role").GetString());
        Assert.True(alice.GetProperty("enabled").GetBoolean());
        Assert.Equal("2026-10-01T00:00:00Z", alice.GetProperty("lastLoginAt").GetString());

        string id = alice.GetProperty("id").GetString()!;
        JsonElement[] rows = await TestService.AuditRowsAsync(test, service, owner);
        JsonElement created = rows.Single(r => r.GetProperty("action").GetString() == "admin.create");
        Assert.Equal("oidc", created.GetProperty("actorType").GetString());
        Assert.Equal(id, created.GetProperty("actorId").GetString());
        Assert.Equal(id, created.GetProperty("target").GetString());
        using (JsonDocument detail = JsonDocument.Parse(created.GetProperty("detail").GetString()!))
        {
            Assert.Equal(idp.Issuer, detail.RootElement.GetProperty("issuer").GetString());
            Assert.Equal("alice", detail.RootElement.GetProperty("subject").GetString());
            Assert.Equal("owner", detail.RootElement.GetProperty("role").GetString());
        }

        JsonElement listed = rows.Single(r => r.GetProperty("action").GetString() == "admin.list");
        Assert.Equal(id, listed.GetProperty("actorId").GetString());
    }

    /// <summary>
    /// Every token the design refuses gets the one answer 401 ELM_UNAUTHORIZED, and a token within the 60 seconds of clock
    /// skew passes (design §14.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service that trusts the fake provider.
    ///
    /// Steps:
    /// - Send tokens: signed by another key, unsigned, of another issuer, of another audience, expired 61 seconds ago, not
    ///   valid for 120 seconds, typ id+jwt, without typ, alg HS256, five parts, 8,193 characters, without sub, with a sub of
    ///   256 characters, with 101 role values; then one that expired 30 seconds ago, one with typ JWT, and one without kid.
    ///
    /// Expected:
    /// - 401 ELM_UNAUTHORIZED with the token message for each refused one; 200 for the last three.
    /// </remarks>
    [Fact]
    public async Task Token_EachRefusal_IsUnauthorized()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        ElmService service = await StartAsync(test, idp);
        long now = test.Time.GetUtcNow().ToUnixTimeSeconds();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string valid = idp.Token();
        string[] parts = valid.Split('.');

        string[] refused =
        [
            idp.Token(key: other),
            parts[0] + "." + parts[1] + ".",
            idp.Token(change: (_, p) => p["iss"] = "https://other.example/realms/test"),
            idp.Token(change: (_, p) => p["aud"] = "other-api"),
            idp.Token(change: (_, p) => p["exp"] = now - 61),
            idp.Token(change: (_, p) => p["nbf"] = now + 120),
            idp.Token(change: (h, _) => h["typ"] = "id+jwt"),
            idp.Token(change: (h, _) => h.Remove("typ")),
            idp.Token(change: (h, _) => h["alg"] = "HS256"),
            "a.b.c.d.e",
            new string('x', ElmService.MaxAccessTokenLength + 1),
            idp.Token(change: (_, p) => p.Remove("sub")),
            idp.Token(new string('s', 256)),
            idp.Token(groups: [.. Enumerable.Range(0, ElmService.MaxRoleValues + 1).Select(i => "g" + i)]),
        ];
        foreach (string token in refused)
        {
            await TestService.AssertProblemAsync(
                await GetAdminsAsync(test, service, token),
                HttpStatusCode.Unauthorized,
                "ELM_UNAUTHORIZED",
                TokenRefused
            );
        }

        string[] accepted =
        [
            idp.Token(change: (_, p) => p["exp"] = now - 30),
            idp.Token(change: (h, _) => h["typ"] = "JWT"),
            idp.Token(change: (h, _) => h.Remove("kid")),
        ];
        foreach (string token in accepted)
        {
            using HttpResponseMessage answer = await GetAdminsAsync(test, service, token);
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        }
    }

    /// <summary>
    /// Roles come from the claim map (design §14.6): the strongest mapped value wins; a new user with no mapped value gets
    /// 403 and no row (audited without an actor ID); an existing administrator with no mapped value loses its role, and its
    /// API keys lose their permissions; a changed role is written with admin.role; the bootstrap owner is owner whatever its
    /// claims; admin.* is for owners only.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service that trusts the fake provider, whose bootstrap owner is boss at that provider; an owner key from setup.
    ///
    /// Steps:
    /// - Sign in carol with groups viewers and admins; dave with no mapped group; boss with no group; alice as owner, who
    ///   makes an API key; alice again with no mapped group; use alice's key; alice as admin.
    ///
    /// Expected:
    /// - carol is admin and gets 403 on the administrators list; dave gets 403 ELM_FORBIDDEN, no row, and an audit row with
    ///   a null actor ID and his issuer and subject; boss lists as owner; alice's second sign-in is 403 and makes her role
    ///   none (admin.role owner to none), and her key gets 403; her third sign-in makes her admin (admin.role none to admin).
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Roles_ComeFromTheClaimMap(string provider)
    {
        await using TestService test = TestService.Open(provider);
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        ElmService service = await StartAsync(test, idp, s => s with { BootstrapOwner = new AdminIdentity(idp.Issuer, "boss") });
        CancellationToken cancel = TestContext.Current.CancellationToken;

        using (HttpResponseMessage carol = await GetAdminsAsync(test, service, idp.Token("carol", ["elm-viewers", "elm-admins"])))
        {
            Assert.Equal(HttpStatusCode.Forbidden, carol.StatusCode);
        }

        await TestService.AssertProblemAsync(
            await GetAdminsAsync(test, service, idp.Token("dave", ["other"])),
            HttpStatusCode.Forbidden,
            "ELM_FORBIDDEN",
            "The access token's claims map to no role."
        );

        JsonElement[] admins = await ListAdminsAsync(test, service, idp.Token("boss", []));
        Assert.Equal("admin", Admin(admins, "carol").GetProperty("role").GetString());
        Assert.Equal("owner", Admin(admins, "boss").GetProperty("role").GetString());
        Assert.DoesNotContain(admins, a => a.GetProperty("subject").GetString() == "dave");

        string aliceKey = await TestService.CreateKeyAsync(test.Http(service, idp.Token("alice")), "\"license.read\"");
        using (HttpResponseMessage unmapped = await GetAdminsAsync(test, service, idp.Token("alice", ["other"])))
        {
            Assert.Equal(HttpStatusCode.Forbidden, unmapped.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await test.Http(service, aliceKey).GetAsync("v1/admin/licenses", cancel)).StatusCode);
        using (HttpResponseMessage asAdmin = await GetAdminsAsync(test, service, idp.Token("alice", ["elm-admins"])))
        {
            Assert.Equal(HttpStatusCode.Forbidden, asAdmin.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await test.Http(service, aliceKey).GetAsync("v1/admin/licenses", cancel)).StatusCode);

        JsonElement[] rows = await TestService.AuditRowsAsync(test, service, owner);
        JsonElement daveRow = rows.Single(r =>
            r.GetProperty("result").GetString() == "ELM_FORBIDDEN" && r.GetProperty("detail").GetString()!.Contains("dave", StringComparison.Ordinal)
        );
        Assert.Equal(JsonValueKind.Null, daveRow.GetProperty("actorId").ValueKind);
        string[] changes =
        [
            .. rows.Where(r => r.GetProperty("action").GetString() == "admin.role").Select(r => r.GetProperty("detail").GetString()!),
        ];
        Assert.Equal(["{\"from\":\"owner\",\"to\":\"none\"}", "{\"from\":\"none\",\"to\":\"admin\"}"], changes);
    }

    /// <summary>
    /// A disabled administrator's tokens are refused with 401, also when its claims map to no role (the disabled check comes
    /// first), and its last login is not written (design §14.6 steps 6 and 7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service that trusts the fake provider; owners alice and bob signed in.
    ///
    /// Steps:
    /// - alice disables bob; bob sends an owner token and a token with no mapped group; one hour later alice enables bob,
    ///   and bob sends a token again.
    ///
    /// Expected:
    /// - 204; 401 twice; bob's last login stays at the first sign-in while disabled; after enabling, 200.
    /// </remarks>
    [Fact]
    public async Task Disabled_Administrator_IsUnauthorized()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        ElmService service = await StartAsync(test, idp);
        string alice = idp.Token("alice");
        _ = await ListAdminsAsync(test, service, alice);
        string bobId = Admin(await ListAdminsAsync(test, service, idp.Token("bob")), "bob").GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(test, service, alice, $"v1/admin/admins/{bobId}/disable")).StatusCode);
        test.Time.Advance(TimeSpan.FromHours(2));
        foreach (string token in new[] { idp.Token("bob"), idp.Token("bob", ["other"]) })
        {
            await TestService.AssertProblemAsync(
                await GetAdminsAsync(test, service, token),
                HttpStatusCode.Unauthorized,
                "ELM_UNAUTHORIZED",
                TokenRefused
            );
        }

        alice = idp.Token("alice");
        Assert.Equal("2026-10-01T00:00:00Z", Admin(await ListAdminsAsync(test, service, alice), "bob").GetProperty("lastLoginAt").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(test, service, alice, $"v1/admin/admins/{bobId}/enable")).StatusCode);
        using HttpResponseMessage back = await GetAdminsAsync(test, service, idp.Token("bob"));
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
    }

    /// <summary>
    /// The last login is written when none is written or the written one is at least an hour old, not on every request
    /// (design §14.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service that trusts the fake provider.
    ///
    /// Steps:
    /// - alice lists at 00:00, 00:59:59, and 01:00:00 (a new token each time), reading her last login after each.
    ///
    /// Expected:
    /// - 00:00:00, still 00:00:00, then 01:00:00.
    /// </remarks>
    [Fact]
    public async Task LastLogin_IsWrittenAtMostHourly()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        ElmService service = await StartAsync(test, idp);
        string[] seen = new string[3];
        TimeSpan[] steps = [TimeSpan.Zero, new(0, 59, 59), TimeSpan.FromSeconds(1)];
        for (int i = 0; i < steps.Length; i++)
        {
            test.Time.Advance(steps[i]);
            seen[i] = Admin(await ListAdminsAsync(test, service, idp.Token("alice")), "alice").GetProperty("lastLoginAt").GetString()!;
        }

        Assert.Equal(["2026-10-01T00:00:00Z", "2026-10-01T00:00:00Z", "2026-10-01T01:00:00Z"], seen);
    }

    /// <summary>
    /// The administrators API (design §14.5): pages by ID; disabling yourself or the bootstrap owner is 409 ELM_CONFLICT;
    /// a change to the same state is 204 with {"unchanged": true}; an unknown or malformed ID is 404; the rows of disable and
    /// enable name the target.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service that trusts the fake provider, whose bootstrap owner is boss; owners alice and boss, admin carol signed in;
    ///   an owner key from setup.
    ///
    /// Steps:
    /// - As alice: list with limit 1 and follow next; disable alice; disable boss; enable carol; disable carol; disable an
    ///   unknown ID and a malformed one; read the audit log.
    ///
    /// Expected:
    /// - Pages of one with next until the last; 409 twice; 204 twice; 404 twice; the enable row has {"unchanged": true}, the
    ///   disable row a null detail, both with carol's ID as the target.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task AdminsApi_AnswersAsDesigned(string provider)
    {
        await using TestService test = TestService.Open(provider);
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        ElmService service = await StartAsync(test, idp, s => s with { BootstrapOwner = new AdminIdentity(idp.Issuer, "boss") });
        string alice = idp.Token("alice");
        _ = await ListAdminsAsync(test, service, idp.Token("boss", []));
        _ = await GetAdminsAsync(test, service, idp.Token("carol", ["elm-admins"]));
        JsonElement[] admins = await ListAdminsAsync(test, service, alice);
        CancellationToken cancel = TestContext.Current.CancellationToken;

        var paged = new List<string>();
        string? after = null;
        bool more = true;
        while (more)
        {
            using HttpResponseMessage page = await test.Http(service, alice)
                .GetAsync("v1/admin/admins?limit=1" + (after is null ? string.Empty : "&after=" + after), cancel);
            using JsonDocument document = JsonDocument.Parse(await page.Content.ReadAsStringAsync(cancel));
            paged.AddRange(document.RootElement.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("id").GetString()!));
            after =
                document.RootElement.TryGetProperty("next", out JsonElement next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            more = after is not null;
        }

        Assert.Equal(admins.Select(a => a.GetProperty("id").GetString()), paged);

        string aliceId = Admin(admins, "alice").GetProperty("id").GetString()!;
        string bossId = Admin(admins, "boss").GetProperty("id").GetString()!;
        string carolId = Admin(admins, "carol").GetProperty("id").GetString()!;
        const string Conflict = "The caller, the bootstrap owner, and the last enabled owner cannot be disabled.";
        await TestService.AssertProblemAsync(
            await PostAsync(test, service, alice, $"v1/admin/admins/{aliceId}/disable"),
            HttpStatusCode.Conflict,
            "ELM_CONFLICT",
            Conflict
        );
        await TestService.AssertProblemAsync(
            await PostAsync(test, service, alice, $"v1/admin/admins/{bossId}/disable"),
            HttpStatusCode.Conflict,
            "ELM_CONFLICT",
            Conflict
        );
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(test, service, alice, $"v1/admin/admins/{carolId}/enable")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(test, service, alice, $"v1/admin/admins/{carolId}/disable")).StatusCode);
        foreach (string id in new[] { Guid.CreateVersion7().ToString("D"), "not-an-id" })
        {
            await TestService.AssertProblemAsync(
                await PostAsync(test, service, alice, $"v1/admin/admins/{id}/disable"),
                HttpStatusCode.NotFound,
                "ELM_NOT_FOUND",
                "No administrator has this ID."
            );
        }

        JsonElement[] rows = await TestService.AuditRowsAsync(test, service, owner);
        JsonElement enabled = rows.Single(r => r.GetProperty("action").GetString() == "admin.enable");
        Assert.Equal("{\"unchanged\":true}", enabled.GetProperty("detail").GetString());
        Assert.Equal(carolId, enabled.GetProperty("target").GetString());
        JsonElement disabled = rows.Single(r =>
            r.GetProperty("action").GetString() == "admin.disable" && r.GetProperty("result").GetString() == "ELM_OK"
        );
        Assert.Equal(JsonValueKind.Null, disabled.GetProperty("detail").ValueKind);
        Assert.Equal(carolId, disabled.GetProperty("target").GetString());
    }

    /// <summary>
    /// The store refuses to disable the last enabled owner even for a caller that is not that owner, holding the audit
    /// chain's lock while it counts (design §14.5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service that trusts the fake provider, without setup; owners alice and bob signed in.
    ///
    /// Steps:
    /// - Through the store, as a caller that is neither, disable alice, then bob.
    ///
    /// Expected:
    /// - Done, then Refused; bob stays enabled.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Store_LastOwner_IsKeptEnabled(string provider)
    {
        await using TestService test = TestService.Open(provider);
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        ElmService service = await StartAsync(test, idp);
        _ = await ListAdminsAsync(test, service, idp.Token("alice"));
        JsonElement[] admins = await ListAdminsAsync(test, service, idp.Token("bob"));
        Guid aliceId = Guid.Parse(Admin(admins, "alice").GetProperty("id").GetString()!);
        Guid bobId = Guid.Parse(Admin(admins, "bob").GetProperty("id").GetString()!);
        Guid stranger = Guid.CreateVersion7();
        AuditEntry Entry(bool unchanged) => new("system", "test", "admin.disable", null, "ELM_OK", null, null);
        CancellationToken cancel = TestContext.Current.CancellationToken;

        Assert.Equal(
            AdminChange.Done,
            await service.Database.SetAdminEnabledAsync(aliceId, false, stranger, null, Entry, test.Time.GetUtcNow(), cancel)
        );
        Assert.Equal(
            AdminChange.Refused,
            await service.Database.SetAdminEnabledAsync(bobId, false, stranger, null, Entry, test.Time.GetUtcNow(), cancel)
        );
        Assert.True((await service.Database.FindAdminAsync(new AdminIdentity(idp.Issuer, "bob"), cancel))!.Enabled);
    }

    /// <summary>
    /// Two first requests of a new subject at once add one administrator and one admin.create row (design §14.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service that trusts the fake provider; an owner key from setup.
    ///
    /// Steps:
    /// - Send eight requests of erin at once; read the audit log.
    ///
    /// Expected:
    /// - All 200 with one erin in the list; exactly one admin.create row for her.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task FirstRequests_AtOnce_AddOneAdministrator(string provider)
    {
        await using TestService test = TestService.Open(provider);
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        ElmService service = await StartAsync(test, idp);
        string token = idp.Token("erin");

        HttpResponseMessage[] answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => GetAdminsAsync(test, service, token)));
        Assert.All(answers, a => Assert.Equal(HttpStatusCode.OK, a.StatusCode));
        Array.ForEach(answers, a => a.Dispose());

        Assert.Single(await ListAdminsAsync(test, service, token), a => a.GetProperty("subject").GetString() == "erin");
        Assert.Single(await TestService.AuditRowsAsync(test, service, owner), r => r.GetProperty("action").GetString() == "admin.create");
    }

    /// <summary>
    /// The 90-day rule (design §14.6, decision 0.66): with OIDC set, a key stops 90 days after the latest of its
    /// administrator's last login, the administrator's creation, and its own creation, and works again after a login; a key
    /// made later by setup works; with the rule at 0, or without OIDC, nothing stops.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service that trusts the fake provider; alice signed in and holding a 365-day key.
    ///
    /// Steps:
    /// - Use the key 89 days later and 90 days later; sign in as alice; use the key; 90 more days later run setup and use its
    ///   key; start a service with the rule at 0 and one without OIDC on the same database and use alice's key.
    ///
    /// Expected:
    /// - 200, 401, 200 after the login; the new setup key 200 while alice's key is 401; both other services 200.
    /// </remarks>
    [Fact]
    public async Task Keys_StopAfterNinetyDaysWithoutLogin()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        ElmService service = await StartAsync(test, idp);
        string key = await TestService.CreateKeyAsync(test.Http(service, idp.Token("alice")), "\"license.read\"", days: 365);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        async Task<HttpStatusCode> UseAsync(ElmService on, string k) => (await test.Http(on, k).GetAsync("v1/admin/licenses", cancel)).StatusCode;

        test.Time.Advance(TimeSpan.FromDays(89));
        Assert.Equal(HttpStatusCode.OK, await UseAsync(service, key));
        test.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(HttpStatusCode.Unauthorized, await UseAsync(service, key));
        _ = await ListAdminsAsync(test, service, idp.Token("alice"));
        Assert.Equal(HttpStatusCode.OK, await UseAsync(service, key));

        test.Time.Advance(TimeSpan.FromDays(90));
        string fresh = await test.SetupAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await UseAsync(service, key));
        Assert.Equal(HttpStatusCode.OK, await UseAsync(service, fresh));

        ElmService off = await StartAsync(test, idp, s => s with { InactiveAdminDays = 0 });
        Assert.Equal(HttpStatusCode.OK, await UseAsync(off, key));
        ElmService plain = await test.StartAsync();
        Assert.Equal(HttpStatusCode.OK, await UseAsync(plain, key));
    }

    /// <summary>
    /// GET /v1/admin/oidc returns the issuer, client ID, and scopes without authentication, with no-store and no audit row;
    /// without OIDC it is 404 ELM_NOT_FOUND (design §14.5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service that trusts the fake provider, one without OIDC, and an owner key from setup.
    ///
    /// Steps:
    /// - Get /v1/admin/oidc from both without a key; read the audit log.
    ///
    /// Expected:
    /// - 200 {"issuer", "clientId": "elm-cli", "scopes": ["openid", "offline_access"]} with Cache-Control no-store; 404;
    ///   no audit row of it.
    /// </remarks>
    [Fact]
    public async Task OidcInfo_IsServedWithoutAuthentication()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        string owner = await test.SetupAsync();
        ElmService service = await StartAsync(test, idp);
        ElmService plain = await test.StartAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;

        using HttpResponseMessage answer = await test.Http(service).GetAsync("v1/admin/oidc", cancel);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.True(answer.Headers.CacheControl?.NoStore);
        using JsonDocument info = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(cancel));
        Assert.Equal(idp.Issuer, info.RootElement.GetProperty("issuer").GetString());
        Assert.Equal(FakeIdp.CliClientId, info.RootElement.GetProperty("clientId").GetString());
        Assert.Equal(["openid", "offline_access"], info.RootElement.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));

        await TestService.AssertProblemAsync(
            await test.Http(plain).GetAsync("v1/admin/oidc", cancel),
            HttpStatusCode.NotFound,
            "ELM_NOT_FOUND",
            "The service does not accept OIDC sign-in."
        );
        Assert.DoesNotContain(
            await TestService.AuditRowsAsync(test, service, owner),
            r => r.GetProperty("action").GetString()!.StartsWith("admin.", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// Discovery (design §14.6): a document that names another issuer gives 401; keys read once stay in use while the
    /// provider is down; a token of a new key ID is refused once and starts the fetch, then passes; a provider down from the
    /// start gives 401.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Services that trust the fake provider.
    ///
    /// Steps:
    /// - With the discovery issuer changed, send a token; on another service, send a token, stop the provider, send another;
    ///   start the provider, rotate its key to k2, send a token signed with k2 twice; on a third service with the provider
    ///   down from the start, send a token.
    ///
    /// Expected:
    /// - 401; 200, 200; 401 then 200; 401.
    /// </remarks>
    [Fact]
    public async Task Discovery_IsCheckedAndCached()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);

        idp.DiscoveryIssuer = "https://other.example/realms/test";
        ElmService mismatched = await StartAsync(test, idp);
        await TestService.AssertProblemAsync(
            await GetAdminsAsync(test, mismatched, idp.Token()),
            HttpStatusCode.Unauthorized,
            "ELM_UNAUTHORIZED",
            TokenRefused
        );
        idp.DiscoveryIssuer = null;

        ElmService service = await StartAsync(test, idp);
        Assert.Equal(HttpStatusCode.OK, (await GetAdminsAsync(test, service, idp.Token())).StatusCode);
        idp.Available = false;
        Assert.Equal(HttpStatusCode.OK, (await GetAdminsAsync(test, service, idp.Token())).StatusCode);
        idp.Available = true;
        idp.RotateKey("k2");
        string rotated = idp.Token();
        HttpStatusCode first = (await GetAdminsAsync(test, service, rotated)).StatusCode;
        HttpStatusCode second = (await GetAdminsAsync(test, service, rotated)).StatusCode;
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.OK), (first, second));

        idp.Available = false;
        ElmService cold = await StartAsync(test, idp);
        await TestService.AssertProblemAsync(
            await GetAdminsAsync(test, cold, idp.Token()),
            HttpStatusCode.Unauthorized,
            "ELM_UNAUTHORIZED",
            TokenRefused
        );
    }

    /// <summary>
    /// Accepting OIDC tokens writes no Data Protection key (design §14.6, Q8 of the M5a Grill): JwtBearer protects nothing.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service that trusts the fake provider.
    ///
    /// Steps:
    /// - Count the keys of the host's Data Protection key manager; send three tokens; count again.
    ///
    /// Expected:
    /// - The same count before and after.
    /// </remarks>
    [Fact]
    public async Task Tokens_WriteNoDataProtectionKey()
    {
        await using TestService test = new();
        await using FakeIdp idp = await FakeIdp.StartAsync(test.Time);
        ElmService service = await StartAsync(test, idp);
        IKeyManager keys = service.Services.GetRequiredService<IKeyManager>();
        int before = keys.GetAllKeys().Count;

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await GetAdminsAsync(test, service, idp.Token())).StatusCode);
        }

        Assert.Equal(before, keys.GetAllKeys().Count);
    }

    private static Task<ElmService> StartAsync(TestService test, FakeIdp idp, Func<ServiceSettings, ServiceSettings>? change = null) =>
        test.StartAsync(s =>
        {
            ServiceSettings withOidc = s with { Oidc = idp.Settings() };
            return change?.Invoke(withOidc) ?? withOidc;
        });

    private static Task<HttpResponseMessage> GetAdminsAsync(TestService test, ElmService service, string token) =>
        test.Http(service, token).GetAsync("v1/admin/admins", TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> PostAsync(TestService test, ElmService service, string token, string path) =>
        test.Http(service, token).PostAsync(path, null, TestContext.Current.CancellationToken);

    private static async Task<JsonElement[]> ListAdminsAsync(TestService test, ElmService service, string token)
    {
        using HttpResponseMessage answer = await GetAdminsAsync(test, service, token);
        string text = await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(answer.StatusCode == HttpStatusCode.OK, text);
        using JsonDocument page = JsonDocument.Parse(text);
        return [.. page.RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone())];
    }

    private static JsonElement Admin(JsonElement[] admins, string subject) => admins.Single(a => a.GetProperty("subject").GetString() == subject);
}
