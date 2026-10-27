using ElasticLM.Issuing;
using ElasticLM.Licensing;

namespace ElasticLM.Leasing.Tests;

/// <summary>
/// Borrowing, early returns, the record of borrows, and replacing the server license in <see cref="LeaseEngine"/>
/// (design §12.7.4, §13.1, §13.4).
/// </summary>
public sealed class BorrowTests : IDisposable
{
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private readonly TestServer _server = new();

    public void Dispose() => _server.Dispose();

    /// <summary>
    /// A borrow up to the smaller of the license's borrow limit and the operator's is granted as a token with brw true and
    /// the borrow period; one second more is refused; a borrow is not renewed.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license with a borrow limit of 7 days; engines without and with borrow.maxDuration of 1 day.
    ///
    /// Steps:
    /// - Borrow 7 days and 7 days + 1 s; with the operator's limit borrow 1 day and 1 day + 1 s; borrow 0.5 s; renew a borrow.
    ///
    /// Expected:
    /// - Granted (the client accepts the token: brw, 7 days); ELM_BORROW_NOT_ALLOWED; granted; ELM_BORROW_NOT_ALLOWED;
    ///   ELM_INVALID_ARGUMENT; ELM_INVALID_ARGUMENT.
    /// </remarks>
    [Fact]
    public void Borrow_UpToTheLimits_IsGrantedAndNotRenewed()
    {
        LeaseEngine engine = _server.OpenEngine();
        TestClient client = _server.Client();

        var week = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Borrow(Week)));
        VerifiedLease lease = Assert.IsType<LeaseVerificationResult.Valid>(_server.VerifyAsClient(week.Token, client)).Lease;
        Assert.True(lease.IsBorrow);
        Assert.Equal(Week, lease.ExpiresAt - lease.IssuedAt);
        AssertRefused(ErrorCode.BorrowNotAllowed, engine.Acquire(client.Borrow(Week + TimeSpan.FromSeconds(1))));
        AssertRefused(ErrorCode.InvalidArgument, engine.Acquire(client.Borrow(TimeSpan.FromMilliseconds(500))));
        AssertRefused(ErrorCode.InvalidArgument, engine.Renew(week.LeaseId, 1, client.KeyId));

        using var server = new TestServer();
        LeaseEngine limited = server.OpenEngine(new LeaseEngineOptions { MaxBorrow = TimeSpan.FromDays(1) });
        TestClient other = server.Client();
        Assert.IsType<LeaseOutcome.Granted>(limited.Acquire(other.Borrow(TimeSpan.FromDays(1))));
        AssertRefused(ErrorCode.BorrowNotAllowed, limited.Acquire(other.Borrow(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1))));
    }

    /// <summary>
    /// A license with a borrow limit of 0 lends no borrow.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license with borrow 0.
    ///
    /// Steps:
    /// - Borrow 1 second.
    ///
    /// Expected:
    /// - ELM_BORROW_NOT_ALLOWED.
    /// </remarks>
    [Fact]
    public void Borrow_LicenseWithoutBorrowing_IsRefused()
    {
        using var server = new TestServer(borrow: TimeSpan.Zero);
        LeaseEngine engine = server.OpenEngine();

        AssertRefused(ErrorCode.BorrowNotAllowed, engine.Acquire(server.Client().Borrow(TimeSpan.FromSeconds(1))));
    }

    /// <summary>
    /// borrow.reserve seats are kept from borrowing but not from ordinary leases.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5 seats; borrow.reserve 2.
    ///
    /// Steps:
    /// - Borrow 3 seats; borrow 1 more; acquire 2 ordinary seats.
    ///
    /// Expected:
    /// - Granted; ELM_NO_SEATS; granted.
    /// </remarks>
    [Fact]
    public void Borrow_Reserve_IsKeptForOrdinaryLeases()
    {
        LeaseEngine engine = _server.OpenEngine(new LeaseEngineOptions { BorrowReserve = 2 });
        TestClient client = _server.Client();

        Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Borrow(Week, count: 3)));
        AssertRefused(ErrorCode.NoSeats, engine.Acquire(client.Borrow(Week)));
        Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request(count: 2)));
    }

    /// <summary>
    /// An installation key may return borrows early as often as brl allows in 30 days; the window moves on after 30 days,
    /// before the purge forgets the old returns.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - brl 2; three borrows of one key.
    ///
    /// Steps:
    /// - Return the first two; return the third; after 30 days (no purge yet) borrow and return again.
    ///
    /// Expected:
    /// - Released, Released, ReturnNotAllowed with the seat still in use; the later return is Released.
    /// </remarks>
    [Fact]
    public void EarlyReturn_IsLimitedPer30Days()
    {
        LeaseEngine engine = _server.OpenEngine();
        TestClient client = _server.Client();
        string[] borrows = [.. Enumerable.Range(0, 3).Select(_ => Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Borrow(Week))).LeaseId)];

        Assert.Equal(ReleaseOutcome.Released, engine.Release(borrows[0], client.KeyId));
        Assert.Equal(ReleaseOutcome.Released, engine.Release(borrows[1], client.KeyId));
        Assert.Equal(ReleaseOutcome.ReturnNotAllowed, engine.Release(borrows[2], client.KeyId));
        Assert.Equal(1, engine.SeatsInUse("pro"));

        _server.Time.Advance(TimeSpan.FromDays(30));
        var later = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Borrow(Week)));
        Assert.Equal(ReleaseOutcome.Released, engine.Release(later.LeaseId, client.KeyId));
    }

    /// <summary>
    /// Early returns count per machine fingerprint as well as per key, so a new installation key on the same machine does not
    /// start a new count; a key on another machine has its own.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - brl 1; two keys on one machine and a key on another machine, each with a borrow.
    ///
    /// Steps:
    /// - Return the first key's borrow; return the second key's borrow; return the other machine's borrow.
    ///
    /// Expected:
    /// - Released; ReturnNotAllowed; Released.
    /// </remarks>
    [Fact]
    public void EarlyReturn_IsCountedPerMachine()
    {
        using var server = new TestServer(borrowReturns: 1);
        LeaseEngine engine = server.OpenEngine();
        TestClient first = server.Client("aa00000000000000000000000000000a");
        TestClient second = server.Client("aa00000000000000000000000000000a");
        TestClient elsewhere = server.Client("bb00000000000000000000000000000b");
        var one = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(first.Borrow(Week)));
        var two = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(second.Borrow(Week)));
        var three = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(elsewhere.Borrow(Week)));

        Assert.Equal(ReleaseOutcome.Released, engine.Release(one.LeaseId, first.KeyId));
        Assert.Equal(ReleaseOutcome.ReturnNotAllowed, engine.Release(two.LeaseId, second.KeyId));
        Assert.Equal(ReleaseOutcome.Released, engine.Release(three.LeaseId, elsewhere.KeyId));
    }

    /// <summary>
    /// A borrow needs the client's machine fingerprints, because early returns are counted by them.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A client.
    ///
    /// Steps:
    /// - Borrow without fingerprints and with an empty list.
    ///
    /// Expected:
    /// - ELM_FINGERPRINT_UNAVAILABLE twice, no seat taken.
    /// </remarks>
    [Fact]
    public void Borrow_WithoutFingerprints_IsRefused()
    {
        LeaseEngine engine = _server.OpenEngine();
        TestClient client = _server.Client();

        AssertRefused(ErrorCode.FingerprintUnavailable, engine.Acquire(client.Borrow(Week) with { Fingerprints = null }));
        AssertRefused(ErrorCode.FingerprintUnavailable, engine.Acquire(client.Borrow(Week) with { Fingerprints = [] }));
        Assert.Equal(0, engine.SeatsInUse("pro"));
    }

    /// <summary>
    /// The administrator cannot release a borrow: it ends only by the client's early return or at its expiry.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A borrow of 1 seat.
    ///
    /// Steps:
    /// - Force-release it.
    ///
    /// Expected:
    /// - The lease is returned with IsBorrow, and its seat is still in use.
    /// </remarks>
    [Fact]
    public void ForcedRelease_OfABorrow_LeavesIt()
    {
        LeaseEngine engine = _server.OpenEngine();
        TestClient client = _server.Client();
        var borrow = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Borrow(Week)));

        Assert.True(engine.ForceRelease(borrow.LeaseId)?.IsBorrow);
        Assert.Equal(1, engine.SeatsInUse("pro"));
    }

    /// <summary>
    /// After the server license changes in content (license ID, feature removed, fewer seats, shorter borrow limit), a borrow
    /// made before cannot be returned early; a reissue that only changes jti and exp does not count as a change.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A borrow under the original license (lic-srv, pro with 5 seats, borrow 7 days).
    ///
    /// Steps:
    /// - Replace the license as the case says; return the borrow early.
    ///
    /// Expected:
    /// - Released for the reissue; ReturnNotAllowed for the others.
    /// </remarks>
    [Theory]
    [InlineData("reissue", ReleaseOutcome.Released)]
    [InlineData("license-id", ReleaseOutcome.ReturnNotAllowed)]
    [InlineData("feature-removed", ReleaseOutcome.ReturnNotAllowed)]
    [InlineData("fewer-seats", ReleaseOutcome.ReturnNotAllowed)]
    [InlineData("shorter-borrow", ReleaseOutcome.ReturnNotAllowed)]
    public void EarlyReturn_AfterTheLicenseChanged_IsRefused(string change, ReleaseOutcome expected)
    {
        LeaseEngine engine = _server.OpenEngine();
        TestClient client = _server.Client();
        var borrow = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Borrow(TimeSpan.FromDays(1))));

        _server.ReplaceServerLicense(spec =>
            change switch
            {
                "reissue" => spec with { ExpiresAt = spec.ExpiresAt!.Value.AddDays(30) },
                "license-id" => spec with { LicenseId = "lic-new" },
                "feature-removed" => spec with { Features = [new FeatureSpec("cam", Seats: 5)] },
                "fewer-seats" => spec with { Features = [new FeatureSpec("pro", Seats: 4)] },
                _ => spec with { BorrowLimit = TimeSpan.FromDays(6) },
            }
        );
        engine.ReplaceLicense(_server.Identity);

        Assert.Equal(expected, engine.Release(borrow.LeaseId, client.KeyId));
    }

    /// <summary>
    /// The record is written before a borrow or a return takes effect; when it cannot be written, the borrow is refused and
    /// the return is not made.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine with a journal that records calls and can be made to fail.
    ///
    /// Steps:
    /// - Borrow 2 seats; return it; make the journal fail; borrow; borrow with it working and return with it failing.
    ///
    /// Expected:
    /// - The journal saw the borrow (token, lic-srv, 5 seats, 7 days, the fingerprint) and the return (lease, key, the
    ///   fingerprint); the failing borrow is ELM_INTERNAL_ERROR with no seat taken; the failing return is ReturnNotAllowed and
    ///   keeps the seat.
    /// </remarks>
    [Fact]
    public void Journal_IsWrittenFirst()
    {
        var journal = new FakeJournal();
        LeaseEngine engine = _server.OpenEngine(new LeaseEngineOptions { Journal = journal });
        TestClient client = _server.Client();

        var borrow = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Borrow(Week, count: 2)));
        BorrowRecord recorded = Assert.Single(journal.Borrows);
        Assert.Equal((borrow.Token, "lic-srv", 5, Week), (recorded.Token, recorded.LicenseId, recorded.FeatureSeats, recorded.FeatureBorrowLimit));
        Assert.Equal([client.Fingerprint], recorded.Fingerprints);
        Assert.Equal(ReleaseOutcome.Released, engine.Release(borrow.LeaseId, client.KeyId));
        BorrowReturn returned = Assert.Single(journal.Returns);
        Assert.Equal((borrow.LeaseId, client.KeyId, client.Fingerprint), (returned.LeaseId, returned.KeyId, Assert.Single(returned.Fingerprints)));

        journal.Fail = true;
        AssertRefused(ErrorCode.InternalError, engine.Acquire(client.Borrow(Week)));
        Assert.Equal(0, engine.SeatsInUse("pro"));

        journal.Fail = false;
        var kept = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Borrow(Week)));
        journal.Fail = true;
        Assert.Equal(ReleaseOutcome.ReturnNotAllowed, engine.Release(kept.LeaseId, client.KeyId));
        Assert.Equal(1, engine.SeatsInUse("pro"));
    }

    /// <summary>
    /// A new engine restores borrows and early returns from the record: unexpired borrows of this server take their seats
    /// and end the reclaim window when they fill every feature; expired and foreign tokens are skipped; the returns count
    /// toward the key's limit; a restored borrow can be returned early by its key.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - brl 2; an engine that recorded two borrows of 2 and 3 seats (5 seats in all), one returned borrow of the same key,
    ///   and a borrow that has expired; a borrow token signed by another key.
    ///
    /// Steps:
    /// - Restore into a new engine; check seats and the window; return the 2-seat borrow early; return the 3-seat one.
    ///
    /// Expected:
    /// - 2 borrows restored, 5 seats in use, the window over; the first return is Released (1 earlier return + this = 2);
    ///   the second is ReturnNotAllowed.
    /// </remarks>
    [Fact]
    public void Restore_BringsBackBorrowsAndReturns()
    {
        var journal = new FakeJournal();
        LeaseEngine before = _server.OpenEngine(new LeaseEngineOptions { Journal = journal });
        TestClient client = _server.Client();
        var expiring = Assert.IsType<LeaseOutcome.Granted>(before.Acquire(client.Borrow(TimeSpan.FromHours(1))));
        var returned = Assert.IsType<LeaseOutcome.Granted>(before.Acquire(client.Borrow(Week)));
        Assert.Equal(ReleaseOutcome.Released, before.Release(returned.LeaseId, client.KeyId));
        _server.Time.Advance(TimeSpan.FromHours(2));
        var two = Assert.IsType<LeaseOutcome.Granted>(before.Acquire(client.Borrow(Week, count: 2)));
        var three = Assert.IsType<LeaseOutcome.Granted>(before.Acquire(client.Borrow(Week, count: 3)));
        string foreign;
        using (System.Security.Cryptography.ECDsa other = KeyFiles.CreateKey())
        {
            foreign = LeaseIssuer.Issue(
                other,
                new LeaseSpec
                {
                    LeaseId = "foreign",
                    ProductId = "acme-cad",
                    LicenseId = "lic-srv",
                    FeatureId = "pro",
                    Seats = 1,
                    InstallationKeyThumbprint = client.KeyId,
                    Duration = Week,
                    IsBorrow = true,
                },
                _server.Time
            );
        }

        LeaseEngine after = _server.Engine();
        BorrowRecord[] borrows = [.. journal.Borrows, new BorrowRecord(foreign, "lic-srv", 5, Week, [client.Fingerprint])];

        Assert.Equal(2, after.Restore(borrows, journal.Returns));
        Assert.Equal(5, after.SeatsInUse("pro"));
        Assert.False(after.IsRecovering());
        Assert.DoesNotContain(after.Leases(), l => l.LeaseId == expiring.LeaseId);
        Assert.Equal(ReleaseOutcome.Released, after.Release(two.LeaseId, client.KeyId));
        Assert.Equal(ReleaseOutcome.ReturnNotAllowed, after.Release(three.LeaseId, client.KeyId));
    }

    /// <summary>
    /// After a replaced server license grants fewer seats or drops a feature, leases stay until they expire: renewals of a
    /// dropped feature are refused, and while more seats are held than allowed, renewals and new leases are refused.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Features pro (5 seats) and cam (5 seats); 4 leases of pro and 1 of cam.
    ///
    /// Steps:
    /// - Replace the license with pro at 2 seats and no cam; renew a pro lease; renew the cam lease; acquire pro; let 2 pro
    ///   leases expire (they are not renewed) and renew another.
    ///
    /// Expected:
    /// - ELM_NO_SEATS; ELM_FEATURE_NOT_LICENSED; ELM_NO_SEATS; once 2 seats are held the renewal is granted and names the new
    ///   server license.
    /// </remarks>
    [Fact]
    public void ReplaceLicense_FewerSeatsOrFeatures_StopRenewals()
    {
        using var server = new TestServer(features: [new FeatureSpec("pro", Seats: 5), new FeatureSpec("cam", Seats: 5)]);
        LeaseEngine engine = server.OpenEngine();
        TestClient client = server.Client();
        string[] pro =
        [
            .. Enumerable.Range(0, 4).Select(_ => Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request(version: null))).LeaseId),
        ];
        var cam = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request(feature: "cam", version: null)));

        server.ReplaceServerLicense(spec => spec with { Features = [new FeatureSpec("pro", Seats: 2)] });
        engine.ReplaceLicense(server.Identity);

        AssertRefused(ErrorCode.NoSeats, engine.Renew(pro[0], 1, client.KeyId));
        AssertRefused(ErrorCode.FeatureNotLicensed, engine.Renew(cam.LeaseId, 1, client.KeyId));
        AssertRefused(ErrorCode.NoSeats, engine.Acquire(client.Request(version: null)));

        Assert.Equal(ReleaseOutcome.Released, engine.Release(pro[0], client.KeyId));
        Assert.Equal(ReleaseOutcome.Released, engine.Release(pro[1], client.KeyId));
        var renewed = Assert.IsType<LeaseOutcome.Granted>(engine.Renew(pro[2], 1, client.KeyId));
        VerifiedLease lease = Assert.IsType<LeaseVerificationResult.Valid>(server.VerifyAsClient(renewed.Token, client)).Lease;
        Assert.Equal(server.Identity.License.TokenId, lease.ServerLicenseTokenId);
    }

    /// <summary>
    /// The administrator's view lists leases and use per feature; a forced release removes a lease; expired leases are
    /// reported through the callback.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine with an Expired callback; a lease of 1 seat and a borrow of 2.
    ///
    /// Steps:
    /// - Read Leases and Usage; force-release the lease and an unknown ID; advance past the borrow's expiry and purge.
    ///
    /// Expected:
    /// - Two leases, pro using 3 with 2 borrowed; the forced release returns the lease and null for the unknown ID; the
    ///   callback receives the borrow.
    /// </remarks>
    [Fact]
    public void Administration_ListsReleasesAndReportsExpiry()
    {
        var expired = new List<LeaseInfo>();
        LeaseEngine engine = _server.OpenEngine(new LeaseEngineOptions { Expired = expired.Add });
        TestClient client = _server.Client();
        var lease = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request()));
        var borrow = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Borrow(TimeSpan.FromHours(1), count: 2)));

        Assert.Equal(2, engine.Leases().Count);
        FeatureUsage usage = Assert.Single(engine.Usage());
        Assert.Equal(("pro", 5, 5L, 3L, 2L), (usage.FeatureId, usage.Seats, usage.Allowed, usage.InUse, usage.Borrowed));

        Assert.Equal(lease.LeaseId, engine.ForceRelease(lease.LeaseId)?.LeaseId);
        Assert.Null(engine.ForceRelease("unknown"));

        _server.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.Equal(1, engine.Purge());
        Assert.Equal(borrow.LeaseId, Assert.Single(expired).LeaseId);
    }

    /// <summary>
    /// A new lease reports the feature's use and seats from inside the feature's lock, and a release reports the lease it
    /// released, so the server needs no walk over every lease to write its usage log.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - pro with 5 seats; a lease of 2 seats.
    ///
    /// Steps:
    /// - Borrow 1 seat; renew the lease; release the borrow; release an unknown ID.
    ///
    /// Expected:
    /// - The borrow reports 3 in use of 5; the renewal reports 0 and 0; the release reports the borrow (pro, 1 seat,
    ///   IsBorrow); the unknown ID reports no lease.
    /// </remarks>
    [Fact]
    public void Outcomes_CarryWhatTheServerLogs()
    {
        LeaseEngine engine = _server.OpenEngine();
        TestClient client = _server.Client();
        var lease = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Request(count: 2)));

        var borrow = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(client.Borrow(Week)));
        Assert.Equal((3L, 5), (borrow.InUse, borrow.Seats));
        var renewed = Assert.IsType<LeaseOutcome.Granted>(engine.Renew(lease.LeaseId, 1, client.KeyId));
        Assert.Equal((0L, 0), (renewed.InUse, renewed.Seats));

        Assert.Equal(ReleaseOutcome.Released, engine.Release(borrow.LeaseId, client.KeyId, out LeaseInfo? released));
        Assert.Equal(("pro", 1, true), (released!.FeatureId, released.Seats, released.IsBorrow));
        Assert.Equal(ReleaseOutcome.NotFound, engine.Release("unknown", client.KeyId, out LeaseInfo? unknown));
        Assert.Null(unknown);
    }

    private static void AssertRefused(ErrorCode code, LeaseOutcome outcome) =>
        Assert.Equal(code, Assert.IsType<LeaseOutcome.Refused>(outcome).Error.Code);

    private sealed class FakeJournal : IBorrowJournal
    {
        public List<BorrowRecord> Borrows { get; } = [];

        public List<BorrowReturn> Returns { get; } = [];

        public bool Fail { get; set; }

        public void Borrowed(BorrowRecord borrow)
        {
            ThrowIfFailing();
            Borrows.Add(borrow);
        }

        public void Returned(BorrowReturn returned)
        {
            ThrowIfFailing();
            Returns.Add(returned);
        }

        private void ThrowIfFailing()
        {
            if (Fail)
            {
                throw new IOException("The disk is full.");
            }
        }
    }
}
