using System.Net;
using System.Text.Json;
using ElasticLM.Issuing;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The administration API of M4a2 (design §14.5, decisions 0.82 to 0.84 and 0.87): products, features, policies, and the
/// signing key's status, on SQLite and on PostgreSQL.
/// </summary>
public sealed class ElmServiceModelTests
{
    private const string NameRule = "name must be 1 to 64 characters.";
    private const string CodeRule = "code must be 1 to 64 lowercase letters, digits, or '-'.";

    /// <summary>
    /// A product and its features are created, read, listed, and renamed, and every change is in the audit log with the
    /// members it sent or changed.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service and an owner key, on each engine.
    ///
    /// Steps:
    /// - Create product "app"; read it; list products; rename it; create features "core" and "pro"; list them with limit 1
    ///   and after the next; rename "pro"; read the audit log.
    ///
    /// Expected:
    /// - 201 with the code, name, null trialPolicyId; the same item read back and listed; 200 with the new name; two 201
    ///   features; one per page, in ID order, the second without a next; 200 renamed; the rows product.create {"code","name"}, product.update
    ///   {"name"}, feature.create, feature.update {"name"} with the right targets.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Products_CreateReadListRename_AnswerAsDesigned(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);

        JsonElement product = await CreatedAsync(admin, "v1/admin/products", """{"code":"app","name":"App"}""");
        string id = product.GetProperty("id").GetString()!;
        Assert.Equal(
            ("app", "App", JsonValueKind.Null),
            (product.GetProperty("code").GetString(), product.GetProperty("name").GetString(), product.GetProperty("trialPolicyId").ValueKind)
        );
        Assert.Equal(product.ToString(), (await GetAsync(admin, $"v1/admin/products/{id}")).ToString());
        Assert.Equal(id, (await GetAsync(admin, "v1/admin/products")).GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal("App 2", (await PatchAsync(admin, $"v1/admin/products/{id}", """{"name":"App 2"}""")).GetProperty("name").GetString());

        await CreatedAsync(admin, $"v1/admin/products/{id}/features", """{"code":"core","name":"Core"}""");
        JsonElement pro = await CreatedAsync(admin, $"v1/admin/products/{id}/features", """{"code":"pro","name":"Pro"}""");

        // Both are made in the same millisecond of the fake clock, so their ID order is not their creation order (UUID
        // version 7 orders to the millisecond); the two pages hold one each, in ID order, and the second has no next.
        JsonElement first = await GetAsync(admin, $"v1/admin/products/{id}/features?limit=1");
        JsonElement second = await GetAsync(admin, $"v1/admin/products/{id}/features?limit=1&after={first.GetProperty("next").GetString()}");
        string[] ids = [first.GetProperty("items")[0].GetProperty("id").GetString()!, second.GetProperty("items")[0].GetProperty("id").GetString()!];
        Assert.Equal(["core", "pro"], new[] { first, second }.Select(p => p.GetProperty("items")[0].GetProperty("code").GetString()).Order());
        Assert.True(string.CompareOrdinal(ids[0], ids[1]) < 0);
        Assert.False(second.TryGetProperty("next", out _));
        string proId = pro.GetProperty("id").GetString()!;
        Assert.Equal(
            "Pro 2",
            (await PatchAsync(admin, $"v1/admin/products/{id}/features/{proId}", """{"name":"Pro 2"}""")).GetProperty("name").GetString()
        );

        JsonElement[] rows = await TestService.AuditRowsAsync(test, service, owner);
        string Detail(string action, string target) =>
            rows.Single(r =>
                    r.GetProperty("action").GetString() == action
                    && r.GetProperty("target").GetString() == target
                    && r.GetProperty("result").GetString() == "ELM_OK"
                )
                .GetProperty("detail")
                .GetString()!;
        Assert.Equal("{\"code\":\"app\",\"name\":\"App\"}", Detail("product.create", id));
        Assert.Equal("{\"name\":\"App 2\"}", Detail("product.update", id));
        Assert.Equal($"{{\"productId\":\"{id}\",\"code\":\"pro\",\"name\":\"Pro\"}}", Detail("feature.create", proId));
        Assert.Equal("{\"name\":\"Pro 2\"}", Detail("feature.update", proId));
    }

