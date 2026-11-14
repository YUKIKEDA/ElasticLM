using System.Net;
using System.Text.Json;

namespace ElasticLM.Measure.Tests;

/// <summary>
/// The lease load's records of requests whose answer broke off (design §20.7, V1d31, Issue #393): <see cref="Renewals"/>
/// and <see cref="BrokenRequests"/>.
/// </summary>
public sealed class ProgramTests
{
    /// <summary>
    /// A refusal that follows a renewal without a whole answer is counted apart, until a renewal of the lease succeeds.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A new <see cref="Renewals"/>.
    ///
    /// Steps:
    /// - Refuse, for one lease in turn: <c>HttpRequestException</c> on a sure lease, <c>ELM_CLONE_DETECTED</c> twice on the
    ///   unsure one, <c>HttpRequestException</c> again on it.
    /// - Refuse <c>ELM_NO_SEATS</c> on another, sure lease.
    ///
    /// Expected:
    /// - Each call returns whether the lease is unsure afterwards: true, true, true, true, false.
    /// - <c>renew</c> holds 2 failures as <c>HttpRequestException</c> and 1 as <c>ELM_NO_SEATS</c>; <c>renewAfterBroken</c>
    ///   holds 2 as <c>ELM_CLONE_DETECTED</c>.
    /// </remarks>
    [Fact]
    public void Renewals_RefusedAfterBrokenRenewal_CountedApart()
    {
        var renewals = new Renewals();

        bool[] unsure =
        [
            renewals.Refused(false, Device.NoAnswer),
            renewals.Refused(true, "ELM_CLONE_DETECTED"),
            renewals.Refused(true, "ELM_CLONE_DETECTED"),
            renewals.Refused(true, Device.NoAnswer),
            renewals.Refused(false, "ELM_NO_SEATS"),
        ];

        Assert.Equal([true, true, true, true, false], unsure);
        using JsonDocument written = Write(writer =>
        {
            renewals.Renewed.Write(writer, "renew");
            renewals.AfterBroken.Write(writer, "renewAfterBroken");
        });
        JsonElement renew = written.RootElement.GetProperty("renew");
        Assert.Equal(3, renew.GetProperty("failed").GetInt32());
        Assert.Equal(2, renew.GetProperty("failures").GetProperty(Device.NoAnswer).GetInt32());
        Assert.Equal(1, renew.GetProperty("failures").GetProperty("ELM_NO_SEATS").GetInt32());
        JsonElement after = written.RootElement.GetProperty("renewAfterBroken");
        Assert.Equal(2, after.GetProperty("failed").GetInt32());
        Assert.Equal(2, after.GetProperty("failures").GetProperty("ELM_CLONE_DETECTED").GetInt32());
    }

    /// <summary>Only the requests without a whole answer are kept, in the order they were sent, with what broke off.</summary>
    /// <remarks>
    /// Preconditions:
    /// - Answers: one without a whole answer sent at 2.0 s, a refusal at 1.0 s, a success at 1.2 s, and one without a whole
    ///   answer whose nonce fetch broke off at 1.5 s.
    ///
    /// Steps:
    /// - Add them in that order and write the array.
    ///
    /// Expected:
    /// - Two items, at 1.5 s then 2.0 s, each with its failed request as <c>path</c> and its error as <c>message</c>.
    /// </remarks>
    [Fact]
    public void BrokenRequests_Write_KeepsOnlyNoAnswerInOrder()
    {
        var broken = new BrokenRequests();
        broken.Add(2.0, new Answer(0, Device.NoAnswer, string.Empty, 3, null, "cut", "POST v1/leases/L4/renew"));
        broken.Add(1.0, new Answer(HttpStatusCode.Conflict, "ELM_CLONE_DETECTED", "{}", 1, null));
        broken.Add(1.2, new Answer(HttpStatusCode.OK, null, "{}", 1, null));
        broken.Add(1.5, new Answer(0, Device.NoAnswer, string.Empty, 0, null, "refused", "GET v1/nonce (before POST v1/leases)"));

        using JsonDocument written = Write(broken.Write);

        JsonElement[] items = [.. written.RootElement.GetProperty("broken").EnumerateArray()];
        Assert.Equal(2, items.Length);
        Assert.Equal(1.5, items[0].GetProperty("atS").GetDouble());
        Assert.Equal("GET v1/nonce (before POST v1/leases)", items[0].GetProperty("path").GetString());
        Assert.Equal("refused", items[0].GetProperty("message").GetString());
        Assert.Equal(2.0, items[1].GetProperty("atS").GetDouble());
        Assert.Equal("POST v1/leases/L4/renew", items[1].GetProperty("path").GetString());
        Assert.Equal("cut", items[1].GetProperty("message").GetString());
    }

    private static JsonDocument Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray());
    }
}
