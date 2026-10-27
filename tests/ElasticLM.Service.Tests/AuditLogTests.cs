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
    /// <summary>
    /// A row's hash can be computed from the design's description alone: ten columns in order, each a 4-byte big-endian length
    /// and its UTF-8 bytes, numbers in decimal, prev as its 32 bytes, NULL as the length 0xFFFFFFFF.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A service after setup (row 1) and one key creation (row 2).
    ///
    /// Steps:
    /// - Read the rows through the API; build each row's bytes here from §18.2, with ts from the ISO time and prev from
    ///   base64url; hash them.
    ///
    /// Expected:
    /// - Each hash equals the row's hash; row 1's prev is 32 zero bytes; row 2's prev is row 1's hash; row 1 is setup's
    ///   apikey.create by system/setup with null ip.
    /// </remarks>
    [Fact]
    public async Task Hash_FromTheDesignText_Matches()
    {
        await using TestService test = TestService.Open("sqlite");
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
    /// - A service after setup and one key creation (rows 1 and 2).
    ///
    /// Steps:
    /// - Delete row 2 in the database; verify.
    ///
    /// Expected:
    /// - Not intact; firstBroken 2.
    /// </remarks>
    [Fact]
    public async Task Verify_LastRowRemoved_ReturnsTheHead()
    {
        await using TestService test = TestService.Open("sqlite");
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
    /// - A database after setup (row 1 at day 0); two rows at day 10; one row at day 20.
    ///
    /// Steps:
    /// - Remove rows older than day 15; verify; remove again.
    ///
    /// Expected:
    /// - 3 rows removed; intact with 1 row checked; 0 removed.
    /// </remarks>
    [Fact]
    public async Task RemoveOlderThan_OldRows_LeavesTheRestCheckable()
    {
        await using TestService test = TestService.Open("sqlite");
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
    /// - A database after setup and 4 appends (rows 1 to 5), one per case.
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
    [InlineData("UPDATE audit_log SET prev = (SELECT hash FROM audit_log WHERE n = 1) WHERE n = 3", 3)]
    [InlineData("UPDATE audit_log SET hash = prev WHERE n = 3", 3)]
    [InlineData("UPDATE audit_log SET n = 9 WHERE n = 3", 3)]
    [InlineData("UPDATE audit_state SET head_n = 6", 6)]
    [InlineData("UPDATE audit_state SET head_hash = anchor_hash", 6)]
    [InlineData("UPDATE audit_state SET anchor_n = 1", 1)]
    [InlineData("UPDATE audit_state SET anchor_hash = head_hash", 1)]
    public async Task Verify_OneColumnChanged_ReturnsFirstBroken(string change, long firstBroken)
    {
        await using TestService test = TestService.Open("sqlite");
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
    /// - A database after setup (row 1 at day 0); rows at day 20, day 5 (the clock moved back), and day 25.
    ///
    /// Steps:
    /// - Remove rows older than day 15; verify.
    ///
    /// Expected:
    /// - 1 row removed (row 1 only: row 2 is inside the period); intact with 3 rows checked.
    /// </remarks>
    [Fact]
    public async Task RemoveOlderThan_ClockMovedBack_StopsAtTheFirstNewRow()
    {
        await using TestService test = TestService.Open("sqlite");
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
