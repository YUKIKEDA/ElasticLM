using System.Net.Http.Headers;
using System.Net.Sockets;
using ElasticLM.Client;

namespace ElasticLM.Cli;

/// <summary>
/// How the <c>elm server</c> commands reach the server's local administration API (design §13.7, V1d2): over the Unix domain
/// socket <c>admin.sock</c> in the data folder that also holds the administrator token, and over nothing else. No name is
/// resolved and no TCP connection is made, so the token goes only to what listens in that folder.
/// </summary>
internal static class ServerAdminApi
{
    /// <summary>
    /// The largest answer body, in bytes, that an <c>elm server</c> command reads from the administration API (design §13.7,
    /// V1d6): 64 MiB. The list of leases is the one answer that grows with the server's use; the design gives its measured
    /// size for each lease and the number of leases the limit holds.
    /// </summary>
    public const int MaxAnswerLength = 64 * 1024 * 1024;

    /// <summary>
    /// The base address of every request. The server does not read the host. <c>.invalid</c> never resolves (RFC 2606 §2,
    /// RFC 6761 §6.4), so a request that lost its connect callback fails instead of going to a TCP port of this machine.
    /// </summary>
    public static readonly Uri BaseAddress = new("http://elm-server.invalid/");

    /// <summary>The line for an answer of the administration API whose body is over <see cref="MaxAnswerLength"/>.</summary>
    public static readonly string AnswerTooLarge =
        $"ELM_MALFORMED: The server's administration API answered with a body larger than {MaxAnswerLength} bytes.";

    /// <summary>
    /// Returns the socket file of a server data folder and its endpoint.
    /// </summary>
    /// <param name="folder">The server's data folder.</param>
    /// <exception cref="CliException">The path is longer than this system takes for a socket file (exit code 2).</exception>
    public static (string Path, UnixDomainSocketEndPoint EndPoint) SocketOf(string folder)
    {
        string path = AdminSocket.PathIn(folder);
        return AdminSocket.TryCreateEndPoint(path, out UnixDomainSocketEndPoint? endPoint, out string? problem)
            ? (path, endPoint)
            : throw new CliException(problem);
    }

    /// <summary>
    /// Creates the handler of the requests to one server: every connection goes to its socket file, through no proxy, and
    /// no redirect is followed, since the answer of another address is not the answer of that server.
    /// </summary>
    /// <param name="host">The command's host, which may replace how the socket is connected.</param>
    /// <param name="endPoint">The server's socket file.</param>
    internal static SocketsHttpHandler CreateHandler(CliHost host, UnixDomainSocketEndPoint endPoint)
    {
        Func<UnixDomainSocketEndPoint, CancellationToken, ValueTask<Stream>> connect = host.ConnectAdminSocket ?? ConnectAsync;
        SocketsHttpHandler handler = BoundedAnswer.CreateHandler();
        handler.UseProxy = false;
        handler.ConnectCallback = (_, cancellationToken) => connect(endPoint, cancellationToken);
        return handler;
    }

    /// <summary>
    /// Sends one request to a server's administration API and reads its answer up to <see cref="MaxAnswerLength"/>, with
    /// the administrator token of the same folder. The whole exchange, the answer's body included, ends after 30 seconds
    /// of the host's clock.
    /// </summary>
    /// <param name="host">The command's host.</param>
    /// <param name="endPoint">The server's socket file.</param>
    /// <param name="method">The request's method.</param>
    /// <param name="path">The request's path, such as <c>admin/v1/status</c>.</param>
    /// <param name="token">The administrator token.</param>
    /// <returns>The answer, or <see cref="BoundedAnswer.TooLarge"/> for a body over the limit.</returns>
    /// <exception cref="HttpRequestException">No connection was made, or the answer ended before it was whole.</exception>
    /// <exception cref="TimeoutException">No whole answer came in time.</exception>
    public static BoundedAnswer Send(CliHost host, UnixDomainSocketEndPoint endPoint, HttpMethod method, string path, string token)
    {
        using HttpClient http = BoundedAnswer.CreateClient(CreateHandler(host, endPoint));
        using var request = new HttpRequestMessage(method, new Uri(BaseAddress, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return BoundedAnswer.SendAsync(http, request, MaxAnswerLength, host.Time, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Returns the sentence for an answer of the administration API that is not the one asked for: its error code and
    /// detail when it has a code, or else its status code.
    /// </summary>
    /// <param name="answer">The answer.</param>
    public static string Unexpected(AdminAnswer answer) =>
        answer.Text("code") is string code ? $"{code}: {answer.Text("detail")}" : $"The server's administration API answered {(int)answer.Status}.";

    private static async ValueTask<Stream> ConnectAsync(UnixDomainSocketEndPoint endPoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
