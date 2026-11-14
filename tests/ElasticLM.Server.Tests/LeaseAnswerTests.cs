using System.Net;
using ElasticLM.Client;
using ElasticLM.Issuing;
using ElasticLM.Leasing;
using ElasticLM.Licensing;
using ElasticLM.Service.Tests;
using static ElasticLM.Server.Tests.LeaseResults;

namespace ElasticLM.Server.Tests;

/// <summary>
/// What <see cref="LicenseClient"/> does with a lease answer that is not the answer to its request (design §5.3 T17,
/// §12.7.5, §10.4, V1d5), against an in-process <c>elm-server</c> over loopback TLS. A relay between the client and the
/// server (<see cref="Recorder"/>) keeps genuine answers and hands them out again; every token it hands out is signed by
/// the server and bound to the client's installation key.
/// </summary>
public sealed class LeaseAnswerTests : IAsyncDisposable
{
    private const string AnotherRequest = "The server's lease answers another request.";
    private const string AnotherKind = "The server's lease is not of the kind asked for.";
    private const string NoLaterSequence = "The server's renewal has no later sequence number than the lease held.";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly Deployment _deployment = new();

    public ValueTask DisposeAsync() => _deployment.DisposeAsync();

    /// <summary>
    /// An answer to an acquisition or a borrow that differs from the request in one thing is refused with
    /// ELM_REQUEST_MISMATCH and the sentence for that thing; an answer that differs in nothing but the request it answers
    /// is refused too. The client holds no lease afterwards and returns none by its ID; it releases what the server issued for
    /// its own request by that request's jti, which leaves the holder's lease alone (design §12.7.7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server with 5 seats of pro and 3 of cam, borrows of up to 7 days, past its reclaim window.
    /// - Two clients, the holder and a second one, on one data folder, so both have the same installation key, and behind
    ///   one relay.
    /// - The holder's request, whose genuine answer the relay keeps, and the second client's request, which the relay
    ///   answers with it without passing the request to the server:
    ///   feature: a lease of pro, then a lease of cam;
    ///   feature-case: a lease of pro, then a lease of Pro, a name that differs only in the case of a letter;
    ///   count: a lease of 1 seat of pro, then of 2 seats;
    ///   count-fewer: a lease of 2 seats of pro, then of 1 seat;
    ///   borrow-for-lease: a borrow of pro for an hour, then a lease of pro;
    ///   lease-for-borrow: a lease of pro, then a borrow of pro for an hour;
    ///   longer-borrow: a borrow of pro for 3601 seconds, then one for 3600 seconds;
    ///   same-lease: a lease of pro, then the same;
    ///   same-borrow: a borrow of pro for an hour, then the same.
    ///
    /// Steps:
    /// - Send the holder's request; count the server's seats and the saved borrows; send the second client's request;
    ///   dispose the second client.
    ///
    /// Expected:
    /// - The holder's request gives a lease. The second gives Failed with ELM_REQUEST_MISMATCH, the case's message, and no
    ///   retry time; the relay replayed one answer.
    /// - No borrow file was added. The only DELETE the second client sent, before or when disposed, is
    ///   DELETE /v1/leases/requests/ with the jti of its own request, which the server never saw; the server's seats are as
    ///   before, and the holder's lease is still Held.
    /// </remarks>
    /// <param name="kind">The case.</param>
    /// <param name="message">The message expected.</param>
    [Theory]
    [InlineData("feature", "The server's lease is for another feature than the one asked for.")]
    [InlineData("feature-case", "The server's lease is for another feature than the one asked for.")]
    [InlineData("count", "The server's lease is for another number of seats than the one asked for.")]
    [InlineData("count-fewer", "The server's lease is for another number of seats than the one asked for.")]
    [InlineData("borrow-for-lease", AnotherKind)]
    [InlineData("lease-for-borrow", AnotherKind)]
    [InlineData("longer-borrow", "The server's borrow is longer than the period asked for.")]
    [InlineData("same-lease", AnotherRequest)]
    [InlineData("same-borrow", AnotherRequest)]
    public async Task Acquire_ReplayedAnswerOfAnotherRequest_IsRefusedAndNoLeaseIsHeld(string kind, string message)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        ElmServer server = await StartOpenServerAsync(TwoFeatures);
        var relay = new Recorder();
        string folder = _deployment.NewFolder();
        LicenseClient holder = Client(server, relay, folder);
        LicenseClient second = Client(server, relay, folder);
        var hour = TimeSpan.FromHours(1);

        Lease held = Acquired(
            kind switch
            {
                "borrow-for-lease" or "same-borrow" => await holder.BorrowLeaseAsync("pro", hour, cancellationToken: cancel),
                "longer-borrow" => await holder.BorrowLeaseAsync("pro", TimeSpan.FromSeconds(3601), cancellationToken: cancel),
                "count-fewer" => await holder.AcquireLeaseAsync("pro", count: 2, cancellationToken: cancel),
                _ => await holder.AcquireLeaseAsync("pro", cancellationToken: cancel),
            }
        );
        KeptAnswer kept = Assert.Single(relay.Answers());
        long seats = server.Engine.SeatsInUse("pro");
        string[] borrows = BorrowFiles(folder);

