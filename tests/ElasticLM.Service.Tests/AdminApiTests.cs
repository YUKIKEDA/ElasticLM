using System.Net;
using System.Text;
using System.Text.Json;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The administration API of M4a1 (design §14.5, §14.6, §14.7, decisions 0.78 and 0.79), on SQLite and on PostgreSQL.
/// </summary>
public sealed class AdminApiTests
{
    public static TheoryData<string> Providers => ["sqlite", "postgres"];

    /// <summary>
    /// A key made by setup creates a key; the new key is shown once, works, and is listed by its prefix only; a stopped key
    /// stops at once, and stopping it again is 204; an unknown ID is 404 ELM_NOT_FOUND.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service and an owner key from setup.
    ///
    /// Steps:
    /// - Create a key with audit.read and apikey.read; list keys with it; stop it; list with it again; stop it again; stop an
    ///   unknown ID and a value that is not an ID.
    ///
    /// Expected:
    /// - 201 with "key", the prefix its first 8 characters, permissions in the fixed order, expiry 90 days; 200 listing two
    ///   keys, none with a "key"; 204; 401; 204; 404 ELM_NOT_FOUND twice.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ApiKeys_CreateListStop(string provider)
    {
        await using TestService test = Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;

        using HttpResponseMessage created = await admin.PostAsync(
            "v1/admin/api-keys",
            Json("""{"name":"ci","permissions":["audit.read","apikey.read"]}"""),
            cancel
        );
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument item = JsonDocument.Parse(await created.Content.ReadAsStringAsync(cancel));
        string key = item.RootElement.GetProperty("key").GetString()!;
        string id = item.RootElement.GetProperty("id").GetString()!;
        Assert.Equal(key[..8], item.RootElement.GetProperty("prefix").GetString());
        Assert.Equal(["apikey.read", "audit.read"], item.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("2026-12-30T00:00:00Z", item.RootElement.GetProperty("expiresAt").GetString());
        Assert.Equal(JsonValueKind.Null, item.RootElement.GetProperty("revokedAt").ValueKind);

        HttpClient ci = test.Http(service, key);
        using (JsonDocument page = JsonDocument.Parse(await ci.GetStringAsync("v1/admin/api-keys", cancel)))
        {
            JsonElement[] items = [.. page.RootElement.GetProperty("items").EnumerateArray()];
            Assert.Equal(2, items.Length);
            Assert.All(items, i => Assert.False(i.TryGetProperty("key", out _)));
            Assert.False(page.RootElement.TryGetProperty("next", out _));
        }

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"v1/admin/api-keys/{id}", cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ci.GetAsync("v1/admin/api-keys", cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"v1/admin/api-keys/{id}", cancel)).StatusCode);
        await AssertProblemAsync(
            await admin.DeleteAsync($"v1/admin/api-keys/{Guid.CreateVersion7():D}", cancel),
            HttpStatusCode.NotFound,
            "ELM_NOT_FOUND"
        );
        await AssertProblemAsync(await admin.DeleteAsync("v1/admin/api-keys/not-an-id", cancel), HttpStatusCode.NotFound, "ELM_NOT_FOUND");
    }

    /// <summary>
    /// The request body is checked at each limit exactly and one past it (design §14.5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service and an owner key.
    ///
    /// Steps:
    /// - Create keys with: a name of 64 characters; of 64 four-byte characters (256 bytes); of 65 characters; empty; 32
    ///   permissions (repeats) and 33; none; an unknown permission; expiresInDays 1, 365, 0, 366; a null permission; an
    ///   unknown member; not JSON; a body of 64 KiB + 1 byte.
    ///
    /// Expected:
    /// - 201, 201, 400, 400; 201 with one permission, 400; 400; 400 ELM_INVALID_ARGUMENT; 201, 201, 400, 400; 400
    ///   ELM_MALFORMED; 201; 400 ELM_MALFORMED; 413 ELM_PAYLOAD_TOO_LARGE.
    /// </remarks>
    [Fact]
    public async Task CreateKey_LimitsAreExact()
    {
        await using TestService test = Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        async Task<HttpResponseMessage> Create(string body) => await admin.PostAsync("v1/admin/api-keys", Json(body), cancel);
        string Body(string name = "n", string permissions = "\"audit.read\"", string more = "") =>
            "{\"name\":" + JsonSerializer.Serialize(name) + ",\"permissions\":[" + permissions + "]" + more + "}";

        Assert.Equal(HttpStatusCode.Created, (await Create(Body(new string('a', 64)))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Create(Body(string.Concat(Enumerable.Repeat("\U0001F600", 64))))).StatusCode);
        await AssertProblemAsync(await Create(Body(new string('a', 65))), HttpStatusCode.BadRequest, "ELM_INVALID_ARGUMENT");
        await AssertProblemAsync(await Create(Body(string.Empty)), HttpStatusCode.BadRequest, "ELM_INVALID_ARGUMENT");

        using (HttpResponseMessage many = await Create(Body(permissions: string.Join(',', Enumerable.Repeat("\"audit.read\"", 32)))))
        {
            Assert.Equal(HttpStatusCode.Created, many.StatusCode);
            using JsonDocument item = JsonDocument.Parse(await many.Content.ReadAsStringAsync(cancel));
            Assert.Equal(1, item.RootElement.GetProperty("permissions").GetArrayLength());
        }

        await AssertProblemAsync(
            await Create(Body(permissions: string.Join(',', Enumerable.Repeat("\"audit.read\"", 33)))),
            HttpStatusCode.BadRequest,
            "ELM_INVALID_ARGUMENT"
        );
        await AssertProblemAsync(await Create(Body(permissions: string.Empty)), HttpStatusCode.BadRequest, "ELM_INVALID_ARGUMENT");
        await AssertProblemAsync(await Create(Body(permissions: "\"audit.write\"")), HttpStatusCode.BadRequest, "ELM_INVALID_ARGUMENT");

        Assert.Equal(HttpStatusCode.Created, (await Create(Body(more: ",\"expiresInDays\":1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Create(Body(more: ",\"expiresInDays\":365"))).StatusCode);
        await AssertProblemAsync(await Create(Body(more: ",\"expiresInDays\":0")), HttpStatusCode.BadRequest, "ELM_INVALID_ARGUMENT");
        await AssertProblemAsync(await Create(Body(more: ",\"expiresInDays\":366")), HttpStatusCode.BadRequest, "ELM_INVALID_ARGUMENT");

        await AssertProblemAsync(await Create(Body(permissions: "null")), HttpStatusCode.BadRequest, "ELM_MALFORMED");
        Assert.Equal(HttpStatusCode.Created, (await Create(Body(more: ",\"later\":true"))).StatusCode);
        await AssertProblemAsync(await Create("not json"), HttpStatusCode.BadRequest, "ELM_MALFORMED");
        await AssertProblemAsync(
            await Create(new string(' ', AdminApi.MaxBodyLength + 1)),
            HttpStatusCode.RequestEntityTooLarge,
            "ELM_PAYLOAD_TOO_LARGE"
        );
    }

    /// <summary>
    /// A key cannot give a key more than it has, and a key without a permission is refused with 403 and an audit row.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service, an owner key, and a key with apikey.create and apikey.read only.
    ///
    /// Steps:
    /// - With the narrow key: create a key with apikey.read; with audit.read; read the audit log.
    ///
    /// Expected:
    /// - 201; 403 ELM_FORBIDDEN; 403 ELM_FORBIDDEN, and the audit log has an audit.list row with result ELM_FORBIDDEN.
    /// </remarks>
    [Fact]
    public async Task Permissions_AreEnforced()
    {
        await using TestService test = Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string narrow = await CreateKeyAsync(test.Http(service, owner), "\"apikey.create\",\"apikey.read\"");
        HttpClient http = test.Http(service, narrow);

        Assert.Equal(
            HttpStatusCode.Created,
            (await http.PostAsync("v1/admin/api-keys", Json("""{"name":"x","permissions":["apikey.read"]}"""), cancel)).StatusCode
        );
        await AssertProblemAsync(
            await http.PostAsync("v1/admin/api-keys", Json("""{"name":"x","permissions":["audit.read"]}"""), cancel),
            HttpStatusCode.Forbidden,
            "ELM_FORBIDDEN"
        );
        await AssertProblemAsync(await http.GetAsync("v1/admin/audit", cancel), HttpStatusCode.Forbidden, "ELM_FORBIDDEN");

        using JsonDocument page = JsonDocument.Parse(await test.Http(service, owner).GetStringAsync("v1/admin/audit?limit=200", cancel));
        Assert.Contains(
            page.RootElement.GetProperty("items").EnumerateArray(),
            r => r.GetProperty("action").GetString() == "audit.list" && r.GetProperty("result").GetString() == "ELM_FORBIDDEN"
        );
    }

    /// <summary>
    /// Every way a key fails is the same 401, and the failures are written as one counted row a minute (design §14.5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service, an owner key, and a key that expires in one day.
    ///
    /// Steps:
    /// - Advance one day (the short key expires); call with no header, a Basic header, an unknown elm_ key, a key one character
    ///   short, and the expired key; advance one more minute; read the audit log.
    ///
    /// Expected:
    /// - 401 ELM_UNAUTHORIZED five times with the same detail; one auth.failed row by system/auth with {"count":5}.
    /// </remarks>
    [Fact]
    public async Task Authentication_FailuresAreOneAnswerAndCounted()
    {
        await using TestService test = Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string shortLived = await CreateKeyAsync(test.Http(service, owner), "\"audit.read\"", days: 1);

        // The day passes before the failures, so the minute timer that fires on the way has nothing to write yet.
        test.Time.Advance(TimeSpan.FromDays(1));
        var details = new List<string>();
        async Task Fail(HttpClient http) =>
            details.Add(await AssertProblemAsync(await http.GetAsync("v1/admin/audit", cancel), HttpStatusCode.Unauthorized, "ELM_UNAUTHORIZED"));
        await Fail(test.Http(service));
        HttpClient basic = test.Http(service);
        basic.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", "dXNlcjpwYXNz");
        await Fail(basic);
        await Fail(test.Http(service, ApiKeys.NewKey()));
        await Fail(test.Http(service, owner[..^1]));
        await Fail(test.Http(service, shortLived));
        Assert.Single(details.Distinct());

        test.Time.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(async () => (await AuditRowsAsync(test, service, owner)).Any(r => r.GetProperty("action").GetString() == "auth.failed"));
        JsonElement row = (await AuditRowsAsync(test, service, owner)).Single(r => r.GetProperty("action").GetString() == "auth.failed");
        Assert.Equal(
            ("system", "auth", "ELM_UNAUTHORIZED", "{\"count\":5}"),
            (
                row.GetProperty("actorType").GetString(),
                row.GetProperty("actorId").GetString(),
                row.GetProperty("result").GetString(),
                row.GetProperty("detail").GetString()
            )
        );
    }

    /// <summary>
    /// Lists page by the documented cursor: ID for keys, row number for the audit log; limit is 1 to 200.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service, an owner key, and 3 more keys.
    ///
    /// Steps:
    /// - List keys with limit 2, then after the returned next; list with limit 0, 201, 200, a non-ID after; list the audit log
    ///   with limit 1 and after its next, and with after -1.
    ///
    /// Expected:
    /// - 2 keys and a next; 2 keys and no next; 400, 400, 200, 400; one row then the following row; 400.
    /// </remarks>
    [Fact]
    public async Task Lists_PageByTheirCursor()
    {
        await using TestService test = Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        for (int i = 0; i < 3; i++)
        {
            await CreateKeyAsync(admin, "\"audit.read\"");
        }

        string next;
        using (JsonDocument first = JsonDocument.Parse(await admin.GetStringAsync("v1/admin/api-keys?limit=2", cancel)))
        {
            Assert.Equal(2, first.RootElement.GetProperty("items").GetArrayLength());
            next = first.RootElement.GetProperty("next").GetString()!;
        }

        using (JsonDocument second = JsonDocument.Parse(await admin.GetStringAsync($"v1/admin/api-keys?limit=2&after={next}", cancel)))
        {
            Assert.Equal(2, second.RootElement.GetProperty("items").GetArrayLength());
            Assert.False(second.RootElement.TryGetProperty("next", out _));
        }

        await AssertProblemAsync(await admin.GetAsync("v1/admin/api-keys?limit=0", cancel), HttpStatusCode.BadRequest, "ELM_INVALID_ARGUMENT");
        await AssertProblemAsync(await admin.GetAsync("v1/admin/api-keys?limit=201", cancel), HttpStatusCode.BadRequest, "ELM_INVALID_ARGUMENT");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("v1/admin/api-keys?limit=200", cancel)).StatusCode);
        await AssertProblemAsync(await admin.GetAsync("v1/admin/api-keys?after=abc", cancel), HttpStatusCode.BadRequest, "ELM_INVALID_ARGUMENT");

        long after;
        using (JsonDocument one = JsonDocument.Parse(await admin.GetStringAsync("v1/admin/audit?limit=1", cancel)))
        {
            Assert.Equal(1, one.RootElement.GetProperty("items")[0].GetProperty("n").GetInt64());
            after = one.RootElement.GetProperty("next").GetInt64();
        }

        using (JsonDocument two = JsonDocument.Parse(await admin.GetStringAsync($"v1/admin/audit?limit=1&after={after}", cancel)))
        {
            Assert.Equal(after + 1, two.RootElement.GetProperty("items")[0].GetProperty("n").GetInt64());
        }

        await AssertProblemAsync(await admin.GetAsync("v1/admin/audit?after=-1", cancel), HttpStatusCode.BadRequest, "ELM_INVALID_ARGUMENT");
    }

    /// <summary>
    /// Requests are limited per source before authentication and per API key after it (design §14.7), and /health answers
    /// without a key.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with rateLimit.perIp 5 and rateLimit.perKey 2.
    ///
    /// Steps:
    /// - /health; with the owner key, three calls in the same instant; one second later one more; then calls until the source
    ///   limit.
    ///
    /// Expected:
    /// - 200; 200, 200, 429 ELM_RATE_LIMITED with Retry-After; 200; 429 from the source limit.
    /// </remarks>
    [Fact]
    public async Task RateLimits_PerSourceAndPerKey()
    {
        await using TestService test = Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync(s => s with { RateLimitPerIp = 5, RateLimitPerKey = 2 });
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.OK, (await test.Http(service).GetAsync("health", cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("v1/admin/api-keys", cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("v1/admin/api-keys", cancel)).StatusCode);
        using (HttpResponseMessage limited = await admin.GetAsync("v1/admin/api-keys", cancel))
        {
            Assert.Equal("1", limited.Headers.RetryAfter?.ToString());
            await AssertProblemAsync(limited, HttpStatusCode.TooManyRequests, "ELM_RATE_LIMITED");
        }

        test.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("v1/admin/api-keys", cancel)).StatusCode);
        HttpStatusCode last = HttpStatusCode.OK;
        for (int i = 0; i < 6 && last != HttpStatusCode.TooManyRequests; i++)
        {
            last = (await test.Http(service).GetAsync("health", cancel)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    internal static TestService Open(string provider)
    {
        if (provider == "postgres")
        {
            Assert.SkipUnless(TestService.PostgresAvailable, $"Set {TestService.PostgresVariable} to a PostgreSQL server to run this case.");
            return new TestService(DatabaseProvider.Postgres);
        }

        return new TestService();
    }

    internal static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    internal static async Task<string> CreateKeyAsync(HttpClient admin, string permissions, int days = 90)
    {
        string body = "{\"name\":\"k\",\"permissions\":[" + permissions + "],\"expiresInDays\":" + days + "}";
        using HttpResponseMessage created = await admin.PostAsync("v1/admin/api-keys", Json(body), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument item = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return item.RootElement.GetProperty("key").GetString()!;
    }

    internal static async Task<JsonElement[]> AuditRowsAsync(TestService test, ElmService service, string key)
    {
        string text = await test.Http(service, key).GetStringAsync("v1/admin/audit?limit=200", TestContext.Current.CancellationToken);
        using JsonDocument page = JsonDocument.Parse(text);
        return [.. page.RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone())];
    }

    internal static async Task<string> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
            return problem.RootElement.GetProperty("detail").GetString()!;
        }
    }

    // The service runs its timers on the fake clock but on thread-pool threads; wait, in real time, for their work.
    internal static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (int i = 0; i < 500; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Fail("The condition did not hold within 10 seconds.");
    }
}