    /// <summary>
    /// A product's code and name are checked at each limit, a used code is 409 ELM_CONFLICT, and two creates of one code at
    /// once give one product; a feature's code is unique within its product only.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service and an owner key, on each engine.
    ///
    /// Steps:
    /// - Create products with a code of 64 characters, 65, an upper-case letter, '_', a line break at the end, empty; names
    ///   of 64 four-byte characters, 65 characters, empty; create "dup" twice at once; create feature "f" in two products and
    ///   twice in one.
    ///
    /// Expected:
    /// - 201, then 400 with the code rule four times; 201, 400 and 400 with the name rule; one 201 and one 409 naming the code;
    ///   201, 201, 409.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Products_CodesAndNames_AreCheckedAndUnique(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        Task<HttpResponseMessage> Create(string code, string name = "n") =>
            admin.PostAsync("v1/admin/products", TestService.Json(JsonSerializer.Serialize(new { code, name })), cancel);

        Assert.Equal(HttpStatusCode.Created, (await Create(new string('a', 64))).StatusCode);
        foreach (string code in new[] { new string('b', 65), "Abc", "a_b", "abc\n", string.Empty })
        {
            await TestService.AssertProblemAsync(await Create(code), HttpStatusCode.BadRequest, "ELM_INVALID_ARGUMENT", CodeRule);
        }

        Assert.Equal(HttpStatusCode.Created, (await Create("emoji", string.Concat(Enumerable.Repeat("\U0001F600", 64)))).StatusCode);
        await TestService.AssertProblemAsync(await Create("long", new string('n', 65)), HttpStatusCode.BadRequest, "ELM_INVALID_ARGUMENT", NameRule);
        await TestService.AssertProblemAsync(await Create("empty", string.Empty), HttpStatusCode.BadRequest, "ELM_INVALID_ARGUMENT", NameRule);

