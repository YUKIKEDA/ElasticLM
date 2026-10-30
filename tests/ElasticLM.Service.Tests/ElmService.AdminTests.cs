using System.Net;
using System.Text.Json;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The administration API of M4a1 (design §14.5, §14.6, §14.7, decisions 0.78 and 0.79), on SQLite and on PostgreSQL.
/// </summary>
public sealed class ElmServiceAdminTests
{
    private const string Malformed = "The request body is not a valid API key request.";
    private const string NameLimit = "name must be 1 to 64 characters.";
    private const string NotFound = "No API key has this ID.";
    private const string Unauthorized = "The API key is missing, unknown, stopped, or expired.";

    /// <summary>
    /// A key made by setup creates a key; the new key is shown once, works, and is listed by its prefix only; a revoked key
    /// stops at once, and revoking it again is 204; an unknown ID is 404 ELM_NOT_FOUND.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service and an owner key from setup.
    ///
    /// Steps:
    /// - Create a key with audit.read and apikey.read; list keys with it; revoke it; list with it again; revoke it again;
    ///   revoke an unknown ID and a value that is not an ID.
    ///
    /// Expected:
    /// - 201 with "key", the prefix its first 8 characters, permissions in the fixed order, expiry 90 days; 200 listing two
    ///   keys, none with a "key"; 204; 401; 204; 404 ELM_NOT_FOUND twice.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task ApiKeys_CreateListRevoke_AnswerAsDesigned(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;

        using HttpResponseMessage created = await admin.PostAsync(
            "v1/admin/api-keys",
            TestService.Json("""{"name":"ci","permissions":["audit.read","apikey.read"]}"""),
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
        await TestService.AssertProblemAsync(
            await admin.DeleteAsync($"v1/admin/api-keys/{Guid.CreateVersion7():D}", cancel),
            HttpStatusCode.NotFound,
            "ELM_NOT_FOUND",
            NotFound
        );
        await TestService.AssertProblemAsync(
            await admin.DeleteAsync("v1/admin/api-keys/not-an-id", cancel),
            HttpStatusCode.NotFound,
            "ELM_NOT_FOUND",
            NotFound
        );
    }

    /// <summary>
    /// The request body is checked at each limit exactly and one past it (design §14.5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service and an owner key, on each engine.
    ///
    /// Steps:
    /// - Create keys with: a name of 64 characters; of 64 four-byte characters (256 bytes); of 65 characters; empty; 32
    ///   permissions (repeats, counted before they are merged) and 33; none; an unknown permission; expiresInDays 1, 365, 0,
    ///   366; an unknown member; a body of exactly 64 KiB; one of 64 KiB + 1 byte with Content-Length; one of 64 KiB + 1
    ///   byte sent chunked, without Content-Length.
    ///
    /// Expected:
    /// - 201, 201, 400, 400; 201 with one permission, 400; 400; 400; 201, 201, 400, 400; 201; 201; 413; 413; each refusal
    ///   with its own detail.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task CreateKey_AtAndPastEachLimit_AcceptsThenRefuses(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        async Task<HttpResponseMessage> Create(string body) => await admin.PostAsync("v1/admin/api-keys", TestService.Json(body), cancel);
        Task Refused(HttpResponseMessage response, string detail) =>
            TestService.AssertProblemAsync(response, HttpStatusCode.BadRequest, "ELM_INVALID_ARGUMENT", detail);

        Assert.Equal(HttpStatusCode.Created, (await Create(Body(new string('a', 64)))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Create(Body(string.Concat(Enumerable.Repeat("\U0001F600", 64))))).StatusCode);
        await Refused(await Create(Body(new string('a', 65))), NameLimit);
        await Refused(await Create(Body(string.Empty)), NameLimit);

        using (HttpResponseMessage many = await Create(Body(permissions: string.Join(',', Enumerable.Repeat("\"audit.read\"", 32)))))
        {
            Assert.Equal(HttpStatusCode.Created, many.StatusCode);
            using JsonDocument item = JsonDocument.Parse(await many.Content.ReadAsStringAsync(cancel));
            Assert.Equal(1, item.RootElement.GetProperty("permissions").GetArrayLength());
        }

