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
/// A server on a loopback port that gives every request the same answer, as a relay or a false server on the path can
/// (design §5.4, §12.1). With TLS it shows a self-signed certificate whose key is made at run time. The tests of the C
/// functions link this file too, since no HTTP handler can be put into a client made through them.
/// </summary>
internal sealed class ProblemServer : IAsyncDisposable
{
    private static readonly DateTimeOffset NotBefore = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<string> _targets = [];
    private readonly X509Certificate2? _certificate;
    private readonly byte[] _answer;
    private readonly Task _accepting;

    private ProblemServer(bool tls, HttpStatusCode status, string body)
    {
        if (tls)
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=problem-server", key, HashAlgorithmName.SHA256);
            using X509Certificate2 made = request.CreateSelfSigned(NotBefore, NotBefore.AddYears(10));
            _certificate = TlsCertificateFiles.ForKestrel(made);
        }

        byte[] content = Encoding.UTF8.GetBytes(body);
        string head =
            $"HTTP/1.1 {(int)status} Problem\r\nContent-Type: application/problem+json\r\nContent-Length: {content.Length}\r\n"
            + "Connection: close\r\n\r\n";
        _answer = [.. Encoding.ASCII.GetBytes(head), .. content];
        _listener.Start();
        Url = new Uri($"{(tls ? "https" : "http")}://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _accepting = AcceptAsync();
    }

    /// <summary>Gets the URL the server listens on.</summary>
    public Uri Url { get; }

    /// <summary>Gets the request lines' method and target of the requests answered so far, such as <c>GET /v1/nonce</c>.</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_targets)
            {
                return [.. _targets];
            }
        }
    }

    /// <summary>Starts a server that answers every request with one status and body.</summary>
    /// <param name="tls"><see langword="true"/> for https with a self-signed certificate, <see langword="false"/> for http.</param>
    /// <param name="status">The status of every answer.</param>
    /// <param name="body">The body of every answer.</param>
    /// <returns>The server, which the caller disposes.</returns>
    public static ProblemServer Start(bool tls, HttpStatusCode status, string body) => new(tls, status, body);

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        await _accepting;
        _certificate?.Dispose();
        _stopping.Dispose();
    }

    // One connection at a time: each answer closes its connection, and a client sends its requests one after another.
    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                using TcpClient connection = await _listener.AcceptTcpClientAsync(_stopping.Token);
                try
                {
                    await AnswerAsync(connection.GetStream());
                }
                catch (Exception e) when (e is IOException or AuthenticationException or SocketException or FormatException or OverflowException)
                {
                    // The client gave the connection up, or what it sent is not a request; the next one is answered.
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stopped.
        }
    }

    private async Task AnswerAsync(NetworkStream network)
    {
        // Disposed also when the handshake throws.
        await using Stream stream = _certificate is null ? network : new SslStream(network);
        if (stream is SslStream tls)
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stopping.Token);
        }

        if (await ReadRequestAsync(stream) is not string target)
        {
            return;
        }

        lock (_targets)
        {
            _targets.Add(target);
        }

        await stream.WriteAsync(_answer, _stopping.Token);
        await stream.FlushAsync(_stopping.Token);
    }

    // Reads the whole request, its body included, so that closing the connection does not reset it before the client has the
    // answer. Returns the method and the target, or null when the connection ended before a whole request came.
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
            int body = length is null ? 0 : int.Parse(length[LengthName.Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            if (received.Length >= end + 4 + body)
            {
                // A line without a target is not a request: the connection ends without an answer.
                string[] requestLine = lines[0].Split(' ');
                return requestLine.Length < 2 ? null : requestLine[0] + " " + requestLine[1];
            }
        }
    }
}
