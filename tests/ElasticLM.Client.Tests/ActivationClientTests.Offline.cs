using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.Client.Tests;

/// <content>
/// Offline request files (design §12.8, §12.8.5, M4c2): made by <see cref="ActivationClient"/>, processed by a real
/// <c>elm-service</c> through <c>POST /v1/offline</c>, and their answers imported.
/// </content>
public sealed partial class ActivationClientTests
{
    /// <summary>
    /// A client without a service URL activates, refreshes, and deactivates by files only; the same request is made again
    /// from the kept claims without the license key; the online operations are refused.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license; a client without a service URL.
    ///
    /// Steps:
    /// - Call the four online operations; make an activation request twice; read activation.json; process it and import the
    ///   answer twice; check; make and process a refresh request and import it; make a deactivation proof twice; check; make an
    ///   activation request; process the proof; forget it; make an activation request.
    ///
    /// Expected:
    /// - Each online operation ELM_INVALID_STATE "No service URL is set; …"; the two requests share the signing input, not
    ///   replaced, and hold the key; activation.json holds no key; Activated twice with sequence 1 and the request's nonce, state
    ///   Active; Valid; sequence 2; the same proof twice, state None, ELM_LICENSE_NOT_FOUND; ELM_INVALID_STATE "A deactivation
    ///   proof is kept; …"; 204; forgotten; a new request is made.
    /// </remarks>
    [Fact]
    public async Task Offline_ActivateRefreshDeactivate_ByFilesOnly()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        (string licenseId, string key) = await vendor.LicenseWithIdAsync(string.Empty);
        await using ActivationClient client = vendor.Client("a", offline: true);
        const string NoService = "No service URL is set; use the offline requests.";

        Assert.Equal(NoService, Failed(await client.ActivateAsync(key, Cancel)).Message);
        Assert.Equal(NoService, Failed(await client.StartTrialAsync(Cancel)).Message);
        Assert.Equal(NoService, Failed(await client.RefreshAsync(Cancel)).Message);
        Assert.Equal((ErrorCode.InvalidState, NoService), Code(await client.DeactivateAsync(Cancel)));

