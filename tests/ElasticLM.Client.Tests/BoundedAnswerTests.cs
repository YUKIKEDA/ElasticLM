using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Client.Tests;

/// <summary>
/// The one reader of an HTTP answer that the client and <c>elm</c> share (design §12.1, §8.6, the V1d6 Grill): the body's limit
/// while it is read, and the one time limit over the whole exchange, against a server that writes its answers byte by byte.
/// </summary>
public sealed class BoundedAnswerTests
{
    private const int Limit = BoundedAnswer.MaxLength;

    // What a server may write beyond what its client read, into the buffers of the two sockets, before the client's close
    // stops it. Measured on Windows 11 loopback in 30 runs of Send_BodyFarOverTheLimit_IsGivenUpNearTheLimit: 524,288 to
    // 917,504 bytes written in chunks, 393,216 to 1,048,576 without a length, and 262,144 to 393,216 with a declared
    // length, of which the client reads nothing. The bound leaves room for the larger buffers of other systems.
    private const long SocketBuffers = 16L * 1024 * 1024;

    // The length of a body that a client must not read to its end: far over the limit and over the socket buffers.
    private const long Offered = 64L * 1024 * 1024;

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    // A bound on the real clock for a call that would otherwise never end. It is shorter than the 30 seconds of the limit
    // under test, so a limit counted on the real clock cannot end a call within it; no passing test waits for it.
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(20);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// A body of exactly the limit is returned whole, and a body of one byte more is too large, whether the answer declares
    /// its length, sends its body in chunks, or ends its body by closing the connection.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server on loopback whose answer to GET /x is a body that starts with "{}" and goes on with spaces: with a
    ///   Content-Length; in chunks; without either, closed at its end. The limit is 262,144 bytes.
    ///
    /// Steps:
    /// - For each of the three, send a request whose answer has 262,144 bytes, and one whose answer has 262,145.
    ///
    /// Expected:
    /// - 262,144 bytes: Whole with status 200 and a body of that length that starts with "{}".
    /// - 262,145 bytes: TooLarge with status 200.
    /// </remarks>
    /// <param name="shape">How the answer delimits its body.</param>
    [Theory]
    [InlineData("declared")]
    [InlineData("chunked")]
    [InlineData("until-close")]
    public async Task Send_BodyAtTheLimitAndOneByteOver_IsWholeThenTooLarge(string shape)
    {
        RawAnswer Shaped(long length) =>
            shape switch
            {
                "declared" => RawAnswer.Whole("{}", length),
                "chunked" => new RawAnswer
                {
                    Chunked = true,
                    Start = "{}",
                    Length = length,
                },
                _ => RawAnswer.UntilClose("{}", length),
            };
        await using RawAnswerServer atLimit = RawAnswerServer.Start(_ => Shaped(Limit));
        await using RawAnswerServer over = RawAnswerServer.Start(_ => Shaped(Limit + 1));
        using HttpClient http = BoundedAnswer.CreateClient(BoundedAnswer.CreateHandler());

        BoundedAnswer whole = await SendAsync(http, atLimit);
        BoundedAnswer tooLarge = await SendAsync(http, over);

        BoundedAnswer.Whole read = Assert.IsType<BoundedAnswer.Whole>(whole);
        Assert.Equal((HttpStatusCode.OK, Limit, "{} "), (read.Status, read.Body.Length, Encoding.ASCII.GetString(read.Body.Span[..3])));
        Assert.Equal(new BoundedAnswer.TooLarge(HttpStatusCode.OK), tooLarge);
    }

    /// <summary>
    /// An answer that declares a body over the limit is too large before any byte of its body comes.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server whose answer, with status 503, has a Content-Length and then no body: the connection stays open. The
    ///   lengths: 262,145 (one over the limit) and 4,294,967,296 (more than a buffer can hold). A clock that does not move.
    ///
    /// Steps:
    /// - Send a request.
    ///
    /// Expected:
    /// - TooLarge with status 503, although no body byte was written and the time limit cannot pass. A reader that waited
    ///   for the body would not return.
    /// </remarks>
    /// <param name="declared">The declared length.</param>
    [Theory]
    [InlineData(Limit + 1L)]
    [InlineData(4_294_967_296L)]
    public async Task Send_DeclaredLengthOverTheLimit_IsTooLargeWithoutABodyByte(long declared)
    {
        await using RawAnswerServer server = RawAnswerServer.Start(_ => RawAnswer.DeclaredOnly(declared, status: 503));
        using HttpClient http = BoundedAnswer.CreateClient(BoundedAnswer.CreateHandler());

        BoundedAnswer answer = await SendAsync(http, server);

        Assert.Equal(new BoundedAnswer.TooLarge(HttpStatusCode.ServiceUnavailable), answer);
        Assert.Equal(0, server.BodyBytesWritten);
    }

