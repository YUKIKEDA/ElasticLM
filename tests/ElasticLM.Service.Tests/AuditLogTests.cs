using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The audit log (design §18.2, decisions 0.70 and 0.77): every operation is recorded, rows are chained by SHA-256, the
/// chain check finds a changed, removed, or renumbered row and a moved head or anchor, and retention keeps the rest
/// checkable.
/// </summary>
public sealed class AuditLogTests
{
    /// <summary>Gets each one-column change of <see cref="Verify_OneColumnChanged_ReturnsFirstBroken"/> on each engine.</summary>
    public static TheoryData<string, string, long> TamperCases
    {
        get
        {
            (string Change, long FirstBroken)[] cases =
            [
                ("UPDATE audit_log SET prev = (SELECT hash FROM audit_log WHERE n = 1) WHERE n = 3", 3),
                ("UPDATE audit_log SET hash = prev WHERE n = 3", 3),
                ("UPDATE audit_log SET n = 9 WHERE n = 3", 3),
                ("UPDATE audit_state SET head_n = 6", 6),
                ("UPDATE audit_state SET head_hash = anchor_hash", 6),
                ("UPDATE audit_state SET anchor_n = 1", 1),
                ("UPDATE audit_state SET anchor_hash = head_hash", 1),
            ];
            var data = new TheoryData<string, string, long>();
            foreach (string provider in new[] { "sqlite", "postgres" })
            {
                foreach ((string change, long firstBroken) in cases)
                {
                    data.Add(provider, change, firstBroken);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// A row's hash can be computed from the design's description alone: ten columns in order, each a 4-byte big-endian length
    /// and its UTF-8 bytes, numbers in decimal, prev as its 32 bytes, NULL as the length 0xFFFFFFFF.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service after setup (row 1) and one key creation (row 2), on each engine.
    ///
    /// Steps:
    /// - Read the rows through the API; build each row's bytes here from §18.2, with ts from the ISO time and prev from
    ///   base64url; hash them.
    ///
    /// Expected:
    /// - Each hash equals the row's hash; row 1's prev is 32 zero bytes; row 2's prev is row 1's hash; row 1 is setup's
    ///   apikey.create by system/setup with null ip.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Hash_FromTheDesignText_Matches(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        await TestService.CreateKeyAsync(test.Http(service, owner), "\"audit.read\"");

        JsonElement[] rows = await TestService.AuditRowsAsync(test, service, owner);
        Assert.Equal(
            ("system", "setup", "apikey.create", JsonValueKind.Null),
            (
                rows[0].GetProperty("actorType").GetString(),
                rows[0].GetProperty("actorId").GetString(),
                rows[0].GetProperty("action").GetString(),
                rows[0].GetProperty("ip").ValueKind
            )
        );
        Assert.Equal(new byte[32], Base64Url.DecodeFromChars(rows[0].GetProperty("prev").GetString()));
        Assert.Equal(rows[0].GetProperty("hash").GetString(), rows[1].GetProperty("prev").GetString());
        foreach (JsonElement row in rows)
        {
            Assert.Equal(row.GetProperty("hash").GetString(), Base64Url.EncodeToString(SHA256.HashData(DesignBytes(row))));
        }
    }

    /// <summary>
    /// The chain check is intact on a written log and points at the first row that does not follow after a row is changed or
    /// removed.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service on the provider after setup and 4 key creations (rows 1 to 5; the verify calls add rows after them).
    ///
    /// Steps:
    /// - Verify; change row 3's detail in the database and verify; delete row 3 and verify.
    ///
    /// Expected:
    /// - Intact with firstBroken null; firstBroken 3; firstBroken 3 (row 4 now follows row 2).
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Verify_RowChangedOrRemoved_ReturnsFirstBroken(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        for (int i = 0; i < 4; i++)
        {
            await TestService.CreateKeyAsync(admin, "\"audit.read\"");
        }

        Assert.Equal((true, (long?)null), await VerifyAsync(admin));

        await test.ExecuteAsync("UPDATE audit_log SET detail = '{\"name\":\"other\"}' WHERE n = 3");
        Assert.Equal((false, (long?)3), await VerifyAsync(admin));

        await test.ExecuteAsync("DELETE FROM audit_log WHERE n = 3");
        Assert.Equal((false, (long?)3), await VerifyAsync(admin));
    }

    /// <summary>
    /// A removed last row is found by the head the state keeps.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service after setup and one key creation (rows 1 and 2), on each engine.
    ///
    /// Steps:
    /// - Delete row 2 in the database; verify.
    ///
    /// Expected:
    /// - Not intact; firstBroken 2.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Verify_LastRowRemoved_ReturnsTheHead(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        await TestService.CreateKeyAsync(test.Http(service, owner), "\"audit.read\"");

        await test.ExecuteAsync("DELETE FROM audit_log WHERE n = 2");
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        AuditCheck check = await database.VerifyAuditAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, check.FirstBroken);
    }

    /// <summary>
    /// Retention removes the oldest rows up to the first one inside the period and moves the anchor, so the rest still checks
    /// (design §18.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A database after setup (row 1 at day 0); two rows at day 10; one row at day 20, on each engine.
    ///
    /// Steps:
    /// - Remove rows older than day 15; verify; remove again.
    ///
    /// Expected:
    /// - 3 rows removed; intact with 1 row checked; 0 removed.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task RemoveOlderThan_OldRows_LeavesTheRestCheckable(string provider)
    {
        await using TestService test = TestService.Open(provider);
        await test.SetupAsync();
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        var entry = new AuditEntry("system", "test", "test.row", null, "ELM_OK", null, null);
        await database.AppendAuditAsync(entry, TestService.Start.AddDays(10), cancel);
        await database.AppendAuditAsync(entry, TestService.Start.AddDays(10), cancel);
        await database.AppendAuditAsync(entry, TestService.Start.AddDays(20), cancel);

        Assert.Equal(3, await database.RemoveAuditOlderThanAsync(TestService.Start.AddDays(15), cancel));
        Assert.Equal(new AuditCheck(1, null), await database.VerifyAuditAsync(cancel));
        Assert.Equal(0, await database.RemoveAuditOlderThanAsync(TestService.Start.AddDays(15), cancel));
    }

    /// <summary>
    /// Two writers at once do not fork the chain: each append locks the chain's state.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A database on the provider after setup.
    ///
    /// Steps:
    /// - Append 20 rows from 20 tasks at once; verify.
    ///
    /// Expected:
    /// - Intact, 21 rows checked.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Append_Concurrent_KeepsOneChain(string provider)
    {
        await using TestService test = TestService.Open(provider);
        await test.SetupAsync();
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        var entry = new AuditEntry("system", "test", "test.row", null, "ELM_OK", null, null);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => database.AppendAuditAsync(entry, TestService.Start, cancel), cancel)));

        Assert.Equal(new AuditCheck(21, null), await database.VerifyAuditAsync(cancel));
    }

    /// <summary>
    /// Each column the chain covers is checked on its own: a row whose prev, hash, or number alone changed, and a state whose
    /// head or anchor alone moved, are found (design §18.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A database after setup and 4 appends (rows 1 to 5), one per case, on each engine.
    ///
    /// Steps:
    /// - Run the case's one change; verify. The cases: row 3's prev set to row 1's hash; row 3's hash set to its prev; row 3
    ///   renumbered 9; the head's number moved to 6; the head's hash set to the anchor's; the anchor's number moved to 1; the
    ///   anchor's hash set to the head's (only the prev check sees this one: row 1 itself is unchanged).
    ///
    /// Expected:
    /// - Not intact, with the case's first broken row: 3, 3, 3, 6, 6, 1, 1.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TamperCases))]
    public async Task Verify_OneColumnChanged_ReturnsFirstBroken(string provider, string change, long firstBroken)
    {
        await using TestService test = TestService.Open(provider);
        await test.SetupAsync();
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        var entry = new AuditEntry("system", "test", "test.row", null, "ELM_OK", null, null);
        for (int i = 0; i < 4; i++)
        {
            await database.AppendAuditAsync(entry, TestService.Start, cancel);
        }

        Assert.Equal(new AuditCheck(5, null), await database.VerifyAuditAsync(cancel));
        await test.ExecuteAsync(change);
        AuditCheck check = await database.VerifyAuditAsync(cancel);
        Assert.Equal(firstBroken, check.FirstBroken);
    }

    /// <summary>
    /// Retention stops at the first row inside the period even when a later row is older, as after the clock moved back, so
    /// it never leaves a hole in the middle of the chain.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A database after setup (row 1 at day 0); rows at day 20, day 5 (the clock moved back), and day 25, on each engine.
    ///
    /// Steps:
    /// - Remove rows older than day 15; verify.
    ///
    /// Expected:
    /// - 1 row removed (row 1 only: row 2 is inside the period); intact with 3 rows checked.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task RemoveOlderThan_ClockMovedBack_StopsAtTheFirstNewRow(string provider)
    {
        await using TestService test = TestService.Open(provider);
        await test.SetupAsync();
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        var entry = new AuditEntry("system", "test", "test.row", null, "ELM_OK", null, null);
        foreach (int day in new[] { 20, 5, 25 })
        {
            await database.AppendAuditAsync(entry, TestService.Start.AddDays(day), cancel);
        }

        Assert.Equal(1, await database.RemoveAuditOlderThanAsync(TestService.Start.AddDays(15), cancel));
        Assert.Equal(new AuditCheck(3, null), await database.VerifyAuditAsync(cancel));
    }

    /// <summary>
    /// A node runs one check of the audit chain at a time: a call that comes while one runs is refused with 429
    /// ELM_RATE_LIMITED and audited, and once the check has ended, here with an exception, the next call runs (design §14.5,
    /// V1d26).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service after setup on PostgreSQL. The check reads audit_log, so it waits while another connection holds that
    ///   table's ACCESS EXCLUSIVE lock. The flag under test is the service's own and the same on both engines; SQLite lets the
    ///   check read beside any writer (WAL), so nothing holds a check there.
    ///
    /// Steps:
    /// - Lock audit_log; send check A and wait until its read waits on the lock; send check B and read its status and
    ///   headers; cancel A on the client and wait until its read is gone; send check C and wait until its read waits on the
    ///   lock; end the lock; read C and B; count the audit rows of audit.verify by result.
    ///
    /// Expected:
    /// - B is 429 with Retry-After 1 while A waits, and its body ELM_RATE_LIMITED "Another check of the audit log is running
    ///   on this node."; C runs (it waits on the lock rather than being refused) and is 200 and intact; rows ELM_RATE_LIMITED
    ///   for B, ELM_NETWORK_ERROR for A, and ELM_OK for C.
    /// </remarks>
    [Fact]
    public async Task Verify_WhileAnotherRuns_IsRefusedUntilItEnds()
    {
        await using TestService test = TestService.Open("postgres");
        string key = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, key);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await using var holder = new Npgsql.NpgsqlConnection(test.ConnectionString);
        await holder.OpenAsync(cancel);

        // The reads that the lock of this test holds back; tests on other schemas of the server do not count.
        string waitingReads =
            "SELECT COUNT(*) FROM pg_stat_activity WHERE query LIKE 'SELECT%' AND query LIKE '%audit_log%' "
            + $"AND {holder.ProcessID} = ANY(pg_blocking_pids(pid))";
        await using Npgsql.NpgsqlTransaction held = await holder.BeginTransactionAsync(cancel);
        await using (var lockTable = new Npgsql.NpgsqlCommand("LOCK TABLE audit_log IN ACCESS EXCLUSIVE MODE", holder, held))
        {
            await lockTable.ExecuteNonQueryAsync(cancel);
        }

        using var stopA = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Task<HttpResponseMessage> a = admin.PostAsync("v1/admin/audit/verify", null, stopA.Token);
        await TestService.WaitForAsync(async () => await test.ScalarAsync(waitingReads) == 1);

        using var requestB = new HttpRequestMessage(HttpMethod.Post, "v1/admin/audit/verify");
        using HttpResponseMessage b = await admin.SendAsync(requestB, HttpCompletionOption.ResponseHeadersRead, cancel);
        Assert.Equal((HttpStatusCode.TooManyRequests, "1", false), (b.StatusCode, b.Headers.RetryAfter?.ToString(), a.IsCompleted));

        await stopA.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        await TestService.WaitForAsync(async () => await test.ScalarAsync(waitingReads) == 0);
        Task<(bool Intact, long? FirstBroken)> c = VerifyAsync(admin);
        await TestService.WaitForAsync(async () => await test.ScalarAsync(waitingReads) == 1);
        await held.RollbackAsync(cancel);

        Assert.Equal((true, (long?)null), await c);
        await TestService.AssertProblemAsync(
            b,
            HttpStatusCode.TooManyRequests,
            "ELM_RATE_LIMITED",
            "Another check of the audit log is running on this node."
        );
        string Count(string result) => $"SELECT COUNT(*) FROM audit_log WHERE action = 'audit.verify' AND result = '{result}'";
        await TestService.WaitForAsync(async () => await test.ScalarAsync(Count("ELM_OK")) == 1);
        Assert.Equal(
            (1L, 1L, 1L),
            (
                await test.ScalarAsync(Count("ELM_RATE_LIMITED")),
                await test.ScalarAsync(Count("ELM_NETWORK_ERROR")),
                await test.ScalarAsync(Count("ELM_OK"))
            )
        );
    }

    private static async Task<(bool Intact, long? FirstBroken)> VerifyAsync(HttpClient admin)
    {
        using HttpResponseMessage response = await admin.PostAsync("v1/admin/audit/verify", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        JsonElement broken = answer.RootElement.GetProperty("firstBroken");
        return (answer.RootElement.GetProperty("intact").GetBoolean(), broken.ValueKind == JsonValueKind.Null ? null : broken.GetInt64());
    }

    // The bytes of design §18.2, written from its text: n, ts, actorType, actorId, action, target, result, ip, detail, prev.
    private static byte[] DesignBytes(JsonElement row)
    {
        var bytes = new List<byte>();
        void Field(byte[]? value)
        {
            byte[] length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, value is null ? 0xFFFFFFFF : (uint)value.Length);
            bytes.AddRange(length);
            bytes.AddRange(value ?? []);
        }

        byte[]? Text(string name) =>
            row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Encoding.UTF8.GetBytes(row.GetProperty(name).GetString()!);

        long ts = DateTimeOffset.Parse(row.GetProperty("ts").GetString()!, CultureInfo.InvariantCulture).ToUnixTimeSeconds();
        Field(Encoding.UTF8.GetBytes(row.GetProperty("n").GetInt64().ToString(CultureInfo.InvariantCulture)));
        Field(Encoding.UTF8.GetBytes(ts.ToString(CultureInfo.InvariantCulture)));
        foreach (string name in new[] { "actorType", "actorId", "action", "target", "result", "ip", "detail" })
        {
            Field(Text(name));
        }

        Field(Base64Url.DecodeFromChars(row.GetProperty("prev").GetString()));
        return [.. bytes];
    }
}