        relay.ReplayToNextAcquisition(kept);
        LeaseResult result = kind switch
        {
            "feature" => await second.AcquireLeaseAsync("cam", cancellationToken: cancel),
            "feature-case" => await second.AcquireLeaseAsync("Pro", cancellationToken: cancel),
            "count" => await second.AcquireLeaseAsync("pro", count: 2, cancellationToken: cancel),
            "lease-for-borrow" or "same-borrow" => await second.BorrowLeaseAsync("pro", hour, cancellationToken: cancel),
            "longer-borrow" => await second.BorrowLeaseAsync("pro", TimeSpan.FromSeconds(3600), cancellationToken: cancel),
            _ => await second.AcquireLeaseAsync("pro", cancellationToken: cancel),
        };

        var failed = Assert.IsType<LeaseResult.Failed>(result);
        Assert.Equal(new LicenseError(ErrorCode.RequestMismatch, message), failed.Error);
        Assert.Null(failed.RetryAfter);
        Assert.Equal(1, relay.Replayed);
        Assert.Equal(borrows, BorrowFiles(folder));
        await second.DisposeAsync();
        string asked = relay.Requests().Last(r => r.Method == "POST" && r.Path == "/v1/leases").ProofId!;
        Assert.Equal(["/v1/leases/requests/" + asked], relay.Requests().Where(r => r.Method == "DELETE").Select(r => r.Path));
        Assert.Equal((seats, 0L), (server.Engine.SeatsInUse("pro"), server.Engine.SeatsInUse("cam")));
        Assert.Equal(LeaseState.Held, held.State);
    }

    /// <summary>
    /// A lease or a borrow that an on-premises server lends for a version outside the server license's range of the feature
    /// is refused with ELM_FEATURE_VERSION_NOT_LICENSED: the client holds and saves nothing and releases it by its request's
    /// jti. A version in the range, no version, and a feature without a range, whatever the version, are taken (design §12.7.5 step 7, V1d18).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server with pro (&gt;=2.0 &lt;3.0, 5 seats) and cam (no range, 3 seats), past its reclaim window; a client behind
    ///   a relay that answers its acquisition, without passing it on, with a lease or a borrow of an hour that the server's
    ///   engine grants for that request's jti, the client's key, and no version, as a server that ignores the version would.
    ///
    /// Steps:
    /// - Acquire or borrow, as the case says: a lease of pro 3.0; a borrow of pro 3.0; a lease of pro "two"; a lease of pro
    ///   2.5; a lease of pro without a version; a lease of cam 3.0; a lease of cam "two". Dispose the client when it refused.
    ///
    /// Expected:
    /// - The first three: Failed with ELM_FEATURE_VERSION_NOT_LICENSED "The feature version is outside the range of the
    ///   server license." and no retry time; no borrow file; the only DELETE is DELETE /v1/leases/requests/ with the request's
    ///   jti, and the server's seats of the feature go back to 0.
    /// - The last four: Acquired, with the server's lease; the server counts its seat until the client is disposed.
    /// </remarks>
    /// <param name="feature">The feature asked for.</param>
    /// <param name="version">The version asked for, or <see langword="null"/>.</param>
    /// <param name="borrow">Whether the request is a borrow.</param>
    /// <param name="taken">Whether the client takes the lease.</param>
    [Theory]
    [InlineData("pro", "3.0", false, false)]
    [InlineData("pro", "3.0", true, false)]
    [InlineData("pro", "two", false, false)]
    [InlineData("pro", "2.5", false, true)]
    [InlineData("pro", null, false, true)]
    [InlineData("cam", "3.0", false, true)]
    [InlineData("cam", "two", false, true)]
    public async Task Acquire_VersionOutsideTheServerLicense_IsRefused(string feature, string? version, bool borrow, bool taken)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        ElmServer server = await StartOpenServerAsync(s =>
            s with
            {
                Features = [new FeatureSpec("pro", ">=2.0 <3.0", Seats: 5), new FeatureSpec("cam", Seats: 3)],
            }
        );
        var relay = new Recorder();
        string folder = _deployment.NewFolder();
        LicenseClient client = Client(server, relay, folder);
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.User, folder).TryLoadOrCreate(out InstallationKey? key, out _));
        System.Security.Cryptography.ECParameters publicKey;
        using (key)
        {
            publicKey = key!.PublicParameters();
        }

        var hour = TimeSpan.FromHours(1);
        relay.AnswerNextAcquisition(sent =>
        {
            var request = new LeaseRequest(
                feature,
                null,
                1,
                client.InstallationKeyThumbprint,
                publicKey,
                sent.ProofId!,
                borrow ? hour : null,
                borrow ? ["00112233445566778899aabbccddeeff"] : null
            );
            var granted = Assert.IsType<LeaseOutcome.Granted>(server.Engine.Acquire(request));
            return new KeptAnswer(
                sent.Path,
                HttpStatusCode.Created,
                $$"""{"lease":"{{granted.Token}}","certificates":[]}""",
                server.Engine.Server.License.TokenId
            );
        });

        LeaseResult result = borrow
            ? await client.BorrowLeaseAsync(feature, hour, version, cancellationToken: cancel)
            : await client.AcquireLeaseAsync(feature, version, cancellationToken: cancel);

        Assert.Equal(1, relay.Replayed);
        string asked = relay.Requests().Single(r => r.Method == "POST" && r.Path == "/v1/leases").ProofId!;
        if (taken)
        {
            Assert.Equal(feature, Acquired(result).FeatureId);
            Assert.Equal(1, server.Engine.SeatsInUse(feature));
            Assert.DoesNotContain(relay.Requests(), r => r.Method == "DELETE");
            return;
        }

        var failed = Assert.IsType<LeaseResult.Failed>(result);
        Assert.Equal(
            new LicenseError(ErrorCode.FeatureVersionNotLicensed, "The feature version is outside the range of the server license."),
            failed.Error
        );
        Assert.Null(failed.RetryAfter);
        Assert.Empty(BorrowFiles(folder));
        await client.DisposeAsync();
        Assert.Equal(["/v1/leases/requests/" + asked], relay.Requests().Where(r => r.Method == "DELETE").Select(r => r.Path));
        Assert.Equal(0, server.Engine.SeatsInUse(feature));
    }

    /// <summary>
    /// The request checks come before the version check (design §12.7.5 step 7, V1d18): an answer to an acquisition of a
    /// version outside the range that carries a lease of another feature is ELM_REQUEST_MISMATCH, not
    /// ELM_FEATURE_VERSION_NOT_LICENSED.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server with pro (&gt;=2.0 &lt;3.0, 5 seats) and cam (3 seats), past its reclaim window; a client behind a relay that
    ///   answers its acquisition, without passing it on, with a lease of cam that the server's engine grants for that
    ///   request's jti and the client's key.
    ///
    /// Steps:
    /// - Acquire pro 3.0; dispose the client.
    ///
    /// Expected:
    /// - Failed with ELM_REQUEST_MISMATCH "The server's lease is for another feature than the one asked for."; the only DELETE
    ///   is DELETE /v1/leases/requests/ with the request's jti, and the server's seats of cam go back to 0.
    /// </remarks>
    [Fact]
    public async Task Acquire_OtherFeatureForAVersionOutsideTheRange_IsAMismatchFirst()
    {
        ElmServer server = await StartOpenServerAsync(s =>
            s with
            {
                Features = [new FeatureSpec("pro", ">=2.0 <3.0", Seats: 5), new FeatureSpec("cam", Seats: 3)],
            }
        );
        var relay = new Recorder();
        string folder = _deployment.NewFolder();
        LicenseClient client = Client(server, relay, folder);
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.User, folder).TryLoadOrCreate(out InstallationKey? key, out _));
        System.Security.Cryptography.ECParameters publicKey;
        using (key)
        {
            publicKey = key!.PublicParameters();
        }

        relay.AnswerNextAcquisition(sent =>
        {
            var request = new LeaseRequest("cam", null, 1, client.InstallationKeyThumbprint, publicKey, sent.ProofId!);
            var granted = Assert.IsType<LeaseOutcome.Granted>(server.Engine.Acquire(request));
            return new KeptAnswer(
                sent.Path,
                HttpStatusCode.Created,
                $$"""{"lease":"{{granted.Token}}","certificates":[]}""",
                server.Engine.Server.License.TokenId
            );
        });

        LeaseResult result = await client.AcquireLeaseAsync("pro", "3.0", cancellationToken: TestContext.Current.CancellationToken);

        var failed = Assert.IsType<LeaseResult.Failed>(result);
        Assert.Equal(new LicenseError(ErrorCode.RequestMismatch, "The server's lease is for another feature than the one asked for."), failed.Error);
        await client.DisposeAsync();
        string asked = relay.Requests().Single(r => r.Method == "POST" && r.Path == "/v1/leases").ProofId!;
        Assert.Equal(["/v1/leases/requests/" + asked], relay.Requests().Where(r => r.Method == "DELETE").Select(r => r.Path));
        Assert.Equal(0, server.Engine.SeatsInUse("cam"));
    }

    /// <summary>
    /// An acquisition or a renewal answer that names an earlier server license the client still keeps is checked against
    /// the server license in use, and refused (design §13.5, V1d18): an earlier license, with a later feature expiry or a
    /// wider version range, never lets a new lease through. A lease tied to the earlier license is still released.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server with server license A, past its reclaim window; a client behind a relay that keeps answers; a lease of pro
    ///   under A, whose genuine answer (ELM-Server-License A) the relay keeps.
    /// - The server reloads server license B (a new jti, the same grants); the client takes a second lease, so it fetches B
    ///   and uses it, and keeps A for the first lease.
    /// - The client releases the first lease; the server then loads A again, so that a fetch of the server license would
    ///   give A (before V1d18 the client verified with the A it kept; a client that fetched again would take A).
    ///
    /// Steps:
    /// - As the case says: acquire pro, which the relay answers with the kept answer under
    ///   A; or move the clock 200 s so the second lease renews, and the relay answers the renewal with it.
    ///
    /// Expected:
    /// - The release gives no error and the lease is Released.
    /// - acquire: Failed with ELM_LEASE_OUT_OF_BOUNDS "The lease was issued under another server license; fetch the server
    ///   license again." (before V1d18 the token verified with A, and the answer failed only at areq as
    ///   ELM_REQUEST_MISMATCH).
    /// - renew: the second lease is Retrying with the same error, and keeps its token.
    /// </remarks>
    /// <param name="kind">The case.</param>
    [Theory]
    [InlineData("acquire")]
    [InlineData("renew")]
    public async Task Answer_UnderAnEarlierServerLicense_IsOutOfBounds(string kind)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        string data = _deployment.NewFolder();
        string path = _deployment.WriteServerLicense(data);
        byte[] fileA = File.ReadAllBytes(path);
        ElmServer server = await _deployment.StartServerAsync(data);
        _deployment.Time.Advance(Window);
        var relay = new Recorder();
        LicenseClient client = Client(server, relay);
        Lease first = Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: cancel));
        KeptAnswer underA = Assert.Single(relay.Answers());

        _deployment.WriteServerLicense(data);
        Assert.Null(server.ReloadLicense());
        Lease second = Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: cancel));
        Assert.NotEqual(underA.ServerLicense, second.Token.ServerLicenseTokenId);
        Assert.Null(await client.ReleaseLeaseAsync(first, cancel));
        Assert.Equal(LeaseState.Released, first.State);

        var outOfBounds = new LicenseError(
            ErrorCode.LeaseOutOfBounds,
            "The lease was issued under another server license; fetch the server license again."
        );

        // The server goes back to A, which it may (a file loaded by hand is not compared with the one it replaces), so a
        // fetch of the server license now gives A: the client must not fetch it for an answer that names A.
        File.WriteAllBytes(path, fileA);
        Assert.Null(server.ReloadLicense());
        if (kind == "acquire")
        {
            relay.ReplayToNextAcquisition(underA);
            Assert.Equal(outOfBounds, Assert.IsType<LeaseResult.Failed>(await client.AcquireLeaseAsync("pro", cancellationToken: cancel)).Error);
        }
        else
        {
            VerifiedLease held = second.Token;
            relay.ReplayToNextRenewal(underA);
            _deployment.Time.Advance(TimeSpan.FromSeconds(200));
            await Deployment.WaitUntilAsync(() => second.State == LeaseState.Retrying);
            Assert.Equal(outOfBounds, second.LastError);
            Assert.Same(held, second.Token);
        }
    }

    /// <summary>
    /// An answer to a renewal that differs from the lease held in one thing is refused with ELM_REQUEST_MISMATCH and the
    /// sentence for that thing: the lease keeps its token, goes to Retrying, and renews at the retry.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server with 5 seats of pro and 3 of cam, 10-minute leases, past its reclaim window; a client holding lease A of
    ///   1 seat of pro, behind a relay.
    /// - The genuine answer the relay gives to A's next renewal, without passing the renewal to the server. Where it is
    ///   another lease's, a second client on the same data folder (the same installation key) got it and returned that
    ///   lease, so that only A renews:
    ///   another-lease: the acquisition answer of another lease of 1 seat of pro;
    ///   another-feature: of a lease of 1 seat of cam;
    ///   another-count: of a lease of 2 seats of pro;
    ///   borrow: of a borrow of 1 seat of pro, which is kept;
    ///   same-sequence: A's own acquisition answer (sequence 1), while A holds sequence 1;
    ///   older-sequence: A's own acquisition answer, after A renewed to sequence 2.
    ///
    /// Steps:
    /// - Move the clock 200 s (A's renewal, answered by the relay); then 15 s (the retry, which reaches the server).
    ///
    /// Expected:
    /// - Retrying with ELM_REQUEST_MISMATCH and the case's message; A's token is the one it held; the relay replayed one
    ///   answer.
    /// - After the retry A is Held with the next sequence number, and the server still counts A's seat.
    /// </remarks>
    /// <param name="kind">The case.</param>
    /// <param name="message">The message expected.</param>
    [Theory]
    [InlineData("another-lease", "The server's renewal is for another lease than the one held.")]
    [InlineData("another-feature", "The server's renewal is for another feature than the lease held.")]
    [InlineData("another-count", "The server's renewal is for another number of seats than the lease held.")]
    [InlineData("borrow", "The server's renewal is a borrow.")]
    [InlineData("same-sequence", NoLaterSequence)]
    [InlineData("older-sequence", NoLaterSequence)]
    public async Task Renewal_ReplayedAnswerOfAnotherRequest_IsRefusedAndTheLeaseRetries(string kind, string message)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        ElmServer server = await StartOpenServerAsync(TwoFeatures);
        var relay = new Recorder();
        string folder = _deployment.NewFolder();
        LicenseClient client = Client(server, relay, folder);
        Lease lease = Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: cancel));
        KeptAnswer own = Assert.Single(relay.Answers());
        LicenseClient source = Client(server, relay, folder);
        switch (kind)
        {
            case "another-lease":
                Assert.Null(await source.ReleaseLeaseAsync(Acquired(await source.AcquireLeaseAsync("pro", cancellationToken: cancel)), cancel));
                break;
            case "another-feature":
                Assert.Null(await source.ReleaseLeaseAsync(Acquired(await source.AcquireLeaseAsync("cam", cancellationToken: cancel)), cancel));
                break;
            case "another-count":
                Assert.Null(
                    await source.ReleaseLeaseAsync(Acquired(await source.AcquireLeaseAsync("pro", count: 2, cancellationToken: cancel)), cancel)
                );
                break;
            case "borrow":
                Acquired(await source.BorrowLeaseAsync("pro", TimeSpan.FromHours(1), cancellationToken: cancel));
                break;
            case "older-sequence":
                _deployment.Time.Advance(TimeSpan.FromSeconds(200));
                await Deployment.WaitUntilAsync(() => lease.Token.Sequence == 2);
                break;
            default:
                break;
        }

        KeptAnswer replayed = kind is "same-sequence" or "older-sequence" ? own : relay.Answers()[1];
        long seats = server.Engine.SeatsInUse("pro");
        VerifiedLease token = lease.Token;
        relay.ReplayToNextRenewal(replayed);

        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => lease.State == LeaseState.Retrying);
        Assert.Equal(new LicenseError(ErrorCode.RequestMismatch, message), lease.LastError);
        Assert.Same(token, lease.Token);
        Assert.Equal(1, relay.Replayed);

        _deployment.Time.Advance(TimeSpan.FromSeconds(15));
        await Deployment.WaitUntilAsync(() => lease.State == LeaseState.Held && lease.Token.Sequence == token.Sequence + 1);
        Assert.Equal(seats, server.Engine.SeatsInUse("pro"));
    }

    /// <summary>
    /// A renewal answer that matches the lease held in everything but the request it answers is refused: the token another
    /// copy of the installation got for the same lease does not renew this copy's lease, and the server then finds the
    /// clone.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server (clone strategy deny) past its reclaim window; a client holding a lease of pro (sequence 1), behind a
    ///   relay.
    /// - Another copy of the installation renews the lease at the server with sequence 1 and gets the token of sequence 2,
    ///   which names that copy's request. The relay answers the client's next renewal with that token, without passing
    ///   the renewal to the server.
    ///
    /// Steps:
    /// - Move the clock 200 s (the client's renewal); then 15 s (its retry, which reaches the server).
    ///
    /// Expected:
    /// - Retrying with ELM_REQUEST_MISMATCH "The server's renewal answers another request.", still holding the token of
    ///   sequence 1.
    /// - The retry, one sequence number behind and naming only the client's own request, is a clone to the server: the
    ///   lease is Lost with ELM_CLONE_DETECTED.
    /// </remarks>
    [Fact]
    public async Task Renewal_ReplayedAnswerToAnotherCopysRenewal_IsRefused()
    {
        ElmServer server = await StartOpenServerAsync();
        var relay = new Recorder();
        LicenseClient client = Client(server, relay);
        Lease lease = Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
        KeptAnswer own = Assert.Single(relay.Answers());
        var other = Assert.IsType<LeaseOutcome.Granted>(server.Engine.Renew(lease.Id, 1, client.InstallationKeyThumbprint, "another-copy", []));
        string answer = $$"""{"lease":"{{other.Token}}","certificates":[]}""";
        relay.ReplayToNextRenewal(new KeptAnswer($"/v1/leases/{lease.Id}/renew", HttpStatusCode.OK, answer, own.ServerLicense));

        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => lease.State == LeaseState.Retrying);
        Assert.Equal(new LicenseError(ErrorCode.RequestMismatch, "The server's renewal answers another request."), lease.LastError);
        Assert.Equal((1L, 1), (lease.Token.Sequence, relay.Replayed));

        _deployment.Time.Advance(TimeSpan.FromSeconds(15));
        await Deployment.WaitUntilAsync(() => lease.State == LeaseState.Lost);
        Assert.Equal(ErrorCode.CloneDetected, lease.LastError?.Code);
    }

    /// <summary>
    /// When the client refuses the answer to a renewal that the server handled, its retry names the refused request, and
    /// the server returns the token of that request again instead of taking the retry for a clone (design §10.4 step 2,
    /// §12.7.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server (clone strategy deny) past its reclaim window; a client holding a lease of pro (sequence 1), behind a
    ///   relay that passes the next renewal to the server, which renews to sequence 2, and gives the client its kept
    ///   acquisition answer (sequence 1) in place of the server's.
    ///
    /// Steps:
    /// - Move the clock 200 s (the renewal); then 15 s (the retry); then 200 s (the next renewal).
    ///
    /// Expected:
    /// - Retrying with ELM_REQUEST_MISMATCH "The server's renewal has no later sequence number than the lease held.".
    /// - The retry's body is {"seq":1,"retry":[the jti of the refused renewal's proof]}; the lease is Held with sequence
    ///   2, and its token names the refused renewal's proof, not the retry's.
    /// - The next renewal gives sequence 3, and the server counts 1 seat: it never stopped the lease.
    /// </remarks>
    [Fact]
    public async Task Renewal_AnswerRefusedAfterTheServerRenewed_RetryGetsThatTokenAgain()
    {
        ElmServer server = await StartOpenServerAsync();
        var relay = new Recorder { PassReplacedOn = true };
        LicenseClient client = Client(server, relay);
        Lease lease = Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
        relay.ReplayToNextRenewal(Assert.Single(relay.Answers()));

        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => lease.State == LeaseState.Retrying);
        Assert.Equal(new LicenseError(ErrorCode.RequestMismatch, NoLaterSequence), lease.LastError);
        Assert.Equal((1L, 1), (lease.Token.Sequence, relay.Replayed));
        string refused = Assert.Single(Renewals(relay)).ProofId!;

        _deployment.Time.Advance(TimeSpan.FromSeconds(15));
        await Deployment.WaitUntilAsync(() => lease.State == LeaseState.Held);
        SentRequest retry = Renewals(relay)[1];
        Assert.Equal($$"""{"seq":1,"retry":["{{refused}}"]}""", retry.Body);
        Assert.NotEqual(refused, retry.ProofId);
        Assert.Equal((2L, refused), (lease.Token.Sequence, lease.Token.AnsweredProofId));

        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => lease.Token.Sequence == 3);
        Assert.Equal((LeaseState.Held, 1L), (lease.State, server.Engine.SeatsInUse("pro")));
    }

    /// <summary>
    /// A renewal whose success answer is larger than 256 KiB counts as one without an answer: the retry names its request,
    /// and the server returns that request's token again. A renewal whose error answer is larger names nothing (design
    /// §12.7.3, §10.4 step 2, the V1d6 Grill).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server (clone strategy deny) past its reclaim window; a client holding a lease of pro (sequence 1), behind a
    ///   relay that can answer the next renewal with a body of 262,145 spaces: with the status of the server's answer,
    ///   after the server handled the renewal (it renews to sequence 2); or with status 503, without the server seeing the
    ///   renewal.
    ///
    /// Steps:
    /// - Arm the relay after the server; move the clock 200 s (the renewal); then 15 s (the retry).
    /// - Arm the relay with status 503; move the clock 200 s (the next renewal); then 15 s (the retry).
    ///
    /// Expected:
    /// - Both renewals: Retrying with ELM_MALFORMED "The server's answer is larger than the limit.", and the token held
    ///   stays.
    /// - The first retry's body is {"seq":1,"retry":[the jti of the oversized renewal's proof]}; the lease is Held with
    ///   sequence 2, and its token names the oversized renewal's proof. A client that does not name it sends {"seq":1} to a
    ///   server at sequence 2 and loses the lease as a clone.
    /// - The second retry's body is {"seq":2}; the lease is Held with sequence 3, and the server counts 1 seat.
    /// </remarks>
    [Fact]
    public async Task Renewal_OversizedAnswer_RetryNamesItsRequestOnlyAfterASuccess()
    {
        ElmServer server = await StartOpenServerAsync();
        var relay = new Recorder();
        LicenseClient client = Client(server, relay);
        Lease lease = Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
        var tooLarge = new LicenseError(ErrorCode.Malformed, "The server's answer is larger than the limit.");

        relay.OversizeNextRenewal(afterTheServer: true);
        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => lease.State == LeaseState.Retrying);
        Assert.Equal((tooLarge, 1L), (lease.LastError, lease.Token.Sequence));
        string oversized = Assert.Single(Renewals(relay)).ProofId!;

        _deployment.Time.Advance(TimeSpan.FromSeconds(15));
        await Deployment.WaitUntilAsync(() => lease.State == LeaseState.Held);
        Assert.Equal($$"""{"seq":1,"retry":["{{oversized}}"]}""", Renewals(relay)[1].Body);
        Assert.Equal((2L, oversized), (lease.Token.Sequence, lease.Token.AnsweredProofId));

        relay.OversizeNextRenewal(afterTheServer: false);
        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => lease.State == LeaseState.Retrying);
        Assert.Equal((tooLarge, 2L), (lease.LastError, lease.Token.Sequence));

        _deployment.Time.Advance(TimeSpan.FromSeconds(15));
        await Deployment.WaitUntilAsync(() => lease.Token.Sequence == 3);
        Assert.Equal("""{"seq":2}""", Renewals(relay)[3].Body);
        Assert.Equal((LeaseState.Held, 1L), (lease.State, server.Engine.SeatsInUse("pro")));
    }

    /// <summary>
    /// The answers of a correct server pass the client's comparison: each token names the proof of its request, a borrow
    /// that the server cut at the end of its license is taken, and so is a renewal that skips a sequence number after the
    /// server flagged a clone.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server whose license ends 60 minutes after the start without grace and has clone strategy flag, past its
    ///   10-minute reclaim window; a client behind a relay that only records.
    ///
    /// Steps:
    /// - Acquire pro; borrow pro for 2 hours; let another copy of the installation renew the lease at the server with
    ///   sequence 1; move the clock 200 s so the client renews with sequence 1.
    ///
    /// Expected:
    /// - The lease's token names the proof of the acquisition.
    /// - The borrow is given although it is shorter than asked: it ends at the license's end, 50 minutes on, and its token
    ///   names the proof of the borrow's request.
    /// - The renewal is flagged and answered with sequence 3, which the client takes: Held, and the token names the proof
    ///   of the renewal.
    /// </remarks>
    [Fact]
    public async Task CorrectServer_CutBorrowAndFlaggedRenewal_AreAccepted()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        ElmServer server = await StartOpenServerAsync(s =>
            s with
            {
                ExpiresAt = Deployment.Start.AddMinutes(60),
                GracePeriod = TimeSpan.Zero,
                CloneStrategy = CloneStrategy.Flag,
            }
        );
        var relay = new Recorder();
        LicenseClient client = Client(server, relay);

        Lease lease = Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: cancel));
        Lease borrow = Acquired(await client.BorrowLeaseAsync("pro", TimeSpan.FromHours(2), cancellationToken: cancel));
        SentRequest[] acquisitions = [.. relay.Requests().Where(r => r.Method == "POST" && r.Path == "/v1/leases")];
        Assert.Equal(new string?[] { lease.Token.AnsweredProofId, borrow.Token.AnsweredProofId }, acquisitions.Select(r => r.ProofId).ToArray());
        Assert.Equal((true, Deployment.Start.AddMinutes(60)), (borrow.IsBorrow, borrow.ExpiresAt));
        Assert.Equal(TimeSpan.FromMinutes(50), borrow.ExpiresAt - borrow.Token.IssuedAt);

        var other = Assert.IsType<LeaseOutcome.Granted>(server.Engine.Renew(lease.Id, 1, client.InstallationKeyThumbprint, "another-copy", []));
        Assert.False(other.CloneFlagged);
        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => lease.Token.Sequence == 3);
        Assert.Equal(LeaseState.Held, lease.State);
        Assert.Equal(Assert.Single(Renewals(relay)).ProofId, lease.Token.AnsweredProofId);
    }

    /// <summary>
    /// When the client does not take a success answer to a renewal that the server handled, because the token fails
    /// verification or the body is not a lease answer, its retry names that request too, and the server returns the token
    /// of that request again (design §10.4 step 2, §12.7.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server (clone strategy deny) past its reclaim window; a client holding a lease of pro (sequence 1), behind a
    ///   relay that passes the next renewal to the server, which renews to sequence 2, and changes the answer on its way
    ///   back: bad-signature changes one character of the token's signature; not-json replaces the body by "{".
    ///
    /// Steps:
    /// - Move the clock 200 s (the renewal); then 15 s (the retry).
    ///
    /// Expected:
    /// - Retrying, still with the token of sequence 1: ELM_BAD_SIGNATURE "The lease is not signed by the server's key."
    ///   for bad-signature, ELM_MALFORMED "The server's lease answer is not valid." for not-json.
    /// - The retry's body is {"seq":1,"retry":[the jti of the first renewal's proof]}; the lease is Held with sequence 2,
    ///   its token names the first renewal's proof, and the server counts 1 seat.
    /// </remarks>
    /// <param name="kind">The case.</param>
    /// <param name="code">The error expected.</param>
    /// <param name="message">The message expected.</param>
    [Theory]
    [InlineData("bad-signature", ErrorCode.BadSignature, "The lease is not signed by the server's key.")]
    [InlineData("not-json", ErrorCode.Malformed, "The server's lease answer is not valid.")]
    public async Task Renewal_AnswerNotTakenAfterTheServerRenewed_RetryGetsThatTokenAgain(string kind, ErrorCode code, string message)
    {
        ElmServer server = await StartOpenServerAsync();
        var relay = new Recorder();
        LicenseClient client = Client(server, relay);
        Lease lease = Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
        relay.RewriteNextRenewal = kind == "not-json" ? _ => "{" : Recorder.WithBrokenSignature;

        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => lease.State == LeaseState.Retrying);
        Assert.Equal(new LicenseError(code, message), lease.LastError);
        Assert.Equal(1, lease.Token.Sequence);
        string refused = Assert.Single(Renewals(relay)).ProofId!;

        _deployment.Time.Advance(TimeSpan.FromSeconds(15));
        await Deployment.WaitUntilAsync(() => lease.State == LeaseState.Held);
        Assert.Equal($$"""{"seq":1,"retry":["{{refused}}"]}""", Renewals(relay)[1].Body);
        Assert.Equal((2L, refused), (lease.Token.Sequence, lease.Token.AnsweredProofId));
        Assert.Equal(1, server.Engine.SeatsInUse("pro"));
    }

    /// <summary>
    /// A server with clone strategy flag whose sequence number is behind the client's answers the client's renewal with a
    /// token later than the client's, which the client takes at once (design §10.4, §12.7.5 step 8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server (clone strategy flag) on port P; a client holding a lease of pro that it renewed twice (sequence 3),
    ///   behind a relay that kept the acquisition's answer (sequence 1).
    /// - The server restarted on P. Another copy of the installation reclaims the lease with the token of sequence 1, so
    ///   the server holds the lease at sequence 1, two behind the client.
    /// - The port is free for a moment before the next start on it: another program could take it then, and that start would
    ///   fail. The window stays (the V1d3b Grill, 3b-4): on Windows the port that a bind to port 0 gets moves on across the
    ///   machine, and in 2,000 binds none came back at once; the tests that start again on a port failed 0 times in 30 local
    ///   runs and 600 CI runs.
    ///
    /// Steps:
    /// - Move the clock 200 s so the client renews with sequence 3.
    ///
    /// Expected:
    /// - The reclaim is accepted. The client's renewal is flagged as a clone and answered with sequence 4, one more than
    ///   the client's: the lease is Held with sequence 4 without having been Retrying, and the client sent no reclaim.
    /// </remarks>
    [Fact]
    public async Task Renewal_FlaggedByAServerBehindTheClient_IsAcceptedAtOnce()
    {
        string data = _deployment.NewFolder();
        _deployment.WriteServerLicense(data, s => s with { CloneStrategy = CloneStrategy.Flag });
        ElmServer before = await _deployment.StartServerAsync(data);
        _deployment.Time.Advance(Window);
        int port = before.PublicUrls[0].Port;
        var relay = new Recorder();
        string folder = _deployment.NewFolder();
        LicenseClient client = Client(before, relay, folder);
        int retrying = 0;
        client.LeaseStateChanged += (_, e) => retrying += e.Current == LeaseState.Retrying ? 1 : 0;
        Lease lease = Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
        string first;
        using (var answer = System.Text.Json.JsonDocument.Parse(Assert.Single(relay.Answers()).Body))
        {
            first = answer.RootElement.GetProperty("lease").GetString()!;
        }

        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => lease.Token.Sequence == 2);
        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => lease.Token.Sequence == 3);

        await before.DisposeAsync();
        ElmServer after = await _deployment.StartServerAsync(
            data,
            s => new ServerSettings
            {
                Product = s.Product,
                Listen = new Uri($"https://127.0.0.1:{port}"),
                LicensePath = s.LicensePath,
                TrustRootKeyFiles = s.TrustRootKeyFiles,
                DataDirectory = s.DataDirectory,
                RateLimitPerIp = s.RateLimitPerIp,
            }
        );
        Assert.True(new InstallationKeyStore("acme-cad", KeyScope.User, folder).TryLoadOrCreate(out InstallationKey? key, out _));
        using (key)
        {
            Assert.Equal(client.InstallationKeyThumbprint, key!.Thumbprint);
            Assert.Null(Assert.Single(after.Engine.Reclaim([first], key.Thumbprint, key.PublicParameters())).Error);
        }

        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => lease.Token.Sequence == 4);
        Assert.Equal(LeaseState.Held, lease.State);
        Assert.Equal(0, retrying);
        Assert.DoesNotContain(relay.Requests(), r => r.Path.EndsWith("/reclaim", StringComparison.Ordinal));
        Assert.Equal(1, after.Engine.SeatsInUse("pro"));
    }

    private static SentRequest[] Renewals(Recorder relay) =>
        [.. relay.Requests().Where(r => r.Method == "POST" && r.Path.EndsWith("/renew", StringComparison.Ordinal))];

    private static LicenseSpec TwoFeatures(LicenseSpec spec) =>
        spec with
        {
            Features = [new FeatureSpec("pro", Seats: 5), new FeatureSpec("cam", Seats: 3)],
        };

    // The saved borrows under a client's data folder, by path.
    private static string[] BorrowFiles(string folder) =>
        [
            .. Directory
                .EnumerateFiles(folder, "*.json", SearchOption.AllDirectories)
                .Where(path => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) == "borrows")
                .Order(),
        ];

    private LicenseClient Client(ElmServer server, Recorder relay, string? folder = null) =>
        _deployment.Client(
            server.PublicUrls[0],
            folder,
            o => new LicenseClientOptions
            {
                ProductId = o.ProductId,
                ServerUrl = o.ServerUrl,
                Trust = o.Trust,
                DataDirectory = o.DataDirectory,
                ClockGuard = o.ClockGuard,
                TimeProvider = o.TimeProvider,
                WrapHandler = relay.Over,
            }
        );

    private async Task<ElmServer> StartOpenServerAsync(Func<LicenseSpec, LicenseSpec>? license = null)
    {
        string data = _deployment.NewFolder();
        _deployment.WriteServerLicense(data, license);
        ElmServer server = await _deployment.StartServerAsync(data);
        _deployment.Time.Advance(Window);
        return server;
    }
}