        OfflineRequestResult.Created first = Created(await client.CreateActivationRequestAsync(key, cancellationToken: Cancel));
        OfflineRequestResult.Created again = Created(await client.CreateActivationRequestAsync(key, cancellationToken: Cancel));
        Assert.Equal((false, false, SigningInput(first.File)), (first.Replaced, again.Replaced, SigningInput(again.File)));
        Assert.Contains(key, Payload(first.File), StringComparison.Ordinal);
        Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(vendor.Folder("a"), "activation.json")), StringComparison.Ordinal);

        string answer = await ProcessedAsync(vendor, again.File);
        VerifiedLicense license = Activated(await client.ImportResponseAsync(answer, Cancel));
        Assert.Equal((licenseId, 1L, NonceOf(first.File)), (license.LicenseId, license.Sequence!.Value, license.OfflineRequestNonce));
        Assert.Equal(1L, Activated(await client.ImportResponseAsync(answer, Cancel)).Sequence);
        Assert.Equal(ActivationState.Active, client.State);
        Assert.IsType<LicenseVerificationResult.Valid>(client.Check("core", null));

        string refresh = Created(await client.CreateRefreshRequestAsync(cancellationToken: Cancel)).File;
        Assert.Equal(2L, Activated(await client.ImportResponseAsync(await ProcessedAsync(vendor, refresh), Cancel)).Sequence);

        string proof = Created(await client.CreateDeactivationProofAsync(Cancel)).File;
        Assert.Equal(proof, Created(await client.CreateDeactivationProofAsync(Cancel)).File);
        Assert.Equal((ActivationState.None, ErrorCode.LicenseNotFound), (client.State, Invalid(client.Check("core", null)).Code));
        Assert.Equal(
            (ErrorCode.InvalidState, "A deactivation proof is kept; pass it to the service and forget it first."),
            Code(await client.CreateActivationRequestAsync(key, cancellationToken: Cancel))
        );
        Assert.Equal(HttpStatusCode.NoContent, (await vendor.ProcessAsync(proof)).Status);
        Assert.Null(await client.ForgetDeactivationProofAsync(Cancel));
        Assert.Null(await client.ForgetDeactivationProofAsync(Cancel));
        Created(await client.CreateActivationRequestAsync(key, cancellationToken: Cancel));
    }

    /// <summary>
    /// A replaced request's nonce stays recorded, so an answer the service gave to it, or to an earlier one, is not a clone
    /// after two replacements; an answer to a request that is no longer waiting is refused.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license; a client without a service URL.
    ///
    /// Steps:
    /// - Make request A and process it (its answer is lost); move the clock 8 days and make B; make C with renew; process A's
    ///   file again and import that answer; process C and import it.
    ///
    /// Expected:
    /// - B and C replaced; A's nonce and B's are in the kept pending list; A's answer ELM_REQUEST_MISMATCH; C's answer Activated
    ///   with sequence 2 and C's nonce, and the lists are empty.
    /// </remarks>
    [Fact]
    public async Task Offline_ReplacedRequests_AreNamedUntilAnswered()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync(string.Empty);
        await using ActivationClient client = vendor.Client("a", offline: true);

        string a = Created(await client.CreateActivationRequestAsync(key, cancellationToken: Cancel)).File;
        string lost = await ProcessedAsync(vendor, a);
        vendor.Test.Time.Advance(TimeSpan.FromDays(8));
        OfflineRequestResult.Created b = Created(await client.CreateActivationRequestAsync(key, cancellationToken: Cancel));
        OfflineRequestResult.Created c = Created(await client.CreateActivationRequestAsync(key, renew: true, cancellationToken: Cancel));
        Assert.Equal((true, true), (b.Replaced, c.Replaced));
        Assert.Equal([NonceOf(a), NonceOf(b.File)], vendor.Kept("a").Pending);

        Assert.Equal(ErrorCode.RequestMismatch, Failed(await client.ImportResponseAsync(lost, Cancel)).Code);
        VerifiedLicense license = Activated(await client.ImportResponseAsync(await ProcessedAsync(vendor, c.File), Cancel));
        Assert.Equal((2L, NonceOf(c.File)), (license.Sequence!.Value, license.OfflineRequestNonce));
        Assert.Equal((0, 0), (vendor.Kept("a").Pending.Count, vendor.Kept("a").Retry.Count));
    }

    /// <summary>
    /// A waiting offline request is named by online requests, and an online success makes it void; a deactivation proof
    /// names a refresh request whose answer was lost; offline imports leave the clock guard as it is.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A policy of tokens of a day; a client with the service URL, activated online.
    ///
    /// Steps:
    /// - Make an offline refresh request and process it (its answer is lost); refresh online; read the kept file; make another
    ///   offline refresh request and process it (lost); make a deactivation proof and process it.
    ///
    /// Expected:
    /// - The online refresh gets sequence 2 (the service's answer to the offline request, named in retry) and the waiting request
    ///   is gone; the proof is processed with 204 (one behind, naming the lost request).
    /// </remarks>
    [Fact]
    public async Task Offline_AndOnline_NameEachOther()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync("\"tokenLifetime\":86400,\"refreshWindow\":3600");
        await using ActivationClient client = vendor.Client("a");
        Activated(await client.ActivateAsync(key, Cancel));

        await ProcessedAsync(vendor, Created(await client.CreateRefreshRequestAsync(cancellationToken: Cancel)).File);
        Assert.Equal(2L, Activated(await client.RefreshAsync(Cancel)).Sequence);
        Assert.Null(vendor.Kept("a").Offline);

        await ProcessedAsync(vendor, Created(await client.CreateRefreshRequestAsync(cancellationToken: Cancel)).File);
        string proof = Created(await client.CreateDeactivationProofAsync(Cancel)).File;
        Assert.Equal(HttpStatusCode.NoContent, (await vendor.ProcessAsync(proof)).Status);
    }

    /// <summary>
    /// Answers that are not for this installation's waiting request are refused and change nothing; a damaged kept file
    /// stops the offline operations; the 16 unanswered requests bound holds with a waiting request counted.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license; clients A and B without a service URL; A with no folder yet.
    ///
    /// Steps:
    /// - A imports an answer; A makes and B makes a request; B's is processed and A imports its answer; A imports text that is
    ///   no answer; write a damaged activation.json for A and make a request, import, make a proof, forget; write A's file with
    ///   16 pending jti and a waiting activation request and renew it; write it with 15 retry jti, an activation, and a waiting
    ///   refresh request, and refresh online with a client that has the URL.
    ///
    /// Expected:
    /// - ELM_REQUEST_MISMATCH; B's answer ELM_WRONG_INSTALLATION (the verifier, before the nonce), A has no activation; ELM_MALFORMED; four
    ///   ELM_MALFORMED "The kept activation file is damaged."; ELM_INVALID_STATE "16 requests got no answer; …" twice.
    /// </remarks>
    [Fact]
    public async Task Offline_ForeignAnswers_DamagedFile_AndTheBound()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync("\"maxActivations\":5");
        await using ActivationClient a = vendor.Client("a", offline: true);
        await using ActivationClient b = vendor.Client("b", offline: true);
        const string Full = "16 requests got no answer; an administrator deactivates this installation before it activates again.";

        Assert.Equal(ErrorCode.RequestMismatch, Failed(await a.ImportResponseAsync("{}", Cancel)).Code);
        Created(await a.CreateActivationRequestAsync(key, cancellationToken: Cancel));
        string foreign = await ProcessedAsync(vendor, Created(await b.CreateActivationRequestAsync(key, cancellationToken: Cancel)).File);
        Assert.Equal(ErrorCode.WrongInstallation, Failed(await a.ImportResponseAsync(foreign, Cancel)).Code);
        Assert.Null(a.ActivationId);
        Assert.Equal(ErrorCode.Malformed, Failed(await a.ImportResponseAsync("not an answer", Cancel)).Code);

        string kept = Path.Combine(vendor.Folder("a"), "activation.json");
        File.WriteAllText(kept, "{");
        const string Damaged = "The kept activation file is damaged.";
        Assert.Equal((ErrorCode.Malformed, Damaged), Code(await a.CreateActivationRequestAsync(key, cancellationToken: Cancel)));
        Assert.Equal(Damaged, Failed(await a.ImportResponseAsync(foreign, Cancel)).Message);
        Assert.Equal((ErrorCode.Malformed, Damaged), Code(await a.CreateDeactivationProofAsync(Cancel)));
        Assert.Equal((ErrorCode.Malformed, Damaged), Code(await a.ForgetDeactivationProofAsync(Cancel)));

        var store = new ActivationStore(vendor.Folder("a"), vendor.Test.Time);
        string[] sixteen = [.. Enumerable.Range(0, 16).Select(i => "jti" + i)];
        var waiting = new OfflineRequest(false, new string('n', 22), 0, long.MaxValue / 2, 0, null, new string('h', 43), [], []);
        store.Write(new StoredActivation(null, 0, null, null, []) { Pending = sixteen, Offline = waiting });
        Assert.Equal((ErrorCode.InvalidState, Full), Code(await a.CreateActivationRequestAsync(key, renew: true, cancellationToken: Cancel)));

        await using ActivationClient online = vendor.Client("c");
        Activated(await online.ActivateAsync(key, Cancel));
        StoredActivation activated = vendor.Kept("c");
        var refresh = new OfflineRequest(true, new string('r', 22), 0, long.MaxValue / 2, 1, activated.ActivationId, null, [], []);
        new ActivationStore(vendor.Folder("c"), vendor.Test.Time).Write(activated with { Retry = sixteen[..15], Offline = refresh });
        Assert.Equal(Full, Failed(await online.RefreshAsync(Cancel)).Message);
    }

    /// <summary>
    /// An imported offline answer leaves the clock guard as it is, while an online answer passes the service's time to it
    /// (design §11.4 step 4, §12.8.5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license with 3 activations; clients A without a service URL and B with it, each with a guard that records the
    ///   server times it is given.
    ///
    /// Steps:
    /// - A makes a request, it is processed, the clock moves 6 days, and A imports the answer; B activates online.
    ///
    /// Expected:
    /// - A's guard got nothing; B's got one time.
    /// </remarks>
    [Fact]
    public async Task Offline_Import_LeavesTheClockGuard()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync("\"maxActivations\":3");
        var offlineGuard = new RecordingGuard();
        var onlineGuard = new RecordingGuard();
        await using ActivationClient a = vendor.Client("a", offline: true, guard: offlineGuard);
        await using ActivationClient b = vendor.Client("b", guard: onlineGuard);

        string answer = await ProcessedAsync(vendor, Created(await a.CreateActivationRequestAsync(key, cancellationToken: Cancel)).File);
        vendor.Test.Time.Advance(TimeSpan.FromDays(6));
        Activated(await a.ImportResponseAsync(answer, Cancel));
        Activated(await b.ActivateAsync(key, Cancel));
        Assert.Equal((0, 1), (offlineGuard.Accepted.Count, onlineGuard.Accepted.Count));
    }

    /// <summary>
    /// Another license key makes another request; an imported refresh keeps the activation requests that got no answer; a
    /// kept proof stops online activations and trials too, and forgetting it keeps those requests recorded.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two licenses; a client with the service URL, activated online on the first.
    ///
    /// Steps:
    /// - Write the kept file with pending jti "p"; make and process an offline refresh and import it; make a proof; activate
    ///   online with the second key and start a trial; forget the proof; make an activation request with the second key, then
    ///   with the first.
    ///
    /// Expected:
    /// - Pending "p" after the import; ELM_INVALID_STATE "A deactivation proof is kept; …" twice; pending "p" after forgetting;
    ///   the second request replaces the first.
    /// </remarks>
    [Fact]
    public async Task Offline_KeysPendingAndProof_AreKeptApart()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync(string.Empty);
        string other = await vendor.LicenseAsync(string.Empty);
        await using ActivationClient client = vendor.Client("a");
        Activated(await client.ActivateAsync(key, Cancel));
        var store = new ActivationStore(vendor.Folder("a"), vendor.Test.Time);
        store.Write(vendor.Kept("a") with { Pending = ["p"] });

        string refresh = Created(await client.CreateRefreshRequestAsync(cancellationToken: Cancel)).File;
        Activated(await client.ImportResponseAsync(await ProcessedAsync(vendor, refresh), Cancel));
        Assert.Equal(["p"], vendor.Kept("a").Pending);

        Created(await client.CreateDeactivationProofAsync(Cancel));
        const string ProofKept = "A deactivation proof is kept; pass it to the service and forget it first.";
        Assert.Equal(ProofKept, Failed(await client.ActivateAsync(other, Cancel)).Message);
        Assert.Equal(ProofKept, Failed(await client.StartTrialAsync(Cancel)).Message);
        Assert.Null(await client.ForgetDeactivationProofAsync(Cancel));
        Assert.Equal(["p"], vendor.Kept("a").Pending);

        Assert.False(Created(await client.CreateActivationRequestAsync(other, cancellationToken: Cancel)).Replaced);
        Assert.True(Created(await client.CreateActivationRequestAsync(key, cancellationToken: Cancel)).Replaced);
    }

    /// <summary>
    /// An online request whose answer is lost after an offline request was made makes that request stale: the next call makes a
    /// new one that names both, and the service takes it without a clone.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license; a client with the service URL, activated online, whose next answer can be lost.
    ///
    /// Steps:
    /// - Make an offline refresh request R; refresh online and lose the answer; make the refresh request again; process it and
    ///   import the answer; read the audit log.
    ///
    /// Expected:
    /// - The second call is replaced and its retry names the lost online request and R, in that order; the answer is imported
    ///   with sequence 3; no activation.clone row.
    /// </remarks>
    [Fact]
    public async Task Offline_RequestAfterALostOnlineAnswer_IsMadeAgain()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync(string.Empty);
        var lose = new LoseNextAnswer();
        await using ActivationClient client = vendor.Client("a", lose);
        Activated(await client.ActivateAsync(key, Cancel));

        string first = Created(await client.CreateRefreshRequestAsync(cancellationToken: Cancel)).File;
        lose.Armed = true;
        Assert.Equal(ErrorCode.NetworkError, Failed(await client.RefreshAsync(Cancel)).Code);
        string online = vendor.Kept("a").Retry.Single();
        OfflineRequestResult.Created again = Created(await client.CreateRefreshRequestAsync(cancellationToken: Cancel));
        Assert.True(again.Replaced);
        using (JsonDocument payload = JsonDocument.Parse(Payload(again.File)))
        {
            Assert.Equal([online, NonceOf(first)], payload.RootElement.GetProperty("retry").EnumerateArray().Select(j => j.GetString()!));
        }

        Assert.Equal(3L, Activated(await client.ImportResponseAsync(await ProcessedAsync(vendor, again.File), Cancel)).Sequence);
        Assert.DoesNotContain(await vendor.AuditAsync(), r => r.GetProperty("action").GetString() == "activation.clone");
    }

    /// <summary>
    /// The owner's callback gets a new file after the clock guard got the service's time, so elm-server's reload through the
    /// same guard sees that time (design §13.7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A license; a client with a guard that records the server times it is given and a callback that records how many it
    ///   had got when it ran.
    ///
    /// Steps:
    /// - Activate online.
    ///
    /// Expected:
    /// - The callback ran once, after one server time.
    /// </remarks>
    [Fact]
    public async Task LicenseKept_RunsAfterTheServerTime()
    {
        await using Vendor vendor = await Vendor.StartAsync();
        string key = await vendor.LicenseAsync(string.Empty);
        var guard = new RecordingGuard();
        var seen = new List<int>();
        await using ActivationClient client = vendor.Client("a", guard: guard, kept: _ => seen.Add(guard.Accepted.Count));

        Activated(await client.ActivateAsync(key, Cancel));
        Assert.Equal([1], seen);
    }

    private static OfflineRequestResult.Created Created(OfflineRequestResult result) =>
        result as OfflineRequestResult.Created ?? throw new InvalidOperationException(((OfflineRequestResult.Failed)result).Error.ToString());

    private static (ErrorCode Code, string Message) Code(OfflineRequestResult result) =>
        Assert.IsType<OfflineRequestResult.Failed>(result).Error is LicenseError e ? (e.Code, e.Message) : default;

    private static (ErrorCode Code, string Message) Code(LicenseError? error) => (error!.Code, error.Message);

    private static async Task<string> ProcessedAsync(Vendor vendor, string file)
    {
        (HttpStatusCode status, string body) = await vendor.ProcessAsync(file);
        Assert.True(status == HttpStatusCode.OK, body);
        return body;
    }

    private static string SigningInput(string file) => file[..file.LastIndexOf('.')];

    private static string Payload(string file) => Encoding.UTF8.GetString(Base64Url.DecodeFromChars(file.Split('.')[1]));

    private static string NonceOf(string file)
    {
        using JsonDocument payload = JsonDocument.Parse(Payload(file));
        return payload.RootElement.GetProperty("nonce").GetString()!;
    }

    // A clock guard that lets every check pass and records the server times it is given.
    private sealed class RecordingGuard : IClockGuard
    {
        public List<DateTimeOffset> Accepted { get; } = [];

        public bool Observe(DateTimeOffset now, DateTimeOffset tokenIssuedAt, TimeSpan allowedSkew) => true;

        public void AcceptServerTime(DateTimeOffset serverTime) => Accepted.Add(serverTime);
    }
}
