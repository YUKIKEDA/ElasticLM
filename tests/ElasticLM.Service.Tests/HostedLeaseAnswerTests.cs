using System.Net;
using ElasticLM.Client;
using ElasticLM.Licensing;
using static ElasticLM.Service.Tests.ElmServiceActivationsTests;
using static ElasticLM.Service.Tests.HostedLeaseClientTests;

namespace ElasticLM.Service.Tests;

/// <summary>
/// What <see cref="LicenseClient"/> with a license key does with a lease answer that is not the answer to its request
/// (design §5.3 T17, §12.7.5, §12.7.6, §10.4, V1d5), against a real <c>elm-service</c> in the same process. A relay between
/// the client and the service (<see cref="Recorder"/>) keeps genuine answers and hands them out again; every token it
/// hands out is signed by the service and bound to the client's installation key.
/// </summary>
public sealed class HostedLeaseAnswerTests
{
    private const string Features = """[{"code":"core","seats":5},{"code":"cam","seats":3}]""";
    private const string AnotherRequest = "The server's lease answers another request.";
    private const string AnotherKind = "The server's lease is not of the kind asked for.";
    private const string NoLaterSequence = "The server's renewal has no later sequence number than the lease held.";

    /// <summary>
    /// An answer of the service to an acquisition or a borrow that differs from the request in one thing is refused with
    /// ELM_REQUEST_MISMATCH and the sentence for that thing; an answer that differs in nothing but the request it answers
    /// is refused too. The client holds no lease afterwards and returns none by its ID; it releases what the service issued
    /// for its own request by that request's jti, which leaves the holder's lease alone (design §12.7.7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy with borrowLimit 86400 and a license with 5 seats of core and 3 of cam.
    /// - Two clients with the license key, the holder and a second one, on one data folder, so both have the same
    ///   installation key, and behind one relay.
    /// - The holder's request, whose genuine answer the relay keeps, and the second client's request, which the relay
    ///   answers with it without passing the request to the service:
    ///   feature: a lease of core, then a lease of cam;
    ///   feature-case: a lease of core, then a lease of Core, a name that differs only in the case of a letter;
    ///   count: a lease of 1 seat of core, then of 2 seats;
    ///   count-fewer: a lease of 2 seats of core, then of 1 seat;
    ///   borrow-for-lease: a borrow of core for an hour, then a lease of core;
    ///   lease-for-borrow: a lease of core, then a borrow of core for an hour;
    ///   longer-borrow: a borrow of core for 3601 seconds, then one for 3600 seconds;
    ///   same-lease: a lease of core, then the same;
    ///   same-borrow: a borrow of core for an hour, then the same.
    ///
    /// Steps:
    /// - Send the holder's request; count the saved borrows; send the second client's request; dispose the second client;
    ///   list the license's leases.
    ///
    /// Expected:
    /// - The holder's request gives a lease. The second gives Failed with ELM_REQUEST_MISMATCH, the case's message, and no
    ///   retry time; the relay replayed one answer.
    /// - No borrow file was added. The only DELETE the second client sent, before or when disposed, is
    ///   DELETE /v1/leases/requests/ with the jti of its own request, which the service never saw; the service lists the
    ///   holder's lease alone, and that lease is still Held.
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
    public async Task Hosted_ReplayedAcquisitionAnswerOfAnotherRequest_IsRefusedAndNoLeaseIsHeld(string kind, string message)
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        (string licenseId, string key) = await LicenseAsync(admin, await PolicyAsync(admin, "deny"), Features);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        var relay = new Recorder();
        string folder = test.FilePath("client");
        LicenseClient holder = HostedLeaseClientTests.Client(test, service, key, folder, wrap: relay.Over);
        LicenseClient second = HostedLeaseClientTests.Client(test, service, key, folder, wrap: relay.Over);
        var hour = TimeSpan.FromHours(1);