    /// <summary>
    /// A body that goes on past the limit is given up near the limit: the client does not take the rest of it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server whose answer has a body of 64 MiB of spaces: in chunks without a last chunk; without a length, closed at
    ///   its end; with a Content-Length of 64 MiB.
    ///
    /// Steps:
    /// - Send a request; dispose the client; wait until the server has stopped writing.
    ///
    /// Expected:
    /// - TooLarge with status 200.
    /// - In chunks and without a length: the server wrote more than the limit, since the client reads to the limit and a
    ///   byte, and at most the limit and 16 MiB, which is what the buffers of the two sockets may hold; a client that
    ///   buffers the answer takes all 64 MiB.
    /// - With a Content-Length: the server wrote at most 16 MiB, since the client reads none of the body.
    /// </remarks>
    /// <param name="shape">How the answer delimits its body.</param>
    [Theory]
    [InlineData("chunked")]
    [InlineData("until-close")]
    [InlineData("declared")]
    public async Task Send_BodyFarOverTheLimit_IsGivenUpNearTheLimit(string shape)
    {
        RawAnswer offered = shape switch
        {
            "chunked" => RawAnswer.Endless(Offered),
            "until-close" => RawAnswer.UntilClose(string.Empty, Offered),
            _ => RawAnswer.Whole(string.Empty, Offered),
        };
        await using RawAnswerServer server = RawAnswerServer.Start(_ => offered);
        BoundedAnswer answer;
        using (HttpClient http = BoundedAnswer.CreateClient(BoundedAnswer.CreateHandler()))
        {
            answer = await SendAsync(http, server);
        }

        long written = await server.SettledBodyBytesAsync();

        Assert.Equal(new BoundedAnswer.TooLarge(HttpStatusCode.OK), answer);
        Assert.InRange(written, shape == "declared" ? 0 : Limit + 1, (shape == "declared" ? 0 : Limit) + SocketBuffers);
    }

    /// <summary>
    /// One time limit of 30 seconds runs from the sending of the request to the end of the body, on the clock given: an
    /// answer whose body ends an instant before the limit is whole, and one that has not ended at the limit is a timeout.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A fake clock. A server that moves that clock 20 seconds on when the request has come, and then either sends the
    ///   head and the first byte "x" of a body in chunks and nothing more, or sends nothing at all.
    /// - A handler under the HTTP client that tells when the answer's head has reached the client, so that the clock
    ///   moves while the client reads the body, not while it still waits for the head.
    ///
    /// Steps:
    /// - before: once the head has reached the client and the server waits, move the clock by 10 seconds less one tick
    ///   (the limit less one tick since the request was sent), then let the server end the body.
    /// - at: once the head has reached the client and the server waits, move the clock by 10 seconds.
    /// - no-head: nothing comes; once the server waits, move the clock by 10 seconds.
    ///
    /// Expected:
    /// - before: Whole with the body "x". A limit shorter than 30 seconds would have ended the exchange.
    /// - at and no-head: TimeoutException with "No whole answer came within 30 seconds.", within 20 seconds of the real
    ///   clock. A limit that started again with the body, a longer one, one that left the body out, or one counted on
    ///   the real clock would not end the exchange in that time.
    /// </remarks>
    /// <param name="moment">The case.</param>
    [Theory]
    [InlineData("before")]
    [InlineData("at")]
    [InlineData("no-head")]
    public async Task Send_AnswerThatStops_EndsAtThirtySecondsOfTheGivenClockSinceTheRequest(string moment)
    {
        var time = new FakeTimeProvider(Start);
        await using RawAnswerServer server = RawAnswerServer.Start(_ =>
        {
            time.Advance(TimeSpan.FromSeconds(20));
            return moment == "no-head" ? RawAnswer.Silent : RawAnswer.Stalled("x", 1);
        });
        var head = new HeadSeen { InnerHandler = BoundedAnswer.CreateHandler() };
        using HttpClient http = BoundedAnswer.CreateClient(head);

        Task<BoundedAnswer> sending = SendAsync(http, server, time);
        await server.Held.WaitAsync(Hang, Cancel);
        if (moment != "no-head")
        {
            await head.Seen.WaitAsync(Hang, Cancel);
        }

        if (moment != "before")
        {
            time.Advance(TimeSpan.FromSeconds(10));
            TimeoutException timeout = await Assert.ThrowsAsync<TimeoutException>(() => sending);
            Assert.Equal("No whole answer came within 30 seconds.", timeout.Message);
            return;
        }

        time.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        server.Release();
        BoundedAnswer.Whole whole = Assert.IsType<BoundedAnswer.Whole>(await sending);
        Assert.Equal("x", Encoding.ASCII.GetString(whole.Body.Span));
    }

