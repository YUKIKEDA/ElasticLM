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
}
