using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ElasticLM.Issuing;

namespace ElasticLM.Client.Tests;

/// <summary>
/// The answer a <see cref="RawAnswerServer"/> gives to one request: its head as written, and a body of a chosen length whose
/// bytes after <see cref="Start"/> are spaces.
/// </summary>
internal sealed record RawAnswer
{
    /// <summary>Gets the status code.</summary>
    public int Status { get; init; } = 200;

    /// <summary>Gets the value of the <c>Content-Length</c> header, or <see langword="null"/> to send none.</summary>
    public long? Declared { get; init; }

    /// <summary>Gets a value indicating whether the body is sent in chunks (<c>Transfer-Encoding: chunked</c>).</summary>
    public bool Chunked { get; init; }

    /// <summary>Gets the first bytes of the body.</summary>
    public string Start { get; init; } = string.Empty;

    /// <summary>Gets the number of body bytes the server writes, <see cref="Start"/> included, unless the client closes first.</summary>
    public long Length { get; init; }

    /// <summary>Gets a value indicating whether the server keeps the connection open after the last byte it writes, without
    /// ending the body, until the client closes or the server is disposed.</summary>
    public bool Hold { get; init; }

    /// <summary>Gets more header lines, each ending with a line break.</summary>
    public string Headers { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the server writes nothing at all, not even the head.</summary>
    public bool NoHead { get; init; }

    /// <summary>Gets no answer: the server reads the request and keeps the connection open.</summary>
    public static RawAnswer Silent { get; } = new() { NoHead = true, Hold = true };

    /// <summary>A whole answer with a <c>Content-Length</c>: <paramref name="start"/> padded with spaces to
    /// <paramref name="length"/> bytes, or <paramref name="start"/> alone.</summary>
    public static RawAnswer Whole(string start, long? length = null, int status = 200) =>
        new()
        {
            Status = status,
            Start = start,
            Length = length ?? Encoding.UTF8.GetByteCount(start),
            Declared = length ?? Encoding.UTF8.GetByteCount(start),
        };

    /// <summary>An answer without a <c>Content-Length</c>, whose body of <paramref name="length"/> bytes ends when the server
    /// closes the connection.</summary>
    public static RawAnswer UntilClose(string start, long length, int status = 200) =>
        new()
        {
            Status = status,
            Start = start,
            Length = length,
            Headers = "Connection: close\r\n",
        };

    /// <summary>An answer that declares <paramref name="declared"/> bytes and sends none of them.</summary>
    public static RawAnswer DeclaredOnly(long declared, int status = 200) =>
        new()
        {
            Status = status,
            Declared = declared,
            Hold = true,
        };

    /// <summary>A body in chunks that goes on for as long as the client reads, up to <paramref name="offered"/> bytes: far
    /// more than any limit, so a client that reads on shows in <see cref="RawAnswerServer.BodyBytesWritten"/>, while a
    /// failing run does not fill the machine's memory.</summary>
    public static RawAnswer Endless(long offered, int status = 200) =>
        new()
        {
            Status = status,
            Chunked = true,
            Length = offered,
            Hold = true,
        };

    /// <summary>A body in chunks of which <paramref name="sent"/> bytes come, and then nothing.</summary>
    public static RawAnswer Stalled(string start, int sent) =>
        new()
        {
            Chunked = true,
            Start = start,
            Length = sent,
            Hold = true,
        };
}

/// <summary>
/// A server on a loopback port, or on a Unix domain socket file, that writes the answers a test gives it byte by byte, as a
/// false server or a relay on the path can (design §5.3 T16, §12.1): a body longer than a limit, a declared length without
/// a body, a body without an end, a body that stops. It counts the body bytes it wrote, which shows how much of an answer
/// a client took before it closed the connection. With TLS it shows a self-signed certificate whose key is made at run
/// time.
/// </summary>
internal sealed class RawAnswerServer : IAsyncDisposable
{
    private static readonly DateTimeOffset NotBefore = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Spaces = Encoding.ASCII.GetBytes(new string(' ', 64 * 1024));
    private static readonly byte[] LastChunk = "0\r\n\r\n"u8.ToArray();

    private readonly Socket _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Func<string, RawAnswer> _answer;
    private readonly X509Certificate2? _certificate;
    private readonly List<string> _requests = [];
    private readonly List<string> _heads = [];
    private readonly List<Task> _serving = [];
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _accepting;
    private long _written;