        await Refused(
            await Create(Body(permissions: string.Join(',', Enumerable.Repeat("\"audit.read\"", 33)))),
            "permissions must hold 1 to 32 permissions."
        );
        await Refused(await Create(Body(permissions: string.Empty)), "permissions must hold 1 to 32 permissions.");
        await Refused(await Create(Body(permissions: "\"audit.write\"")), "'audit.write' is not a permission.");

        Assert.Equal(HttpStatusCode.Created, (await Create(Body(more: ",\"expiresInDays\":1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Create(Body(more: ",\"expiresInDays\":365"))).StatusCode);
        await Refused(await Create(Body(more: ",\"expiresInDays\":0")), "expiresInDays must be between 1 and 365.");
        await Refused(await Create(Body(more: ",\"expiresInDays\":366")), "expiresInDays must be between 1 and 365.");
        Assert.Equal(HttpStatusCode.Created, (await Create(Body(more: ",\"later\":true"))).StatusCode);

        string exact = Body();
        exact += new string(' ', AdminApi.MaxBodyLength - exact.Length);
        Assert.Equal(HttpStatusCode.Created, (await Create(exact)).StatusCode);
        await TestService.AssertProblemAsync(
            await Create(exact + " "),
            HttpStatusCode.RequestEntityTooLarge,
            "ELM_PAYLOAD_TOO_LARGE",
            "The request body is larger than 64 KiB."
        );
        await TestService.AssertProblemAsync(
            await admin.PostAsync("v1/admin/api-keys", new ChunkedContent(exact + " "), cancel),
            HttpStatusCode.RequestEntityTooLarge,
            "ELM_PAYLOAD_TOO_LARGE",
            "The request body is larger than 64 KiB."
        );
    }

    /// <summary>
    /// A body that is not a request of the documented shape is 400 ELM_MALFORMED, never a 500: JSON nested deeper than 8,
    /// a value of the wrong type, a missing or null name, a member given twice, and text that is not valid UTF-16.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service and an owner key.
    ///
    /// Steps:
    /// - Create keys with: an unknown member nested to depth 8 in all; to depth 9; "name":5; "expiresInDays":"x"; 1.5; no
    ///   name; "name":null; a null permission; "name" twice; a name of one lone surrogate escape; not JSON.
    ///
    /// Expected:
    /// - 201; then 400 ELM_MALFORMED with the same detail for each other case.
    /// </remarks>
    [Fact]
    public async Task CreateKey_BodyNotAValidRequest_IsMalformed()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        async Task<HttpResponseMessage> Create(string body) => await admin.PostAsync("v1/admin/api-keys", TestService.Json(body), cancel);

        // The object is depth 1; each array inside "later" adds one.
        Assert.Equal(HttpStatusCode.Created, (await Create(Body(more: ",\"later\":" + new string('[', 7) + new string(']', 7)))).StatusCode);
        string[] malformed =
        [
            Body(more: ",\"later\":" + new string('[', 8) + new string(']', 8)),
            """{"name":5,"permissions":["audit.read"]}""",
            Body(more: ",\"expiresInDays\":\"x\""),
            Body(more: ",\"expiresInDays\":1.5"),
            """{"permissions":["audit.read"]}""",
            """{"name":null,"permissions":["audit.read"]}""",
            Body(permissions: "null"),
            """{"name":"a","name":"b","permissions":["audit.read"]}""",
            """{"name":"\ud800","permissions":["audit.read"]}""",
            "not json",
        ];
        foreach (string body in malformed)
        {
            await TestService.AssertProblemAsync(await Create(body), HttpStatusCode.BadRequest, "ELM_MALFORMED", Malformed);
        }
    }

    /// <summary>
    /// A key cannot give a key more than it has, and a key without a permission is refused with 403 and an audit row.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service, an owner key, and a key with apikey.create and apikey.read only, on each engine.
    ///
    /// Steps:
    /// - With the narrow key: create a key with apikey.read; with audit.read; read the audit log.
    ///
    /// Expected:
    /// - 201; 403 ELM_FORBIDDEN naming audit.read as beyond the creator; 403 ELM_FORBIDDEN naming the missing permission, and
    ///   the audit log has an audit.list row with result ELM_FORBIDDEN.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Permissions_BeyondTheKey_AreForbiddenAndAudited(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string narrow = await TestService.CreateKeyAsync(test.Http(service, owner), "\"apikey.create\",\"apikey.read\"");
        HttpClient http = test.Http(service, narrow);

        Assert.Equal(
            HttpStatusCode.Created,
            (await http.PostAsync("v1/admin/api-keys", TestService.Json("""{"name":"x","permissions":["apikey.read"]}"""), cancel)).StatusCode
        );
        await TestService.AssertProblemAsync(
            await http.PostAsync("v1/admin/api-keys", TestService.Json("""{"name":"x","permissions":["audit.read"]}"""), cancel),
            HttpStatusCode.Forbidden,
            "ELM_FORBIDDEN",
            "A key cannot be given the permission audit.read, which its creator does not have."
        );
        await TestService.AssertProblemAsync(
            await http.GetAsync("v1/admin/audit", cancel),
            HttpStatusCode.Forbidden,
            "ELM_FORBIDDEN",
            "The API key does not have the permission audit.read."
        );

        JsonElement[] rows = await TestService.AuditRowsAsync(test, service, owner);
        Assert.Contains(rows, r => r.GetProperty("action").GetString() == "audit.list" && r.GetProperty("result").GetString() == "ELM_FORBIDDEN");
    }

    /// <summary>
    /// A key holds only what its administrator's current role allows, and stops when the administrator is disabled
    /// (design §14.6): the role is read on every request, not when the key was made.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service, an owner key, and a key with apikey.create and audit.read, on each engine.
    ///
    /// Steps:
    /// - Change the owner's role to viewer in the database; with the key, read the audit log and create a key; disable the
    ///   administrator; read the audit log.
    ///
    /// Expected:
    /// - 200; 403 naming apikey.create; 401 ELM_UNAUTHORIZED.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Permissions_AdminRoleChangedOrDisabled_CutTheKey(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        HttpClient key = test.Http(service, await TestService.CreateKeyAsync(test.Http(service, owner), "\"apikey.create\",\"audit.read\""));

        await test.ExecuteAsync("UPDATE admins SET role = 'viewer'");
        Assert.Equal(HttpStatusCode.OK, (await key.GetAsync("v1/admin/audit", cancel)).StatusCode);
        await TestService.AssertProblemAsync(
            await key.PostAsync("v1/admin/api-keys", TestService.Json("""{"name":"x","permissions":["audit.read"]}"""), cancel),
            HttpStatusCode.Forbidden,
            "ELM_FORBIDDEN",
            "The API key does not have the permission apikey.create."
        );

        await test.ExecuteAsync("UPDATE admins SET enabled = false");
        await TestService.AssertProblemAsync(
            await key.GetAsync("v1/admin/audit", cancel),
            HttpStatusCode.Unauthorized,
            "ELM_UNAUTHORIZED",
            Unauthorized
        );
    }

    /// <summary>
    /// Every way a key fails is the same 401, the scheme name is matched without case, and the failures are written as one
    /// counted row a minute (design §14.5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service, an owner key, and a key that expires in one day.
    ///
    /// Steps:
    /// - Advance one day (the short key expires); call with no header, a Basic header, an unknown elm_ key, a key one character
    ///   short, and the expired key; call with the owner key under the scheme "bearer"; advance one more minute; read the audit
    ///   log.
    ///
    /// Expected:
    /// - 401 ELM_UNAUTHORIZED five times with the same detail; 200; one auth.failed row by system/auth with {"count":5}.
    /// </remarks>
    [Fact]
    public async Task Authentication_AnyFailure_IsOneAnswerCountedOnce()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string shortLived = await TestService.CreateKeyAsync(test.Http(service, owner), "\"audit.read\"", days: 1);

        // The day passes before the failures, so the minute timer that fires on the way has nothing to write yet.
        test.Time.Advance(TimeSpan.FromDays(1));
        async Task Fail(HttpClient http) =>
            await TestService.AssertProblemAsync(
                await http.GetAsync("v1/admin/audit", cancel),
                HttpStatusCode.Unauthorized,
                "ELM_UNAUTHORIZED",
                Unauthorized
            );
        await Fail(test.Http(service));
        HttpClient basic = test.Http(service);
        basic.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", "dXNlcjpwYXNz");
        await Fail(basic);
        await Fail(test.Http(service, ApiKeys.NewKey()));
        await Fail(test.Http(service, owner[..^1]));
        await Fail(test.Http(service, shortLived));

        HttpClient lower = test.Http(service);
        lower.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "bearer " + owner);
        Assert.Equal(HttpStatusCode.OK, (await lower.GetAsync("v1/admin/api-keys", cancel)).StatusCode);

        test.Time.Advance(TimeSpan.FromMinutes(1));
        await TestService.WaitForAsync(async () =>
            (await TestService.AuditRowsAsync(test, service, owner)).Any(r => r.GetProperty("action").GetString() == "auth.failed")
        );
        JsonElement row = (await TestService.AuditRowsAsync(test, service, owner)).Single(r => r.GetProperty("action").GetString() == "auth.failed");
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
    /// Lists page by the documented cursor: ID for keys, row number for the audit log; limit is 1 to 200; a cursor that names
    /// no row still returns the rows after it (design §14.5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service, an owner key, and 3 more keys, on each engine (the key ID is text on SQLite and uuid on PostgreSQL).
    ///
    /// Steps:
    /// - List keys with limit 2, then after the returned next; list with limit 0, 201, 200, a non-ID after; list keys after
    ///   an ID older than every key and after one newer than every key; list the audit log with limit 1 and after its next,
    ///   after a number past the last row, and with after -1.
    ///
    /// Expected:
    /// - 2 keys and a next; 2 keys and no next; 400, 400, 200, 400; 4 keys; none; one row then the following row; none; 400.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Lists_WithCursor_PageInOrder(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        for (int i = 0; i < 3; i++)
        {
            await TestService.CreateKeyAsync(admin, "\"audit.read\"");
        }

        async Task<int> CountAsync(string path)
        {
            using JsonDocument page = JsonDocument.Parse(await admin.GetStringAsync(path, cancel));
            return page.RootElement.GetProperty("items").GetArrayLength();
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

        const string LimitDetail = "limit must be between 1 and 200.";
        await TestService.AssertProblemAsync(
            await admin.GetAsync("v1/admin/api-keys?limit=0", cancel),
            HttpStatusCode.BadRequest,
            "ELM_INVALID_ARGUMENT",
            LimitDetail
        );
        await TestService.AssertProblemAsync(
            await admin.GetAsync("v1/admin/api-keys?limit=201", cancel),
            HttpStatusCode.BadRequest,
            "ELM_INVALID_ARGUMENT",
            LimitDetail
        );
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("v1/admin/api-keys?limit=200", cancel)).StatusCode);
        await TestService.AssertProblemAsync(
            await admin.GetAsync("v1/admin/api-keys?after=abc", cancel),
            HttpStatusCode.BadRequest,
            "ELM_INVALID_ARGUMENT",
            "after must be an API key ID."
        );
        Assert.Equal(4, await CountAsync($"v1/admin/api-keys?after={Guid.CreateVersion7(TestService.Start.AddDays(-1)):D}"));
        Assert.Equal(0, await CountAsync($"v1/admin/api-keys?after={Guid.CreateVersion7(TestService.Start.AddDays(1)):D}"));

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

        Assert.Equal(0, await CountAsync("v1/admin/audit?after=1000"));
        await TestService.AssertProblemAsync(
            await admin.GetAsync("v1/admin/audit?after=-1", cancel),
            HttpStatusCode.BadRequest,
            "ELM_INVALID_ARGUMENT",
            "after must be a row number."
        );
    }

    /// <summary>
    /// The audit list reads newest first with ?order=desc (design §14.5, M5d1): after gives the rows below it, an unknown
    /// after above the head reads from the newest, and order takes only asc or desc, once.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with an owner key from setup and five audit rows written by reading the key list; each engine.
    ///
    /// Steps:
    /// - List with order desc and limit 2; then with its next; then with after 1000; then with order asc and limit 1; then
    ///   with order up, and with order given twice.
    ///
    /// Expected:
    /// - Two rows in falling n, the first the newest row, next the second's n; then two rows below that n; then the newest
    ///   row first again; then row 1; 400 ELM_INVALID_ARGUMENT "order must be asc or desc." twice.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Audit_OrderDesc_ReadsNewestFirst(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("v1/admin/api-keys", cancel)).StatusCode);
        }

        long newest = (await service.Database.ListAuditAsync(null, true, 1, cancel))[0].N;
        async Task<(long[] Rows, long? Next)> PageAsync(string query)
        {
            using JsonDocument page = JsonDocument.Parse(await admin.GetStringAsync("v1/admin/audit?" + query, cancel));
            long[] rows = [.. page.RootElement.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("n").GetInt64())];
            return (
                rows,
                page.RootElement.TryGetProperty("next", out JsonElement next) && next.ValueKind == JsonValueKind.Number ? next.GetInt64() : null
            );
        }

        (long[] first, long? cursor) = await PageAsync("order=desc&limit=2");
        Assert.Equal([newest, newest - 1], first);
        Assert.Equal(newest - 1, cursor);
        (long[] second, _) = await PageAsync($"order=desc&limit=2&after={cursor}");
        Assert.Equal([newest - 2, newest - 3], second);
        (long[] unknown, _) = await PageAsync("order=desc&limit=1&after=1000");
        Assert.True(unknown[0] >= newest);
        (long[] ascending, _) = await PageAsync("order=asc&limit=1");
        Assert.Equal([1L], ascending);

        foreach (string query in new[] { "order=up", "order=asc&order=desc" })
        {
            await TestService.AssertProblemAsync(
                await admin.GetAsync("v1/admin/audit?" + query, cancel),
                HttpStatusCode.BadRequest,
                "ELM_INVALID_ARGUMENT",
                "order must be asc or desc."
            );
        }
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
    /// - 200; 200, 200, 429 ELM_RATE_LIMITED with Retry-After naming the key; 200; 429 naming the address.
    /// </remarks>
    [Fact]
    public async Task RateLimits_PerSourceAndPerKey_Answer429()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync(s => s with { RateLimitPerIp = 5, RateLimitPerKey = 2 });
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.OK, (await test.Http(service).GetAsync("health", cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("v1/admin/api-keys", cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("v1/admin/api-keys", cancel)).StatusCode);
        HttpResponseMessage limited = await admin.GetAsync("v1/admin/api-keys", cancel);
        Assert.Equal("1", limited.Headers.RetryAfter?.ToString());
        await TestService.AssertProblemAsync(limited, HttpStatusCode.TooManyRequests, "ELM_RATE_LIMITED", "Too many requests with this API key.");

        test.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("v1/admin/api-keys", cancel)).StatusCode);
        HttpResponseMessage last = await test.Http(service).GetAsync("health", cancel);
        for (int i = 0; i < 6 && last.StatusCode != HttpStatusCode.TooManyRequests; i++)
        {
            last.Dispose();
            last = await test.Http(service).GetAsync("health", cancel);
        }

        await TestService.AssertProblemAsync(last, HttpStatusCode.TooManyRequests, "ELM_RATE_LIMITED", "Too many requests from this address.");
    }

    private static string Body(string name = "n", string permissions = "\"audit.read\"", string more = "") =>
        "{\"name\":" + JsonSerializer.Serialize(name) + ",\"permissions\":[" + permissions + "]" + more + "}";

    // A body without Content-Length: HttpClient sends it chunked, so only the read loop's own limit can stop it.
    private sealed class ChunkedContent : HttpContent
    {
        private readonly byte[] _bytes;

        public ChunkedContent(string text)
        {
            _bytes = System.Text.Encoding.UTF8.GetBytes(text);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
