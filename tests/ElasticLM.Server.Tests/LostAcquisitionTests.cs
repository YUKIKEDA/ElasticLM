using System.Net;
using ElasticLM.Client;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using ElasticLM.Service.Tests;

namespace ElasticLM.Server.Tests;

/// <summary>
/// What <see cref="LicenseClient"/> does when the answer to an acquisition or a borrow does not come or is not taken, against
/// an in-process <c>elm-server</c> over loopback TLS (design §12.7.7, V1d5b): it releases the lease the server issued for that
/// request by the jti of the request's proof, and owes the release, in memory, until it gets through. A relay
/// (<see cref="Recorder"/>) loses, enlarges, or holds answers after the server handled the request.
/// </summary>
public sealed class LostAcquisitionTests : IAsyncDisposable
{
    private const string RequestReleases = "/v1/leases/requests/";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly Deployment _deployment = new();

    public ValueTask DisposeAsync() => _deployment.DisposeAsync();

    /// <summary>
    /// An acquisition or a borrow whose answer the client does not get or does not take is released at once by its proof's
    /// jti: the seat is free again without the app acquiring again, and a borrow counts as an early return.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server with 2 seats of pro and 2 early returns per 30 days, past its reclaim window; a client behind a relay, on
    ///   a data folder of its own. The relay passes the request to the server, which issues the lease, and then:
    ///   lost: throws the answer away; oversized: gives a body of 256 KiB and a byte; bad-signature: changes one character
    ///   of the token's signature; borrow-lost: throws away the answer to a borrow of an hour; borrow-unsaved: passes the
    ///   answer to a borrow of an hour, while a file named borrows in the key's folder keeps it from being saved.
    ///
    /// Steps:
    /// - Acquire pro (or borrow it for an hour); count the server's seats; then, for a lease, acquire pro twice; for a borrow,
    ///   borrow pro for an hour, return it, and borrow and return once more.
    ///
    /// Expected:
    /// - Failed with the case's code and no retry time.
    /// - Right after the acquisition, the client sent DELETE /v1/leases/requests/ with the acquisition's jti, and no DELETE
    ///   of a lease ID; the server holds no seat.
    /// - For a lease, both acquisitions give a lease. For a borrow, the return after it is Released and the next return is
    ///   refused with ELM_RETURN_NOT_ALLOWED: the first release counted as one of the 2 early returns.
    /// </remarks>
    /// <param name="kind">The case.</param>
    /// <param name="code">The error expected.</param>
    [Theory]
    [InlineData("lost", ErrorCode.NetworkError)]
    [InlineData("oversized", ErrorCode.Malformed)]
    [InlineData("bad-signature", ErrorCode.BadSignature)]
    [InlineData("borrow-lost", ErrorCode.NetworkError)]
    [InlineData("borrow-unsaved", ErrorCode.InternalError)]
    public async Task Acquire_AnswerNotTaken_ReleasesThatRequestsLeaseAtOnce(string kind, ErrorCode code)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        ElmServer server = await StartOpenServerAsync();
        var relay = new Recorder();
        string folder = _deployment.NewFolder();
        LicenseClient client = Client(server, relay, folder);
        bool borrow = kind.StartsWith("borrow", StringComparison.Ordinal);
        switch (kind)
        {
            case "lost" or "borrow-lost":
                relay.LoseNextAcquisitionAnswer = true;
                break;
            case "oversized":
                relay.OversizeNextAcquisition = true;
                break;
            case "bad-signature":
                relay.Rewrite = Recorder.WithBrokenSignature;
                break;
            default:
                File.WriteAllText(System.IO.Path.Combine(KeyFolder(folder), "borrows"), "not a folder");
                break;
        }

        LeaseResult result = borrow
            ? await client.BorrowLeaseAsync("pro", TimeSpan.FromHours(1), cancellationToken: cancel)
            : await client.AcquireLeaseAsync("pro", cancellationToken: cancel);
        relay.Rewrite = null;

