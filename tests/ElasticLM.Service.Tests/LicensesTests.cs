using System.Text;
using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The rules of a license body and of the computed state (design §9.4, §14.5, decision 0.85).
/// </summary>
public sealed class LicensesTests
{
    private const long Now = 1_800_000_000;

    private static readonly PolicyRow Standard = Policies.ToRow(Guid.CreateVersion7(), Guid.CreateVersion7(), "p", PolicySettings.Defaults, 0, 0);
    private static readonly PolicyRow Server = Standard with { LicenseKind = Policies.Server };
    private static readonly PolicyRow FirstActivation = Standard with { ExpirationBasis = Policies.FromFirstActivation };
    private static readonly HashSet<string> Codes = ["core", "pro"];

    /// <summary>
    /// The state resolves overlapping marks as revoked, then suspended, then expired, then active, and an expiry at the
    /// instant now has passed.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Rows with each combination of the revoked mark, the suspended mark, and an expiry before, at, after now, or none.
    ///
    /// Steps:
    /// - Compute the state of each.
    ///
    /// Expected:
    /// - Revoked whenever revoked; else suspended whenever suspended; else expired for an expiry before or at now; else
    ///   active.
    /// </remarks>
    [Fact]
    public void State_OverlappingMarks_ResolveInOrder()
    {
        LicenseRow row = new(Guid.CreateVersion7(), Guid.Empty, Guid.Empty, "n", [], null, null, null, null, "[]", "{}", 0, 0, 0);
        foreach (long? expires in new long?[] { null, Now - 1, Now, Now + 1 })
        {
            string free = expires is long e && e <= Now ? Licenses.Expired : Licenses.Active;
            Assert.Equal(free, Licenses.State(row with { ExpiresAt = expires }, Now));
            Assert.Equal(Licenses.Suspended, Licenses.State(row with { ExpiresAt = expires, SuspendedAt = 1 }, Now));
            Assert.Equal(Licenses.Revoked, Licenses.State(row with { ExpiresAt = expires, RevokedAt = 1 }, Now));
            Assert.Equal(Licenses.Revoked, Licenses.State(row with { ExpiresAt = expires, SuspendedAt = 1, RevokedAt = 1 }, Now));
        }
    }

    /// <summary>
    /// A license's features are checked at each rule: count 1 to 256, codes of the product once each, a version range of at
    /// most 64 characters, seats 1 to 1,000,000 for a server license only, and an expiry after now and not after the
    /// license's; each refusal has its own message.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A standard policy, a server policy, a policy whose expiry starts at the first activation; product codes core and
    ///   pro; a license expiry 10 days after now.
    ///
    /// Steps:
    /// - Read accepted lists (a version range padded to 64 characters, seats 1 and 1,000,000, a feature expiry equal to the
    ///   license's) and refused ones (none, 257, an unknown code, a code twice, a range of 65 characters, text that is not a
    ///   range, seats 0, 1,000,001, none on a server, seats on a standard license, an expiry at now, one after the license's,
    ///   any expiry before the first activation, a code that is a number).
    ///
    /// Expected:
    /// - The accepted lists read back; each refusal ELM_INVALID_ARGUMENT with its message, the number ELM_MALFORMED.
    /// </remarks>
    [Fact]
    public void Features_EachRule_IsCheckedExactly()
    {
        long expiry = Now + (10 * 86_400);
        string at = Iso(expiry);
        IReadOnlyList<LicenseFeature> Read(string json, PolicyRow? policy = null) =>
            Parse(json, (body, value) => Licenses.Features(body, value, policy ?? Standard, Codes, expiry, Now));
        (ErrorCode, string) Refused(string json, PolicyRow? policy = null) => Fails(() => Read(json, policy));

        string range = ">=1.0 <2.0".PadRight(64);
        Assert.Equal(range, Read($$"""[{"code":"core","ver":"{{range}}"}]""")[0].Ver);
        Assert.Equal([1L, 1_000_000L], Read("""[{"code":"core","seats":1},{"code":"pro","seats":1000000}]""", Server).Select(f => f.Seats!.Value));
        Assert.Equal(expiry, Read($$"""[{"code":"core","expiresAt":"{{at}}"}]""")[0].ExpiresAt);

        const string Count = "features must hold 1 to 256 features.";
        Assert.Equal((ErrorCode.InvalidArgument, Count), Refused("[]"));
        Assert.Equal((ErrorCode.InvalidArgument, Count), Refused("[" + string.Join(',', Enumerable.Repeat("""{"code":"core"}""", 257)) + "]"));
        Assert.Equal(
            (ErrorCode.InvalidArgument, "features[].code must name a feature of the license's product: 'other'."),
            Refused("""[{"code":"other"}]""")
        );
        Assert.Equal((ErrorCode.InvalidArgument, "features[].code 'core' is given twice."), Refused("""[{"code":"core"},{"code":"core"}]"""));
        const string Ver = "features[].ver must be a version range such as '>=2.0 <3.0', at most 64 characters.";
        Assert.Equal((ErrorCode.InvalidArgument, Ver), Refused($$"""[{"code":"core","ver":"{{range}} "}]"""));
        Assert.Equal((ErrorCode.InvalidArgument, Ver), Refused("""[{"code":"core","ver":"2.0"}]"""));
        const string Seats = "features[].seats must be between 1 and 1000000 for a server license.";
        Assert.Equal((ErrorCode.InvalidArgument, Seats), Refused("""[{"code":"core","seats":0}]""", Server));
        Assert.Equal((ErrorCode.InvalidArgument, Seats), Refused("""[{"code":"core","seats":1000001}]""", Server));
        Assert.Equal((ErrorCode.InvalidArgument, Seats), Refused("""[{"code":"core"}]""", Server));
        Assert.Equal((ErrorCode.InvalidArgument, "features[].seats is only for a server license."), Refused("""[{"code":"core","seats":1}]"""));
        const string Expiry = "features[].expiresAt must be later than now and no later than the license's expiry.";
        Assert.Equal((ErrorCode.InvalidArgument, Expiry), Refused($$"""[{"code":"core","expiresAt":"{{Iso(Now)}}"}]"""));
        Assert.Equal((ErrorCode.InvalidArgument, Expiry), Refused($$"""[{"code":"core","expiresAt":"{{Iso(expiry + 1)}}"}]"""));
        Assert.Equal(
            (ErrorCode.InvalidArgument, "features[].expiresAt cannot be set before the first activation."),
            Refused($$"""[{"code":"core","expiresAt":"{{at}}"}]""", FirstActivation)
        );
        Assert.Equal((ErrorCode.Malformed, Licenses.Malformed), Refused("""[{"code":5}]"""));
    }

