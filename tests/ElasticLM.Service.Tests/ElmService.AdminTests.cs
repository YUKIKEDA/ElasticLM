using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ElasticLM.Licensing;
using ElasticLM.Licensing.Tests;
using Microsoft.Data.Sqlite;

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
    /// after above the head reads from the newest, after 0 gives no row, and order takes only asc or desc, once.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service with an owner key from setup and five audit rows written by reading the key list; each engine.
    ///
    /// Steps:
    /// - List with order desc and limit 2; then with its next; then with after 1000; then with after 0; then with order asc
    ///   and limit 1; then with order up, and with order given twice.
    ///
    /// Expected:
    /// - Two rows in falling n, the first the newest row, next the second's n; then two rows below that n; then the newest
    ///   row first again; then no row and no next; then row 1; 400 ELM_INVALID_ARGUMENT "order must be asc or desc." twice.
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
        (long[] none, long? noNext) = await PageAsync("order=desc&after=0");
        Assert.Empty(none);
        Assert.Null(noNext);
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

    /// <summary>
    /// A refusal whose answer cannot be written because the caller is gone is still audited (design §18.2, V1d9): the row
    /// has the result ELM_NETWORK_ERROR and the refusal's detail.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service on a clock that can hold one read of its timestamp; an owner key and a key with apikey.read alone.
    ///
    /// Steps:
    /// - Hold the second read of the timestamp from here on: the first is the limit per source, and the second the limit
    ///   per key, which comes after authentication and before the permission check.
    /// - On a socket of its own, GET /v1/admin/audit with the narrow key. Once the service waits in the held read, end the
    ///   caller's side and read until the service has closed the connection; let the held read go on; wait for one more
    ///   audit.list row.
    /// - Run these steps 50 times. Kestrel cancels the request's token on the thread pool after it has closed the
    ///   connection, and until then it drops the answer's write without an exception: in 1 run of 15 on Windows 11 the
    ///   write came first. The service asks the response's writer whether the connection is closed; a service that only
    ///   waits for an exception leaves an ELM_FORBIDDEN row in such a run.
    /// - Check the chain with POST /v1/admin/audit/verify.
    ///
    /// Expected:
    /// - Each run adds exactly one audit.list row, with the result ELM_NETWORK_ERROR, the narrow key as its actor, and the
    ///   detail {"permission":"audit.read"}. The chain is intact.
    /// </remarks>
    [Fact]
    public async Task Refusal_CallerGoneBeforeItsAnswer_IsAuditedAsNetworkError()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using var clock = new HeldClock(test.Time);
        await using ElmService service = await ElmService.StartAsync(
            test.Settings(),
            new ServiceHostOptions
            {
                Time = clock,
                Environment = test.HostOptions().Environment,
                MinimumLogLevel = Microsoft.Extensions.Logging.LogLevel.Critical,
            },
            cancel
        );
        HttpClient admin = test.Http(service, owner);
        string narrow = await TestService.CreateKeyAsync(admin, "\"apikey.read\"");
        string request = $"GET /v1/admin/audit HTTP/1.1\r\nHost: {service.Url.Authority}\r\nAuthorization: Bearer {narrow}\r\n\r\n";

        string? narrowId = (await RowsAsync(service, "apikey.create"))[^1].Target;
        for (int run = 0; run < 50; run++)
        {
            Task held = clock.HoldAsync(2);
            using Socket caller = await SendAsync(service, request);
            await held.WaitAsync(TimeSpan.FromSeconds(10), cancel);
            await LeaveAsync(caller);
            clock.Release();
            await TestService.WaitForAsync(async () => (await RowsAsync(service, "audit.list")).Length > run);
            AuditEntry[] rows = await RowsAsync(service, "audit.list");
            Assert.Equal(run + 1, rows.Length);
            Assert.Equal(
                ("ELM_NETWORK_ERROR", "apikey", narrowId, "{\"permission\":\"audit.read\"}", null),
                (rows[^1].Result, rows[^1].ActorType, rows[^1].ActorId, rows[^1].Detail, rows[^1].Target)
            );
        }

        await TestService.AssertAuditIntactAsync(admin);
    }

    /// <summary>
    /// A state change whose answer cannot be written because the caller left after the commit keeps its one audit row,
    /// the transaction's (design §18.2, V1d9).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service on SQLite and an owner key. The database file is put into rollback-journal mode, in which a writer's
    ///   COMMIT waits for every reader: this lets the test hold a commit open, as a slow database does.
    /// - A reader of the test holds its read lock.
    ///
    /// Steps:
    /// - On a socket of its own, POST /v1/admin/products. Wait until SQLite refuses a new reader, which it does from the
    ///   moment the writer waits to commit; end the caller's side and read until the service has closed the connection;
    ///   end the test's reader; wait until the product is stored; stop the service, which waits for the request; read the
    ///   rows and check the chain.
    ///
    /// Expected:
    /// - The product is stored, and exactly one product.create row exists: result ELM_OK, target an ID. No row has the
    ///   result ELM_NETWORK_ERROR. The chain is intact.
    /// </remarks>
    [Fact]
    public async Task StateChange_CallerGoneAfterItsCommit_LeavesOneRow()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;

        await using SqliteConnection reader = await OpenCommitHolderAsync(test, cancel);
        const string Product = """{"code":"p1","name":"P1"}""";
        string request =
            $"POST /v1/admin/products HTTP/1.1\r\nHost: {service.Url.Authority}\r\nAuthorization: Bearer {owner}\r\n"
            + $"Content-Type: application/json\r\nContent-Length: {Product.Length}\r\n\r\n{Product}";
        await SendAndLeaveInCommitAsync(test, service, reader, request);
        await TestService.WaitForAsync(async () => await test.ScalarAsync("SELECT COUNT(*) FROM products WHERE code = 'p1'") == 1);
        await service.StopAsync();

        AuditEntry row = Assert.Single(await RowsAsync(service, "product.create"));
        Assert.Equal("ELM_OK", row.Result);
        Assert.True(Guid.TryParseExact(row.Target, "D", out _), "The row's target is the product's ID.");
        Assert.DoesNotContain(await RowsAsync(service, null), r => r.Result == "ELM_NETWORK_ERROR");
        Assert.Null((await service.Database.VerifyAuditAsync(cancel)).FirstBroken);
    }

    /// <summary>
    /// The row of a license file whose answer cannot be written because the caller is gone names the license and the
    /// token that was signed (design §18.2, V1d9).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service on a clock that can hold one read of the time; an owner key; a license of an unbound policy, without
    ///   expiry.
    ///
    /// Steps:
    /// - Hold the third read of the time from here on: the first is authentication's, the second the license's state, and
    ///   the third is made while the file is issued, after the operation's last read of the database.
    /// - On a socket of its own, POST /v1/admin/licenses/{id}/file. Once the service waits in the held read, end the
    ///   caller's side and read until the service has closed the connection; let the held read go on; wait for one more
    ///   license.file row.
    /// - Run these steps 10 times: the answer's write either throws on the canceled request or is dropped by Kestrel
    ///   without an exception, whichever comes first, and the row must be the same.
    /// - Check the chain with POST /v1/admin/audit/verify.
    ///
    /// Expected:
    /// - Each run adds exactly one license.file row, with the result ELM_NETWORK_ERROR, the license as its target, and a
    ///   detail with a jti and an exp of null. The chain is intact.
    /// </remarks>
    [Fact]
    public async Task LicenseFile_CallerGoneBeforeItsAnswer_RowNamesTheLicenseAndTheToken()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using var clock = new HeldClock(test.Time);
        await using ElmService service = await StartOnClockAsync(test, clock);
        HttpClient admin = test.Http(service, owner);
        string product = await ElmServiceActivationsTests.ProductAsync(admin, "app", "core");
        string policy = await TestService.PolicyAsync(admin, product, "\"binding\":\"unbound\"");
        (string license, _) = await ElmServiceActivationsTests.LicenseAsync(admin, policy);
        string request =
            $"POST /v1/admin/licenses/{license}/file HTTP/1.1\r\nHost: {service.Url.Authority}\r\nAuthorization: Bearer {owner}\r\n"
            + "Content-Length: 0\r\n\r\n";

        for (int run = 0; run < 10; run++)
        {
            Task held = clock.HoldTimeAsync(3);
            using Socket caller = await SendAsync(service, request);
            await held.WaitAsync(TimeSpan.FromSeconds(10), cancel);
            await LeaveAsync(caller);
            clock.Release();
            await TestService.WaitForAsync(async () => (await RowsAsync(service, "license.file")).Length > run);
            AuditEntry[] rows = await RowsAsync(service, "license.file");
            Assert.Equal(run + 1, rows.Length);
            Assert.Equal(("ELM_NETWORK_ERROR", license), (rows[^1].Result, rows[^1].Target));
            using JsonDocument detail = JsonDocument.Parse(rows[^1].Detail!);
            Assert.Equal(["jti", "exp"], detail.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.NotEqual(string.Empty, detail.RootElement.GetProperty("jti").GetString());
            Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("exp").ValueKind);
        }

        await TestService.AssertAuditIntactAsync(admin);
    }

    /// <summary>
    /// The row of an offline file whose answer cannot be written because the caller left after the activation was stored
    /// names the activation and the operation; the activation's own row stands beside it (design §12.8.4, §18.2, V1d9).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service on SQLite, in rollback-journal mode so that the test's reader can hold a commit; an owner key; a
    ///   node-locked license; a device's offline activation request for each run.
    ///
    /// Steps:
    /// - On a socket of its own, POST /v1/admin/offline with the request. While the service waits to commit the
    ///   activation, end the caller's side and read until the service has closed the connection; end the reader; wait for
    ///   one more activation.offline row.
    /// - Run these steps 5 times, each with a new device: the answer's write either throws on the canceled request or is
    ///   dropped by Kestrel without an exception, whichever comes first, and the row must be the same.
    /// - Stop the service; check the chain.
    ///
    /// Expected:
    /// - Each run adds one activation.activate row with ELM_OK and one activation.offline row with the result
    ///   ELM_NETWORK_ERROR, whose target is that activation and whose detail is
    ///   {"operation":"activate","channel":"offline"}. The chain is intact.
    /// </remarks>
    [Fact]
    public async Task Offline_CallerGoneAfterTheActivationIsStored_RowNamesTheActivation()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;
        HttpClient admin = test.Http(service, owner);
        string product = await ElmServiceActivationsTests.ProductAsync(admin, "app", "core");
        (_, string key) = await ElmServiceActivationsTests.LicenseAsync(
            admin,
            await TestService.PolicyAsync(admin, product, "\"maxActivations\":20")
        );
        await using SqliteConnection reader = await OpenCommitHolderAsync(test, cancel);

        for (int run = 0; run < 5; run++)
        {
            using var device = TestDevice.Create(test, service);
            string file = device.ActivationFile(key, TestDevice.Fingerprints(2)) + "\n";
            string request =
                $"POST /v1/admin/offline HTTP/1.1\r\nHost: {service.Url.Authority}\r\nAuthorization: Bearer {owner}\r\n"
                + $"Content-Type: text/plain\r\nContent-Length: {file.Length}\r\n\r\n{file}";
            await SendAndLeaveInCommitAsync(test, service, reader, request);
            await TestService.WaitForAsync(async () => (await RowsAsync(service, "activation.offline")).Length > run);
            AuditEntry[] offline = await RowsAsync(service, "activation.offline");
            AuditEntry[] activated = await RowsAsync(service, "activation.activate");
            Assert.Equal((run + 1, run + 1), (offline.Length, activated.Length));
            AuditEntry last = offline[^1];
            Assert.Equal("ELM_OK", activated[^1].Result);
            Assert.Equal(
                ("ELM_NETWORK_ERROR", activated[^1].Target, "{\"operation\":\"activate\",\"channel\":\"offline\"}"),
                (last.Result, last.Target, last.Detail)
            );
            Assert.NotNull(last.Target);
        }

        await service.StopAsync();
        Assert.Null((await service.Database.VerifyAuditAsync(cancel)).FirstBroken);
    }

    /// <summary>
    /// A read that ends with the database's exception before anything is stored is audited with the result
    /// ELM_INTERNAL_ERROR and answered 500 as before (design §14.5, §18.2, V1d9).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service and an owner key; the table of the products renamed, so that reading it fails as a database that goes
    ///   away does; each engine.
    ///
    /// Steps:
    /// - GET /v1/admin/products; give the table its name back; check the chain with POST /v1/admin/audit/verify; read the
    ///   rows.
    ///
    /// Expected:
    /// - 500 ELM_INTERNAL_ERROR "The database failed."; exactly one product.list row: result ELM_INTERNAL_ERROR, no target,
    ///   no detail. The chain is intact.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Read_DatabaseFails_IsAuditedAsInternalErrorAndAnswered500(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);

        await test.ExecuteAsync("ALTER TABLE products RENAME TO products_gone");
        await TestService.AssertProblemAsync(
            await admin.GetAsync("v1/admin/products", TestContext.Current.CancellationToken),
            HttpStatusCode.InternalServerError,
            "ELM_INTERNAL_ERROR",
            "The database failed."
        );
        await test.ExecuteAsync("ALTER TABLE products_gone RENAME TO products");

        await TestService.AssertAuditIntactAsync(admin);
        AuditEntry row = Assert.Single(await RowsAsync(service, "product.list"));
        Assert.Equal(("ELM_INTERNAL_ERROR", null, null), (row.Result, row.Target, row.Detail));
    }

    /// <summary>
    /// GET /v1/admin/me, whose success writes no row, writes an admin.me row when it ends with an exception (design
    /// §14.5, V1d9).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service and an owner key; the column subject of the administrators renamed. Authentication does not read it;
    ///   the operation's read of the caller's administrator does, and fails.
    ///
    /// Steps:
    /// - GET /v1/admin/me; give the column its name back; GET /v1/admin/me again; read the rows.
    ///
    /// Expected:
    /// - 500 ELM_INTERNAL_ERROR "The database failed.", then 200; exactly one admin.me row, with the result
    ///   ELM_INTERNAL_ERROR.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Me_EndsWithAnException_WritesItsRow(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        CancellationToken cancel = TestContext.Current.CancellationToken;

        await test.ExecuteAsync("ALTER TABLE admins RENAME COLUMN subject TO subject_gone");
        await TestService.AssertProblemAsync(
            await admin.GetAsync("v1/admin/me", cancel),
            HttpStatusCode.InternalServerError,
            "ELM_INTERNAL_ERROR",
            "The database failed."
        );
        await test.ExecuteAsync("ALTER TABLE admins RENAME COLUMN subject_gone TO subject");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("v1/admin/me", cancel)).StatusCode);

        AuditEntry row = Assert.Single(await RowsAsync(service, "admin.me"));
        Assert.Equal("ELM_INTERNAL_ERROR", row.Result);
    }

    // A service on the given clock, with the test's settings and environment.
    private static Task<ElmService> StartOnClockAsync(TestService test, TimeProvider clock) =>
        ElmService.StartAsync(
            test.Settings(),
            new ServiceHostOptions
            {
                Time = clock,
                Environment = test.HostOptions().Environment,
                MinimumLogLevel = Microsoft.Extensions.Logging.LogLevel.Critical,
            },
            TestContext.Current.CancellationToken
        );

    private static string Body(string name = "n", string permissions = "\"audit.read\"", string more = "") =>
        "{\"name\":" + JsonSerializer.Serialize(name) + ",\"permissions\":[" + permissions + "]" + more + "}";

    private static async Task<AuditEntry[]> RowsAsync(ElmService service, string? action)
    {
        IReadOnlyList<AuditRow> rows = await service.Database.ListAuditAsync(null, false, AuditLog.MaxLimit, TestContext.Current.CancellationToken);
        return [.. rows.Select(r => r.Entry).Where(e => action is null || e.Action == action)];
    }

    // Sends one request on a connection of its own and returns the socket, so that the test decides when the caller goes.
    private static async Task<Socket> SendAsync(ElmService service, string request)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, service.Url.Port, TestContext.Current.CancellationToken);
            await socket.SendAsync(Encoding.ASCII.GetBytes(request), TestContext.Current.CancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // The caller goes: it ends its side of the connection and reads until the service has closed it too, which Kestrel does
    // when it has taken the request as aborted. A connection that the service resets counts as closed.
    private static async Task LeaveAsync(Socket caller)
    {
        caller.Shutdown(SocketShutdown.Send);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        byte[] buffer = new byte[1_024];
        try
        {
            while (await caller.ReceiveAsync(buffer, limit.Token) > 0) { }
        }
        catch (SocketException) { }
    }

    private static async Task<SqliteConnection> OpenUnpooledAsync(TestService test, CancellationToken cancel)
    {
        var connection = new SqliteConnection(test.ConnectionString + ";Pooling=False");
        await connection.OpenAsync(cancel);
        return connection;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancel)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancel);
    }

    // Puts the test's SQLite file into rollback-journal mode, in which a writer's COMMIT waits for every reader, and returns
    // a connection for the reader that holds commits. The mode changes only while no other connection is open; the service's
    // idle ones are in the pool, which is emptied before each try.
    private static async Task<SqliteConnection> OpenCommitHolderAsync(TestService test, CancellationToken cancel)
    {
        SqliteConnection reader = await OpenUnpooledAsync(test, cancel);
        await TestService.WaitForAsync(async () =>
        {
            SqliteConnection.ClearAllPools();
            return (string?)await ScalarAsync(reader, "PRAGMA journal_mode = DELETE", cancel) == "delete";
        });
        return reader;
    }

    // Sends a request whose transaction the reader keeps from committing, lets the caller go while the service waits to commit,
    // and ends the reader, so that the commit succeeds for a caller that is gone. SQLite refuses a new reader from the moment a
    // writer waits to commit; the probe asks SQLite itself, which answers at once, where a command of Microsoft.Data.Sqlite
    // would try again for a second. The service gives its COMMIT 5 seconds.
    private static async Task SendAndLeaveInCommitAsync(TestService test, ElmService service, SqliteConnection reader, string request)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await ScalarAsync(reader, "BEGIN", cancel);
        await ScalarAsync(reader, "SELECT COUNT(*) FROM audit_log", cancel);
        using Socket caller = await SendAsync(service, request);
        await using (SqliteConnection probe = await OpenUnpooledAsync(test, cancel))
        {
            const int Busy = 5;
            await TestService.WaitForAsync(() =>
                Task.FromResult(SQLitePCL.raw.sqlite3_exec(probe.Handle, "SELECT COUNT(*) FROM audit_state") == Busy)
            );
        }

        await LeaveAsync(caller);
        await ScalarAsync(reader, "ROLLBACK", cancel);
    }

    // The test's fake clock for a service, which can make one read of the timestamp or of the time wait until the test lets
    // it go on. The service reads the timestamp in its rate limits and the time in its operations, so a held read keeps a
    // request between two of its steps.
    private sealed class HeldClock(TimeProvider inner) : TimeProvider, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(true);
        private readonly Lock _lock = new();
        private TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _timestampsUntilHeld;
        private int _timesUntilHeld;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        // Holds the given read of the timestamp, counted from this call, and returns a task that ends once that read waits.
        public Task HoldAsync(int read) => HoldAsync(read, 0);

        // Holds the given read of the time, counted from this call, and returns a task that ends once that read waits.
        public Task HoldTimeAsync(int read) => HoldAsync(0, read);

        public void Release() => _release.Set();

        public override DateTimeOffset GetUtcNow()
        {
            Wait(ref _timesUntilHeld);
            return inner.GetUtcNow();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime, period);

        public override long GetTimestamp()
        {
            Wait(ref _timestampsUntilHeld);
            return inner.GetTimestamp();
        }

        public void Dispose() => _release.Dispose();

        private Task HoldAsync(int timestamp, int time)
        {
            lock (_lock)
            {
                _release.Reset();
                _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                (_timestampsUntilHeld, _timesUntilHeld) = (timestamp, time);
                return _held.Task;
            }
        }

        private void Wait(ref int readsUntilHeld)
        {
            bool hold;
            lock (_lock)
            {
                hold = readsUntilHeld > 0 && --readsUntilHeld == 0;
                if (hold)
                {
                    _held.SetResult();
                }
            }

            if (hold)
            {
                // The limit only ends a test that never lets the read go on.
                _release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None);
            }
        }
    }

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

    /// <summary>
    /// The error code reference gives, for each error code, the HTTP status the service answers it with (design §19, §20.7,
    /// V1c2b).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - docs/guide/reference/error-codes.md and docs/guide.ja/reference/error-codes.md under the repository root.
    ///
    /// Steps:
    /// - For every pair of <see cref="ElmService.ErrorStatuses"/>, find the table row whose cell is the code's wire name in a code
    ///   span, and read its cell in the column headed "HTTP status" or "HTTP の状態".
    ///
    /// Expected:
    /// - On both pages, that cell starts with the status as its first whole number, such as 409 for ELM_TRIAL_USED; the message
    ///   lists every pair that it does not.
    /// </remarks>
    [Fact]
    public void ErrorStatuses_AllPairs_AppearOnReferencePage()
    {
        IReadOnlyList<string> missing = ReferencePages.FindMissingStatuses(
            "error-codes.md",
            ElmService.ErrorStatuses.Select(pair => (ErrorCodes.ToWireName(pair.Key), pair.Value))
        );

        Assert.True(missing.Count == 0, ReferencePages.Message(missing));
    }
}