    /// <summary>
    /// The caller's cancellation is not reported as a timeout.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A fake clock. A server that sends the first byte of a body in chunks and then nothing.
    ///
    /// Steps:
    /// - Send a request with a token; once the server waits, cancel the token.
    ///
    /// Expected:
    /// - OperationCanceledException, not TimeoutException: the clock did not move.
    /// </remarks>
    [Fact]
    public async Task Send_CallerCancels_IsNotATimeout()
    {
        var time = new FakeTimeProvider(Start);
        await using RawAnswerServer server = RawAnswerServer.Start(_ => RawAnswer.Stalled("x", 1));
        using HttpClient http = BoundedAnswer.CreateClient(BoundedAnswer.CreateHandler());
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.Url, "x"));

        Task<BoundedAnswer> sending = BoundedAnswer.SendAsync(http, request, Limit, time, caller.Token).WaitAsync(Hang, Cancel);
        await server.Held.WaitAsync(Hang, Cancel);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
    }

    /// <summary>
    /// A body that ends before its declared length is a failed request, as it was when the HTTP client read the body itself.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server whose answer declares 40 bytes, sends 1, and closes the connection.
    ///
    /// Steps:
    /// - Send a request.
    ///
    /// Expected:
    /// - HttpRequestException with the error ResponseEnded and "The answer ended before it was whole.".
    /// </remarks>
    [Fact]
    public async Task Send_BodyCutShort_IsAFailedRequest()
    {
        await using RawAnswerServer server = RawAnswerServer.Start(_ => new RawAnswer
        {
            Declared = 40,
            Start = "{",
            Length = 1,
        });
        using HttpClient http = BoundedAnswer.CreateClient(BoundedAnswer.CreateHandler());

        HttpRequestException failed = await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync(http, server));

        Assert.Equal((HttpRequestError.ResponseEnded, "The answer ended before it was whole."), (failed.HttpRequestError, failed.Message));
    }

    /// <summary>
    /// With a limit that the doubling buffer does not meet exactly, a body of the limit is whole and one of a byte more is
    /// too large: the buffer stops growing at the limit, and the byte after it is asked for on its own.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A limit of 1,500 bytes; the buffer starts at 1,024 bytes and would double to 2,048.
    /// - A server whose answer has a body in one chunk, of 1,500 bytes, and of 1,501 bytes.
    ///
    /// Steps:
    /// - Send a request with the limit 1,500 to each.
    ///
    /// Expected:
    /// - 1,500 bytes: Whole with a body of 1,500 bytes. 1,501 bytes: TooLarge. A buffer that grew to 1,501 bytes would take
    ///   the whole of the longer body in one read and return it as whole.
    /// </remarks>
    [Fact]
    public async Task Send_LimitNotAPowerOfTwo_IsWholeAtItAndTooLargePastIt()
    {
        const int Odd = 1_500;
        static RawAnswer Shaped(int length) =>
            new()
            {
                Chunked = true,
                Start = "{}",
                Length = length,
            };
        await using RawAnswerServer atLimit = RawAnswerServer.Start(_ => Shaped(Odd));
        await using RawAnswerServer over = RawAnswerServer.Start(_ => Shaped(Odd + 1));
        using HttpClient http = BoundedAnswer.CreateClient(BoundedAnswer.CreateHandler());
        var time = new FakeTimeProvider(Start);

        using var first = new HttpRequestMessage(HttpMethod.Get, new Uri(atLimit.Url, "x"));
        BoundedAnswer whole = await BoundedAnswer.SendAsync(http, first, Odd, time, Cancel).WaitAsync(Hang, Cancel);
        using var second = new HttpRequestMessage(HttpMethod.Get, new Uri(over.Url, "x"));
        BoundedAnswer tooLarge = await BoundedAnswer.SendAsync(http, second, Odd, time, Cancel).WaitAsync(Hang, Cancel);

        Assert.Equal(Odd, Assert.IsType<BoundedAnswer.Whole>(whole).Body.Length);
        Assert.Equal(new BoundedAnswer.TooLarge(HttpStatusCode.OK), tooLarge);
    }

    /// <summary>
    /// A body whose chunks are not valid HTTP is a failed request that says so, not one that ended early.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server whose answer says Transfer-Encoding: chunked and then sends a chunk size that is not hexadecimal, "zz".
    ///
    /// Steps:
    /// - Send a request.
    ///
    /// Expected:
    /// - HttpRequestException with the error InvalidResponse and "The answer's body is not valid HTTP.".
    /// </remarks>
    [Fact]
    public async Task Send_ChunksNotValid_IsAFailedRequestOfAnInvalidAnswer()
    {
        await using RawAnswerServer server = RawAnswerServer.Start(_ =>
            RawAnswer.Bytes(Encoding.ASCII.GetBytes("zz\r\nabc\r\n0\r\n\r\n")) with
            {
                Declared = null,
                Headers = "Transfer-Encoding: chunked\r\n",
            }
        );
        using HttpClient http = BoundedAnswer.CreateClient(BoundedAnswer.CreateHandler());

        HttpRequestException failed = await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync(http, server));

        Assert.Equal((HttpRequestError.InvalidResponse, "The answer's body is not valid HTTP."), (failed.HttpRequestError, failed.Message));
    }

    private static async Task<BoundedAnswer> SendAsync(HttpClient http, RawAnswerServer server, TimeProvider? time = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.Url, "x"));
        return await BoundedAnswer.SendAsync(http, request, Limit, time ?? new FakeTimeProvider(Start), Cancel).WaitAsync(Hang, Cancel);
    }

    // Tells when the handler below has returned an answer: its head has been read, and its body has not.
    private sealed class HeadSeen : DelegatingHandler
    {
        private readonly TaskCompletionSource _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Seen => _seen.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            _seen.TrySetResult();
            return response;
        }
    }
}
