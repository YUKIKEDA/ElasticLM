using ElasticLM.Issuing;
using ElasticLM.Licensing;

namespace ElasticLM.Leasing.Tests;

/// <summary>
/// The lease rules of <see cref="LeaseEngine"/> (design §9.3, §10.4, §12.7.1, §12.7.2, §13.2–§13.4).
/// </summary>
public sealed class LeaseEngineTests : IDisposable
{
    private readonly TestServer _server = new();

    public void Dispose() => _server.Dispose();

    /// <summary>
    /// Seats with overage are floor(seats × (100 + overage) / 100): the engine grants exactly that many and refuses one more.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server license with 5 seats of pro and the overage of the case; the reclaim window is over.
    ///
    /// Steps:
    /// - Acquire one seat at a time until refused.
    ///
    /// Expected:
    /// - The number of grants is the allowed count; the next request is ELM_NO_SEATS and leaves the count unchanged.
    /// </remarks>
    [Theory]
    [InlineData(0, 5)]
    [InlineData(30, 6)]
    [InlineData(50, 7)]
    [InlineData(100, 10)]
    public void Acquire_SeatsWithOverage_GrantsTheFlooredCount(int overage, int allowed)
    {
        using var server = new TestServer(overage: overage);
        LeaseEngine engine = server.OpenEngine();
        TestClient client = server.Client();

        for (int i = 0; i < allowed; i++)
        {
            Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request()));
        }

        AssertRefused(ErrorCode.NoSeats, engine.Acquire(client.Request()));
        Assert.Equal(allowed, engine.SeatsInUse("pro"));
    }

    /// <summary>
    /// A request counts its seats: 5 seats fit one request of 5 or 3 + 2, and a request of 2 after 4 is refused although one
    /// seat is free.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5 seats, no overage; the window is over.
    ///
    /// Steps:
    /// - Acquire 3 then 2; release both; acquire 4 then 2; acquire 1.
    ///
    /// Expected:
    /// - 3 and 2 granted; 4 granted, 2 ELM_NO_SEATS, 1 granted.
    /// </remarks>
    [Fact]
    public void Acquire_Count_IsCountedAsSeats()
    {
        LeaseEngine engine = _server.OpenEngine();
        TestClient client = _server.Client();

        var three = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request(count: 3)));
        var two = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request(count: 2)));
        Assert.Equal(ReleaseOutcome.Released, engine.Release(three.LeaseId, client.KeyId));
        Assert.Equal(ReleaseOutcome.Released, engine.Release(two.LeaseId, client.KeyId));

        Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request(count: 4)));
        AssertRefused(ErrorCode.NoSeats, engine.Acquire(client.Request(count: 2)));
        Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request(count: 1)));
    }

    /// <summary>
    /// A count below 1 or above the feature's seats is an invalid argument; exactly the seats is allowed.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5 seats with 100 % overage (10 allowed), so the limit tested is the feature's seats, not the free seats.
    ///
    /// Steps:
    /// - Acquire with the count of the case.
    ///
    /// Expected:
    /// - ELM_INVALID_ARGUMENT for 0, -1, 6, and int.MaxValue; granted for 5.
    /// </remarks>
    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(6, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(5, true)]
    public void Acquire_Count_MustBeWithinTheFeatureSeats(int count, bool granted)
    {
        using var server = new TestServer(overage: 100);
        LeaseEngine engine = server.OpenEngine();

        LeaseOutcome outcome = engine.Acquire(server.Client().Request(count: count));

        if (granted)
        {
            Assert.IsType<LeaseOutcome.Granted>(outcome);
        }
        else
        {
            AssertRefused(ErrorCode.InvalidArgument, outcome);
        }
    }

    /// <summary>
    /// The feature must be in the server license, unexpired, and the version inside its range; a version that is not
    /// major.minor[.patch] is an invalid argument, and no version skips the range check.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Features pro (&gt;=2.0 &lt;3.0, 5 seats) and old (5 seats, expired one day after the start); the window is over and
    ///   two days have passed.
    ///
    /// Steps:
    /// - Acquire: feature cam; pro 3.0; pro "two"; pro without version; old.
    ///
    /// Expected:
    /// - ELM_FEATURE_NOT_LICENSED, ELM_FEATURE_VERSION_NOT_LICENSED, ELM_INVALID_ARGUMENT, granted, ELM_FEATURE_EXPIRED.
    /// </remarks>
    [Fact]
    public void Acquire_Feature_IsCheckedAgainstTheServerLicense()
    {
        using var server = new TestServer(
            features: [new FeatureSpec("pro", ">=2.0 <3.0", Seats: 5), new FeatureSpec("old", Seats: 5, ExpiresAt: TestServer.Start.AddDays(1))]
        );
        LeaseEngine engine = server.OpenEngine();
        server.Time.Advance(TimeSpan.FromDays(2));
        TestClient client = server.Client();

        AssertRefused(ErrorCode.FeatureNotLicensed, engine.Acquire(client.Request(feature: "cam")));
        AssertRefused(ErrorCode.FeatureVersionNotLicensed, engine.Acquire(client.Request(version: "3.0")));
        AssertRefused(ErrorCode.InvalidArgument, engine.Acquire(client.Request(version: "two")));
        Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request(version: null)));
        AssertRefused(ErrorCode.FeatureExpired, engine.Acquire(client.Request(feature: "old", version: null)));
    }

    /// <summary>
    /// A granted lease is a token the client accepts, for its key, with sequence number 1 and the license's lease duration.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The window is over.
    ///
    /// Steps:
    /// - Acquire 2 seats; verify the token as the client does.
    ///
    /// Expected:
    /// - Valid; 2 seats, sequence 1, 10 minutes, the server license's jti in slic; the outcome's expiry matches.
    /// </remarks>
    [Fact]
    public void Acquire_Token_PassesTheClientCheck()
    {
        LeaseEngine engine = _server.OpenEngine();
        TestClient client = _server.Client();

        var granted = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request(count: 2)));

        VerifiedLease lease = Valid(granted.Token, client);
        Assert.Equal(granted.LeaseId, lease.LeaseId);
        Assert.Equal(2, lease.Seats);
        Assert.Equal(1, lease.Sequence);
        Assert.Equal(TimeSpan.FromMinutes(10), lease.ExpiresAt - lease.IssuedAt);
        Assert.Equal(_server.Identity.License.TokenId, lease.ServerLicenseTokenId);
        Assert.Equal(lease.ExpiresAt, granted.ExpiresAt);
    }

    /// <summary>
    /// A lease never outlives the server license's exp + grace (design §12.7.1), and after that time the engine refuses new
    /// leases and renewals with ELM_EXPIRED.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server license that expires 15 minutes after the start with 2 minutes of grace; the window (10 minutes) is over.
    ///
    /// Steps:
    /// - Acquire at minute 10; renew at minute 16; acquire at minute 17 (exactly exp + grace) and renew.
    ///
    /// Expected:
    /// - The first lease ends at minute 17, not 20; the renewal also ends at 17; at minute 17 both are ELM_EXPIRED.
    /// </remarks>
    [Fact]
    public void Lease_IsCappedAtTheServerLicenseExpiryAndGrace()
    {
        using var server = new TestServer(expires: TestServer.Start.AddMinutes(15), grace: TimeSpan.FromMinutes(2));
        LeaseEngine engine = server.OpenEngine();
        TestClient client = server.Client();
        DateTimeOffset limit = TestServer.Start.AddMinutes(17);

        var granted = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request()));
        Assert.Equal(limit, granted.ExpiresAt);
        Assert.IsType<LeaseVerificationResult.Valid>(server.VerifyAsClient(granted.Token, client));

        server.Time.Advance(TimeSpan.FromMinutes(6));
        var renewed = Assert.IsType<LeaseOutcome.Granted>(engine.Renew(granted.LeaseId, 1, client.KeyId));
        Assert.Equal(limit, renewed.ExpiresAt);

        server.Time.Advance(TimeSpan.FromMinutes(1));
        AssertRefused(ErrorCode.Expired, engine.Acquire(client.Request()));
        AssertRefused(ErrorCode.Expired, engine.Renew(granted.LeaseId, 2, client.KeyId));
    }

    /// <summary>
    /// Renewal judges the sequence number (design §10.4 step 3): the same number gets the next token; one less gets the last
    /// token again only when the retry names the unanswered request that produced it; anything else is a clone.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A lease (sequence 1); clone strategy deny.
    ///
    /// Steps:
    /// - Renew with 1 by proof P (its answer is lost); retry with 1 naming [Q, P]; renew with 2; renew with 0.
    ///
    /// Expected:
    /// - Sequence 2; the same token again without a new signature; sequence 3; ELM_CLONE_DETECTED.
    /// </remarks>
    [Fact]
    public void Renew_Sequence_IsJudged()
    {
        LeaseEngine engine = _server.OpenEngine();
        TestClient client = _server.Client();
        var granted = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request()));

        var second = Assert.IsType<LeaseOutcome.Granted>(engine.Renew(granted.LeaseId, 1, client.KeyId, "P", []));
        Assert.Equal(2, Valid(second.Token, client).Sequence);
        int signed = _server.Signed;

        var retried = Assert.IsType<LeaseOutcome.Granted>(engine.Renew(granted.LeaseId, 1, client.KeyId, "R", ["Q", "P"]));
        Assert.Equal(second.Token, retried.Token);
        Assert.Equal(signed, _server.Signed);

        var third = Assert.IsType<LeaseOutcome.Granted>(engine.Renew(granted.LeaseId, 2, client.KeyId));
        Assert.Equal(3, Valid(third.Token, client).Sequence);

        AssertRefused(ErrorCode.CloneDetected, engine.Renew(granted.LeaseId, 1, client.KeyId));
    }

    /// <summary>
    /// Two copies of an installation that renew in lockstep (a cloned VM with the same timers) are detected: the copy that
    /// comes second is one sequence number behind but cannot name the other copy's request (design §10.4).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A lease (sequence 1) held by copies A and B of one installation; clone strategy deny.
    ///
    /// Steps:
    /// - A renews with 1 by proof PA; B renews with 1 by proof PB, naming its own unanswered proof PX; B renews with 1
    ///   naming nothing.
    ///
    /// Expected:
    /// - A gets sequence 2; B's first renewal is ELM_CLONE_DETECTED and the lease is stopped, so the second is refused too.
    /// </remarks>
    [Fact]
    public void Renew_LockstepClones_AreDetected()
    {
        LeaseEngine engine = _server.OpenEngine();
        TestClient client = _server.Client();
        var granted = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request()));

        Assert.IsType<LeaseOutcome.Granted>(engine.Renew(granted.LeaseId, 1, client.KeyId, "PA", []));

        AssertRefused(ErrorCode.CloneDetected, engine.Renew(granted.LeaseId, 1, client.KeyId, "PB", ["PX"]));
        AssertRefused(ErrorCode.CloneDetected, engine.Renew(granted.LeaseId, 1, client.KeyId, "PC", []));
    }

    /// <summary>
    /// A reclaimed lease has no known producing request, so a request one behind it is a clone, not a retry.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A lease renewed once by proof P (sequence 2) and reclaimed into a new engine with its sequence-2 token.
    ///
    /// Steps:
    /// - Renew with 1 naming P.
    ///
    /// Expected:
    /// - ELM_CLONE_DETECTED.
    /// </remarks>
    [Fact]
    public void Renew_RetryOnAReclaimedLease_IsAClone()
    {
        LeaseEngine before = _server.OpenEngine();
        TestClient client = _server.Client();
        var granted = Assert.IsType<LeaseOutcome.Granted>(before.Acquire(client.Request()));
        var second = Assert.IsType<LeaseOutcome.Granted>(before.Renew(granted.LeaseId, 1, client.KeyId, "P", []));
        LeaseEngine after = _server.Engine();
        Assert.Null(Assert.Single(after.Reclaim([second.Token], client.KeyId, client.PublicKey)).Error);

        AssertRefused(ErrorCode.CloneDetected, after.Renew(granted.LeaseId, 1, client.KeyId, "Q", ["P"]));
    }

    /// <summary>
    /// Under clone strategy deny, a detected clone stops the lease: it cannot be renewed with any number, released, or
    /// reclaimed, and keeps its seats until it expires.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5 seats, deny; one lease of 5 seats renewed once (sequence 2).
    ///
    /// Steps:
    /// - Renew with 0 (clone); renew with 2; release; reclaim the newest token; acquire 1 seat; wait past the expiry; acquire.
    ///
    /// Expected:
    /// - ELM_CLONE_DETECTED for both renewals and the reclaim; release reports Stopped; no seat is free until the lease
    ///   expires, then the acquire is granted.
    /// </remarks>
    [Fact]
    public void Deny_StopsTheLeaseUntilItExpires()
    {
        LeaseEngine engine = _server.OpenEngine();
        TestClient client = _server.Client();
        var granted = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request(count: 5)));
        var newest = Assert.IsType<LeaseOutcome.Granted>(engine.Renew(granted.LeaseId, 1, client.KeyId));

        AssertRefused(ErrorCode.CloneDetected, engine.Renew(granted.LeaseId, 0, client.KeyId));
        AssertRefused(ErrorCode.CloneDetected, engine.Renew(granted.LeaseId, 2, client.KeyId));
        Assert.Equal(ReleaseOutcome.Stopped, engine.Release(granted.LeaseId, client.KeyId));
        ReclaimResult reclaimed = Assert.Single(engine.Reclaim([newest.Token], client.KeyId, client.PublicKey));
        Assert.Equal(ErrorCode.CloneDetected, reclaimed.Error?.Code);
        AssertRefused(ErrorCode.NoSeats, engine.Acquire(client.Request()));

        _server.Time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request()));
    }

    /// <summary>
    /// Under clone strategy flag, a rolled-back sequence number is reported and answered as usual with the next token.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Clone strategy flag; a lease renewed twice (sequence 3).
    ///
    /// Steps:
    /// - Renew with 1; reclaim the first token.
    ///
    /// Expected:
    /// - Granted with CloneFlagged and sequence 4; the reclaim succeeds with CloneFlagged.
    /// </remarks>
    [Fact]
    public void Flag_ReportsAndAnswersAsUsual()
    {
        using var server = new TestServer(clone: CloneStrategy.Flag);
        LeaseEngine engine = server.OpenEngine();
        TestClient client = server.Client();
        var granted = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request()));
        Assert.IsType<LeaseOutcome.Granted>(engine.Renew(granted.LeaseId, 1, client.KeyId));
        Assert.IsType<LeaseOutcome.Granted>(engine.Renew(granted.LeaseId, 2, client.KeyId));

        var flagged = Assert.IsType<LeaseOutcome.Granted>(engine.Renew(granted.LeaseId, 1, client.KeyId));
        Assert.True(flagged.CloneFlagged);
        Assert.Equal(4, Valid(flagged.Token, client, server).Sequence);

        ReclaimResult reclaimed = Assert.Single(engine.Reclaim([granted.Token], client.KeyId, client.PublicKey));
        Assert.Null(reclaimed.Error);
        Assert.True(reclaimed.CloneFlagged);
    }

    /// <summary>
    /// Another installation key can neither renew nor release a lease; a release is idempotent; a released lease is unknown.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A lease of client A; client B.
    ///
    /// Steps:
    /// - B renews and releases A's lease; A releases twice; A renews; anyone releases an unknown ID.
    ///
    /// Expected:
    /// - ELM_PROOF_INVALID and WrongKey for B, with the seat still held; Released then NotFound for A; ELM_UNKNOWN_LEASE;
    ///   NotFound.
    /// </remarks>
    [Fact]
    public void Lease_BelongsToItsKey()
    {
        LeaseEngine engine = _server.OpenEngine();
        TestClient a = _server.Client();
        TestClient b = _server.Client();
        var granted = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(a.Request()));

        AssertRefused(ErrorCode.ProofInvalid, engine.Renew(granted.LeaseId, 1, b.KeyId));
        Assert.Equal(ReleaseOutcome.WrongKey, engine.Release(granted.LeaseId, b.KeyId));
        Assert.Equal(1, engine.SeatsInUse("pro"));

        Assert.Equal(ReleaseOutcome.Released, engine.Release(granted.LeaseId, a.KeyId));
        Assert.Equal(ReleaseOutcome.NotFound, engine.Release(granted.LeaseId, a.KeyId));
        AssertRefused(ErrorCode.UnknownLease, engine.Renew(granted.LeaseId, 1, a.KeyId));
        Assert.Equal(ReleaseOutcome.NotFound, engine.Release("no-such-lease", a.KeyId));
        Assert.Equal(0, engine.SeatsInUse("pro"));
    }

    /// <summary>
    /// An expired lease frees its seats and is unknown to renewal, even before the purge.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5 seats; one lease of 5 seats.
    ///
    /// Steps:
    /// - Wait exactly to the expiry and acquire; wait one second more; acquire; renew the old lease; purge.
    ///
    /// Expected:
    /// - At the expiry the acquire is ELM_NO_SEATS (a lease is valid through its exp second); one second later it is granted
    ///   and the old lease is ELM_UNKNOWN_LEASE; the purge finds nothing left to remove.
    /// </remarks>
    [Fact]
    public void Expired_Lease_FreesSeats()
    {
        LeaseEngine engine = _server.OpenEngine();
        TestClient client = _server.Client();
        var lease = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request(count: 5)));

        _server.Time.Advance(TimeSpan.FromMinutes(10));
        AssertRefused(ErrorCode.NoSeats, engine.Acquire(client.Request()));
        _server.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request()));
        AssertRefused(ErrorCode.UnknownLease, engine.Renew(lease.LeaseId, 1, client.KeyId));
        Assert.Equal(1, engine.LeaseCount);
        Assert.Equal(0, engine.Purge());
    }

    /// <summary>
    /// The purge removes expired leases that no request touched.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two leases of different features.
    ///
    /// Steps:
    /// - Wait past the expiry; purge.
    ///
    /// Expected:
    /// - Two removed; no lease held.
    /// </remarks>
    [Fact]
    public void Purge_RemovesExpiredLeases()
    {
        using var server = new TestServer(features: [new FeatureSpec("pro", Seats: 5), new FeatureSpec("cam", Seats: 5)]);
        LeaseEngine engine = server.OpenEngine();
        TestClient client = server.Client();
        Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request(version: null)));
        Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request(feature: "cam", version: null)));

        server.Time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        Assert.Equal(2, engine.Purge());
        Assert.Equal(0, engine.LeaseCount);
    }

    /// <summary>
    /// After a start, new leases are refused for the lease duration with a Retry-After to the end; at the end they are granted.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A fresh engine (lease duration 10 minutes).
    ///
    /// Steps:
    /// - Acquire at once; at 9 min 59.5 s; at 10 min.
    ///
    /// Expected:
    /// - ELM_SERVER_RECOVERING with Retry-After 600 s, then 1 s (rounded up); granted at 10 min.
    /// </remarks>
    [Fact]
    public void Acquire_DuringTheReclaimWindow_IsRefused()
    {
        LeaseEngine engine = _server.Engine();
        TestClient client = _server.Client();

        var refused = AssertRefused(ErrorCode.ServerRecovering, engine.Acquire(client.Request()));
        Assert.Equal(TimeSpan.FromMinutes(10), refused.RetryAfter);
        Assert.True(engine.IsRecovering());

        _server.Time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.FromSeconds(1), AssertRefused(ErrorCode.ServerRecovering, engine.Acquire(client.Request())).RetryAfter);

        _server.Time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(engine.IsRecovering());
        Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request()));
    }

    /// <summary>
    /// After a restart, clients reclaim their leases with the tokens they hold: the lease keeps its ID and sequence number and
    /// can be renewed; the window ends early once every feature's seats are reclaimed.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Features pro and cam with 2 seats each; an engine that granted pro ×2 to client A and cam ×2 to client B; a new engine
    ///   (the restart) with the same server identity.
    ///
    /// Steps:
    /// - A reclaims its token; renew it with its sequence number; check the window; B reclaims; check the window.
    ///
    /// Expected:
    /// - A's reclaim succeeds and the renewal gives sequence 2; the window stays open while cam has no seat back; after B's
    ///   reclaim it is over and an acquire is ELM_NO_SEATS, not ELM_SERVER_RECOVERING.
    /// </remarks>
    [Fact]
    public void Reclaim_AfterRestart_RestoresLeasesAndEndsTheWindowEarly()
    {
        using var server = new TestServer(features: [new FeatureSpec("pro", Seats: 2), new FeatureSpec("cam", Seats: 2)]);
        LeaseEngine before = server.OpenEngine();
        TestClient a = server.Client();
        TestClient b = server.Client();
        var pro = Assert.IsType<LeaseOutcome.Granted>(before.Acquire(a.Request(count: 2, version: null)));
        var cam = Assert.IsType<LeaseOutcome.Granted>(before.Acquire(b.Request(feature: "cam", count: 2, version: null)));

        LeaseEngine after = server.Engine();
        ReclaimResult reclaimed = Assert.Single(after.Reclaim([pro.Token], a.KeyId, a.PublicKey));
        Assert.Equal(new ReclaimResult(pro.LeaseId, null), reclaimed);
        Assert.True(after.TryGetLeaseKey(pro.LeaseId, out string? keyId, out _));
        Assert.Equal(a.KeyId, keyId);
        var renewed = Assert.IsType<LeaseOutcome.Granted>(after.Renew(pro.LeaseId, 1, a.KeyId));
        Assert.Equal(2, Valid(renewed.Token, a, server).Sequence);
        Assert.True(after.IsRecovering());

        Assert.Null(Assert.Single(after.Reclaim([cam.Token], b.KeyId, b.PublicKey)).Error);
        Assert.False(after.IsRecovering());
        AssertRefused(ErrorCode.NoSeats, after.Acquire(a.Request(version: null)));
    }

    /// <summary>
    /// A reclaim is checked per token: a token bound to another key, an expired token, a token signed by another key, a
    /// borrow, a damaged token, and a token past the free seats after the window are refused; a token sent twice counts once.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5 seats; client A holds a lease of 2 and one of 3 from the previous engine, plus an expired lease; another server key;
    ///   a borrow token of the server; a new engine.
    ///
    /// Steps:
    /// - B reclaims A's token. A reclaims: the expired token, a token from the other key, the borrow, "x.y.z", and the lease
    ///   of 2 twice. The window ends; client C acquires 1; A reclaims the lease of 3.
    ///
    /// Expected:
    /// - ELM_WRONG_INSTALLATION, ELM_EXPIRED, ELM_BAD_SIGNATURE, ELM_BORROW_NOT_ALLOWED, ELM_MALFORMED, then OK twice with
    ///   2 seats in use; the late reclaim of 3 does not fit beside 3 seats in use and is ELM_NO_SEATS.
    /// </remarks>
    [Fact]
    public void Reclaim_ChecksEachToken()
    {
        LeaseEngine before = _server.OpenEngine();
        TestClient a = _server.Client();
        TestClient b = _server.Client();
        TestClient c = _server.Client();
        var expired = Assert.IsType<LeaseOutcome.Granted>(before.Acquire(a.Request()));
        Assert.Equal(ReleaseOutcome.Released, before.Release(expired.LeaseId, a.KeyId));
        _server.Time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        var two = Assert.IsType<LeaseOutcome.Granted>(before.Acquire(a.Request(count: 2)));
        var three = Assert.IsType<LeaseOutcome.Granted>(before.Acquire(a.Request(count: 3)));
        string foreign;
        using (System.Security.Cryptography.ECDsa other = KeyFiles.CreateKey())
        {
            foreign = LeaseIssuer.Issue(other, Spec(a, "foreign"), _server.Time);
        }

        string borrow = LeaseIssuer.Issue(_server.Key, Spec(a, "borrow") with { IsBorrow = true, Duration = TimeSpan.FromDays(1) }, _server.Time);

        LeaseEngine after = _server.Engine();
        Assert.Equal(ErrorCode.WrongInstallation, Assert.Single(after.Reclaim([two.Token], b.KeyId, b.PublicKey)).Error?.Code);
        IReadOnlyList<ReclaimResult> results = after.Reclaim([expired.Token, foreign, borrow, "x.y.z", two.Token, two.Token], a.KeyId, a.PublicKey);

        Assert.Equal(
            [ErrorCode.Expired, ErrorCode.BadSignature, ErrorCode.BorrowNotAllowed, ErrorCode.Malformed, ErrorCode.Ok, ErrorCode.Ok],
            results.Select(r => r.Error?.Code ?? ErrorCode.Ok)
        );
        Assert.Equal(two.LeaseId, results[4].LeaseId);
        Assert.Equal(2, after.SeatsInUse("pro"));

        _server.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.IsType<LeaseOutcome.Granted>(after.Acquire(c.Request()));
        Assert.Equal(ErrorCode.NoSeats, Assert.Single(after.Reclaim([three.Token], a.KeyId, a.PublicKey)).Error?.Code);
    }

    /// <summary>
    /// A lease issued under an earlier server license of the same server is reclaimed after a restart with a replaced server
    /// license, and its renewal names the new license; a client still checks leases against the current license only.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A lease from an engine under server license L1; the server license replaced by L2 (same server key, new jti); a new
    ///   engine under L2.
    ///
    /// Steps:
    /// - Check the old token as a client against L2; reclaim it; renew it.
    ///
    /// Expected:
    /// - The client check is ELM_LEASE_OUT_OF_BOUNDS; the reclaim succeeds; the renewed token names L2 and passes the client
    ///   check.
    /// </remarks>
    [Fact]
    public void Reclaim_TokenUnderAnEarlierServerLicense_IsAccepted()
    {
        LeaseEngine before = _server.OpenEngine();
        TestClient client = _server.Client();
        var granted = Assert.IsType<LeaseOutcome.Granted>(before.Acquire(client.Request()));

        _server.ReplaceServerLicense();
        LeaseEngine after = _server.Engine();
        var outOfBounds = Assert.IsType<LeaseVerificationResult.Invalid>(_server.VerifyAsClient(granted.Token, client));
        Assert.Equal(ErrorCode.LeaseOutOfBounds, outOfBounds.Error.Code);

        Assert.Null(Assert.Single(after.Reclaim([granted.Token], client.KeyId, client.PublicKey)).Error);
        var renewed = Assert.IsType<LeaseOutcome.Granted>(after.Renew(granted.LeaseId, 1, client.KeyId));
        Assert.Equal(_server.Identity.License.TokenId, Valid(renewed.Token, client).ServerLicenseTokenId);
    }

    /// <summary>
    /// A reclaim of a lease the server holds: the same sequence number is the same client; a token one behind is a clone,
    /// because a reclaim names no unanswered request (design §10.4, 0.47).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Deny; a lease renewed twice (tokens with sequence 1, 2, 3).
    ///
    /// Steps:
    /// - Reclaim the token of sequence 3, then 2, then 1; renew with 3.
    ///
    /// Expected:
    /// - OK, then ELM_CLONE_DETECTED twice (the lease is stopped at the second); the renewal is ELM_CLONE_DETECTED.
    /// </remarks>
    [Fact]
    public void Reclaim_KnownLease_IsJudgedBySequence()
    {
        LeaseEngine engine = _server.OpenEngine();
        TestClient client = _server.Client();
        var first = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request()));
        var second = Assert.IsType<LeaseOutcome.Granted>(engine.Renew(first.LeaseId, 1, client.KeyId));
        var third = Assert.IsType<LeaseOutcome.Granted>(engine.Renew(first.LeaseId, 2, client.KeyId));

        Assert.Null(Assert.Single(engine.Reclaim([third.Token], client.KeyId, client.PublicKey)).Error);
        Assert.Equal(ErrorCode.CloneDetected, Assert.Single(engine.Reclaim([second.Token], client.KeyId, client.PublicKey)).Error?.Code);
        Assert.Equal(ErrorCode.CloneDetected, Assert.Single(engine.Reclaim([first.Token], client.KeyId, client.PublicKey)).Error?.Code);
        AssertRefused(ErrorCode.CloneDetected, engine.Renew(first.LeaseId, 3, client.KeyId));
    }

    /// <summary>
    /// A reclaim carries at most 64 tokens.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine.
    ///
    /// Steps:
    /// - Reclaim 64 damaged tokens; reclaim 65.
    ///
    /// Expected:
    /// - 64 results; ArgumentException for 65.
    /// </remarks>
    [Fact]
    public void Reclaim_TokenCount_IsLimited()
    {
        LeaseEngine engine = _server.Engine();
        TestClient client = _server.Client();

        Assert.Equal(64, engine.Reclaim([.. Enumerable.Repeat("x", 64)], client.KeyId, client.PublicKey).Count);
        Assert.Throws<ArgumentException>(() => engine.Reclaim([.. Enumerable.Repeat("x", 65)], client.KeyId, client.PublicKey));
    }

    /// <summary>
    /// Concurrent requests for one feature never exceed its seats (design §13.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5 seats with 100 % overage (10 allowed).
    ///
    /// Steps:
    /// - 200 acquires of 1 seat from parallel threads.
    ///
    /// Expected:
    /// - Exactly 10 granted and 10 seats in use.
    /// </remarks>
    [Fact]
    public void Acquire_Concurrent_NeverExceedsSeats()
    {
        using var server = new TestServer(overage: 100);
        LeaseEngine engine = server.OpenEngine();
        TestClient client = server.Client();
        int granted = 0;

        Parallel.For(
            0,
            200,
            _ =>
            {
                if (engine.Acquire(client.Request()) is LeaseOutcome.Granted)
                {
                    Interlocked.Increment(ref granted);
                }
            }
        );

        Assert.Equal(10, granted);
        Assert.Equal(10, engine.SeatsInUse("pro"));
    }

    private static LeaseOutcome.Refused AssertRefused(ErrorCode code, LeaseOutcome outcome)
    {
        var refused = Assert.IsType<LeaseOutcome.Refused>(outcome);
        Assert.Equal(code, refused.Error.Code);
        return refused;
    }

    private LeaseSpec Spec(TestClient client, string id) =>
        new()
        {
            LeaseId = id,
            ProductId = "acme-cad",
            LicenseId = "lic-srv",
            FeatureId = "pro",
            Seats = 1,
            InstallationKeyThumbprint = client.KeyId,
            Duration = TimeSpan.FromMinutes(10),
            ServerLicenseTokenId = _server.Identity.License.TokenId,
            Sequence = 1,
        };

    private VerifiedLease Valid(string token, TestClient client, TestServer? server = null) =>
        Assert.IsType<LeaseVerificationResult.Valid>((server ?? _server).VerifyAsClient(token, client)).Lease;
}
