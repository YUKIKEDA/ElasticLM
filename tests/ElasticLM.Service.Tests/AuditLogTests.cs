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
/// chain check finds a changed, removed, or reordered row, and retention keeps the rest checkable.
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
    public async Task RowHash_FollowsTheDesign()
    {
        await using TestService test = AdminApiTests.Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        await AdminApiTests.CreateKeyAsync(test.Http(service, owner), "\"audit.read\"");

        JsonElement[] rows = await AdminApiTests.AuditRowsAsync(test, service, owner);
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
    [MemberData(nameof(AdminApiTests.Providers), MemberType = typeof(AdminApiTests))]
    public async Task Verify_FindsTheFirstBrokenRow(string provider)
    {
        await using TestService test = AdminApiTests.Open(provider);
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        HttpClient admin = test.Http(service, owner);
        for (int i = 0; i < 4; i++)
        {
            await AdminApiTests.CreateKeyAsync(admin, "\"audit.read\"");
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
    public async Task Verify_FindsACutHead()
    {
        await using TestService test = AdminApiTests.Open("sqlite");
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync();
        await AdminApiTests.CreateKeyAsync(test.Http(service, owner), "\"audit.read\"");

        await test.ExecuteAsync("DELETE FROM audit_log WHERE n = 2");
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        AuditCheck check = await AuditLog.VerifyAsync(database, TestContext.Current.CancellationToken);
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
    public async Task Retention_KeepsTheRestCheckable()
    {
        await using TestService test = AdminApiTests.Open("sqlite");
        await test.SetupAsync();
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        var entry = new AuditEntry("system", "test", "test.row", null, "ELM_OK", null, null);
        await AuditLog.AppendAsync(database, entry, TestService.Start.AddDays(10), cancel);
        await AuditLog.AppendAsync(database, entry, TestService.Start.AddDays(10), cancel);
        await AuditLog.AppendAsync(database, entry, TestService.Start.AddDays(20), cancel);

        Assert.Equal(3, await AuditLog.RemoveOlderThanAsync(database, TestService.Start.AddDays(15), cancel));
        Assert.Equal(new AuditCheck(1, null), await AuditLog.VerifyAsync(database, cancel));
        Assert.Equal(0, await AuditLog.RemoveOlderThanAsync(database, TestService.Start.AddDays(15), cancel));
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
    [MemberData(nameof(AdminApiTests.Providers), MemberType = typeof(AdminApiTests))]
    public async Task ConcurrentAppends_KeepOneChain(string provider)
    {
        await using TestService test = AdminApiTests.Open(provider);
        await test.SetupAsync();
        await using Database database = Database.Create(test.Provider, test.ConnectionString);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        var entry = new AuditEntry("system", "test", "test.row", null, "ELM_OK", null, null);

        await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => Task.Run(() => AuditLog.AppendAsync(database, entry, TestService.Start, cancel), cancel))
        );

        Assert.Equal(new AuditCheck(21, null), await AuditLog.VerifyAsync(database, cancel));
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
