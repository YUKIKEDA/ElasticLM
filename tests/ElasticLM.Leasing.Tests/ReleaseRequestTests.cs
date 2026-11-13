using ElasticLM.Licensing;

namespace ElasticLM.Leasing.Tests;

/// <summary>
/// The release of the lease an acquisition or a borrow issued, named by the jti of that request's proof, in
/// <see cref="LeaseEngine.ReleaseRequest"/> (design §12.7.7, V1d5b).
/// </summary>
public sealed class ReleaseRequestTests : IDisposable
{
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private readonly TestServer _server = new();

    public void Dispose() => _server.Dispose();

    /// <summary>
    /// The lease an acquisition issued is released by that request's proof ID and the same key, once; another lease of the
    /// key stays, and a lease already released by its ID is not found again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - pro with 5 seats; one key holding a lease of 2 seats (request A) and a lease of 1 seat (request B).
    ///
    /// Steps:
    /// - Release by A's proof ID; again by A's; release B by its lease ID; then by B's proof ID.
    ///
    /// Expected:
    /// - Released with A's lease (2 seats, not a borrow), 1 seat in use; NotFound; Released; NotFound, 0 seats in use, and
    ///   the engine keeps no entry for either request.
    /// </remarks>
    [Fact]
    public void ReleaseRequest_TheRequestsLease_IsReleasedOnce()
    {
        LeaseEngine engine = _server.OpenEngine();
        TestClient client = _server.Client();
        LeaseRequest a = client.Request(count: 2);
        LeaseRequest b = client.Request();
        var first = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(a));
        var second = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(b));

        Assert.Equal(ReleaseOutcome.Released, engine.ReleaseRequest(a.ProofId, client.KeyId, out LeaseInfo? released));
        Assert.Equal((first.LeaseId, 2, false), (released!.LeaseId, released.Seats, released.IsBorrow));
        Assert.Equal(1, engine.SeatsInUse("pro"));
        Assert.Equal(ReleaseOutcome.NotFound, engine.ReleaseRequest(a.ProofId, client.KeyId, out LeaseInfo? again));
        Assert.Null(again);

        Assert.Equal(ReleaseOutcome.Released, engine.Release(second.LeaseId, client.KeyId));
        Assert.Equal(ReleaseOutcome.NotFound, engine.ReleaseRequest(b.ProofId, client.KeyId, out _));
        Assert.Equal((0L, 0), (engine.SeatsInUse("pro"), engine.RequestEntries));
    }

    /// <summary>
    /// A proof ID finds nothing for another key, for a request that issued no lease, for a lease renewed since, for a lease
    /// taken back by a reclaim, and for a lease that expired before the purge; nothing is released.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two keys; key 1 holds lease A (request A) and lease R (request R), both of 10 minutes, and R was renewed to
    ///   sequence 2 two minutes later.
    ///
    /// Steps:
    /// - Release by A's proof ID with key 2; by a proof ID no request had, with key 1; by R's proof ID with key 1.
    /// - On a new engine after a restart, key 1 reclaims A's token; release by A's proof ID.
    /// - Move the clock to one second past A's expiry, without a purge; release by A's proof ID with key 1 on the first
    ///   engine.
    ///
    /// Expected:
    /// - NotFound each time, with A and R still held (2 seats) and A held on the new engine (1 seat).
    /// - On the first engine after A expired: NotFound and 1 seat in use (R), not a count below what the leases hold.
    /// </remarks>
    [Fact]
    public void ReleaseRequest_WhatItDoesNotName_IsNotFound()
    {
        LeaseEngine engine = _server.OpenEngine();
        TestClient client = _server.Client();
        TestClient other = _server.Client();
        LeaseRequest a = client.Request();
        LeaseRequest r = client.Request();
        var lease = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(a));
        var renewed = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(r));
        _server.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.IsType<LeaseOutcome.Granted>(engine.Renew(renewed.LeaseId, 1, client.KeyId));

        Assert.Equal(ReleaseOutcome.NotFound, engine.ReleaseRequest(a.ProofId, other.KeyId, out _));
        Assert.Equal(ReleaseOutcome.NotFound, engine.ReleaseRequest("no-such-request", client.KeyId, out _));
        Assert.Equal(ReleaseOutcome.NotFound, engine.ReleaseRequest(r.ProofId, client.KeyId, out _));
        Assert.Equal(2, engine.SeatsInUse("pro"));

        LeaseEngine restarted = _server.Engine();
        Assert.Null(Assert.Single(restarted.Reclaim([lease.Token], client.KeyId, client.PublicKey)).Error);
        Assert.Equal(ReleaseOutcome.NotFound, restarted.ReleaseRequest(a.ProofId, client.KeyId, out _));
        Assert.Equal(1, restarted.SeatsInUse("pro"));

        _server.Time.Advance(TimeSpan.FromMinutes(8) + TimeSpan.FromSeconds(1));
        Assert.Equal(ReleaseOutcome.NotFound, engine.ReleaseRequest(a.ProofId, client.KeyId, out _));
        Assert.Equal(1, engine.SeatsInUse("pro"));
    }

    /// <summary>
    /// A lease stopped as a clone is not released by its request's proof ID either: it holds its seat until it expires
    /// (design §10.4 deny, §12.7.7 step 5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Clone strategy deny; a lease of request A, stopped by a renewal with sequence 5 that names no retry.
    ///
    /// Steps:
    /// - Release by A's proof ID.
    ///
    /// Expected:
    /// - Stopped, with the lease; 1 seat still in use.
    /// </remarks>
    [Fact]
    public void ReleaseRequest_StoppedClone_IsRefused()
    {
        LeaseEngine engine = _server.OpenEngine();
        TestClient client = _server.Client();
        LeaseRequest a = client.Request();
        var lease = Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(a));
        Assert.Equal(ErrorCode.CloneDetected, Assert.IsType<LeaseOutcome.Refused>(engine.Renew(lease.LeaseId, 5, client.KeyId)).Error.Code);

        Assert.Equal(ReleaseOutcome.Stopped, engine.ReleaseRequest(a.ProofId, client.KeyId, out LeaseInfo? stopped));
        Assert.Equal((lease.LeaseId, true), (stopped!.LeaseId, stopped.IsStopped));
        Assert.Equal(1, engine.SeatsInUse("pro"));
    }

    /// <summary>
    /// A borrow released by its request's proof ID is an early return: it is recorded, counts toward the key's limit of 30
    /// days with the returns by lease ID, and is refused past it (design §12.7.4, 0.40, 0.57).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - brl 2; an engine with a record of borrows; three borrows of one key (requests 1 to 3).
    ///
    /// Steps:
    /// - Release borrow 1 by its lease ID; borrow 2 by its request's proof ID; borrow 3 by its request's proof ID.
    ///
    /// Expected:
    /// - Released; Released, with the return of borrow 2 recorded; ReturnNotAllowed, with borrow 3 still holding its seat.
    /// </remarks>
    [Fact]
    public void ReleaseRequest_Borrow_IsAnEarlyReturn()
    {
        var journal = new Journal();
        LeaseEngine engine = _server.OpenEngine(new LeaseEngineOptions { Journal = journal });
        TestClient client = _server.Client();
        LeaseRequest[] requests = [client.Borrow(Week), client.Borrow(Week), client.Borrow(Week)];
        string[] leases = [.. requests.Select(r => Assert.IsType<LeaseOutcome.Granted>(engine.Acquire(r)).LeaseId)];

        Assert.Equal(ReleaseOutcome.Released, engine.Release(leases[0], client.KeyId));
        Assert.Equal(ReleaseOutcome.Released, engine.ReleaseRequest(requests[1].ProofId, client.KeyId, out LeaseInfo? returned));
        Assert.True(returned!.IsBorrow);
        Assert.Equal([leases[0], leases[1]], journal.Returns.Select(r => r.LeaseId));
        Assert.Equal(ReleaseOutcome.ReturnNotAllowed, engine.ReleaseRequest(requests[2].ProofId, client.KeyId, out _));
        Assert.Equal(1, engine.SeatsInUse("pro"));
    }

    /// <summary>
    /// A borrow restored from the record after a restart is found by the proof ID its token answers (areq), and released
    /// with the same key.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine with a record of borrows; a borrow of 2 seats (request B).
    ///
    /// Steps:
    /// - Restore the record into a new engine; release by B's proof ID with another key; then with B's key.
    ///
    /// Expected:
    /// - NotFound; Released, and 0 seats in use.
    /// </remarks>
    [Fact]
    public void ReleaseRequest_RestoredBorrow_IsFoundByTheProofItAnswers()
    {
        var journal = new Journal();
        LeaseEngine before = _server.OpenEngine(new LeaseEngineOptions { Journal = journal });
        TestClient client = _server.Client();
        LeaseRequest b = client.Borrow(Week, count: 2);
        Assert.IsType<LeaseOutcome.Granted>(before.Acquire(b));

        LeaseEngine after = _server.Engine();
        Assert.Equal(1, after.Restore(journal.Borrows, journal.Returns));
        Assert.Equal(ReleaseOutcome.NotFound, after.ReleaseRequest(b.ProofId, _server.Client().KeyId, out _));
        Assert.Equal(ReleaseOutcome.Released, after.ReleaseRequest(b.ProofId, client.KeyId, out _));
        Assert.Equal(0, after.SeatsInUse("pro"));
    }

    private sealed class Journal : IBorrowJournal
    {
        public List<BorrowRecord> Borrows { get; } = [];

        public List<BorrowReturn> Returns { get; } = [];

        public void Borrowed(BorrowRecord borrow) => Borrows.Add(borrow);

        public void Returned(BorrowReturn returned) => Returns.Add(returned);
    }
}