        var failed = Assert.IsType<LeaseResult.Failed>(result);
        Assert.Equal((code, (TimeSpan?)null), (failed.Error.Code, failed.RetryAfter));
        SentRequest[] sent = Requests(relay);
        Assert.Equal(["POST /v1/leases", "DELETE " + RequestReleases + sent[0].ProofId], sent.Select(r => r.Method + " " + r.Path));
        Assert.NotNull(sent[1].ProofId);
        Assert.Equal(0, server.Engine.SeatsInUse("pro"));

        if (!borrow)
        {
            Assert.IsType<LeaseResult.Acquired>(await client.AcquireLeaseAsync("pro", cancellationToken: cancel));
            Assert.IsType<LeaseResult.Acquired>(await client.AcquireLeaseAsync("pro", cancellationToken: cancel));
            return;
        }

        File.Delete(System.IO.Path.Combine(KeyFolder(folder), "borrows"));
        Lease returned = Acquired(await client.BorrowLeaseAsync("pro", TimeSpan.FromHours(1), cancellationToken: cancel));
        Assert.Null(await client.ReleaseLeaseAsync(returned, cancel));
        Lease refused = Acquired(await client.BorrowLeaseAsync("pro", TimeSpan.FromHours(1), cancellationToken: cancel));
        Assert.Equal(ErrorCode.ReturnNotAllowed, (await client.ReleaseLeaseAsync(refused, cancel))?.Code);
    }

    /// <summary>
    /// A release that does not get through is owed: it is sent before the next acquisition, before the next renewal, and
    /// when the client is disposed, and the seat is then free.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server with 2 seats of pro, past its reclaim window; a client behind a relay that loses the answer of the next
    ///   acquisition after the server issued the lease and makes the next release by a request's jti fail before it is sent.
    ///
    /// Steps:
    /// - Acquire pro (J1); acquire pro again (L).
    /// - Arm the relay again; acquire pro (J2); move the clock 200 s, so the client renews L.
    /// - Arm the relay again; acquire pro (J3); return L; acquire pro (M).
    /// - Arm the relay again; acquire pro (J4); dispose the client.
    ///
    /// Expected:
    /// - The first acquisition fails with ELM_NETWORK_ERROR and its release fails: 1 seat is held. The next acquisition
    ///   sends DELETE /v1/leases/requests/J1 before its POST and gives L: 1 seat is held.
    /// - J2's lease holds a seat until the renewal, which sends DELETE /v1/leases/requests/J2 before L's renewal: then 1 seat.
    /// - J3's lease holds a seat until L is returned: DELETE /v1/leases/requests/J3 goes before DELETE /v1/leases/L, and then
    ///   no seat is held; M holds 1.
    /// - J4's lease holds a seat until the client is disposed, which sends DELETE /v1/leases/requests/J4 and returns M: then
    ///   no seat.
    /// </remarks>
    [Fact]
    public async Task Acquire_ReleaseNotThrough_IsSentBeforeTheNextRequestAndOnDispose()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        ElmServer server = await StartOpenServerAsync();
        var relay = new Recorder();
        LicenseClient client = Client(server, relay);

        string j1 = await LoseAcquisitionAsync(client, relay);
        Assert.Equal(1, server.Engine.SeatsInUse("pro"));
        Lease lease = Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: cancel));
        Assert.Equal(["DELETE " + RequestReleases + j1, "POST /v1/leases"], After(relay, j1).Take(2));
        Assert.Equal(1, server.Engine.SeatsInUse("pro"));

        string j2 = await LoseAcquisitionAsync(client, relay);
        Assert.Equal(2, server.Engine.SeatsInUse("pro"));
        _deployment.Time.Advance(TimeSpan.FromSeconds(200));
        await Deployment.WaitUntilAsync(() => lease.Token.Sequence == 2);
        Assert.Equal(["DELETE " + RequestReleases + j2, $"POST /v1/leases/{lease.Id}/renew"], After(relay, j2));
        Assert.Equal(1, server.Engine.SeatsInUse("pro"));

        string j3 = await LoseAcquisitionAsync(client, relay);
        Assert.Equal(2, server.Engine.SeatsInUse("pro"));
        Assert.Null(await client.ReleaseLeaseAsync(lease, cancel));
        Assert.Equal(["DELETE " + RequestReleases + j3, $"DELETE /v1/leases/{lease.Id}"], After(relay, j3));
        Assert.Equal(0, server.Engine.SeatsInUse("pro"));
        Lease next = Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: cancel));

        string j4 = await LoseAcquisitionAsync(client, relay);
        Assert.Equal(2, server.Engine.SeatsInUse("pro"));
        await client.DisposeAsync();
        Assert.Equal(["DELETE " + RequestReleases + j4, $"DELETE /v1/leases/{next.Id}"], After(relay, j4));
        Assert.Equal(0, server.Engine.SeatsInUse("pro"));
    }

    /// <summary>
    /// The client owes at most 16 releases and drops the oldest past that; it sends them oldest first and stops at the first
    /// that does not get through, so a server it cannot reach costs one wait per request, not one per release.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server with 20 seats of pro, past its reclaim window; a client behind a relay that loses the answer of every
    ///   acquisition after the server issued the lease and makes every release by a request's jti fail.
    ///
    /// Steps:
    /// - Acquire pro 17 times (J1 to J17); let the releases through and the answers too; acquire pro.
    ///
    /// Expected:
    /// - 17 failures with ELM_NETWORK_ERROR and 17 seats held. The first acquisition tried one release (its own); each later
    ///   one tried two (the oldest owed one, then its own): 33 tries.
    /// - The last acquisition sends DELETE /v1/leases/requests/ for J2 to J17 in that order, then its POST, and gives a lease:
    ///   2 seats are held, J1's and the new one.
    /// </remarks>
    [Fact]
    public async Task OwedReleases_AreSixteenAtMost_AndStopAtTheFirstFailure()
    {
        ElmServer server = await StartOpenServerAsync(s => s with { Features = [new FeatureSpec("pro", ">=2.0 <3.0", Seats: 20)] });
        var relay = new Recorder { FailRequestReleases = int.MaxValue };
        LicenseClient client = Client(server, relay);
        var lost = new List<string>();
        for (int i = 0; i < 17; i++)
        {
            relay.LoseNextAcquisitionAnswer = true;
            var failed = Assert.IsType<LeaseResult.Failed>(
                await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken)
            );
            Assert.Equal(ErrorCode.NetworkError, failed.Error.Code);
            lost.Add(relay.Requests().Last(r => r.Method == "POST").ProofId!);
        }

        Assert.Equal(17, server.Engine.SeatsInUse("pro"));
        Assert.Equal(33, relay.Requests().Count(r => r.Path.StartsWith(RequestReleases, StringComparison.Ordinal)));

        relay.FailRequestReleases = 0;
        int before = Requests(relay).Length;
        Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(
            [.. lost.Skip(1).Select(j => "DELETE " + RequestReleases + j), "POST /v1/leases"],
            Requests(relay).Skip(before).Select(r => r.Method + " " + r.Path)
        );
        Assert.Equal(2, server.Engine.SeatsInUse("pro"));
    }

    /// <summary>
    /// A release by a request's jti got through when the answer's status is neither 429 nor 5xx: only then is it not sent
    /// again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server with 2 seats of pro, past its reclaim window; a client behind a relay that loses the answer of the next
    ///   acquisition after the server issued the lease and answers the release that follows with the case's status and no
    ///   body, without passing it to the server.
    ///
    /// Steps:
    /// - Acquire pro (J); acquire pro again.
    ///
    /// Expected:
    /// - 429 and 503: the second acquisition sends DELETE /v1/leases/requests/J before its POST, and 1 seat is held.
    /// - 404 and 409: the second acquisition sends only its POST, and 2 seats are held (J's lease ends at its expiry).
    /// </remarks>
    /// <param name="status">The status of the release's answer.</param>
    /// <param name="owed">Whether the release is sent again.</param>
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Conflict, false)]
    public async Task Release_StatusOfItsAnswer_DecidesWhetherItIsOwed(HttpStatusCode status, bool owed)
    {
        ElmServer server = await StartOpenServerAsync();
        var relay = new Recorder();
        LicenseClient client = Client(server, relay);
        relay.LoseNextAcquisitionAnswer = true;
        relay.RefuseRequestReleases(status);
        Assert.IsType<LeaseResult.Failed>(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
        string jti = Requests(relay).Last(r => r.Method == "POST").ProofId!;

        int before = Requests(relay).Length;
        Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
        string[] expected = owed ? ["DELETE " + RequestReleases + jti, "POST /v1/leases"] : ["POST /v1/leases"];
        Assert.Equal(expected, Requests(relay).Skip(before).Select(r => r.Method + " " + r.Path));
        Assert.Equal(owed ? 1 : 2, server.Engine.SeatsInUse("pro"));
    }

    /// <summary>
    /// An acquisition that the app cancels after the server issued the lease does not wait for its release: the release is
    /// owed and sent before the next request.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server with 2 seats of pro, past its reclaim window; a client behind a relay that holds the answer of the next
    ///   acquisition after the server issued the lease.
    ///
    /// Steps:
    /// - Acquire pro with a token; once the server issued the lease, cancel the token; then acquire pro again.
    ///
    /// Expected:
    /// - Failed with ELM_NETWORK_ERROR, and 1 seat held (the canceled acquisition's).
    /// - The next acquisition sends DELETE /v1/leases/requests/ with the canceled acquisition's jti before its POST, gives a
    ///   lease, and 1 seat is held.
    /// </remarks>
    [Fact]
    public async Task Acquire_Canceled_OwesTheReleaseWithoutWaiting()
    {
        ElmServer server = await StartOpenServerAsync();
        var relay = new Recorder();
        LicenseClient client = Client(server, relay);
        using var cancel = new CancellationTokenSource();

        Task held = relay.HoldNextAcquisitionAnswer();
        Task<LeaseResult> acquiring = client.AcquireLeaseAsync("pro", cancellationToken: cancel.Token);
        await held;
        await cancel.CancelAsync();
        var failed = Assert.IsType<LeaseResult.Failed>(await acquiring);
        Assert.Equal(ErrorCode.NetworkError, failed.Error.Code);
        Assert.Equal(1, server.Engine.SeatsInUse("pro"));
        string canceled = relay.Requests().Single(r => r.Method == "POST").ProofId!;

        int before = Requests(relay).Length;
        Acquired(await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(["DELETE " + RequestReleases + canceled, "POST /v1/leases"], Requests(relay).Skip(before).Select(r => r.Method + " " + r.Path));
        Assert.Equal(1, server.Engine.SeatsInUse("pro"));
    }

    private static Lease Acquired(LeaseResult result) => Assert.IsType<LeaseResult.Acquired>(result).Lease;

    // The requests after an acquisition that lost its answer and the release that failed after it, as "METHOD path",
    // without the nonce requests.
    private static string[] After(Recorder relay, string jti)
    {
        SentRequest[] sent = Requests(relay);
        int at = Array.FindIndex(sent, r => r.Method == "POST" && r.ProofId == jti);
        return [.. sent.Skip(at + 2).Select(r => r.Method + " " + r.Path)];
    }

    // The requests the client sent through the relay, without the nonce requests.
    private static SentRequest[] Requests(Recorder relay) => [.. relay.Requests().Where(r => r.Method != "GET")];

    // Acquires pro through a relay armed to lose the answer and to fail the release once; returns the acquisition's jti.
    private static async Task<string> LoseAcquisitionAsync(LicenseClient client, Recorder relay)
    {
        relay.LoseNextAcquisitionAnswer = true;
        relay.FailRequestReleases = 1;
        var failed = Assert.IsType<LeaseResult.Failed>(
            await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken)
        );
        Assert.Equal(ErrorCode.NetworkError, failed.Error.Code);
        SentRequest[] sent = Requests(relay);
        string jti = sent.Last(r => r.Method == "POST" && r.Path == "/v1/leases").ProofId!;
        Assert.Equal("DELETE " + RequestReleases + jti, sent[^1].Method + " " + sent[^1].Path);
        return jti;
    }

    private static string KeyFolder(string dataDirectory) => new InstallationKeyStore("acme-cad", KeyScope.User, dataDirectory).Directory;

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
