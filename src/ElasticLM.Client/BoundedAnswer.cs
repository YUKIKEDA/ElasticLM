using System.Net;
using System.Net.Http.Headers;

namespace ElasticLM.Client;

// design §12.1 (the V1d6 Grill, 6-1 and 6-4), §8.6.

/// <summary>
/// An HTTP answer read with a limit on its body and one time limit over the whole exchange. The answer comes from
/// outside, so its body is never held beyond the limit: the request is sent without buffering the answer, an answer
/// that declares a longer body is given up before any of the body is read, and a body of unknown length is read one
/// byte past the limit at most.
/// </summary>
internal abstract record BoundedAnswer
{
    /// <summary>The largest body, in bytes, read from a license server, the activation service, or their relays.</summary>
    public const int MaxLength = 256 * 1024;

    /// <summary>The time allowed from sending a request to the end of its answer's body.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    // design §12.1 (V1d6): the answers of elm-server come in chunks, and most are a few KiB. Against a 16 KiB first buffer, a
    // lease request allocated 15 KiB less with this one (measured on Windows 11, JIT Release).
    private const int FirstBufferLength = 1024;

    // design §10.4 step 2 (V1d24e): the mark of the exchange that runs on this asynchronous flow. SendAsync sets a new one
    // for each request; a connection's plaintext stream sets it when the request's bytes are written.
    private static readonly AsyncLocal<WriteMark?> CurrentMark = new();

    // The filter is kept in one field so that CreateClient can tell a handler of CreateHandler by reference.
    private static readonly Func<SocketsHttpPlaintextStreamFilterContext, CancellationToken, ValueTask<Stream>> MarkingFilter = Mark;

    private BoundedAnswer(HttpStatusCode status) => Status = status;

    /// <summary>Gets the answer's status.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>Gets a value indicating whether the status is a success (2xx).</summary>
    public bool IsSuccess => (int)Status is >= 200 and <= 299;

    /// <summary>
    /// Creates the handler that every bounded exchange uses. It follows no redirect: the answer of another address is not
    /// the answer asked for, and a request's body would go there. It notes whether any byte of a request was written, so
    /// that <see cref="NeverSent"/> can tell a request that timed out or was cancelled before it left this device. The
    /// caller does not replace its <see cref="SocketsHttpHandler.PlaintextStreamFilter"/>.
    /// </summary>
    /// <returns>The handler, which the caller may set up further and owns.</returns>
    public static SocketsHttpHandler CreateHandler() => new() { AllowAutoRedirect = false, PlaintextStreamFilter = MarkingFilter };

