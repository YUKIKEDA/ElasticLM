using System.Net;
using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The administration API of M4a3 (design §9.4, §14.5, decisions 0.85 and 0.86): licenses, their keys, the state operations,
/// and the unbound license file, on SQLite and on PostgreSQL.
/// </summary>
public sealed class ElmServiceLicensesTests
{
    private const string Licenses = "v1/admin/licenses";

    /// <summary>
    /// A license is created with its key shown once, read and listed without it, and changed in the members sent; the key is
    /// in no audit row.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Products A (features core, pro) and B (core), a standard policy of each, A's for 365 days; an owner key; each engine.
    ///
    /// Steps:
    /// - Create a license of A's policy with core and a version range, meta, and a name; read it; list by product A, by
    ///   policy B, by productId "x"; create one of B's policy and list by policy B; patch the first with a name,
    ///   maxActivations 3, empty meta, and features pro; patch expiresAt, policyId, productId; read the audit log.
    ///
    /// Expected:
    /// - 201 with a key of the §9.5 form, expiry 365 days after the start, state active, maxActivations null, the feature
    ///   with null seats and expiry; the same without "key" when read; lists of 1, 0, 400, 1; 200 with the changes; 400
    ///   "expiresAt is changed by extend." and "… cannot be changed" twice; the create row without the key and the update row with the changed members.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Licenses_CreateReadListPatch_AnswerAsDesigned(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        (string a, string policyA) = await ModelAsync(admin, "a", "\"duration\":31536000", "core", "pro");
        (string _, string policyB) = await ModelAsync(admin, "b", string.Empty, "core");

        JsonElement created = await TestService.CreatedAsync(
            admin,
            Licenses,
            $$$"""{"policyId":"{{{policyA}}}","name":"Acme","features":[{"code":"core","ver":">=1.0 <2.0"}],"meta":{"customer":"acme"}}"""
        );
        string id = created.GetProperty("id").GetString()!;
        string key = created.GetProperty("key").GetString()!;
        Assert.Matches("^ELM(-[0-9A-HJKMNP-TV-Z]{5}){4}$", key);
        Assert.Equal(
            (a, policyA, "Acme", "active", "2027-10-01T00:00:00Z", JsonValueKind.Null),
            (
                created.GetProperty("productId").GetString(),
                created.GetProperty("policyId").GetString(),
                created.GetProperty("name").GetString(),
                created.GetProperty("state").GetString(),
                created.GetProperty("expiresAt").GetString(),
                created.GetProperty("maxActivations").ValueKind
            )
        );
        JsonElement feature = created.GetProperty("features")[0];
        Assert.Equal(
            ("core", ">=1.0 <2.0", JsonValueKind.Null, JsonValueKind.Null),
            (
                feature.GetProperty("code").GetString(),
                feature.GetProperty("ver").GetString(),
                feature.GetProperty("seats").ValueKind,
                feature.GetProperty("expiresAt").ValueKind
            )
        );
        Assert.Equal("""{"customer":"acme"}""", created.GetProperty("meta").GetRawText());

        JsonElement read = await TestService.GetAsync(admin, $"{Licenses}/{id}");
        Assert.False(read.TryGetProperty("key", out _));
        Assert.Equal(created.GetProperty("features").GetRawText(), read.GetProperty("features").GetRawText());
        Assert.Equal(1, (await TestService.GetAsync(admin, $"{Licenses}?productId={a}")).GetProperty("items").GetArrayLength());
        Assert.Equal(0, (await TestService.GetAsync(admin, $"{Licenses}?policyId={policyB}")).GetProperty("items").GetArrayLength());
        await TestService.AssertProblemAsync(
            await admin.GetAsync($"{Licenses}?productId=x", cancel),
            HttpStatusCode.BadRequest,
            "ELM_INVALID_ARGUMENT",
            "productId must be a product ID."
        );
        await TestService.CreatedAsync(admin, Licenses, $$"""{"policyId":"{{policyB}}","name":"B","features":[{"code":"core"}]}""");
        Assert.Equal(1, (await TestService.GetAsync(admin, $"{Licenses}?policyId={policyB}")).GetProperty("items").GetArrayLength());

