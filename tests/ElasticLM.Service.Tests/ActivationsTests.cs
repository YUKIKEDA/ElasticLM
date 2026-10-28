using System.Text;
using ElasticLM.Licensing;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The rules of the device requests (design §8.6, §10.4, §12.3): the body limits and the sequence judgement.
/// </summary>
public sealed class ActivationsTests
{
    private const string Print = "0123456789abcdef0123456789abcdef";

    /// <summary>
    /// Each limit of design §8.6 on an activation's body holds at its edge and refuses one past it with ELM_MALFORMED; a
    /// fingerprint sent twice counts once.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Bodies built in the test.
    ///
    /// Steps:
    /// - Read: 8 fingerprints and 9; one of 31 and 33 characters, upper case, not hexadecimal; the same one twice; client
    ///   host and user of 255 bytes (85 three-byte characters) and 256; app of 64 bytes and 65; retry of 16 and 17 entries,
    ///   an entry of 64 characters, 65, and an empty one; seq 0 and -1; client that is not an object; no licenseKey; no product; a product that is not a code; no
    ///   fingerprints.
    ///
    /// Expected:
    /// - The edges are read back as sent (the twice-sent fingerprint once); every case past an edge is ELM_MALFORMED.
    /// </remarks>
    [Fact]
    public void ReadActivation_EachLimit_IsExact()
    {
        static ActivationRequest Read(string members) =>
            Activations.ReadActivation(RequestBody.Parse(Encoding.UTF8.GetBytes("{" + members + "}"), Activations.Malformed));
        static string Prints(int count, string print = Print) =>
            "\"fingerprints\":[" + string.Join(',', Enumerable.Repeat($"\"{print}\"", count)) + "]";
        static void Malformed(string members) => Assert.Equal(ErrorCode.Malformed, Assert.Throws<BodyException>(() => Read(members)).Code);
        string key = "\"licenseKey\":\"k\",\"product\":\"app\",";
        string[] eight = [.. Enumerable.Range(0, 8).Select(i => Print[..^1] + i)];

        Assert.Equal(8, Read(key + "\"fingerprints\":[" + string.Join(',', eight.Select(p => $"\"{p}\"")) + "]").Fingerprints.Count);
        Malformed(key + "\"fingerprints\":[" + string.Join(',', eight.Append(Print[..^1] + "9").Select(p => $"\"{p}\"")) + "]");
        Malformed(key + Prints(1, Print[..^1]));
        Malformed(key + Prints(1, Print + "0"));
        Malformed(key + Prints(1, Print.ToUpperInvariant()));
        Malformed(key + Prints(1, "g" + Print[1..]));
        Assert.Equal([Print], Read(key + Prints(2)).Fingerprints);

        string host = new('あ', 85);
        ActivationRequest full = Read(
            key
                + Prints(1)
                + $",\"client\":{{\"host\":\"{host}\",\"user\":\"{host}\"}},\"app\":\"{new string('a', 64)}\",\"seq\":0,\"retry\":["
                + string.Join(',', Enumerable.Repeat($"\"{new string('j', 64)}\"", 16))
                + "]"
        );
        Assert.Equal((host, host, 64, 0L, 16), (full.Client!.Host, full.Client.User, full.App!.Length, full.Seq!.Value, full.Retry.Count));
        Malformed(key + Prints(1) + $",\"client\":{{\"host\":\"{host}a\",\"user\":\"u\"}}");
        Malformed(key + Prints(1) + $",\"client\":{{\"host\":\"h\",\"user\":\"{host}a\"}}");
        Malformed(key + Prints(1) + ",\"client\":\"pc\"");
        Malformed(key + Prints(1) + $",\"app\":\"{new string('a', 65)}\"");
        Malformed(key + Prints(1) + ",\"retry\":[" + string.Join(',', Enumerable.Repeat("\"j\"", 17)) + "]");
        Malformed(key + Prints(1) + $",\"retry\":[\"{new string('j', 65)}\"]");
        Malformed(key + Prints(1) + ",\"retry\":[\"\"]");
        Malformed(key + Prints(1) + ",\"seq\":-1");
        Malformed(Prints(1));
        Malformed(key.TrimEnd(','));
        Malformed("\"licenseKey\":\"k\"," + Prints(1));
        Malformed("\"licenseKey\":\"k\",\"product\":\"App\"," + Prints(1));
    }

    /// <summary>
    /// A refresh and a deactivation need seq and may name retries (design §12.6.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Bodies built in the test.
    ///
    /// Steps:
    /// - Read {"seq":3,"retry":["a"]}; {"seq":3}; {"seq":3,"retry":null}; {} and {"seq":null}.
    ///
    /// Expected:
    /// - 3 with ["a"]; 3 with none twice; ELM_MALFORMED twice.
    /// </remarks>
    [Fact]
    public void ReadSequence_SeqIsRequired()
    {
        static SequenceRequest Read(string json) => Activations.ReadSequence(RequestBody.Parse(Encoding.UTF8.GetBytes(json), Activations.Malformed));

        SequenceRequest refresh = Read("{\"seq\":3,\"retry\":[\"a\"]}");
        Assert.Equal((3L, "a"), (refresh.Seq, refresh.Retry.Single()));
        Assert.Empty(Read("{\"seq\":3}").Retry);
        Assert.Empty(Read("{\"seq\":3,\"retry\":null}").Retry);
        Assert.Equal(ErrorCode.Malformed, Assert.Throws<BodyException>(() => Read("{}")).Code);
        Assert.Equal(ErrorCode.Malformed, Assert.Throws<BodyException>(() => Read("{\"seq\":null}")).Code);
    }

    /// <summary>
    /// The judgement of design §10.4 step 3 for an activation at sequence 5 whose last token came from request "p".
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An activation at seq 5, last proof "p"; the same, blocked.
    ///
    /// Steps:
    /// - Judge seq 5; 4 with retry [x, p]; 4 with [x]; 3 with [p]; 6; none with [p]; the blocked one at 5.
    ///
    /// Expected:
    /// - Current; Retry; Clone; Clone; Clone; Clone; Blocked.
    /// </remarks>
    [Fact]
    public void Judge_EachBranch()
    {
        var activation = new ActivationRow(Guid.Empty, Guid.Empty, "k", [], [], "[]", null, null, null, 5, [], "p", null, 0, 0);

        Assert.Equal(SequenceJudgement.Current, Activations.Judge(activation, 5, []));
        Assert.Equal(SequenceJudgement.Retry, Activations.Judge(activation, 4, ["x", "p"]));
        Assert.Equal(SequenceJudgement.Clone, Activations.Judge(activation, 4, ["x"]));
        Assert.Equal(SequenceJudgement.Clone, Activations.Judge(activation, 3, ["p"]));
        Assert.Equal(SequenceJudgement.Clone, Activations.Judge(activation, 6, []));
        Assert.Equal(SequenceJudgement.Clone, Activations.Judge(activation, null, ["p"]));
        Assert.Equal(SequenceJudgement.Blocked, Activations.Judge(activation with { BlockedAt = 1 }, 5, []));
    }
}
