using System.Security.Cryptography;
using System.Text;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using ElasticLM.Licensing.Tests;
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

    /// <summary>Gets each way the verifier gets a proof's key with each form of a token without a valid signature.</summary>
    public static TheoryData<string, string> KeySourcesAndSignatureForms => SignatureForms.Cross("embedded", "known", "cached");

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
    /// A server reachable under several public URLs accepts a proof for any of them, and refuses one for none of them
    /// (design §12.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Proofs for https://licenses.example.com/v1/leases.
    ///
    /// Steps:
    /// - Verify against [another host, the proof's host] and against [two other hosts].
    ///
    /// Expected:
    /// - Valid; ELM_PROOF_INVALID.
    /// </remarks>
    [Fact]
    public void Verify_SeveralTargets_AcceptsAnyOfThem()
    {
        Uri[] withMatch = [new("https://lic.internal:7070/v1/leases"), Target];
        Uri[] withoutMatch = [new("https://lic.internal:7070/v1/leases"), new("https://localhost:7070/v1/leases")];

        string first = ProofBuilder.Create(_key, "POST", Target, _nonces.Issue(), Body, includePublicKey: true, _time);
        Assert.IsType<ProofResult.Valid>(_verifier.Verify(first, "POST", withMatch, Body));

        string second = ProofBuilder.Create(_key, "POST", Target, _nonces.Issue(), Body, includePublicKey: true, _time);
        var invalid = Assert.IsType<ProofResult.Invalid>(_verifier.Verify(second, "POST", withoutMatch, Body));
        Assert.Equal(ErrorCode.ProofInvalid, invalid.Error.Code);
        Assert.Throws<ArgumentException>(() => _verifier.Verify(second, "POST", Array.Empty<Uri>(), Body));
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
    /// A proof that carries no valid signature is refused whichever way the verifier gets the key (from the proof, from the
    /// caller, or from its own cache): with an empty signature part, with a signature that is not 64 bytes or not the
    /// key's, and with an alg other than ES256 (design §8.2 rules 3 and 5, §12.2, §20.7 V1d14).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A valid proof: with its key embedded; without it, for a verifier whose caller knows the key; without it, for a
    ///   verifier that has verified an earlier proof of the key.
    /// - A copy of it in a form of <see cref="SignatureForms"/> with the same payload: an empty signature part; the first 63
    ///   bytes of its signature; 64 zero bytes; its signature and one more zero byte; alg none and an empty signature part;
    ///   alg HS256 and an ES256 signature of the installation key over the changed header.
    ///
    /// Steps:
    /// - Verify the copy, then the proof itself.
    ///
    /// Expected:
    /// - ELM_PROOF_INVALID: "The request proof signature does not verify." for the four signatures, "The request proof is
    ///   missing or not a valid elm-proof+jws." for the two algs. Then Valid, so the copy recorded nothing.
    /// </remarks>
    [Theory]
    [MemberData(nameof(KeySourcesAndSignatureForms))]
    public void Verify_ProofWithoutAValidSignature_IsRefused(string keyFrom, string form)
    {
        ECParameters key = _key.PublicParameters();
        Func<string, ECParameters?>? lookup = keyFrom == "known" ? kid => kid == _key.Thumbprint ? key : null : null;
        if (keyFrom == "cached")
        {
            string earlier = ProofBuilder.Create(_key, "POST", Target, _nonces.Issue(), Body, includePublicKey: true, _time);
            Assert.IsType<ProofResult.Valid>(_verifier.Verify(earlier, "POST", Target, Body));
        }

        string proof = ProofBuilder.Create(_key, "POST", Target, _nonces.Issue(), Body, includePublicKey: keyFrom == "embedded", _time);

        var invalid = Assert.IsType<ProofResult.Invalid>(
            _verifier.Verify(SignatureForms.Change(proof, form, _key.Sign), "POST", Target, Body, lookup)
        );

        string message = SignatureForms.ChangesAlgorithm(form)
            ? "The request proof is missing or not a valid elm-proof+jws."
            : "The request proof signature does not verify.";
        Assert.Equal((ErrorCode.ProofInvalid, message), (invalid.Error.Code, invalid.Error.Message));
        Assert.IsType<ProofResult.Valid>(_verifier.Verify(proof, "POST", Target, Body, lookup));
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
    /// A full replay store refuses new proofs with ELM_SERVER_BUSY until its records expire, 60 seconds after their nonces
    /// (design §12.2, V1d23).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A replay store with capacity 2; the nonces issued at a whole second, so they expire 300 seconds later.
    ///
    /// Steps:
    /// - Verify three different proofs; advance to the nonces' expiry plus 60 seconds and verify a fourth with a new nonce;
    ///   advance a tick and verify a fifth.
    ///
    /// Expected:
    /// - Valid, Valid, ELM_SERVER_BUSY; ELM_SERVER_BUSY (the records are still kept); Valid.
    /// </remarks>
    [Fact]
    public void Verify_ReplayStoreFull_ReturnsServerBusyUntilExpiry()
    {
        var verifier = new ProofVerifier(_nonces, new MemoryReplayStore(_time, capacity: 2));
        string Proof() => ProofBuilder.Create(_key, "POST", Target, _nonces.Issue(), Body, includePublicKey: true, _time);

        Assert.IsType<ProofResult.Valid>(verifier.Verify(Proof(), "POST", Target, Body));
        Assert.IsType<ProofResult.Valid>(verifier.Verify(Proof(), "POST", Target, Body));
        Assert.Equal(ErrorCode.ServerBusy, Assert.IsType<ProofResult.Invalid>(verifier.Verify(Proof(), "POST", Target, Body)).Error.Code);
        _time.Advance(TimeSpan.FromSeconds(360));
        Assert.Equal(ErrorCode.ServerBusy, Assert.IsType<ProofResult.Invalid>(verifier.Verify(Proof(), "POST", Target, Body)).Error.Code);
        _time.Advance(TimeSpan.FromTicks(1));
        Assert.IsType<ProofResult.Valid>(verifier.Verify(Proof(), "POST", Target, Body));
    }

    /// <summary>
    /// A nonce is accepted up to its issue time plus 300 seconds, that instant included, and not a tick later, compared below
    /// the second; so a proof cannot pass a second time after the replay record of its jti has expired (design §12.2, V1d23,
    /// finding F19).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The clock 0.4 seconds past a whole second S; a nonce issued then (it holds S); a proof with it, verified once.
    ///
    /// Steps:
    /// - Check the nonce and verify the same proof again at S + 300 s minus a tick, at S + 300 s, at S + 300 s plus a tick,
    ///   and at S + 300.5 s. Record the jti in the store with the expiry S + 300 s at S + 360 s and a tick later.
    ///
    /// Expected:
    /// - The nonce is valid with expiresAt S + 300 s at the first two instants, and the proof is ELM_REPLAYED; at the last
    ///   two the nonce is not valid and the proof is ELM_NONCE_REQUIRED (before V1d23 it was valid again at S + 300.5 s,
    ///   the record having expired at S + 300 s). The record of the jti is kept to S + 360 s, that instant included, and
    ///   not a tick later, so a check that read the clock before the expiry and records the jti later meets it.
    /// </remarks>
    [Fact]
    public void Verify_ProofAtTheNonceExpiry_IsNeverValidTwice()
    {
        DateTimeOffset second = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromMilliseconds(400));
        string nonce = _nonces.Issue();
        string proof = ProofBuilder.Create(_key, "POST", Target, nonce, Body, includePublicKey: true, _time);
        string jti = Assert.IsType<ProofResult.Valid>(_verifier.Verify(proof, "POST", Target, Body)).ProofId;
        DateTimeOffset expiry = second.AddSeconds(300);

        ErrorCode At(DateTimeOffset instant, bool nonceValid)
        {
            _time.SetUtcNow(instant);
            Assert.Equal(nonceValid, _nonces.IsValid(nonce, out DateTimeOffset expiresAt));
            Assert.Equal(nonceValid ? expiry : default, expiresAt);
            return Assert.IsType<ProofResult.Invalid>(_verifier.Verify(proof, "POST", Target, Body)).Error.Code;
        }

        Assert.Equal(ErrorCode.Replayed, At(expiry.AddTicks(-1), nonceValid: true));
        Assert.Equal(ErrorCode.Replayed, At(expiry, nonceValid: true));
        Assert.Equal(ErrorCode.NonceRequired, At(expiry.AddTicks(1), nonceValid: false));
        Assert.Equal(ErrorCode.NonceRequired, At(expiry.AddMilliseconds(500), nonceValid: false));

        // A check that read the clock up to the expiry and records the jti up to 60 seconds later still meets the record
        // (design §12.2).
        _time.SetUtcNow(expiry.AddSeconds(60));
        Assert.Equal(ReplayCheck.Replayed, _replays.TryUse(jti, expiry));
        _time.SetUtcNow(expiry.AddSeconds(60).AddTicks(1));
        Assert.Equal(ReplayCheck.Accepted, _replays.TryUse(jti, expiry));
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