        JsonElement patched = await TestService.PatchAsync(
            admin,
            $"{Licenses}/{id}",
            """{"name":"Acme 2","maxActivations":3,"meta":{},"features":[{"code":"pro"}]}"""
        );
        Assert.Equal(
            ("Acme 2", 3, "{}", "pro"),
            (
                patched.GetProperty("name").GetString(),
                patched.GetProperty("maxActivations").GetInt32(),
                patched.GetProperty("meta").GetRawText(),
                patched.GetProperty("features")[0].GetProperty("code").GetString()
            )
        );
        foreach (
            (string member, string detail) in new[]
            {
                ("expiresAt", "expiresAt is changed by extend."),
                ("policyId", "policyId cannot be changed."),
                ("productId", "productId cannot be changed."),
            }
        )
        {
            await TestService.AssertProblemAsync(
                await admin.PatchAsync($"{Licenses}/{id}", TestService.Json($"{{\"{member}\":\"x\"}}"), cancel),
                HttpStatusCode.BadRequest,
                "ELM_INVALID_ARGUMENT",
                detail
            );
        }

        JsonElement[] rows = await TestService.AuditRowsAsync(test, service, owner);
        Assert.DoesNotContain(
            rows,
            r =>
                r.GetRawText().Contains(key, StringComparison.Ordinal)
                || r.GetRawText().Contains(key.Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal)
        );
        string Detail(string action) =>
            rows.First(r =>
                    r.GetProperty("action").GetString() == action
                    && r.GetProperty("target").GetString() == id
                    && r.GetProperty("result").GetString() == "ELM_OK"
                )
                .GetProperty("detail")
                .GetString()!;
        Assert.StartsWith($"{{\"policyId\":\"{policyA}\",\"name\":\"Acme\",\"features\":[", Detail("license.create"), StringComparison.Ordinal);
        Assert.Equal(
            "{\"name\":\"Acme 2\",\"maxActivations\":3,\"features\":[{\"code\":\"pro\",\"ver\":null,\"seats\":null,\"expiresAt\":null}],\"meta\":{}}",
            Detail("license.update")
        );
    }

    /// <summary>
    /// The expiry follows the policy and the body (design §14.5): duration from creation, expiresAt to override it, none for
    /// a perpetual policy, and none before the first activation; name and maxActivations are checked at their limits.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A product with feature core; policies for 30 days, perpetual, and fromFirstActivation.
    ///
    /// Steps:
    /// - Create licenses: of each policy; with expiresAt 10 days on; with expiresAt now; with expiresAt on the
    ///   fromFirstActivation policy; with an unknown policyId; names of 256 four-byte characters and 257 characters;
    ///   maxActivations 1, 100000, 0, 100001.
    ///
    /// Expected:
    /// - Expiries 30 days on, null, null; 10 days on; 400 and 400 with their messages; 400 naming policyId; 201 and 400;
    ///   201, 201, 400, 400.
    /// </remarks>
    [Fact]
    public async Task Create_ExpiryAndLimits_FollowThePolicy()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        (string product, string thirty) = await ModelAsync(admin, "a", "\"duration\":2592000", "core");
        string perpetual = await TestService.PolicyAsync(admin, product, string.Empty);
        string firstActivation = await TestService.PolicyAsync(admin, product, "\"expirationBasis\":\"fromFirstActivation\"");
        string Body(string policy, string more = "") => $$"""{"policyId":"{{policy}}","name":"n","features":[{"code":"core"}]{{more}}}""";
        async Task<JsonElement> Create(string body) => await TestService.CreatedAsync(admin, Licenses, body);
        async Task Refused(string body, string detail) =>
            await TestService.AssertProblemAsync(
                await admin.PostAsync(Licenses, TestService.Json(body), cancel),
                HttpStatusCode.BadRequest,
                "ELM_INVALID_ARGUMENT",
                detail
            );

        Assert.Equal("2026-10-31T00:00:00Z", (await Create(Body(thirty))).GetProperty("expiresAt").GetString());
        Assert.Equal(JsonValueKind.Null, (await Create(Body(perpetual))).GetProperty("expiresAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await Create(Body(firstActivation))).GetProperty("expiresAt").ValueKind);
        Assert.Equal(
            "2026-10-11T00:00:00Z",
            (await Create(Body(thirty, ",\"expiresAt\":\"2026-10-11T00:00:00Z\""))).GetProperty("expiresAt").GetString()
        );
        await Refused(Body(thirty, ",\"expiresAt\":\"2026-10-01T00:00:00Z\""), "expiresAt must be later than now.");
        await Refused(
            Body(firstActivation, ",\"expiresAt\":\"2026-10-11T00:00:00Z\""),
            "expiresAt cannot be set for a policy whose expiry starts at the first activation."
        );
        await Refused(Body(Guid.CreateVersion7().ToString("D")), "policyId must name a policy.");

        string longest = string.Concat(Enumerable.Repeat("\U0001F600", 256));
        Assert.Equal(
            longest,
            (await Create($$"""{"policyId":"{{perpetual}}","name":"{{longest}}","features":[{"code":"core"}]}""")).GetProperty("name").GetString()
        );
        await Refused(
            $$"""{"policyId":"{{perpetual}}","name":"{{new string('n', 257)}}","features":[{"code":"core"}]}""",
            "name must be 1 to 256 characters."
        );
        foreach (int ok in new[] { 1, 100_000 })
        {
            Assert.Equal(ok, (await Create(Body(perpetual, $",\"maxActivations\":{ok}"))).GetProperty("maxActivations").GetInt32());
        }

        foreach (int bad in new[] { 0, 100_001 })
        {
            await Refused(Body(perpetual, $",\"maxActivations\":{bad}"), "maxActivations must be between 1 and 100000, or null.");
        }
    }

    /// <summary>
    /// Every transition of the §9.4 diagram is taken, overlapping marks resolve in their order, the transitions it does not
    /// allow are 409 ELM_CONFLICT, and an operation that changes nothing is 200 with an "unchanged" audit row.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license L that expires 10 days after the start, a perpetual license P, and licenses R1 and R2 for 10 days; each
    ///   engine.
    ///
    /// Steps:
    /// - L: suspend, suspend a second later, resume, resume; move the clock 11 days and make R3 (ends on day 21); read L; suspend, resume;
    ///   extend L to day 10.5 (after its expiry, before now) and to now, R3 to day 12 (after now, before its expiry); extend L
    ///   to day 20; suspend, extend; revoke, revoke a second later, resume, suspend, extend. P: extend. R1: extend with no expiresAt, extend,
    ///   revoke. R2: revoke while expired. Read the audit log.
    ///
    /// Expected:
    /// - suspended, suspended (unchanged, still suspended at the first time), active, active (unchanged); expired;
    ///   suspended, expired; 400 three times (later than both is required), then active; suspended, 409; revoked, revoked
    ///   (unchanged, at the first time), 409, 409, 409; 409 for P; 400 ELM_MALFORMED, active, revoked for R1; revoked for
    ///   R2; three "unchanged" rows.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task States_EveryTransition_FollowsTheDesign(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        (string product, string tenDays) = await ModelAsync(admin, "a", "\"duration\":864000", "core");
        string perpetualPolicy = await TestService.PolicyAsync(admin, product, string.Empty);
        async Task<string> NewLicense(string policy) =>
            (await TestService.CreatedAsync(admin, Licenses, $$"""{"policyId":"{{policy}}","name":"n","features":[{"code":"core"}]}"""))
                .GetProperty("id")
                .GetString()!;
        string l = await NewLicense(tenDays);
        string p = await NewLicense(perpetualPolicy);
        string r1 = await NewLicense(tenDays);
        string r2 = await NewLicense(tenDays);

        // suspend, resume, and revoke are sent without a body, as design §14.5 has them; extend with its body.
        async Task<string> Op(string license, string op, string? body = null)
        {
            using HttpResponseMessage response = await admin.PostAsync(
                $"{Licenses}/{license}/{op}",
                body is null ? null : TestService.Json(body),
                cancel
            );
            string text = await response.Content.ReadAsStringAsync(cancel);
            Assert.True(response.StatusCode == HttpStatusCode.OK, text);
            return JsonDocument.Parse(text).RootElement.GetProperty("state").GetString()!;
        }

        async Task Conflict(string license, string op, string detail, string? body = null) =>
            await TestService.AssertProblemAsync(
                await admin.PostAsync($"{Licenses}/{license}/{op}", body is null ? null : TestService.Json(body), cancel),
                HttpStatusCode.Conflict,
                "ELM_CONFLICT",
                detail
            );

        Assert.Equal("suspended", await Op(l, "suspend"));
        test.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("suspended", await Op(l, "suspend"));
        Assert.Equal("2026-10-01T00:00:00Z", (await TestService.GetAsync(admin, $"{Licenses}/{l}")).GetProperty("suspendedAt").GetString());
        Assert.Equal("active", await Op(l, "resume"));
        Assert.Equal("active", await Op(l, "resume"));

        // Eleven days on, L's expiry (day 10) is before now (day 11); R3, made now, ends on day 21.
        test.Time.Advance(TimeSpan.FromDays(11));
        string r3 = await NewLicense(tenDays);
        Assert.Equal("expired", (await TestService.GetAsync(admin, $"{Licenses}/{l}")).GetProperty("state").GetString());
        Assert.Equal("suspended", await Op(l, "suspend"));
        Assert.Equal("expired", await Op(l, "resume"));
        const string Later = "expiresAt must be later than the license's expiry and now.";
        async Task TooEarly(string license, string to) =>
            await TestService.AssertProblemAsync(
                await admin.PostAsync($"{Licenses}/{license}/extend", TestService.Json($"{{\"expiresAt\":\"{to}\"}}"), cancel),
                HttpStatusCode.BadRequest,
                "ELM_INVALID_ARGUMENT",
                Later
            );
        await TooEarly(l, "2026-10-11T12:00:00Z");
        await TooEarly(l, "2026-10-12T00:00:00Z");
        await TooEarly(r3, "2026-10-13T00:00:00Z");
        Assert.Equal("active", await Op(l, "extend", """{"expiresAt":"2026-10-21T00:00:00Z"}"""));
        Assert.Equal("suspended", await Op(l, "suspend"));
        await Conflict(l, "extend", "A suspended license cannot be extended.", """{"expiresAt":"2026-10-31T00:00:00Z"}""");
        Assert.Equal("revoked", await Op(l, "revoke"));
        string revokedAt = (await TestService.GetAsync(admin, $"{Licenses}/{l}")).GetProperty("revokedAt").GetString()!;
        test.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("revoked", await Op(l, "revoke"));
        Assert.Equal(revokedAt, (await TestService.GetAsync(admin, $"{Licenses}/{l}")).GetProperty("revokedAt").GetString());
        await Conflict(l, "resume", "A revoked license cannot be resumed.");
        await Conflict(l, "suspend", "A revoked license cannot be suspended.");
        await Conflict(l, "extend", "A revoked license cannot be extended.", """{"expiresAt":"2026-10-31T00:00:00Z"}""");
        await Conflict(p, "extend", "The license has no expiry to extend.", """{"expiresAt":"2026-10-31T00:00:00Z"}""");
        await TestService.AssertProblemAsync(
            await admin.PostAsync($"{Licenses}/{r1}/extend", TestService.Json("{}"), cancel),
            HttpStatusCode.BadRequest,
            "ELM_MALFORMED",
            ElasticLM.Service.Licenses.Malformed
        );
        Assert.Equal("expired", (await TestService.GetAsync(admin, $"{Licenses}/{r2}")).GetProperty("state").GetString());
        Assert.Equal("revoked", await Op(r2, "revoke"));
        Assert.Equal("active", await Op(r1, "extend", """{"expiresAt":"2026-10-31T00:00:00Z"}"""));
        Assert.Equal("revoked", await Op(r1, "revoke"));

        int unchanged = (await TestService.AuditRowsAsync(test, service, owner)).Count(r =>
            r.GetProperty("detail").GetString() == "{\"unchanged\":true}"
        );
        Assert.Equal(3, unchanged);
    }

    /// <summary>
    /// The state filter of the list gives the licenses whose computed state it names, with the clock deciding expiry.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Four licenses for one day: one left active (perpetual), one suspended, one left to expire, one suspended and then
    ///   revoked; each engine.
    ///
    /// Steps:
    /// - Move the clock one day; list with each state; list with state "other".
    ///
    /// Expected:
    /// - One license each, the right one; 400 naming the states.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task List_StateFilter_MatchesTheComputedState(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        (string product, string oneDay) = await ModelAsync(admin, "a", "\"duration\":86400", "core");
        string perpetual = await TestService.PolicyAsync(admin, product, string.Empty);
        async Task<string> NewLicense(string policy) =>
            (await TestService.CreatedAsync(admin, Licenses, $$"""{"policyId":"{{policy}}","name":"n","features":[{"code":"core"}]}"""))
                .GetProperty("id")
                .GetString()!;
        var expected = new Dictionary<string, string>
        {
            ["active"] = await NewLicense(perpetual),
            ["suspended"] = await NewLicense(oneDay),
            ["expired"] = await NewLicense(oneDay),
            ["revoked"] = await NewLicense(oneDay),
        };
        await TestService.PostOkAsync(admin, $"{Licenses}/{expected["suspended"]}/suspend", "{}");
        await TestService.PostOkAsync(admin, $"{Licenses}/{expected["revoked"]}/suspend", "{}");
        await TestService.PostOkAsync(admin, $"{Licenses}/{expected["revoked"]}/revoke", "{}");
        test.Time.Advance(TimeSpan.FromDays(1));

        foreach ((string state, string id) in expected)
        {
            JsonElement items = (await TestService.GetAsync(admin, $"{Licenses}?state={state}")).GetProperty("items");
            Assert.Equal([id], items.EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        }

        await TestService.AssertProblemAsync(
            await admin.GetAsync($"{Licenses}?state=other", cancel),
            HttpStatusCode.BadRequest,
            "ELM_INVALID_ARGUMENT",
            "state must be one of active, suspended, expired, revoked."
        );
    }

    /// <summary>
    /// An active license of an unbound policy has a license file that the app's verifier accepts, ending when the license
    /// does with the policy's grace, or without expiry when the license has none; other licenses have none (design §8.5,
    /// decisions 0.76 and 0.86).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Product "app" with feature core; an unbound policy for 30 days with 7 days of grace; an unbound perpetual policy; a
    ///   node-locked policy.
    ///
    /// Steps:
    /// - Get the file of a license of each, the first with core, a version range, a feature expiry, and meta; verify the
    ///   first two with the vendor's root key for product app, feature core, version 1.5; suspend the first and get its file;
    ///   move the clock 31 days and get the second's after creating a third for 30 days; read the audit log.
    ///
    /// Expected:
    /// - 200 with an attachment named after the license; valid, with the license ID, expiry 30 days on, grace 7 days, the
    ///   feature; valid without expiry; 409 for the node-locked license, the suspended one, and the expired one; a
    ///   license.file row with the token's jti and exp.
    /// </remarks>
    [Fact]
    public async Task File_UnboundLicense_VerifiesAndMatches()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        (string product, string unbound) = await ModelAsync(admin, "app", "\"binding\":\"unbound\",\"duration\":2592000", "core");
        string forever = await TestService.PolicyAsync(admin, product, "\"binding\":\"unbound\"");
        string locked = await TestService.PolicyAsync(admin, product, string.Empty);
        async Task<string> NewLicense(string policy, string features = """[{"code":"core"}]""", string more = "") =>
            (await TestService.CreatedAsync(admin, Licenses, $$"""{"policyId":"{{policy}}","name":"n","features":{{features}}{{more}}}"""))
                .GetProperty("id")
                .GetString()!;
        string first = await NewLicense(
            unbound,
            """[{"code":"core","ver":">=1.0 <2.0","expiresAt":"2026-10-21T00:00:00Z"}]""",
            ",\"meta\":{\"customer\":\"acme\"}"
        );
        string second = await NewLicense(forever);
        string third = await NewLicense(locked);
        TrustStore trust = TrustStore.Create([File.ReadAllText(test.FilePath("root.pub.pem"))], []);
        var verifier = new LicenseVerifier(trust, test.Time, StatelessClockGuard.Instance);
        var requirement = new LicenseRequirement("app", LicenseKind.Standard, "core", "1.5", null);

        using (HttpResponseMessage response = await admin.PostAsync($"{Licenses}/{first}/file", null, cancel))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal($"{first}.elmlic", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
            var valid = Assert.IsType<LicenseVerificationResult.Valid>(
                verifier.Verify(await response.Content.ReadAsByteArrayAsync(cancel), requirement)
            );
            Assert.Equal(
                (first, (DateTimeOffset?)TestService.Start.AddDays(30), TimeSpan.FromDays(7), "core"),
                (valid.License.LicenseId, valid.License.ExpiresAt, valid.License.GracePeriod, valid.License.Features[0].Id)
            );
        }

        using (HttpResponseMessage response = await admin.PostAsync($"{Licenses}/{second}/file", null, cancel))
        {
            var valid = Assert.IsType<LicenseVerificationResult.Valid>(
                verifier.Verify(await response.Content.ReadAsByteArrayAsync(cancel), requirement)
            );
            Assert.Null(valid.License.ExpiresAt);
        }

        async Task NoFile(string license, string detail) =>
            await TestService.AssertProblemAsync(
                await admin.PostAsync($"{Licenses}/{license}/file", null, cancel),
                HttpStatusCode.Conflict,
                "ELM_CONFLICT",
                detail
            );
        await NoFile(third, "Only a license of an unbound policy has a license file.");
        await TestService.PostOkAsync(admin, $"{Licenses}/{first}/suspend", "{}");
        await NoFile(first, "A suspended license has no license file.");
        string fourth = await NewLicense(unbound);
        test.Time.Advance(TimeSpan.FromDays(31));
        await NoFile(fourth, "An expired license has no license file.");

        JsonElement row = (
            await TestService.AuditRowsAsync(test, service, test.Http(service, owner).DefaultRequestHeaders.Authorization!.Parameter!)
        ).First(r => r.GetProperty("action").GetString() == "license.file" && r.GetProperty("target").GetString() == first);
        using JsonDocument detail = JsonDocument.Parse(row.GetProperty("detail").GetString()!);
        Assert.Equal("2026-10-31T00:00:00Z", detail.RootElement.GetProperty("exp").GetString());
        Assert.NotEmpty(detail.RootElement.GetProperty("jti").GetString()!);
    }

    /// <summary>
    /// A change and a state operation sent at once both stay: each reads the license in its write transaction.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A perpetual license, on each engine.
    ///
    /// Steps:
    /// - 20 times: send at once a new name and a suspend or a resume in turn; read the license.
    ///
    /// Expected:
    /// - Each time the new name and the state the operation set.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Patch_AndStateAtOnce_BothStay(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        (string _, string policy) = await ModelAsync(admin, "a", string.Empty, "core");
        string id = (await TestService.CreatedAsync(admin, Licenses, $$"""{"policyId":"{{policy}}","name":"n","features":[{"code":"core"}]}"""))
            .GetProperty("id")
            .GetString()!;

        for (int i = 0; i < 20; i++)
        {
            string op = i % 2 == 0 ? "suspend" : "resume";
            await Task.WhenAll(
                TestService.PatchAsync(admin, $"{Licenses}/{id}", $"{{\"name\":\"n{i}\"}}"),
                TestService.PostOkAsync(admin, $"{Licenses}/{id}/{op}", "{}")
            );
            JsonElement license = await TestService.GetAsync(admin, $"{Licenses}/{id}");
            Assert.Equal(
                ($"n{i}", i % 2 == 0 ? "suspended" : "active"),
                (license.GetProperty("name").GetString(), license.GetProperty("state").GetString())
            );
        }
    }

    /// <summary>
    /// The license permissions are enforced, and an unknown or malformed license ID is 404 ELM_LICENSE_NOT_FOUND.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license, and keys with license.read only and with license.update only.
    ///
    /// Steps:
    /// - With license.read: create, read. With license.update: suspend, revoke, file. Read an unknown ID and "x"; suspend an
    ///   unknown ID.
    ///
    /// Expected:
    /// - 403 naming license.create; 200; 200; 403 naming license.revoke and license.file; 404 three times.
    /// </remarks>
    [Fact]
    public async Task Permissions_AndUnknownIds_AreRefused()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        (string _, string policy) = await ModelAsync(admin, "a", string.Empty, "core");
        string body = $$"""{"policyId":"{{policy}}","name":"n","features":[{"code":"core"}]}""";
        string id = (await TestService.CreatedAsync(admin, Licenses, body)).GetProperty("id").GetString()!;
        HttpClient reader = test.Http(service, await TestService.CreateKeyAsync(admin, "\"license.read\""));
        HttpClient updater = test.Http(service, await TestService.CreateKeyAsync(admin, "\"license.update\""));
        async Task Forbidden(Task<HttpResponseMessage> call, string permission) =>
            await TestService.AssertProblemAsync(
                await call,
                HttpStatusCode.Forbidden,
                "ELM_FORBIDDEN",
                $"The API key does not have the permission {permission}."
            );

        await Forbidden(reader.PostAsync(Licenses, TestService.Json(body), cancel), "license.create");
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Licenses}/{id}", cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await updater.PostAsync($"{Licenses}/{id}/suspend", null, cancel)).StatusCode);
        await Forbidden(updater.PostAsync($"{Licenses}/{id}/revoke", TestService.Json("{}"), cancel), "license.revoke");
        await Forbidden(updater.PostAsync($"{Licenses}/{id}/file", null, cancel), "license.file");

        foreach (
            Task<HttpResponseMessage> call in new[]
            {
                admin.GetAsync($"{Licenses}/{Guid.CreateVersion7():D}", cancel),
                admin.GetAsync($"{Licenses}/x", cancel),
                admin.PostAsync($"{Licenses}/{Guid.CreateVersion7():D}/suspend", TestService.Json("{}"), cancel),
            }
        )
        {
            await TestService.AssertProblemAsync(await call, HttpStatusCode.NotFound, "ELM_LICENSE_NOT_FOUND", "No license has this ID.");
        }
    }

    // A product with features and one policy of it (members as JSON, or empty); returns their IDs.
    private static async Task<(string Product, string Policy)> ModelAsync(
        HttpClient admin,
        string code,
        string policyMembers,
        params string[] features
    )
    {
        string product = (await TestService.CreatedAsync(admin, "v1/admin/products", $$"""{"code":"{{code}}","name":"{{code}}"}"""))
            .GetProperty("id")
            .GetString()!;
        foreach (string feature in features)
        {
            await TestService.CreatedAsync(admin, $"v1/admin/products/{product}/features", $$"""{"code":"{{feature}}","name":"{{feature}}"}""");
        }

        return (product, await TestService.PolicyAsync(admin, product, policyMembers));
    }
}