    private RawAnswerServer(EndPoint endPoint, bool tls, Func<string, RawAnswer> answer)
    {
        _answer = answer;
        if (tls)
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=raw-answer-server", key, HashAlgorithmName.SHA256);
            using X509Certificate2 made = request.CreateSelfSigned(NotBefore, NotBefore.AddYears(10));
            _certificate = TlsCertificateFiles.ForKestrel(made);
        }

        _listener = new Socket(endPoint.AddressFamily, SocketType.Stream, endPoint is IPEndPoint ? ProtocolType.Tcp : ProtocolType.Unspecified);
        _listener.Bind(endPoint);
        _listener.Listen(16);
        Url = _listener.LocalEndPoint is IPEndPoint bound
            ? new Uri($"{(tls ? "https" : "http")}://127.0.0.1:{bound.Port}/")
            : new Uri("http://elm-server.invalid/");
        _accepting = Task.Run(AcceptAsync);
    }

    /// <summary>Gets the URL the server listens on. A server on a socket file has none that a client can connect to.</summary>
    public Uri Url { get; }

    /// <summary>Gets the number of body bytes written so far on all connections. A chunk's frame is not counted.</summary>
    public long BodyBytesWritten => Interlocked.Read(ref _written);

    /// <summary>Gets a task that ends when an answer with <see cref="RawAnswer.Hold"/> has written its last byte and waits.</summary>
    public Task Held => _held.Task;

    /// <summary>Gets the method and target of the requests read so far, such as <c>GET /v1/nonce</c>.</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Gets the heads of the requests read so far: the request line and the header lines.</summary>
    public IReadOnlyList<string> Heads
    {
        get
        {
            lock (_requests)
            {
                return [.. _heads];
            }
        }
    }

    /// <summary>Starts a server on a loopback port.</summary>
    /// <param name="answer">The answer to each request, by its method and target, such as <c>GET /v1/nonce</c>.</param>
    /// <param name="tls"><see langword="true"/> for https with a self-signed certificate.</param>
    /// <returns>The server, which the caller disposes.</returns>
    public static RawAnswerServer Start(Func<string, RawAnswer> answer, bool tls = false) => new(new IPEndPoint(IPAddress.Loopback, 0), tls, answer);

    /// <summary>Starts a server on a Unix domain socket file, without TLS.</summary>
    /// <param name="path">The socket file, which must not exist.</param>
    /// <param name="answer">The answer to each request, by its method and target.</param>
    /// <returns>The server, which the caller disposes.</returns>
    public static RawAnswerServer StartOnSocket(string path, Func<string, RawAnswer> answer) =>
        new(new UnixDomainSocketEndPoint(path), tls: false, answer);

    /// <summary>Ends the body of every answer in chunks that is held, now or later, so that its client gets a whole answer.</summary>
    public void Release() => _released.TrySetResult();

    /// <summary>
    /// Waits until the body bytes written stop changing, and returns their number: after a client has closed its connection,
    /// the server's last write fails and the count stands. The wait ends on the count, not on a time.
    /// </summary>
    public async Task<long> SettledBodyBytesAsync()
    {
        long last = -1;
        for (int i = 0; i < 1_000; i++)
        {
            Task[] serving;
            lock (_serving)
            {
                serving = [.. _serving];
            }

            long now = BodyBytesWritten;
            if (now == last && serving.All(t => t.IsCompleted))
            {
                return now;
            }

            last = now;
            await Task.WhenAny(Task.WhenAll(serving), Task.Delay(10, TestContext.Current.CancellationToken));
        }

        Assert.Fail("The server went on writing: a client still reads.");
        return last;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Dispose();
        await _accepting;
        Task[] serving;
        lock (_serving)
        {
            serving = [.. _serving];
        }

        await Task.WhenAll(serving);
        _certificate?.Dispose();
        _stopping.Dispose();
    }

