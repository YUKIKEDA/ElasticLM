using System.Security.Cryptography;
using System.Text;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Client.Tests;

/// <summary>
/// Proofs built by <see cref="ProofBuilder"/> checked by the server's <see cref="ProofVerifier"/> (design §12.2).
/// </summary>
public sealed class ProofTests : IDisposable
{
    private static readonly Uri Target = new("https://licenses.example.com/v1/leases");
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"feature\":\"pro\",\"count\":1}");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "elm-proof-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private readonly InstallationKey _key;
    private readonly NonceService _nonces;
    private readonly MemoryReplayStore _replays;
    private readonly ProofVerifier _verifier;

    public ProofTests()
    {
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.User, _dir).TryLoadOrCreate(out InstallationKey? key, out _));
        _key = key!;
        _nonces = new NonceService(_secret, _time);
        _replays = new MemoryReplayStore(_time);
        _verifier = new ProofVerifier(_nonces, _replays);
    }

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static ECParameters OtherPublic(InstallationKey key) => key.PublicParameters();

    /// <summary>
    /// A first proof with the embedded key verifies and names the installation key; a later proof without the key verifies
    /// against the known key; a target that differs only in case, default port, and query matches.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An installation key and a fresh nonce.
    ///
    /// Steps:
    /// - Build with includePublicKey and verify without a known key; build without the key and verify with the known key,
    ///   the server naming the target as HTTPS://Licenses.Example.COM:443/v1/leases?x=1.
    ///
    /// Expected:
    /// - Both Valid, with the key's thumbprint as KeyId.
    /// </remarks>
    [Fact]
    public void Verify_FirstAndLaterProofs_AreValid()
    {
        string first = ProofBuilder.Create(_key, "POST", Target, _nonces.Issue(), Body, includePublicKey: true, _time);
        var valid = Assert.IsType<ProofResult.Valid>(_verifier.Verify(first, "POST", Target, Body));
        Assert.Equal(_key.Thumbprint, valid.KeyId);

        string later = ProofBuilder.Create(_key, "POST", Target, _nonces.Issue(), Body, includePublicKey: false, _time);
        ECParameters known = valid.Key;
        var serverForm = new Uri("HTTPS://Licenses.Example.COM:443/v1/leases?x=1");
        Assert.IsType<ProofResult.Valid>(_verifier.Verify(later, "POST", serverForm, Body, kid => kid == _key.Thumbprint ? known : null));
    }

    /// <summary>
    /// Each rule of design §12.2, broken alone, fails with its own error.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A valid first proof, changed in one way per case.
    ///
    /// Steps:
    /// - Verify: another method, another path, another body, a nonce 301 s old, a nonce from another secret, the same proof
    ///   twice, a later proof without key when the server does not know the key, no proof, a non-proof string, a signature
    ///   from another key.
    ///
    /// Expected:
    /// - The error code listed for each case.
    /// </remarks>
    [Theory]
    [InlineData("method", ErrorCode.ProofInvalid)]
    [InlineData("path", ErrorCode.ProofInvalid)]
    [InlineData("body", ErrorCode.ProofInvalid)]
    [InlineData("nonce-expired", ErrorCode.NonceRequired)]
    [InlineData("nonce-other-secret", ErrorCode.NonceRequired)]
    [InlineData("replay", ErrorCode.Replayed)]
    [InlineData("no-key", ErrorCode.ProofInvalid)]
    [InlineData("missing", ErrorCode.ProofInvalid)]
    [InlineData("garbage", ErrorCode.ProofInvalid)]
    [InlineData("other-signer", ErrorCode.ProofInvalid)]
    public void Verify_RuleBroken_FailsWithItsError(string kind, ErrorCode expected)
    {
        string nonce = kind == "nonce-other-secret" ? new NonceService(RandomNumberGenerator.GetBytes(32), _time).Issue() : _nonces.Issue();
        string? proof = ProofBuilder.Create(_key, "POST", Target, nonce, Body, includePublicKey: kind != "no-key", _time);
        string method = "POST";
        Uri target = Target;
        byte[] body = Body;
        switch (kind)
        {
            case "method":
                method = "DELETE";
                break;
            case "path":
                target = new Uri("https://licenses.example.com/v1/leases/x/renew");
                break;
            case "body":
                body = Encoding.UTF8.GetBytes("{\"feature\":\"pro\",\"count\":9}");
                break;
            case "nonce-expired":
                _time.Advance(TimeSpan.FromSeconds(301));
                break;
            case "replay":
                Assert.IsType<ProofResult.Valid>(_verifier.Verify(proof, method, target, body));
                break;
            case "missing":
                proof = null;
                break;
            case "garbage":
                proof = "not.a.proof";
                break;
            case "other-signer":
                string otherDir = _dir + "-other";
                Assert.True(new InstallationKeyStore("acme-cad", KeyScope.User, otherDir).TryLoadOrCreate(out InstallationKey? other, out _));
                using (other)
                {
                    string[] mine = proof.Split('.');
                    string[] theirs = ProofBuilder.Create(other!, "POST", Target, nonce, Body, includePublicKey: true, _time).Split('.');
                    proof = $"{mine[0]}.{mine[1]}.{theirs[2]}";
                }

                Directory.Delete(otherDir, recursive: true);
                break;
            default:
                break;
        }

        var invalid = Assert.IsType<ProofResult.Invalid>(_verifier.Verify(proof, method, target, body));
        Assert.Equal(expected, invalid.Error.Code);
    }

    /// <summary>
    /// Once a key is cached by its kid, a proof under that kid with another key's signature still fails; a known key whose
    /// thumbprint is not the kid is refused.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A verifier that has verified one proof of the installation key (its key is cached).
    ///
    /// Steps:
    /// - Verify a proof with this key's header and payload and another key's signature; verify a later proof whose known-key
    ///   lookup returns another key, on a fresh verifier.
    ///
    /// Expected:
    /// - ELM_PROOF_INVALID both times.
    /// </remarks>
    [Fact]
    public void Verify_CachedKidOrWrongKnownKey_StillChecksSignature()
    {
        Assert.IsType<ProofResult.Valid>(
            _verifier.Verify(ProofBuilder.Create(_key, "POST", Target, _nonces.Issue(), Body, includePublicKey: true, _time), "POST", Target, Body)
        );
        string otherDir = _dir + "-forger";
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.User, otherDir).TryLoadOrCreate(out InstallationKey? other, out _));
        string nonce = _nonces.Issue();
        string[] mine = ProofBuilder.Create(_key, "POST", Target, nonce, Body, includePublicKey: false, _time).Split('.');
        string[] theirs = ProofBuilder.Create(other!, "POST", Target, nonce, Body, includePublicKey: false, _time).Split('.');
        ECParameters otherKey = OtherPublic(other!);
        other!.Dispose();
        Directory.Delete(otherDir, recursive: true);

        var forged = Assert.IsType<ProofResult.Invalid>(_verifier.Verify($"{mine[0]}.{mine[1]}.{theirs[2]}", "POST", Target, Body));
        Assert.Equal(ErrorCode.ProofInvalid, forged.Error.Code);

        var fresh = new ProofVerifier(_nonces, new MemoryReplayStore(_time));
        string later = ProofBuilder.Create(_key, "POST", Target, _nonces.Issue(), Body, includePublicKey: false, _time);
        var wrongKnown = Assert.IsType<ProofResult.Invalid>(fresh.Verify(later, "POST", Target, Body, _ => otherKey));
        Assert.Equal(ErrorCode.ProofInvalid, wrongKnown.Error.Code);
    }

    /// <summary>
    /// A proof signed by key B under the kid of key A is refused even when the known-key lookup returns key B, so a caller
    /// cannot act as another installation by naming its kid.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Installation keys A (this test's key) and B.
    ///
    /// Steps:
    /// - Build a proof with A's kid in the header and sign it with B; verify with a lookup that returns B's public key.
    ///
    /// Expected:
    /// - ELM_PROOF_INVALID: the key's thumbprint is not the kid.
    /// </remarks>
    [Fact]
    public void Verify_KidOfAnotherKey_IsRefused()
    {
        string otherDir = _dir + "-b";
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.User, otherDir).TryLoadOrCreate(out InstallationKey? b, out _));
        using (b)
        {
            string[] parts = ProofBuilder.Create(_key, "POST", Target, _nonces.Issue(), Body, includePublicKey: false, _time).Split('.');
            string signingInput = parts[0] + "." + parts[1];
            string forged = signingInput + "." + System.Buffers.Text.Base64Url.EncodeToString(b!.Sign(Encoding.ASCII.GetBytes(signingInput)));
            ECParameters bKey = b.PublicParameters();

            var result = Assert.IsType<ProofResult.Invalid>(_verifier.Verify(forged, "POST", Target, Body, _ => bKey));
            Assert.Equal(ErrorCode.ProofInvalid, result.Error.Code);
        }

        Directory.Delete(otherDir, recursive: true);
    }

    /// <summary>
    /// A failed proof does not record its jti, so the same proof sent correctly afterwards is not taken for a replay.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A valid proof.
    ///
    /// Steps:
    /// - Verify it with another body (fails); verify it with the right body.
    ///
    /// Expected:
    /// - The second verification is Valid.
    /// </remarks>
    [Fact]
    public void Verify_FailedProof_DoesNotRecordJti()
    {
        string proof = ProofBuilder.Create(_key, "POST", Target, _nonces.Issue(), Body, includePublicKey: true, _time);

        Assert.IsType<ProofResult.Invalid>(_verifier.Verify(proof, "POST", Target, [1]));
        Assert.IsType<ProofResult.Valid>(_verifier.Verify(proof, "POST", Target, Body));
    }

    /// <summary>
    /// A full replay store refuses new proofs with ELM_SERVER_BUSY until records expire.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A replay store with capacity 2.
    ///
    /// Steps:
    /// - Verify three different proofs; advance past the nonce lifetime; verify a fourth with a new nonce.
    ///
    /// Expected:
    /// - Valid, Valid, ELM_SERVER_BUSY, then Valid.
    /// </remarks>
    [Fact]
    public void Verify_ReplayStoreFull_ReturnsServerBusyUntilExpiry()
    {
        var verifier = new ProofVerifier(_nonces, new MemoryReplayStore(_time, capacity: 2));
        string Proof() => ProofBuilder.Create(_key, "POST", Target, _nonces.Issue(), Body, includePublicKey: true, _time);

        Assert.IsType<ProofResult.Valid>(verifier.Verify(Proof(), "POST", Target, Body));
        Assert.IsType<ProofResult.Valid>(verifier.Verify(Proof(), "POST", Target, Body));
        Assert.Equal(ErrorCode.ServerBusy, Assert.IsType<ProofResult.Invalid>(verifier.Verify(Proof(), "POST", Target, Body)).Error.Code);
        _time.Advance(TimeSpan.FromSeconds(301));
        Assert.IsType<ProofResult.Valid>(verifier.Verify(Proof(), "POST", Target, Body));
    }

    /// <summary>
    /// Malformed nonces are refused without an exception.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A nonce service.
    ///
    /// Steps:
    /// - Check null, empty, non-base64url, a valid nonce cut short, one with a changed byte, and a 200-character string.
    ///
    /// Expected:
    /// - False each time.
    /// </remarks>
    [Fact]
    public void NonceIsValid_Malformed_ReturnsFalse()
    {
        string good = _nonces.Issue();
        char flipped = good[5] == 'A' ? 'B' : 'A';

        Assert.False(_nonces.IsValid(null, out _));
        Assert.False(_nonces.IsValid(string.Empty, out _));
        Assert.False(_nonces.IsValid("***", out _));
        Assert.False(_nonces.IsValid(good[..20], out _));
        Assert.False(_nonces.IsValid(good[..5] + flipped + good[6..], out _));
        Assert.False(_nonces.IsValid(new string('A', 200), out _));
        Assert.True(_nonces.IsValid(good, out _));
    }
}
