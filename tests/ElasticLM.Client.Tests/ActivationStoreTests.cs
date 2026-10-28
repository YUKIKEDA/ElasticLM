using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Client.Tests;

/// <summary>
/// The kept activation file (design §12.6.2): what is written reads back, the retry list keeps the 16 newest, and every
/// other form is damaged.
/// </summary>
public sealed class ActivationStoreTests : IDisposable
{
    private const string Id = "0196f3a0-0000-7000-8000-000000000001";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "elm-activation-store-" + Guid.CreateVersion7().ToString("N"));

    public ActivationStoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>
    /// An activation written reads back the same; a request before the first activation keeps only its retry list; the
    /// retry list keeps the 16 newest.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty folder.
    ///
    /// Steps:
    /// - Read; write an activation with a license and two jti and read it; write a pending one (no ID, no license, seq 0)
    ///   and read it; write one with a retry list, a pending list, and an unkept hash and read it; add 17 jti one by one to
    ///   an empty activation; add one to the pending list.
    ///
    /// Expected:
    /// - Null and not damaged; the same values; the same values; both lists and the hash; the 16 last jti in order; the
    ///   pending one.
    /// </remarks>
    [Fact]
    public void WriteRead_RoundTrips()
    {
        var store = new ActivationStore(_folder, new FakeTimeProvider());
        Assert.Null(store.Read(out bool damaged));
        Assert.False(damaged);

        store.Write(new StoredActivation(Id, 3, "standard", Encoding.UTF8.GetBytes("""{"format":"elasticlm-license"}"""), ["a", "b"]));
        StoredActivation read = store.Read(out _)!;
        Assert.Equal(
            (Id, 3L, """{"format":"elasticlm-license"}""", "a b"),
            (read.ActivationId, read.Seq, Encoding.UTF8.GetString(read.License!), string.Join(' ', read.Retry))
        );

        store.Write(new StoredActivation(null, 0, null, null, ["p"]));
        read = store.Read(out _)!;
        Assert.Equal(((string?)null, 0L, true, "p"), (read.ActivationId, read.Seq, read.License is null, string.Join(' ', read.Retry)));

        store.Write(new StoredActivation(null, 0, null, null, ["r"]) { Pending = ["p1", "p2"], Unkept = new string('A', 43) });
        read = store.Read(out _)!;
        Assert.Equal(("r", "p1 p2", new string('A', 43)), (string.Join(' ', read.Retry), string.Join(' ', read.Pending), read.Unkept));

        StoredActivation growing = new(null, 0, null, null, []);
        for (int i = 0; i < 17; i++)
        {
            growing = growing.WithRetry("j" + i);
        }

        Assert.Equal(Enumerable.Range(1, 16).Select(i => "j" + i), growing.Retry);
        Assert.Equal(["p"], new StoredActivation(null, 0, null, null, []).WithRetry("p", pending: true).Pending);
    }

    /// <summary>
    /// Every form the writer does not make is damaged, at each limit: the retry list's 16 entries and 64 characters, the ID's
    /// form, the ID and the license together, a negative sequence number.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Files written in the test.
    ///
    /// Steps:
    /// - Read 16 jti of 64 characters; then 17 jti; a jti of 65 characters; an empty jti; an ID that is not a UUID; an ID
    ///   without a license; a license without an ID; seq -1; a missing member; a kind not in the three; an ID and license
    ///   without a kind; a pending list of 17 and one with an empty jti; an unkept hash of 42 and 44 characters and a
    ///   number; an array; a member twice.
    ///
    /// Expected:
    /// - The first reads; every other is null and damaged.
    /// </remarks>
    [Fact]
    public void Read_EachOtherForm_IsDamaged()
    {
        var store = new ActivationStore(_folder, new FakeTimeProvider());
        string path = Path.Combine(_folder, "activation.json");
        string Retry(int count, int length) => "[" + string.Join(',', Enumerable.Repeat($"\"{new string('j', length)}\"", count)) + "]";
        bool Damaged(string json)
        {
            File.WriteAllText(path, json);
            return store.Read(out bool damaged) is null && damaged;
        }

        File.WriteAllText(path, $$"""{"activationId":null,"seq":0,"kind":null,"license":null,"retry":{{Retry(16, 64)}}}""");
        Assert.Equal(16, store.Read(out _)!.Retry.Count);
        Assert.True(Damaged($$"""{"activationId":null,"seq":0,"kind":null,"license":null,"retry":{{Retry(17, 1)}}}"""));
        Assert.True(Damaged($$"""{"activationId":null,"seq":0,"kind":null,"license":null,"retry":{{Retry(1, 65)}}}"""));
        Assert.True(Damaged("""{"activationId":null,"seq":0,"kind":null,"license":null,"retry":[""]}"""));
        Assert.True(Damaged("""{"activationId":"x","seq":1,"kind":"standard","license":{},"retry":[]}"""));
        Assert.True(Damaged($$"""{"activationId":"{{Id}}","seq":1,"kind":"standard","license":null,"retry":[]}"""));
        Assert.True(Damaged("""{"activationId":null,"seq":1,"kind":null,"license":{},"retry":[]}"""));
        Assert.True(Damaged("""{"activationId":null,"seq":-1,"kind":null,"license":null,"retry":[]}"""));
        Assert.True(Damaged("""{"activationId":null,"seq":0,"kind":null,"license":null}"""));
        Assert.True(Damaged($$"""{"activationId":"{{Id}}","seq":1,"kind":"other","license":{},"retry":[]}"""));
        Assert.True(Damaged($$"""{"activationId":"{{Id}}","seq":1,"kind":null,"license":{},"retry":[]}"""));
        Assert.True(Damaged($$"""{"activationId":null,"seq":0,"kind":null,"license":null,"retry":[],"pending":{{Retry(17, 1)}}}"""));
        Assert.True(Damaged("""{"activationId":null,"seq":0,"kind":null,"license":null,"retry":[],"pending":[""]}"""));
        Assert.True(
            Damaged("""{"activationId":null,"seq":0,"kind":null,"license":null,"retry":[],"unkept":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""")
        );
        Assert.True(
            Damaged("""{"activationId":null,"seq":0,"kind":null,"license":null,"retry":[],"unkept":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""")
        );
        Assert.True(Damaged("""{"activationId":null,"seq":0,"kind":null,"license":null,"retry":[],"unkept":1}"""));
        Assert.True(Damaged("[]"));
        Assert.True(Damaged("""{"activationId":null,"seq":0,"seq":0,"kind":null,"license":null,"retry":[]}"""));
    }

    /// <summary>
    /// A waiting offline request and a deactivation proof read back the same (design §12.8.5), at the limits of their
    /// members.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty folder.
    ///
    /// Steps:
    /// - Write an activation request with 16 retry jti and 8 fingerprints and a proof of 16 KiB; read; write a refresh request;
    ///   read.
    ///
    /// Expected:
    /// - Every member of each request and the proof the same.
    /// </remarks>
    [Fact]
    public void WriteRead_OfflineRequestAndProof_RoundTrip()
    {
        var store = new ActivationStore(_folder, new FakeTimeProvider());
        string[] retry = [.. Enumerable.Range(0, 16).Select(i => "j" + i)];
        string[] prints = [.. Enumerable.Range(0, 8).Select(i => new string((char)('0' + i), 32))];
        var activate = new OfflineRequest(false, new string('n', 22), 10, 20, 0, null, new string('k', 43), retry, prints);
        string proof = new('p', 16 * 1024);
        store.Write(new StoredActivation(null, 0, null, null, []) { Offline = activate, DeactivationProof = proof });
        StoredActivation read = store.Read(out _)!;
        Assert.Equal(
            (false, activate.Nonce, 10L, 20L, 0L, (string?)null, activate.KeyHash, proof),
            (
                read.Offline!.Refresh,
                read.Offline.Nonce,
                read.Offline.IssuedAt,
                read.Offline.ExpiresAt,
                read.Offline.Seq,
                read.Offline.ActivationId,
                read.Offline.KeyHash,
                read.DeactivationProof
            )
        );
        Assert.Equal(retry, read.Offline.Retry);
        Assert.Equal(prints, read.Offline.Fingerprints);

        var refresh = new OfflineRequest(true, new string('m', 22), 1, 2, 5, Id, null, ["r"], []);
        store.Write(new StoredActivation(Id, 5, "standard", Encoding.UTF8.GetBytes("{}"), []) { Offline = refresh });
        read = store.Read(out _)!;
        Assert.Equal(
            (true, 5L, Id, (string?)null, "r"),
            (read.Offline!.Refresh, read.Offline.Seq, read.Offline.ActivationId, read.Offline.KeyHash, read.Offline.Retry.Single())
        );
    }

    /// <summary>
    /// Each form of a waiting offline request or a deactivation proof that Write does not make is damaged (design §12.6.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Files written in the test.
    ///
    /// Steps:
    /// - Read a valid activation request; then one with each change: a nonce of 21 characters, another op, an act, no key, a
    ///   key of 42 characters, 17 retry jti, 9 fingerprints, an upper-case fingerprint, a negative exp, no fp; a refresh request
    ///   with a key, without an act, with an act that is not a UUID; a proof of 16 KiB and a character, a proof that is a number.
    ///
    /// Expected:
    /// - The first reads; every other is null and damaged.
    /// </remarks>
    [Fact]
    public void Read_EachOtherOfflineForm_IsDamaged()
    {
        var store = new ActivationStore(_folder, new FakeTimeProvider());
        string path = Path.Combine(_folder, "activation.json");
        string nonce = new('n', 22);
        string key = new('k', 43);
        string print = new('a', 32);
        string Offline(string members) =>
            "{\"activationId\":null,\"seq\":0,\"kind\":null,\"license\":null,\"retry\":[],\"offline\":{" + members + "}}";
        string Activate(string change = "") =>
            Offline(
                $"\"op\":\"activate\",\"nonce\":\"{nonce}\",\"iat\":1,\"exp\":2,\"seq\":0,\"act\":null,\"key\":\"{key}\",\"retry\":[],\"fp\":[\"{print}\"]{change}"
            );
        bool Damaged(string json)
        {
            File.WriteAllText(path, json);
            return store.Read(out bool damaged) is null && damaged;
        }

        File.WriteAllText(path, Activate());
        Assert.NotNull(store.Read(out _)!.Offline);
        string Replace(string from, string to) => Activate().Replace(from, to, StringComparison.Ordinal);
        Assert.True(Damaged(Replace(nonce, new string('n', 21))));
        Assert.True(Damaged(Replace("\"activate\"", "\"other\"")));
        Assert.True(Damaged(Replace("\"act\":null", $"\"act\":\"{Id}\"")));
        Assert.True(Damaged(Replace($"\"key\":\"{key}\"", "\"key\":null")));
        Assert.True(Damaged(Replace(key, new string('k', 42))));
        Assert.True(Damaged(Replace("\"retry\":[]", "\"retry\":[" + string.Join(',', Enumerable.Repeat("\"j\"", 17)) + "]")));
        Assert.True(Damaged(Replace($"[\"{print}\"]", "[" + string.Join(',', Enumerable.Repeat($"\"{print}\"", 9)) + "]")));
        Assert.True(Damaged(Replace(print, new string('A', 32))));
        Assert.True(Damaged(Replace("\"exp\":2", "\"exp\":-1")));
        Assert.True(Damaged(Replace($",\"fp\":[\"{print}\"]", string.Empty)));
        string refresh = Replace("\"activate\"", "\"refresh\"").Replace("\"act\":null", $"\"act\":\"{Id}\"", StringComparison.Ordinal);
        Assert.True(Damaged(refresh));
        Assert.True(
            Damaged(
                refresh
                    .Replace($"\"key\":\"{key}\"", "\"key\":null", StringComparison.Ordinal)
                    .Replace($"\"act\":\"{Id}\"", "\"act\":null", StringComparison.Ordinal)
            )
        );
        Assert.True(
            Damaged(refresh.Replace($"\"key\":\"{key}\"", "\"key\":null", StringComparison.Ordinal).Replace(Id, "x", StringComparison.Ordinal))
        );
        Assert.True(
            Damaged($$"""{"activationId":null,"seq":0,"kind":null,"license":null,"retry":[],"deactivation":"{{new string('p', (16 * 1024) + 1)}}"}""")
        );
        Assert.True(Damaged("""{"activationId":null,"seq":0,"kind":null,"license":null,"retry":[],"deactivation":1}"""));
    }
}