    /// <summary>
    /// meta holds at most 32 string pairs, keys 1 to 64 bytes and values at most 1,024 bytes in UTF-8 (design §8.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Objects built in the test.
    ///
    /// Steps:
    /// - Read 32 entries with a key of 16 four-byte characters (64 bytes) and a value of 1,024 bytes; then 33 entries; a key of
    ///   65 bytes; an empty key; a value of 1,025 bytes; a value that is a number.
    ///
    /// Expected:
    /// - The first read back; ELM_INVALID_ARGUMENT with the meta rule four times; ELM_MALFORMED for the number.
    /// </remarks>
    [Fact]
    public void Meta_Limits_AreExact()
    {
        Dictionary<string, string> Read(Dictionary<string, object> meta) =>
            Parse(JsonSerializer.Serialize(meta), (body, value) => Licenses.Meta(body, value));
        Dictionary<string, object> Entries(int count) => Enumerable.Range(0, count).ToDictionary(i => "k" + i, object (_) => "v");
        const string Rule = "meta must hold at most 32 entries, each key 1 to 64 bytes and each value at most 1024 bytes in UTF-8.";

        Dictionary<string, object> full = Entries(31);
        full[string.Concat(Enumerable.Repeat("\U0001F600", 16))] = new string('v', 1_024);
        Assert.Equal(32, Read(full).Count);
        Assert.Equal((ErrorCode.InvalidArgument, Rule), Fails(() => Read(Entries(33))));
        Assert.Equal((ErrorCode.InvalidArgument, Rule), Fails(() => Read(new() { [new string('k', 65)] = "v" })));
        Assert.Equal((ErrorCode.InvalidArgument, Rule), Fails(() => Read(new() { [string.Empty] = "v" })));
        Assert.Equal((ErrorCode.InvalidArgument, Rule), Fails(() => Read(new() { ["k"] = new string('v', 1_025) })));
        Assert.Equal((ErrorCode.Malformed, Licenses.Malformed), Fails(() => Read(new() { ["k"] = 5 })));
    }

    private static string Iso(long seconds) =>
        DateTimeOffset
            .FromUnixTimeSeconds(seconds)
            .UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static T Parse<T>(string json, Func<RequestBody, JsonElement, T> read)
    {
        using RequestBody body = RequestBody.Parse(Encoding.UTF8.GetBytes("{\"x\":" + json + "}"), Licenses.Malformed);
        Assert.True(body.TryGet("x", out JsonElement value));
        return read(body, value);
    }

    private static (ErrorCode Code, string Message) Fails(Action read)
    {
        BodyException e = Assert.Throws<BodyException>(read);
        return (e.Code, e.Message);
    }
}