        Lease held = Acquired(
            kind switch
            {
                "borrow-for-lease" or "same-borrow" => await holder.BorrowLeaseAsync("core", hour, cancellationToken: cancel),
                "longer-borrow" => await holder.BorrowLeaseAsync("core", TimeSpan.FromSeconds(3601), cancellationToken: cancel),
                "count-fewer" => await holder.AcquireLeaseAsync("core", count: 2, cancellationToken: cancel),
                _ => await holder.AcquireLeaseAsync("core", cancellationToken: cancel),
            }
        );
        KeptAnswer kept = Assert.Single(relay.Answers());
        string[] borrows = BorrowFiles(folder);

        relay.ReplayToNextAcquisition(kept);
        LeaseResult result = kind switch
        {
            "feature" => await second.AcquireLeaseAsync("cam", cancellationToken: cancel),
            "feature-case" => await second.AcquireLeaseAsync("Core", cancellationToken: cancel),
            "count" => await second.AcquireLeaseAsync("core", count: 2, cancellationToken: cancel),
            "lease-for-borrow" or "same-borrow" => await second.BorrowLeaseAsync("core", hour, cancellationToken: cancel),
            "longer-borrow" => await second.BorrowLeaseAsync("core", TimeSpan.FromSeconds(3600), cancellationToken: cancel),
            _ => await second.AcquireLeaseAsync("core", cancellationToken: cancel),
        };