        HttpResponseMessage[] both = await Task.WhenAll(Create("dup"), Create("dup"));
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], both.Select(r => r.StatusCode).Order());
        await TestService.AssertProblemAsync(
            both.Single(r => r.StatusCode == HttpStatusCode.Conflict),
            HttpStatusCode.Conflict,
            "ELM_CONFLICT",
            "The product code 'dup' is in use."
        );

        string p1 = (await CreatedAsync(admin, "v1/admin/products", """{"code":"p1","name":"P1"}""")).GetProperty("id").GetString()!;
        string p2 = (await CreatedAsync(admin, "v1/admin/products", """{"code":"p2","name":"P2"}""")).GetProperty("id").GetString()!;
        await CreatedAsync(admin, $"v1/admin/products/{p1}/features", """{"code":"f","name":"F"}""");
        await CreatedAsync(admin, $"v1/admin/products/{p2}/features", """{"code":"f","name":"F"}""");
        await TestService.AssertProblemAsync(
            await admin.PostAsync($"v1/admin/products/{p1}/features", TestService.Json("""{"code":"f","name":"F"}"""), cancel),
            HttpStatusCode.Conflict,
            "ELM_CONFLICT",
            "The feature code 'f' is in use in this product."
        );
    }

    /// <summary>
    /// A product's trial policy must be a trial policy of that product, and null removes it (design §9.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Products A and B; a trial policy and a standard policy of A; a trial policy of B.
    ///
    /// Steps:
    /// - Set A's trialPolicyId to A's trial policy; to A's standard policy; to B's trial policy; to a text that is not an ID;
    ///   to null.
    ///
    /// Expected:
    /// - 200 with it; 400 three times with the rule; 200 with null; the update rows record each change.
    /// </remarks>
    [Fact]
    public async Task ProductTrialPolicy_OnlyATrialOfTheProduct()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string a = (await CreatedAsync(admin, "v1/admin/products", """{"code":"a","name":"A"}""")).GetProperty("id").GetString()!;
        string b = (await CreatedAsync(admin, "v1/admin/products", """{"code":"b","name":"B"}""")).GetProperty("id").GetString()!;
        string trialA = await PolicyAsync(admin, a, "\"licenseKind\":\"trial\",\"gracePeriod\":0");
        string standardA = await PolicyAsync(admin, a, string.Empty);
        string trialB = await PolicyAsync(admin, b, "\"licenseKind\":\"trial\",\"gracePeriod\":0");

        Assert.Equal(
            trialA,
            (await PatchAsync(admin, $"v1/admin/products/{a}", $"{{\"trialPolicyId\":\"{trialA}\"}}")).GetProperty("trialPolicyId").GetString()
        );
        foreach (string other in new[] { standardA, trialB, "x" })
        {
            await TestService.AssertProblemAsync(
                await admin.PatchAsync($"v1/admin/products/{a}", TestService.Json($"{{\"trialPolicyId\":\"{other}\"}}"), cancel),
                HttpStatusCode.BadRequest,
                "ELM_INVALID_ARGUMENT",
                "trialPolicyId must name a trial policy of this product."
            );
        }

        Assert.Equal(
            JsonValueKind.Null,
            (await PatchAsync(admin, $"v1/admin/products/{a}", """{"trialPolicyId":null}""")).GetProperty("trialPolicyId").ValueKind
        );
        string[] details =
        [
            .. (await TestService.AuditRowsAsync(test, service, owner))
                .Where(r => r.GetProperty("action").GetString() == "product.update" && r.GetProperty("result").GetString() == "ELM_OK")
                .Select(r => r.GetProperty("detail").GetString()!),
        ];
        Assert.Equal([$"{{\"trialPolicyId\":\"{trialA}\"}}", "{\"trialPolicyId\":null}"], details);
    }

    /// <summary>
    /// A policy is created with the defaults for the members left out, read back with every member, listed by product, and
    /// changed only in the members sent, after the whole result is checked again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service, an owner key, and products A and B, on each engine.
    ///
    /// Steps:
    /// - Create a policy of A with only productId and name, and one of B with duration 365 days and overage 10; read the
    ///   first; list with productId A, with productId B, with no filter, with a productId that is not an ID; patch the first
    ///   with maxActivations 5 and duration null; patch it with licenseKind, binding, productId; patch a trial policy with
    ///   gracePeriod 1; read the audit log.
    ///
    /// Expected:
    /// - The first item has the §9.3 defaults and null for the "none" members; lists of 1, 1, 2, and 400; 200 with
    ///   maxActivations 5 and the rest unchanged; 400 naming each fixed member; 400 with the trial rule; the create row
    ///   holds only the members sent, the update row only maxActivations (duration was already null).
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Policies_CreateReadListPatch_AnswerAsDesigned(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string a = (await CreatedAsync(admin, "v1/admin/products", """{"code":"a","name":"A"}""")).GetProperty("id").GetString()!;
        string b = (await CreatedAsync(admin, "v1/admin/products", """{"code":"b","name":"B"}""")).GetProperty("id").GetString()!;

        string first = await PolicyAsync(admin, a, string.Empty);
        await PolicyAsync(admin, b, "\"duration\":31536000,\"overage\":10");
        JsonElement read = await GetAsync(admin, $"v1/admin/policies/{first}");
        Assert.Equal(
            "standard nodeLocked 1 null fromIssue null 2592000 1728000 604800 any deny none 600 null null 2 none 1209600",
            string.Join(' ', Policies.Members.Select(m => read.GetProperty(m.Name).ToString() is { Length: > 0 } s ? s : "null"))
        );
        Assert.Equal(1, (await GetAsync(admin, $"v1/admin/policies?productId={a}")).GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await GetAsync(admin, $"v1/admin/policies?productId={b}")).GetProperty("items").GetArrayLength());
        Assert.Equal(2, (await GetAsync(admin, "v1/admin/policies")).GetProperty("items").GetArrayLength());
        await TestService.AssertProblemAsync(
            await admin.GetAsync("v1/admin/policies?productId=x", cancel),
            HttpStatusCode.BadRequest,
            "ELM_INVALID_ARGUMENT",
            "productId must be a product ID."
        );

        JsonElement patched = await PatchAsync(admin, $"v1/admin/policies/{first}", """{"maxActivations":5,"duration":null}""");
        Assert.Equal((5, 2_592_000L), (patched.GetProperty("maxActivations").GetInt32(), patched.GetProperty("tokenLifetime").GetInt64()));
        foreach (string member in new[] { "licenseKind", "binding", "productId" })
        {
            await TestService.AssertProblemAsync(
                await admin.PatchAsync($"v1/admin/policies/{first}", TestService.Json($"{{\"{member}\":\"x\"}}"), cancel),
                HttpStatusCode.BadRequest,
                "ELM_INVALID_ARGUMENT",
                $"{member} cannot be changed."
            );
        }

        string trial = await PolicyAsync(admin, a, "\"licenseKind\":\"trial\",\"gracePeriod\":0");
        await TestService.AssertProblemAsync(
            await admin.PatchAsync($"v1/admin/policies/{trial}", TestService.Json("""{"gracePeriod":1}"""), cancel),
            HttpStatusCode.BadRequest,
            "ELM_INVALID_ARGUMENT",
            "A trial policy has gracePeriod 0."
        );

        JsonElement[] rows = await TestService.AuditRowsAsync(test, service, owner);
        Assert.Equal(
            $"{{\"productId\":\"{a}\",\"name\":\"p\"}}",
            rows.First(r => r.GetProperty("action").GetString() == "policy.create").GetProperty("detail").GetString()
        );
        Assert.Equal(
            "{\"maxActivations\":5}",
            rows.Single(r => r.GetProperty("action").GetString() == "policy.update" && r.GetProperty("result").GetString() == "ELM_OK")
                .GetProperty("detail")
                .GetString()
        );
    }

    /// <summary>
    /// A policy body is checked before anything is written: missing or null required members and wrong types are
    /// ELM_MALFORMED, an unknown product and a broken rule are ELM_INVALID_ARGUMENT.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service, an owner key, and a product.
    ///
    /// Steps:
    /// - Create policies with no productId; productId null; productId not an ID; an unknown productId; no name; name 5;
    ///   maxActivations "1"; an unbound fromFirstActivation policy; then list policies.
    ///
    /// Expected:
    /// - 400 ELM_MALFORMED twice; 400 naming productId twice; 400 ELM_MALFORMED twice; 400 ELM_MALFORMED; 400 with the
    ///   unbound rule; no policy stored.
    /// </remarks>
    [Fact]
    public async Task Policies_BadBody_IsRefusedBeforeWriting()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string a = (await CreatedAsync(admin, "v1/admin/products", """{"code":"a","name":"A"}""")).GetProperty("id").GetString()!;
        async Task Refused(string body, string code, string detail) =>
            await TestService.AssertProblemAsync(
                await admin.PostAsync("v1/admin/policies", TestService.Json(body), cancel),
                HttpStatusCode.BadRequest,
                code,
                detail
            );

        await Refused("""{"name":"p"}""", "ELM_MALFORMED", PolicyMember.Malformed);
        await Refused("""{"productId":null,"name":"p"}""", "ELM_MALFORMED", PolicyMember.Malformed);
        await Refused("""{"productId":"x","name":"p"}""", "ELM_INVALID_ARGUMENT", "productId must name a product.");
        await Refused($"{{\"productId\":\"{Guid.CreateVersion7():D}\",\"name\":\"p\"}}", "ELM_INVALID_ARGUMENT", "productId must name a product.");
        await Refused($"{{\"productId\":\"{a}\"}}", "ELM_MALFORMED", PolicyMember.Malformed);
        await Refused($"{{\"productId\":\"{a}\",\"name\":5}}", "ELM_MALFORMED", PolicyMember.Malformed);
        await Refused($"{{\"productId\":\"{a}\",\"name\":\"p\",\"maxActivations\":\"1\"}}", "ELM_MALFORMED", PolicyMember.Malformed);
        await Refused(
            $"{{\"productId\":\"{a}\",\"name\":\"p\",\"binding\":\"unbound\",\"expirationBasis\":\"fromFirstActivation\"}}",
            "ELM_INVALID_ARGUMENT",
            "An unbound policy uses expirationBasis fromIssue."
        );
        Assert.Equal(0, (await GetAsync(admin, "v1/admin/policies")).GetProperty("items").GetArrayLength());
    }

    /// <summary>
    /// IDs in the path that name nothing, or are not IDs, are 404 ELM_NOT_FOUND; a feature is found only under its product.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Products A and B, and a feature of A.
    ///
    /// Steps:
    /// - Read an unknown product and "x"; patch a product, list features, and create a feature under an unknown product;
    ///   patch A's feature under B; read and patch an unknown policy.
    ///
    /// Expected:
    /// - 404 ELM_NOT_FOUND each time, naming the entity.
    /// </remarks>
    [Fact]
    public async Task UnknownIds_AreNotFound()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string a = (await CreatedAsync(admin, "v1/admin/products", """{"code":"a","name":"A"}""")).GetProperty("id").GetString()!;
        string b = (await CreatedAsync(admin, "v1/admin/products", """{"code":"b","name":"B"}""")).GetProperty("id").GetString()!;
        string f = (await CreatedAsync(admin, $"v1/admin/products/{a}/features", """{"code":"f","name":"F"}""")).GetProperty("id").GetString()!;
        string unknown = Guid.CreateVersion7().ToString("D");
        async Task NotFound(Task<HttpResponseMessage> call, string detail) =>
            await TestService.AssertProblemAsync(await call, HttpStatusCode.NotFound, "ELM_NOT_FOUND", detail);

        await NotFound(admin.GetAsync($"v1/admin/products/{unknown}", cancel), "No product has this ID.");
        await NotFound(admin.GetAsync("v1/admin/products/x", cancel), "No product has this ID.");
        await NotFound(admin.PatchAsync($"v1/admin/products/{unknown}", TestService.Json("{}"), cancel), "No product has this ID.");
        await NotFound(admin.GetAsync($"v1/admin/products/{unknown}/features", cancel), "No product has this ID.");
        await NotFound(
            admin.PostAsync($"v1/admin/products/{unknown}/features", TestService.Json("""{"code":"f","name":"F"}"""), cancel),
            "No product has this ID."
        );
        await NotFound(admin.PatchAsync($"v1/admin/products/{b}/features/{f}", TestService.Json("{}"), cancel), "No feature has this ID.");
        await NotFound(admin.GetAsync($"v1/admin/policies/{unknown}", cancel), "No policy has this ID.");
        await NotFound(admin.PatchAsync($"v1/admin/policies/{unknown}", TestService.Json("{}"), cancel), "No policy has this ID.");
    }

    /// <summary>
    /// The model's permissions are enforced and cut by the administrator's role: product.read and policy.read read, the
    /// write permissions write, and a viewer's key cannot write whatever it was given.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service, an owner key, and a key with product.read, policy.read, and signingkey.read only.
    ///
    /// Steps:
    /// - With the narrow key: list products, list policies, read the signing key, create a product, create a policy; give a
    ///   key product.write and change the owner's role to viewer; create a product with it.
    ///
    /// Expected:
    /// - 200 three times; 403 naming product.write and policy.write; 403 naming product.write.
    /// </remarks>
    [Fact]
    public async Task Permissions_ModelEndpoints_AreEnforced()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        HttpClient narrow = test.Http(
            service,
            await TestService.CreateKeyAsync(test.Http(service, owner), "\"product.read\",\"policy.read\",\"signingkey.read\"")
        );
        HttpClient writer = test.Http(service, await TestService.CreateKeyAsync(test.Http(service, owner), "\"product.write\""));

        foreach (string path in new[] { "v1/admin/products", "v1/admin/policies", "v1/admin/signing-key" })
        {
            Assert.Equal(HttpStatusCode.OK, (await narrow.GetAsync(path, cancel)).StatusCode);
        }

        await TestService.AssertProblemAsync(
            await narrow.PostAsync("v1/admin/products", TestService.Json("""{"code":"a","name":"A"}"""), cancel),
            HttpStatusCode.Forbidden,
            "ELM_FORBIDDEN",
            "The API key does not have the permission product.write."
        );
        await TestService.AssertProblemAsync(
            await narrow.PostAsync("v1/admin/policies", TestService.Json("{}"), cancel),
            HttpStatusCode.Forbidden,
            "ELM_FORBIDDEN",
            "The API key does not have the permission policy.write."
        );

        await test.ExecuteAsync("UPDATE admins SET role = 'viewer'");
        await TestService.AssertProblemAsync(
            await writer.PostAsync("v1/admin/products", TestService.Json("""{"code":"a","name":"A"}"""), cancel),
            HttpStatusCode.Forbidden,
            "ELM_FORBIDDEN",
            "The API key does not have the permission product.write."
        );
    }

    /// <summary>
    /// The signing key's status names the key, its uses, and its certificate's end, and warns from 30 days before the end
    /// (design §14.5, decision 0.75): exactly 30 days left is not yet a warning.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service whose certificate for license and lease ends 364 days after the start, and a key with signingkey.read
    ///   for 365 days.
    ///
    /// Steps:
    /// - Read the status now; move the clock to exactly 30 days before the end and read; move one more second and read.
    ///
    /// Expected:
    /// - keyId, ["license","lease"], the end; expiresSoon false, false, true.
    /// </remarks>
    [Fact]
    public async Task SigningKey_Status_WarnsWithinThirtyDays()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        string reader = await TestService.CreateKeyAsync(test.Http(service, owner), "\"signingkey.read\"", days: 365);
        HttpClient admin = test.Http(service, reader);
        DateTimeOffset end = TestService.Start.AddDays(364);

        JsonElement status = await GetAsync(admin, "v1/admin/signing-key");
        Assert.Equal(
            (KeyFiles.KeyId(test.SigningKey), "[\"license\",\"lease\"]", "2027-09-30T00:00:00Z", false),
            (
                status.GetProperty("keyId").GetString(),
                status.GetProperty("uses").ToString(),
                status.GetProperty("expiresAt").GetString(),
                status.GetProperty("expiresSoon").GetBoolean()
            )
        );
        Assert.Equal(end, DateTimeOffset.Parse(status.GetProperty("expiresAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture));

        test.Time.Advance(end - TimeSpan.FromDays(30) - test.Time.GetUtcNow());
        Assert.False((await GetAsync(admin, "v1/admin/signing-key")).GetProperty("expiresSoon").GetBoolean());
        test.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await GetAsync(admin, "v1/admin/signing-key")).GetProperty("expiresSoon").GetBoolean());
    }

    private static async Task<string> PolicyAsync(HttpClient admin, string productId, string members)
    {
        string body = $"{{\"productId\":\"{productId}\",\"name\":\"p\"{(members.Length > 0 ? "," + members : string.Empty)}}}";
        return (await CreatedAsync(admin, "v1/admin/policies", body)).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> CreatedAsync(HttpClient admin, string path, string body)
    {
        using HttpResponseMessage response = await admin.PostAsync(path, TestService.Json(body), TestContext.Current.CancellationToken);
        string text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> GetAsync(HttpClient admin, string path) =>
        JsonDocument.Parse(await admin.GetStringAsync(path, TestContext.Current.CancellationToken)).RootElement.Clone();

    private static async Task<JsonElement> PatchAsync(HttpClient admin, string path, string body)
    {
        using HttpResponseMessage response = await admin.PatchAsync(path, TestService.Json(body), TestContext.Current.CancellationToken);
        string text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