    // Each connection is served on its own task; the loop ends when the listener is disposed, whatever the accept throws
    // for that on this system.
    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await _listener.AcceptAsync(_stopping.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            lock (_serving)
            {
                _serving.Add(Task.Run(() => ServeAsync(connection)));
            }
        }
    }

    private async Task ServeAsync(Socket connection)
    {
        try
        {
            await using var network = new NetworkStream(connection, ownsSocket: true);
            await using Stream stream = _certificate is null ? network : new SslStream(network);
            if (stream is SslStream tls)
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stopping.Token);
            }

            while (await ReadRequestAsync(stream) is string target)
            {
                RawAnswer answer = _answer(target);
                await WriteAsync(stream, answer);
                if (answer.Hold)
                {
                    _held.TrySetResult();
                    Task closed = WaitForCloseAsync(stream);
                    if (await Task.WhenAny(closed, _released.Task) != closed && answer.Chunked)
                    {
                        await stream.WriteAsync(LastChunk, _stopping.Token);
                        await stream.FlushAsync(_stopping.Token);
                    }

                    // A connection that was held takes no further request.
                    await closed;
                    return;
                }

                if (answer.Declared != answer.Length && !answer.Chunked)
                {
                    // The body ends with the connection, before its declared length when it has one. The whole request
                    // was read, so the close does not reset the connection.
                    return;
                }
            }
        }
        catch (Exception e)
            when (e
                    is IOException
                        or AuthenticationException
                        or SocketException
                        or OperationCanceledException
                        or ObjectDisposedException
                        or FormatException
                        or OverflowException
            )
        {
            // The client closed the connection, the server is disposed, or what came is not a request.
        }
    }

    private async Task WriteAsync(Stream stream, RawAnswer answer)
    {
        if (answer.NoHead)
        {
            return;
        }

        var head = new StringBuilder();
        head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {answer.Status} Answer\r\nContent-Type: application/json\r\n");
        if (answer.Declared is long declared)
        {
            head.Append(CultureInfo.InvariantCulture, $"Content-Length: {declared}\r\n");
        }

        if (answer.Chunked)
        {
            head.Append("Transfer-Encoding: chunked\r\n");
        }

        head.Append(answer.Headers).Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), _stopping.Token);
        await stream.FlushAsync(_stopping.Token);

        byte[] start = Encoding.UTF8.GetBytes(answer.Start);
        long left = answer.Length;
        while (left > 0)
        {
            ReadOnlyMemory<byte> part = left == answer.Length && start.Length > 0 ? start : Spaces.AsMemory(0, (int)Math.Min(left, Spaces.Length));
            if (answer.Chunked)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(part.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n"), _stopping.Token);
            }

            await stream.WriteAsync(part, _stopping.Token);
            if (answer.Chunked)
            {
                await stream.WriteAsync("\r\n"u8.ToArray(), _stopping.Token);
            }

            await stream.FlushAsync(_stopping.Token);
            Interlocked.Add(ref _written, part.Length);
            left -= part.Length;
        }

        if (answer.Chunked && !answer.Hold)
        {
            await stream.WriteAsync(LastChunk, _stopping.Token);
            await stream.FlushAsync(_stopping.Token);
        }
    }

    // Returns when the client closes its side, or throws when the server is disposed.
    private async Task WaitForCloseAsync(Stream stream)
    {
        byte[] sink = new byte[1024];
        while (await stream.ReadAsync(sink, _stopping.Token) > 0)
        {
            // What a client sends on a connection whose answer never ends is not looked at.
        }
    }

    // Reads the whole request, its body included. Returns the method and the target, or null when the connection ended before
    // a whole request came.
    private async Task<string?> ReadRequestAsync(Stream stream)
    {
        var received = new MemoryStream();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, _stopping.Token);
            if (read == 0)
            {
                return null;
            }

            received.Write(chunk, 0, read);
            string text = Encoding.Latin1.GetString(received.GetBuffer(), 0, (int)received.Length);
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0)
            {
                continue;
            }

            string[] lines = text[..end].Split("\r\n");
            const string LengthName = "Content-Length:";
            string? length = lines.FirstOrDefault(line => line.StartsWith(LengthName, StringComparison.OrdinalIgnoreCase));
            int body = length is null ? 0 : int.Parse(length[LengthName.Length..].Trim(), CultureInfo.InvariantCulture);
            if (received.Length >= end + 4 + body)
            {
                string[] requestLine = lines[0].Split(' ');
                if (requestLine.Length < 2)
                {
                    return null;
                }

                lock (_requests)
                {
                    _requests.Add(requestLine[0] + " " + requestLine[1]);
                    _heads.Add(text[..end]);
                }

                return requestLine[0] + " " + requestLine[1];
            }
        }
    }
}