    /// <summary>
    /// Creates an HTTP client for <see cref="SendAsync"/>. Its own timeout is off: it runs on real timers and does not
    /// cover a body that is read after the headers.
    /// </summary>
    /// <param name="handler">The handler, which the client disposes. When it is a handler of <see cref="CreateHandler"/>,
    /// or delegating handlers in front of one, <see cref="NeverSent"/> also tells the requests that timed out or were
    /// cancelled before any byte was written.</param>
    /// <returns>The client.</returns>
    public static HttpClient CreateClient(HttpMessageHandler handler) =>
        new MarkingClient(handler, Marks(handler)) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Sends a request and reads its answer.
    /// </summary>
    /// <param name="http">A client from <see cref="CreateClient"/>.</param>
    /// <param name="request">The request.</param>
    /// <param name="limit">The largest body to read, in bytes.</param>
    /// <param name="time">The clock that counts <see cref="Deadline"/>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns><see cref="Whole"/> with the body, or <see cref="TooLarge"/> when the body is longer than
    /// <paramref name="limit"/>.</returns>
    /// <exception cref="HttpRequestException">The request could not be sent, or the answer ended before it was whole.</exception>
    /// <exception cref="TimeoutException">No whole answer came within <see cref="Deadline"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<BoundedAnswer> SendAsync(
        HttpClient http,
        HttpRequestMessage request,
        int limit,
        TimeProvider time,
        CancellationToken cancellationToken
    )
    {
        using var deadline = new CancellationTokenSource(Deadline, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);

        // Set on this method's own flow, so the caller's flow keeps its value; the request's bytes are written on this flow.
        // Only HTTP/1.x writes a request on the request's own flow: HTTP/2 writes on the connection's, where the mark would
        // stay unset and every timeout would read as never sent. A request that may go out as HTTP/2 gets no mark.
        WriteMark? mark =
            http is MarkingClient { MarksWrites: true }
            && request.Version.Major == 1
            && request.VersionPolicy != HttpVersionPolicy.RequestVersionOrHigher
                ? new WriteMark()
                : null;
        CurrentMark.Value = mark;
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            ReadOnlyMemory<byte>? body = await ReadAsync(response.Content, limit, linked.Token).ConfigureAwait(false);
            return body is ReadOnlyMemory<byte> read ? new Whole(response.StatusCode, read, response.Headers) : new TooLarge(response.StatusCode);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException && deadline.IsCancellationRequested)
        {
            string message = $"No whole answer came within {Deadline.TotalSeconds:0} seconds.";
            throw mark is { Written: false } ? new UnwrittenTimeoutException(message, e) : new TimeoutException(message, e);
        }
        catch (OperationCanceledException e) when (mark is { Written: false })
        {
            throw new UnwrittenCancellationException(e.Message, e, e.CancellationToken);
        }
        catch (IOException e)
        {
            // With the headers read first, a body that breaks off surfaces from the stream, not from the HTTP client. The
            // stream names the kind of failure: a body that ends before its length, or chunks that are not valid HTTP.
            HttpRequestError error = (e as HttpIOException)?.HttpRequestError ?? HttpRequestError.ResponseEnded;
            string message = error switch
            {
                HttpRequestError.ResponseEnded => "The answer ended before it was whole.",
                HttpRequestError.InvalidResponse => "The answer's body is not valid HTTP.",
                _ => "The answer's body could not be read.",
            };
            throw new HttpRequestException(error, message, e);
        }
    }

    // design §10.4 step 2 (V1d24b). In dotnet/runtime release/10.0 these four values are set only before the request is
    // written: ConnectHelper.CreateWrappedException (name resolution, connection) and EstablishSslConnectionAsync (TLS),
    // HttpConnectionPool.EstablishProxyTunnelAsync and EstablishSocksTunnel, and the ALPN check of HttpConnectionPool.Http3. A
    // failure on a connection that was made is Unknown or a read error (HttpConnection.cs), and a request with a body is sent
    // again on another connection there only with Expect: 100-continue, which these clients do not set; they send HTTP/1.1.
    // A timeout or a cancellation has no HttpRequestError at any stage (V1d24e, measured on SDK 10.0.404): it is told by the
    // mark of the plaintext stream instead, and only on a client whose handler sets the mark.

    /// <summary>
    /// Returns whether a failed exchange never sent its request: the server's name was not resolved; the connection, its
    /// TLS handshake, or a proxy's tunnel was not made; or, for an HTTP/1.x request on a client over a handler of
    /// <see cref="CreateHandler"/>, the time limit or the caller's cancellation ended the exchange before any byte of the
    /// request was written. The server
    /// cannot have acted on such a request.
    /// </summary>
    /// <param name="failure">The exception of <see cref="SendAsync"/>.</param>
    /// <returns><see langword="true"/> when the request did not leave this device.</returns>
    public static bool NeverSent(Exception failure) =>
        failure
            is UnwrittenTimeoutException
                or UnwrittenCancellationException
                or HttpRequestException
                {
                    HttpRequestError: HttpRequestError.NameResolutionError
                        or HttpRequestError.ConnectionError
                        or HttpRequestError.SecureConnectionError
                        or HttpRequestError.ProxyTunnelError,
                };

    // Whether the handler, or the last inner handler of a chain of delegating handlers in front of it, sets the mark.
    private static bool Marks(HttpMessageHandler handler)
    {
        HttpMessageHandler? inner = handler;
        while (inner is DelegatingHandler delegating)
        {
            inner = delegating.InnerHandler;
        }

        return inner is SocketsHttpHandler sockets && ReferenceEquals(sockets.PlaintextStreamFilter, MarkingFilter);
    }

    // design §10.4 step 2 (V1d24e, question 24e-4): the connection to a proxy that carries a CONNECT tunnel is not marked. Its
    // bytes are the CONNECT request and, once the tunnel is made, the TLS handshake to the server, neither of which is the
    // request. The connection inside the tunnel gets its own filter and is marked. In dotnet/runtime release/10.0 the filter
    // runs once a connection, after TLS (HttpConnectionPool.ApplyPlaintextFilterAsync); measured on SDK 10.0.404, the
    // connection of the tunnel gets the CONNECT request as its InitialRequestMessage. CreateClient looks for this filter once,
    // when it makes the client (Marks): a filter replaced on the handler after that is not seen.
    private static ValueTask<Stream> Mark(SocketsHttpPlaintextStreamFilterContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            context.InitialRequestMessage.Method == HttpMethod.Connect ? context.PlaintextStream : new MarkingStream(context.PlaintextStream)
        );

    // Returns null for a body longer than the limit. A declared length is believed only to refuse or to size the buffer:
    // the HTTP client's stream ends at the declared length, and a body that ends before it throws. The body is not copied
    // once read: the caller gets the part of the buffer that holds it, so one answer holds at most 1.5 times the limit
    // while the buffer grows, and the limit once it is read.
    private static async Task<ReadOnlyMemory<byte>?> ReadAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        long? declared = content.Headers.ContentLength;
        if (declared > limit)
        {
            return null;
        }

        Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            if (declared is long known)
            {
                byte[] whole = new byte[known];
                await stream.ReadExactlyAsync(whole, cancellationToken).ConfigureAwait(false);
                return whole;
            }

            // The buffer never grows past the limit. Once it holds the limit, one byte more is asked for into a byte of its
            // own, so a body of unknown length shows that it is too long without more of it being taken.
            byte[] buffer = new byte[Math.Min(limit, FirstBufferLength)];
            byte[] probe = new byte[1];
            int length = 0;
            while (true)
            {
                if (length == limit)
                {
                    // Two returns, not a conditional: in `? buffer.AsMemory(…) : null` the null converts to an empty Memory
                    // through a null array, and an answer over the limit read as an empty body (seen in this row's tests).
                    if (await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false) != 0)
                    {
                        return null;
                    }

                    return buffer.AsMemory(0, length);
                }

                if (length == buffer.Length)
                {
                    Array.Resize(ref buffer, (int)Math.Min(limit, buffer.Length * 2L));
                }

                int read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.AsMemory(0, length);
                }

                length += read;
            }
        }
    }

    /// <summary>An answer whose body is within the limit.</summary>
    /// <param name="Status">The answer's status.</param>
    /// <param name="Body">The body.</param>
    /// <param name="Headers">The answer's headers.</param>
    public sealed record Whole(HttpStatusCode Status, ReadOnlyMemory<byte> Body, HttpResponseHeaders Headers) : BoundedAnswer(Status);

    /// <summary>
    /// An answer whose body is longer than the limit, or declares that it is. Only its status is known: its headers are
    /// not handed on, since nothing of such an answer is acted on.
    /// </summary>
    /// <param name="Status">The answer's status.</param>
    public sealed record TooLarge(HttpStatusCode Status) : BoundedAnswer(Status);

    // Whether any byte of one request was written to a connection's plaintext stream. The bytes are written on the stream's
    // thread, which need not be the one that reads the mark.
    private sealed class WriteMark
    {
        private volatile bool _written;

        public bool Written => _written;

        public void Set() => _written = true;
    }

    // A client that knows whether its handler sets the mark.
    private sealed class MarkingClient(HttpMessageHandler handler, bool marksWrites) : HttpClient(handler)
    {
        public bool MarksWrites { get; } = marksWrites;
    }

    // A time limit that ended an exchange before any byte of its request was written.
    private sealed class UnwrittenTimeoutException(string message, Exception inner) : TimeoutException(message, inner);

    // A cancellation that ended an exchange before any byte of its request was written.
    private sealed class UnwrittenCancellationException(string message, Exception inner, CancellationToken token)
        : OperationCanceledException(message, inner, token);

    // A connection's plaintext stream that sets the mark of the exchange whose flow writes to it. HTTP/1.1 writes a request
    // on the flow of the request that the connection serves (HttpConnection.SendAsync), also on a pooled connection.
    private sealed class MarkingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override bool CanTimeout => inner.CanTimeout;

        public override int ReadTimeout
        {
            get => inner.ReadTimeout;
            set => inner.ReadTimeout = value;
        }

        public override int WriteTimeout
        {
            get => inner.WriteTimeout;
            set => inner.WriteTimeout = value;
        }

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
            Written();
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Written();
            inner.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Written();
            return inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Written();
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private static void Written() => CurrentMark.Value?.Set();
    }
}