        var failed = Assert.IsType<LeaseResult.Failed>(result);
        Assert.Equal(new LicenseError(ErrorCode.RequestMismatch, message), failed.Error);
        Assert.Null(failed.RetryAfter);
        Assert.Equal(1, relay.Replayed);
        Assert.Equal(borrows, BorrowFiles(folder));
        await second.DisposeAsync();
        string asked = relay.Requests().Last(r => r.Method == "POST" && r.Path == "/v1/leases").ProofId!;
        Assert.Equal(["/v1/leases/requests/" + asked], relay.Requests().Where(r => r.Method == "DELETE").Select(r => r.Path));
        ListedLeases leases = await LeasesAsync(admin, licenseId);
        Assert.Equal([held.Id], leases.Ids);
        Assert.Equal(LeaseState.Held, held.State);
        await holder.DisposeAsync();
    }

    /// <summary>
    /// An answer of the service to a renewal that differs from the lease held in one thing is refused with
    /// ELM_REQUEST_MISMATCH and the sentence for that thing: the lease keeps its token, goes to Retrying, and renews at
    /// the retry.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy (lease 600 seconds, borrowLimit 86400) and a license with 5 seats of core and 3 of cam; a
    ///   client holding lease A of 1 seat of core, behind a relay.
    /// - The genuine answer the relay gives to A's next renewal, without passing the renewal to the service. Where it is
    ///   another lease's, a second client on the same data folder (the same installation key) got it and returned that
    ///   lease, so that only A renews:
    ///   another-lease: the acquisition answer of another lease of 1 seat of core;
    ///   another-feature: of a lease of 1 seat of cam;
    ///   another-count: of a lease of 2 seats of core;
    ///   borrow: of a borrow of 1 seat of core, which is kept;
    ///   same-sequence: A's own acquisition answer (sequence 1), while A holds sequence 1;
    ///   older-sequence: A's own acquisition answer, after A renewed to sequence 2.
    ///
    /// Steps:
    /// - Move the clock 200 s (A's renewal, answered by the relay); then 15 s (the retry, which reaches the service).
    ///
    /// Expected:
    /// - Retrying with ELM_REQUEST_MISMATCH and the case's message; A's token is the one it held; the relay replayed one
    ///   answer.
    /// - After the retry A is Held with the next sequence number.
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
    public async Task Hosted_ReplayedRenewalAnswerOfAnotherRequest_IsRefusedAndTheLeaseRetries(string kind, string message)
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        (_, string key) = await LicenseAsync(admin, await PolicyAsync(admin, "deny"), Features);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        var relay = new Recorder();
        string folder = test.FilePath("client");
        LicenseClient client = HostedLeaseClientTests.Client(test, service, key, folder, wrap: relay.Over);
        Lease lease = Acquired(await client.AcquireLeaseAsync("core", cancellationToken: cancel));
        KeptAnswer own = Assert.Single(relay.Answers());
        LicenseClient source = HostedLeaseClientTests.Client(test, service, key, folder, wrap: relay.Over);
        switch (kind)
        {
            case "another-lease":
                Assert.Null(await source.ReleaseLeaseAsync(Acquired(await source.AcquireLeaseAsync("core", cancellationToken: cancel)), cancel));
                break;
            case "another-feature":
                Assert.Null(await source.ReleaseLeaseAsync(Acquired(await source.AcquireLeaseAsync("cam", cancellationToken: cancel)), cancel));
                break;
            case "another-count":
                Assert.Null(
                    await source.ReleaseLeaseAsync(Acquired(await source.AcquireLeaseAsync("core", count: 2, cancellationToken: cancel)), cancel)
                );
                break;
            case "borrow":
                Acquired(await source.BorrowLeaseAsync("core", TimeSpan.FromHours(1), cancellationToken: cancel));
                break;
            case "older-sequence":
                test.Time.Advance(TimeSpan.FromSeconds(200));
                await TestService.WaitForAsync(() => Task.FromResult(lease.Token.Sequence == 2));
                break;
            default:
                break;
        }

        KeptAnswer replayed = kind is "same-sequence" or "older-sequence" ? own : relay.Answers()[1];
        VerifiedLease token = lease.Token;
        relay.ReplayToNextRenewal(replayed);

        test.Time.Advance(TimeSpan.FromSeconds(200));
        await TestService.WaitForAsync(() => Task.FromResult(lease.State == LeaseState.Retrying));
        Assert.Equal(new LicenseError(ErrorCode.RequestMismatch, message), lease.LastError);
        Assert.Same(token, lease.Token);
        Assert.Equal(1, relay.Replayed);

        test.Time.Advance(TimeSpan.FromSeconds(15));
        await TestService.WaitForAsync(() => Task.FromResult(lease.State == LeaseState.Held && lease.Token.Sequence == token.Sequence + 1));
        await source.DisposeAsync();
        await client.DisposeAsync();
    }

    /// <summary>
    /// A renewal answer of the service that matches the lease held in everything but the request it answers is refused: the
    /// token another copy of the installation got for the same lease does not renew this copy's lease, and the service
    /// then finds the clone.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy with cloneStrategy deny and a license with seats of core; a client holding a lease of core
    ///   (sequence 1), behind a relay.
    /// - Another copy of the installation (the same key folder) renews the lease at the service with sequence 1 and gets
    ///   the answer with sequence 2, whose token names that copy's request. The relay answers the client's next renewal
    ///   with that answer, without passing the renewal to the service.
    ///
    /// Steps:
    /// - Move the clock 200 s (the client's renewal); then 15 s (its retry, which reaches the service).
    ///
    /// Expected:
    /// - Retrying with ELM_REQUEST_MISMATCH "The server's renewal answers another request.", still holding the token of
    ///   sequence 1.
    /// - The retry, one sequence number behind and naming only the client's own request, is a clone to the service: the
    ///   lease is Lost with ELM_CLONE_DETECTED.
    /// </remarks>
    [Fact]
    public async Task Hosted_ReplayedAnswerToAnotherCopysRenewal_IsRefused()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        (_, string key) = await LicenseAsync(admin, await PolicyAsync(admin, "deny"), Features);
        var relay = new Recorder();
        string folder = test.FilePath("client");
        LicenseClient client = HostedLeaseClientTests.Client(test, service, key, folder, wrap: relay.Over);
        Lease lease = Acquired(await client.AcquireLeaseAsync("core", cancellationToken: TestContext.Current.CancellationToken));
        using (var copy = TestDevice.Create(test, service, folder))
        {
            Assert.Equal(client.InstallationKeyThumbprint, copy.KeyId);
            DeviceAnswer other = await copy.RenewAsync(lease.Id, 1);
            Assert.Equal((HttpStatusCode.OK, 2L), (other.Status, copy.VerifyLease(other).Sequence));
            relay.ReplayToNextRenewal(new KeptAnswer($"/v1/leases/{lease.Id}/renew", HttpStatusCode.OK, other.Text, null));
        }

        test.Time.Advance(TimeSpan.FromSeconds(200));
        await TestService.WaitForAsync(() => Task.FromResult(lease.State == LeaseState.Retrying));
        Assert.Equal(new LicenseError(ErrorCode.RequestMismatch, "The server's renewal answers another request."), lease.LastError);
        Assert.Equal((1L, 1), (lease.Token.Sequence, relay.Replayed));

        test.Time.Advance(TimeSpan.FromSeconds(15));
        await TestService.WaitForAsync(() => Task.FromResult(lease.State == LeaseState.Lost));
        Assert.Equal(ErrorCode.CloneDetected, lease.LastError?.Code);
        await client.DisposeAsync();
    }

    /// <summary>
    /// When the client refuses the answer to a renewal that the service handled, its retry names the refused request, and
    /// the service returns the token of that request again instead of taking the retry for a clone (design §10.4 step 2,
    /// §12.7.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy with cloneStrategy deny and a license with seats of core; a client holding a lease of core
    ///   (sequence 1), behind a relay that passes the next renewal to the service, which renews to sequence 2, and gives
    ///   the client its kept acquisition answer (sequence 1) in place of the service's.
    ///
    /// Steps:
    /// - Move the clock 200 s (the renewal); then 15 s (the retry); then 200 s (the next renewal).
    ///
    /// Expected:
    /// - Retrying with ELM_REQUEST_MISMATCH "The server's renewal has no later sequence number than the lease held.".
    /// - The retry's body is {"seq":1,"retry":[the jti of the refused renewal's proof]}; the lease is Held with sequence
    ///   2, and its token names the refused renewal's proof, not the retry's.
    /// - The next renewal gives sequence 3, and the service lists the lease as not stopped.
    /// </remarks>
    [Fact]
    public async Task Hosted_AnswerRefusedAfterTheServiceRenewed_RetryGetsThatTokenAgain()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        (string licenseId, string key) = await LicenseAsync(admin, await PolicyAsync(admin, "deny"), Features);
        var relay = new Recorder { PassReplacedOn = true };
        LicenseClient client = HostedLeaseClientTests.Client(test, service, key, test.FilePath("client"), wrap: relay.Over);
        Lease lease = Acquired(await client.AcquireLeaseAsync("core", cancellationToken: TestContext.Current.CancellationToken));
        relay.ReplayToNextRenewal(Assert.Single(relay.Answers()));

        test.Time.Advance(TimeSpan.FromSeconds(200));
        await TestService.WaitForAsync(() => Task.FromResult(lease.State == LeaseState.Retrying));
        Assert.Equal(new LicenseError(ErrorCode.RequestMismatch, NoLaterSequence), lease.LastError);
        Assert.Equal((1L, 1), (lease.Token.Sequence, relay.Replayed));
        string refused = Assert.Single(Renewals(relay)).ProofId!;

        test.Time.Advance(TimeSpan.FromSeconds(15));
        await TestService.WaitForAsync(() => Task.FromResult(lease.State == LeaseState.Held));
        SentRequest retry = Renewals(relay)[1];
        Assert.Equal($$"""{"seq":1,"retry":["{{refused}}"]}""", retry.Body);
        Assert.NotEqual(refused, retry.ProofId);
        Assert.Equal((2L, refused), (lease.Token.Sequence, lease.Token.AnsweredProofId));

        test.Time.Advance(TimeSpan.FromSeconds(200));
        await TestService.WaitForAsync(() => Task.FromResult(lease.Token.Sequence == 3));
        Assert.Equal(LeaseState.Held, lease.State);
        ListedLeases listed = await LeasesAsync(admin, licenseId);
        Assert.False(Assert.Single(listed.Stopped));
        await client.DisposeAsync();
    }

    /// <summary>
    /// The answers of a correct service pass the client's comparison: each token names the proof of its request, a borrow
    /// that the service cut at the license's expiry is taken, the token returned again after a lost answer is taken, and so
    /// is a renewal that skips a sequence number after the service flagged a clone.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy (lease 600 seconds, borrowLimit 86400, cloneStrategy flag) and a license with seats of
    ///   core that expires 3000 seconds after the start; a client behind a relay that records, and loses one answer.
    ///
    /// Steps:
    /// - Acquire core; borrow core for 2 hours.
    /// - Make the relay lose the answer of the next renewal after the service handled it; move the clock 200 s (the
    ///   renewal); then 15 s (the retry).
    /// - Let another copy of the installation (the same key folder) renew the lease with sequence 2; move the clock 200 s
    ///   so the client renews with sequence 2.
    ///
    /// Expected:
    /// - The lease's token names the proof of the acquisition. The borrow is given although it is shorter than asked: it
    ///   ends at the license's expiry, 3000 seconds on, and its token names the proof of the borrow's request.
    /// - Retrying with ELM_NETWORK_ERROR; after the retry Held with sequence 2, and the token names the proof of the
    ///   renewal whose answer was lost, not the retry's.
    /// - The copy gets sequence 3. The client's renewal is flagged and answered with sequence 4, which the client takes:
    ///   Held, and the token names the proof of that renewal.
    /// </remarks>
    [Fact]
    public async Task Hosted_CorrectService_CutBorrowLostAnswerAndFlaggedRenewal_AreAccepted()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        (_, string key) = await LicenseAsync(admin, await PolicyAsync(admin, "flag"), Features, $",\"expiresAt\":\"{Iso(3000)}\"");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        var relay = new Recorder();
        string folder = test.FilePath("client");
        LicenseClient client = HostedLeaseClientTests.Client(test, service, key, folder, wrap: relay.Over);

        Lease lease = Acquired(await client.AcquireLeaseAsync("core", cancellationToken: cancel));
        Lease borrow = Acquired(await client.BorrowLeaseAsync("core", TimeSpan.FromHours(2), cancellationToken: cancel));
        SentRequest[] acquisitions = [.. relay.Requests().Where(r => r.Method == "POST" && r.Path == "/v1/leases")];
        Assert.Equal(new string?[] { lease.Token.AnsweredProofId, borrow.Token.AnsweredProofId }, acquisitions.Select(r => r.ProofId).ToArray());
        Assert.Equal((true, Time(3000)), (borrow.IsBorrow, borrow.ExpiresAt));
        Assert.Equal(TimeSpan.FromSeconds(3000), borrow.ExpiresAt - borrow.Token.IssuedAt);

        relay.LoseNextRenewalAnswer = true;
        test.Time.Advance(TimeSpan.FromSeconds(200));
        await TestService.WaitForAsync(() => Task.FromResult(lease.State == LeaseState.Retrying));
        Assert.Equal(ErrorCode.NetworkError, lease.LastError?.Code);
        test.Time.Advance(TimeSpan.FromSeconds(15));
        await TestService.WaitForAsync(() => Task.FromResult(lease.State == LeaseState.Held));
        Assert.Equal(2, Renewals(relay).Length);
        Assert.Equal((2L, Renewals(relay)[0].ProofId), (lease.Token.Sequence, lease.Token.AnsweredProofId));

        using (var copy = TestDevice.Create(test, service, folder))
        {
            DeviceAnswer other = await copy.RenewAsync(lease.Id, 2);
            Assert.Equal((HttpStatusCode.OK, 3L), (other.Status, copy.VerifyLease(other).Sequence));
        }

        test.Time.Advance(TimeSpan.FromSeconds(200));
        await TestService.WaitForAsync(() => Task.FromResult(lease.Token.Sequence == 4));
        Assert.Equal(LeaseState.Held, lease.State);
        Assert.Equal((3, Renewals(relay)[^1].ProofId), (Renewals(relay).Length, lease.Token.AnsweredProofId));
        await client.DisposeAsync();
    }

    /// <summary>
    /// When the client does not take a success answer to a renewal that the service handled, because the token fails
    /// verification or the body is not a lease answer, its retry names that request too, and the service returns the token
    /// of that request again (design §10.4 step 2, §12.7.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy with cloneStrategy deny and a license with seats of core; a client holding a lease of core
    ///   (sequence 1), behind a relay that passes the next renewal to the service, which renews to sequence 2, and changes
    ///   the answer on its way back: bad-signature changes one character of the token's signature; not-json replaces the
    ///   body by "{".
    ///
    /// Steps:
    /// - Move the clock 200 s (the renewal); then 15 s (the retry).
    ///
    /// Expected:
    /// - Retrying, still with the token of sequence 1: ELM_BAD_SIGNATURE "The lease signature does not verify." for
    ///   bad-signature, ELM_MALFORMED "The server's lease answer is not valid." for not-json.
    /// - The retry's body is {"seq":1,"retry":[the jti of the first renewal's proof]}; the lease is Held with sequence 2,
    ///   and its token names the first renewal's proof.
    /// </remarks>
    /// <param name="kind">The case.</param>
    /// <param name="code">The error expected.</param>
    /// <param name="message">The message expected.</param>
    [Theory]
    [InlineData("bad-signature", ErrorCode.BadSignature, "The lease signature does not verify.")]
    [InlineData("not-json", ErrorCode.Malformed, "The server's lease answer is not valid.")]
    public async Task Hosted_AnswerNotTakenAfterTheServiceRenewed_RetryGetsThatTokenAgain(string kind, ErrorCode code, string message)
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        (_, string key) = await LicenseAsync(admin, await PolicyAsync(admin, "deny"), Features);
        var relay = new Recorder();
        LicenseClient client = HostedLeaseClientTests.Client(test, service, key, test.FilePath("client"), wrap: relay.Over);
        Lease lease = Acquired(await client.AcquireLeaseAsync("core", cancellationToken: TestContext.Current.CancellationToken));
        relay.RewriteNextRenewal = kind == "not-json" ? _ => "{" : Recorder.WithBrokenSignature;

        test.Time.Advance(TimeSpan.FromSeconds(200));
        await TestService.WaitForAsync(() => Task.FromResult(lease.State == LeaseState.Retrying));
        Assert.Equal(new LicenseError(code, message), lease.LastError);
        Assert.Equal(1, lease.Token.Sequence);
        string refused = Assert.Single(Renewals(relay)).ProofId!;

        test.Time.Advance(TimeSpan.FromSeconds(15));
        await TestService.WaitForAsync(() => Task.FromResult(lease.State == LeaseState.Held));
        Assert.Equal($$"""{"seq":1,"retry":["{{refused}}"]}""", Renewals(relay)[1].Body);
        Assert.Equal((2L, refused), (lease.Token.Sequence, lease.Token.AnsweredProofId));
        await client.DisposeAsync();
    }

    /// <summary>
    /// A service with cloneStrategy flag whose sequence number is behind the client's answers the client's renewal with a
    /// token later than the client's, which the client takes at once (design §10.4, §12.7.5 step 8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy with cloneStrategy flag and a license with seats of core; a client holding a lease of core
    ///   that it renewed twice (sequence 3).
    /// - The lease's row is set back to sequence 1 in the database, as a database restored from an older backup would
    ///   have it.
    ///
    /// Steps:
    /// - Move the clock 200 s so the client renews with sequence 3.
    ///
    /// Expected:
    /// - The renewal is flagged as a clone and answered with sequence 4, one more than the client's: the lease is Held with
    ///   sequence 4 after that one renewal, three renewals in all.
    /// </remarks>
    [Fact]
    public async Task Hosted_FlaggedByAServiceBehindTheClient_IsAcceptedAtOnce()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        (_, string key) = await LicenseAsync(admin, await PolicyAsync(admin, "flag"), Features);
        var relay = new Recorder();
        LicenseClient client = HostedLeaseClientTests.Client(test, service, key, test.FilePath("client"), wrap: relay.Over);
        Lease lease = Acquired(await client.AcquireLeaseAsync("core", cancellationToken: TestContext.Current.CancellationToken));
        test.Time.Advance(TimeSpan.FromSeconds(200));
        await TestService.WaitForAsync(() => Task.FromResult(lease.Token.Sequence == 2));
        test.Time.Advance(TimeSpan.FromSeconds(200));
        await TestService.WaitForAsync(() => Task.FromResult(lease.Token.Sequence == 3));
        await test.ExecuteAsync($"UPDATE leases SET seq = 1 WHERE id = '{lease.Id}'");
        Assert.Equal(1, await test.ScalarAsync($"SELECT seq FROM leases WHERE id = '{lease.Id}'"));

        test.Time.Advance(TimeSpan.FromSeconds(200));
        await TestService.WaitForAsync(() => Task.FromResult(lease.Token.Sequence == 4));
        Assert.Equal(LeaseState.Held, lease.State);
        Assert.Equal(3, Renewals(relay).Length);
        Assert.Equal(4, await test.ScalarAsync($"SELECT seq FROM leases WHERE id = '{lease.Id}'"));
        await client.DisposeAsync();
    }

    /// <summary>
    /// With a license key, an acquisition or a borrow whose answer the client does not get or does not take is released at
    /// once by its proof's jti, and a release that does not get through is sent before the next request: the seat is free
    /// again without the app acquiring again, and a borrow counts as an early return (design §12.7.7, V1d5b).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A cloud-floating policy with borrowLimit 86400 and borrowReturnLimit 1, and a license with 5 seats of core and 3 of
    ///   cam; a client with its key behind a relay that passes each request to the service, which issues the lease.
    ///
    /// Steps:
    /// - Lose the answer of an acquisition of core (J1); list the leases.
    /// - Give the answer of a borrow of core over 256 KiB (J2); list the leases.
    /// - Lose the answer of an acquisition of cam (J3) and make its release fail; list the leases; acquire cam; list.
    /// - Borrow core and return it.
    ///
    /// Expected:
    /// - ELM_NETWORK_ERROR; right after the acquisition, DELETE /v1/leases/requests/J1; no lease is listed.
    /// - ELM_MALFORMED; DELETE /v1/leases/requests/J2; no lease is listed.
    /// - ELM_NETWORK_ERROR and one lease listed; the next acquisition sends DELETE /v1/leases/requests/J3 before its POST,
    ///   and only its own lease is listed.
    /// - The return is refused with ELM_RETURN_NOT_ALLOWED: J2's release was the early return of 30 days.
    /// </remarks>
    [Fact]
    public async Task Hosted_AnswerNotTaken_ReleasesThatRequestsLease()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin, _) = await StartAsync(test);
        string product = await ProductAsync(admin, "app", "core", "cam");
        string policy = await TestService.PolicyAsync(admin, product, "\"floating\":\"cloud\",\"borrowLimit\":86400,\"borrowReturnLimit\":1");
        (string licenseId, string key) = await LicenseAsync(admin, policy, Features);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        var relay = new Recorder();
        LicenseClient client = HostedLeaseClientTests.Client(test, service, key, test.FilePath("client"), wrap: relay.Over);
        string[] Released() => [.. relay.Requests().Where(r => r.Method == "DELETE").Select(r => r.Path)];
        string LastAcquisition() => relay.Requests().Last(r => r.Method == "POST" && r.Path == "/v1/leases").ProofId!;

        relay.LoseNextAcquisitionAnswer = true;
        Assert.Equal(
            ErrorCode.NetworkError,
            Assert.IsType<LeaseResult.Failed>(await client.AcquireLeaseAsync("core", cancellationToken: cancel)).Error.Code
        );
        Assert.Equal(["/v1/leases/requests/" + LastAcquisition()], Released());
        Assert.Empty((await LeasesAsync(admin, licenseId)).Ids);

        relay.OversizeNextAcquisition = true;
        LeaseResult oversized = await client.BorrowLeaseAsync("core", TimeSpan.FromHours(1), cancellationToken: cancel);
        Assert.Equal(ErrorCode.Malformed, Assert.IsType<LeaseResult.Failed>(oversized).Error.Code);
        Assert.Equal("/v1/leases/requests/" + LastAcquisition(), Released()[^1]);
        Assert.Empty((await LeasesAsync(admin, licenseId)).Ids);

        relay.LoseNextAcquisitionAnswer = true;
        relay.FailRequestReleases = 1;
        Assert.Equal(
            ErrorCode.NetworkError,
            Assert.IsType<LeaseResult.Failed>(await client.AcquireLeaseAsync("cam", cancellationToken: cancel)).Error.Code
        );
        string owed = LastAcquisition();
        Assert.Single((await LeasesAsync(admin, licenseId)).Ids);
        int before = relay.Requests().Length;
        Lease cam = Acquired(await client.AcquireLeaseAsync("cam", cancellationToken: cancel));
        Assert.Equal(
            ["DELETE /v1/leases/requests/" + owed, "POST /v1/leases"],
            relay.Requests().Skip(before).Where(r => r.Method != "GET").Select(r => r.Method + " " + r.Path)
        );
        Assert.Equal([cam.Id], (await LeasesAsync(admin, licenseId)).Ids);

        Lease borrow = Acquired(await client.BorrowLeaseAsync("core", TimeSpan.FromHours(1), cancellationToken: cancel));
        Assert.Equal(ErrorCode.ReturnNotAllowed, (await client.ReleaseLeaseAsync(borrow, cancel))?.Code);
        await client.DisposeAsync();
    }

    private static SentRequest[] Renewals(Recorder relay) =>
        [.. relay.Requests().Where(r => r.Method == "POST" && r.Path.EndsWith("/renew", StringComparison.Ordinal))];

    // The saved borrows under a client's data folder, by path.
    private static string[] BorrowFiles(string folder) =>
        [
            .. Directory
                .EnumerateFiles(folder, "*.json", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(Path.GetDirectoryName(path)) == "borrows")
                .Order(),
        ];

    // Product app with features core and cam, and a cloud-floating policy of it that lends borrows of up to a day.
    private static async Task<string> PolicyAsync(HttpClient admin, string cloneStrategy)
    {
        string product = await ProductAsync(admin, "app", "core", "cam");
        return await TestService.PolicyAsync(admin, product, $"\"floating\":\"cloud\",\"borrowLimit\":86400,\"cloneStrategy\":\"{cloneStrategy}\"");
    }

    // The IDs and the stopped flags of a license's leases, as the administration API lists them.
    private static async Task<ListedLeases> LeasesAsync(HttpClient admin, string licenseId)
    {
        System.Text.Json.JsonElement items = (await TestService.GetAsync(admin, $"v1/admin/licenses/{licenseId}/leases")).GetProperty("items");
        return new ListedLeases(
            [.. items.EnumerateArray().Select(i => i.GetProperty("id").GetString()!)],
            [.. items.EnumerateArray().Select(i => i.GetProperty("stopped").GetBoolean())]
        );
    }

    private sealed record ListedLeases(string[] Ids, bool[] Stopped);
}
