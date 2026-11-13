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

    private BoundedAnswer(HttpStatusCode status) => Status = status;

    /// <summary>Gets the answer's status.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>Gets a value indicating whether the status is a success (2xx).</summary>
    public bool IsSuccess => (int)Status is >= 200 and <= 299;

    /// <summary>
    /// Creates the handler that every bounded exchange uses. It follows no redirect: the answer of another address is not
    /// the answer asked for, and a request's body would go there.
    /// </summary>
    /// <returns>The handler, which the caller may set up further and owns.</returns>
    public static SocketsHttpHandler CreateHandler() => new() { AllowAutoRedirect = false };

    /// <summary>
    /// Creates an HTTP client for <see cref="SendAsync"/>. Its own timeout is off: it runs on real timers and does not
    /// cover a body that is read after the headers.
    /// </summary>
    /// <param name="handler">The handler, which the client disposes.</param>
    /// <returns>The client.</returns>
    public static HttpClient CreateClient(HttpMessageHandler handler) => new(handler) { Timeout = Timeout.InfiniteTimeSpan };

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
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            byte[]? body = await ReadAsync(response.Content, limit, linked.Token).ConfigureAwait(false);
            return body is null ? new TooLarge(response.StatusCode) : new Whole(response.StatusCode, body, response.Headers);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException && deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"No whole answer came within {Deadline.TotalSeconds:0} seconds.", e);
        }
        catch (IOException e)
        {
            // With the headers read first, a body that breaks off surfaces from the stream, not from the HTTP client.
            throw new HttpRequestException(HttpRequestError.ResponseEnded, "The answer ended before it was whole.", e);
        }
    }

    // Returns null for a body longer than the limit. A declared length is believed only to refuse or to size the buffer:
    // the HTTP client's stream ends at the declared length, and a body that ends before it throws.
    private static async Task<byte[]?> ReadAsync(HttpContent content, int limit, CancellationToken cancellationToken)
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

            // The buffer never grows past the limit and one byte, so a body of unknown length shows that it is too long
            // without more of it being taken.
            byte[] buffer = new byte[Math.Min(limit + 1, FirstBufferLength)];
            int length = 0;
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer[..length];
                }

                length += read;
                if (length > limit)
                {
                    return null;
                }

                if (length == buffer.Length)
                {
                    Array.Resize(ref buffer, (int)Math.Min(limit + 1L, buffer.Length * 2L));
                }
            }
        }
    }

    /// <summary>An answer whose body is within the limit.</summary>
    /// <param name="Status">The answer's status.</param>
    /// <param name="Body">The body.</param>
    /// <param name="Headers">The answer's headers.</param>
    public sealed record Whole(HttpStatusCode Status, byte[] Body, HttpResponseHeaders Headers) : BoundedAnswer(Status);

    /// <summary>
    /// An answer whose body is longer than the limit, or declares that it is. Only its status is known: its headers are
    /// not handed on, since nothing of such an answer is acted on.
    /// </summary>
    /// <param name="Status">The answer's status.</param>
    public sealed record TooLarge(HttpStatusCode Status) : BoundedAnswer(Status);
}
