using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Client.Tests;

/// <summary>
/// What <see cref="LicenseClient"/> and <see cref="ActivationClient"/> do with an answer that is larger than 256 KiB, or
/// that does not end (design §5.3 T16, §8.6, §12.1, the V1d6 Grill), on each request they send: the first connection to an
/// on-premises server, a nonce request, a lease request, and an activation. The server is one that writes its answers byte
/// by byte, as a relay or a false server on the path can.
/// </summary>
public sealed class AnswerLimitTests : IDisposable
{
    private const int Limit = 256 * 1024;
    private const long SocketBuffers = 16L * 1024 * 1024;
    private const long Offered = 64L * 1024 * 1024;
    private const string LicenseKey = "ELM-AAAA-AAAA-AAAA-AAAA";
    private const string NoSeats = """{"code":"ELM_NO_SEATS","detail":"none"}""";
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    // A bound on the real clock for a call that would otherwise never end. It is shorter than the client's 30 seconds, so a
    // limit counted on the real clock cannot end a call within it; no passing test waits for it.
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(20);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "elm-limit-" + Guid.CreateVersion7().ToString("N"));
    private readonly ECDsa _root = KeyFiles.CreateKey();

    public AnswerLimitTests() => Directory.CreateDirectory(_directory);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _root.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// An answer of exactly 256 KiB is read, and an answer of one byte more is ELM_MALFORMED, on every request of the two
    /// clients.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server on loopback (with TLS and a self-signed certificate for server-license) that answers the request of the
    ///   case with status 409 and the problem {"code":"ELM_NO_SEATS","detail":"none"} followed by spaces, with a
    ///   Content-Length; a nonce request that is not the case's is answered with a nonce.
    /// - The cases, by the request that gets the answer: server-license (an on-premises LicenseClient's GET
    ///   /v1/server/license), lease-nonce and lease (a LicenseClient with a license key: GET /v1/nonce, POST /v1/leases),
    ///   activation-nonce and activation (an ActivationClient: GET /v1/nonce, POST /v1/activations).
    ///
    /// Steps:
    /// - Acquire a lease, or activate, against an answer of 262,144 bytes; then, with a new client, against one of 262,145.
    ///
    /// Expected:
    /// - 262,144 bytes: ELM_NO_SEATS with the answer's detail "none"; on the first connection, whose detail is never used,
    ///   with "The server answered 409.".
    /// - 262,145 bytes: ELM_MALFORMED with "The server's answer is larger than the limit." from LicenseClient and "The
    ///   service's answer is larger than the limit." from ActivationClient.
    /// </remarks>
    /// <param name="path">The case.</param>
    [Theory]
    [InlineData("server-license")]
    [InlineData("lease-nonce")]
    [InlineData("lease")]
    [InlineData("activation-nonce")]
    [InlineData("activation")]
    public async Task Request_AnswerAtTheLimitAndOneByteOver_IsReadThenMalformed(string path)
    {
        var time = new FakeTimeProvider(Start);
        await using RawAnswerServer atLimit = Server(path, RawAnswer.Whole(NoSeats, Limit, status: 409));
        await using RawAnswerServer over = Server(path, RawAnswer.Whole(NoSeats, Limit + 1, status: 409));

        LicenseError read;
        await using (Caller caller = Client(path, atLimit, time, "at"))
        {
            read = (await caller.CallAsync()).Error;
        }

        LicenseError refused;
        await using (Caller caller = Client(path, over, time, "over"))
        {
            refused = (await caller.CallAsync()).Error;
        }

        Assert.Equal(new LicenseError(ErrorCode.NoSeats, path == "server-license" ? "The server answered 409." : "none"), read);
        Assert.Equal(new LicenseError(ErrorCode.Malformed, TooLarge(path)), refused);
    }

    /// <summary>
    /// An answer whose body goes on past 256 KiB, or that declares such a body, is ELM_MALFORMED on every request of the
    /// two clients, with a success status or an error status, and the client takes no more of it than the limit.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The server and the cases of <see cref="Request_AnswerAtTheLimitAndOneByteOver_IsReadThenMalformed"/>. The answer to
    ///   the case's request, by shape: endless (64 MiB of spaces in chunks, without a last chunk), until-close (64 MiB
    ///   without a length), declared (a Content-Length of 4 GiB and no body). Each with status 200 and with status 503.
    ///
    /// Steps:
    /// - Acquire a lease, or activate; dispose the client; wait until the server has stopped writing.
    ///
    /// Expected:
    /// - ELM_MALFORMED with the client's "… answer is larger than the limit.", for both statuses.
    /// - endless and until-close: the server wrote more than 256 KiB and at most 256 KiB and 16 MiB, which is what the
    ///   buffers of the two sockets may hold. A client that reads the whole answer before it checks the size takes all
    ///   64 MiB.
    /// - declared: the server wrote no body byte of that answer (only the 17 bytes of the nonce answer before it, in the
    ///   lease and activation cases), and the client returned without the clock moving.
    /// </remarks>
    /// <param name="path">The case.</param>
    /// <param name="shape">The shape of the answer.</param>
    /// <param name="status">The status of the answer.</param>
    [Theory]
    [MemberData(nameof(OversizedCases))]
    public async Task Request_AnswerFarOverTheLimit_IsMalformedAndNotTaken(string path, string shape, int status)
    {
        RawAnswer offered = shape switch
        {
            "endless" => RawAnswer.Endless(Offered, status),
            "until-close" => RawAnswer.UntilClose(string.Empty, Offered, status),
            _ => RawAnswer.DeclaredOnly(4_294_967_296L, status),
        };
        await using RawAnswerServer server = Server(path, offered);

        LicenseError error;
        await using (Caller caller = Client(path, server, new FakeTimeProvider(Start), "c"))
        {
            error = (await caller.CallAsync()).Error;
        }

        long written = await server.SettledBodyBytesAsync();

        Assert.Equal(new LicenseError(ErrorCode.Malformed, TooLarge(path)), error);
        if (shape == "declared")
        {
            // The lease and activation cases get a nonce first, whose answer has a body.
            Assert.Equal(path is "lease" or "activation" ? """{"nonce":"first"}""".Length : 0, written);
        }
        else
        {
            Assert.InRange(written, Limit + 1, Limit + SocketBuffers);
        }
    }

    /// <summary>
    /// An answer whose body stops ends the request as ELM_NETWORK_ERROR after 30 seconds of the client's clock, on every
    /// request of the two clients.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The server and the cases of <see cref="Request_AnswerAtTheLimitAndOneByteOver_IsReadThenMalformed"/>. The answer to
    ///   the case's request is a head with status 200 and the first byte of a body in chunks, and then nothing.
    /// - Clients on a fake clock.
    ///
    /// Steps:
    /// - Acquire a lease, or activate; once the server waits, move the clock 30 seconds.
    ///
    /// Expected:
    /// - ELM_NETWORK_ERROR with "The server cannot be reached: No whole answer came within 30 seconds." from LicenseClient
    ///   and "The service did not answer: No whole answer came within 30 seconds." from ActivationClient, within 20
    ///   seconds of the real clock. A client whose limit runs on the real clock would not return in that time.
    /// </remarks>
    /// <param name="path">The case.</param>
    [Theory]
    [InlineData("server-license")]
    [InlineData("lease-nonce")]
    [InlineData("lease")]
    [InlineData("activation-nonce")]
    [InlineData("activation")]
    public async Task Request_AnswerThatStops_IsANetworkErrorAfterThirtySeconds(string path)
    {
        var time = new FakeTimeProvider(Start);
        await using RawAnswerServer server = Server(path, RawAnswer.Stalled("{", 1));
        await using Caller caller = Client(path, server, time, "c");

        Task<(LicenseError Error, TimeSpan? RetryAfter)> calling = caller.CallAsync();
        await server.Held.WaitAsync(Hang, Cancel);
        time.Advance(TimeSpan.FromSeconds(30));
        LicenseError error = (await calling).Error;

        string start = path.StartsWith("activation", StringComparison.Ordinal) ? "The service did not answer: " : "The server cannot be reached: ";
        Assert.Equal(new LicenseError(ErrorCode.NetworkError, start + "No whole answer came within 30 seconds."), error);
    }

    /// <summary>
    /// Nothing of an answer that is too large is acted on: its Retry-After is not given to the app, and its ELM-Nonce is not
    /// used for the next request (design §12.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server that answers GET /v1/nonce with the nonce "first"; the first POST with status 503, the headers
    ///   Retry-After: 7 and ELM-Nonce: second, and a body of 262,145 bytes; the second POST with status 409, Retry-After: 9,
    ///   ELM-Nonce: third, and the problem ELM_NO_SEATS.
    /// - The cases: lease (a LicenseClient with a license key) and activation (an ActivationClient).
    ///
    /// Steps:
    /// - With one client, acquire a lease, or activate, twice.
    ///
    /// Expected:
    /// - First: ELM_MALFORMED and no retry time. Second: ELM_NO_SEATS and a retry time of 9 seconds, so the header is read
    ///   from an answer within the limit.
    /// - The proof of the second POST carries the nonce "first", and the server got one nonce request.
    /// </remarks>
    /// <param name="path">The case.</param>
    [Theory]
    [InlineData("lease")]
    [InlineData("activation")]
    public async Task Request_AnswerOverTheLimit_GivesNoRetryTimeAndNoNonce(string path)
    {
        int posts = 0;
        await using RawAnswerServer server = RawAnswerServer.Start(target =>
            target == "GET /v1/nonce" ? RawAnswer.Whole("""{"nonce":"first"}""")
            : Interlocked.Increment(ref posts) == 1
                ? RawAnswer.Whole(NoSeats, Limit + 1, status: 503) with
                {
                    Headers = "Retry-After: 7\r\nELM-Nonce: second\r\n",
                }
            : RawAnswer.Whole(NoSeats, status: 409) with { Headers = "Retry-After: 9\r\nELM-Nonce: third\r\n" }
        );
        await using Caller caller = Client(path, server, new FakeTimeProvider(Start), "c");

        (LicenseError Error, TimeSpan? RetryAfter) first = await caller.CallAsync();
        (LicenseError Error, TimeSpan? RetryAfter) second = await caller.CallAsync();

        Assert.Equal((new LicenseError(ErrorCode.Malformed, TooLarge(path)), null), first);
        Assert.Equal((new LicenseError(ErrorCode.NoSeats, "none"), TimeSpan.FromSeconds(9)), second);
        string[] heads = [.. server.Heads];
        Assert.Equal(["GET", "POST", "POST"], heads.Select(h => h.Split(' ')[0]));
        Assert.Equal("first", ProofNonce(heads[2]));
    }

    /// <summary>Gets the cases of <see cref="Request_AnswerFarOverTheLimit_IsMalformedAndNotTaken"/>.</summary>
    public static TheoryData<string, string, int> OversizedCases()
    {
        var cases = new TheoryData<string, string, int>();
        foreach (string path in new[] { "server-license", "lease-nonce", "lease", "activation-nonce", "activation" })
        {
            foreach (string shape in new[] { "endless", "until-close", "declared" })
            {
                cases.Add(path, shape, 200);
                cases.Add(path, shape, 503);
            }
        }

        return cases;
    }

    private static string TooLarge(string path) =>
        path.StartsWith("activation", StringComparison.Ordinal)
            ? "The service's answer is larger than the limit."
            : "The server's answer is larger than the limit.";

    // The nonce claim of the proof in a request's head.
    private static string? ProofNonce(string head)
    {
        string proof = head.Split("\r\n")
            .Single(line => line.StartsWith("ELM-Proof:", StringComparison.OrdinalIgnoreCase))["ELM-Proof:".Length..]
            .Trim();
        using JsonDocument payload = JsonDocument.Parse(Base64Url.DecodeFromChars(proof.Split('.')[1]));
        return payload.RootElement.GetProperty("nonce").GetString();
    }

    // A server that gives the case's request the shaped answer, and a nonce to a nonce request that is not the case's. Any
    // other request gets a problem that no case expects.
    private static RawAnswerServer Server(string path, RawAnswer shaped) =>
        RawAnswerServer.Start(
            target =>
                (path, target) switch
                {
                    ("server-license", "GET /v1/server/license") => shaped,
                    ("lease-nonce" or "activation-nonce", "GET /v1/nonce") => shaped,
                    ("lease", "POST /v1/leases") => shaped,
                    ("activation", "POST /v1/activations") => shaped,
                    (_, "GET /v1/nonce") => RawAnswer.Whole("""{"nonce":"first"}"""),
                    _ => RawAnswer.Whole("""{"code":"ELM_UNKNOWN_LEASE","detail":"unexpected"}""", status: 404),
                },
            tls: path == "server-license"
        );

    // A client of the case in a folder of its own, and its one operation: an acquisition, or an activation.
    private Caller Client(string path, RawAnswerServer server, TimeProvider time, string folder)
    {
        TrustStore trust = TrustStore.Create([KeyFiles.ExportPublic(_root)], []);
        string data = Path.Combine(_directory, folder);
        if (path.StartsWith("activation", StringComparison.Ordinal))
        {
            var options = new ActivationClientOptions
            {
                ProductId = "app",
                ServiceUrl = server.Url,
                Trust = trust,
                DataDirectory = data,
                ClockGuard = StatelessClockGuard.Instance,
                TimeProvider = time,
            };
            Assert.True(ActivationClient.TryCreate(options, out ActivationClient? activation, out LicenseError? failed), failed?.Message);
            return new Caller(
                activation!,
                async () =>
                {
                    ActivationResult.Failed result = Assert.IsType<ActivationResult.Failed>(
                        await activation!.ActivateAsync(LicenseKey, Cancel).WaitAsync(Hang, Cancel)
                    );
                    return (result.Error, result.RetryAfter);
                }
            );
        }

        var leaseOptions = new LicenseClientOptions
        {
            ProductId = "acme-cad",
            ServerUrl = server.Url,
            LicenseKey = path == "server-license" ? null : LicenseKey,
            Trust = trust,
            DataDirectory = data,
            ClockGuard = StatelessClockGuard.Instance,
            TimeProvider = time,
        };
        Assert.True(LicenseClient.TryCreate(leaseOptions, out LicenseClient? client, out LicenseError? error), error?.Message);
        return new Caller(
            client!,
            async () =>
            {
                LeaseResult.Failed result = Assert.IsType<LeaseResult.Failed>(
                    await client!.AcquireLeaseAsync("pro", cancellationToken: Cancel).WaitAsync(Hang, Cancel)
                );
                return (result.Error, result.RetryAfter);
            }
        );
    }

    private sealed class Caller(IAsyncDisposable client, Func<Task<(LicenseError Error, TimeSpan? RetryAfter)>> call) : IAsyncDisposable
    {
        public Task<(LicenseError Error, TimeSpan? RetryAfter)> CallAsync() => call();

        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}
