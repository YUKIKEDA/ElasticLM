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
    /// The base address of every request. The server does not read the host. <c>.invalid</c> never resolves (RFC 2606 §2,
    /// RFC 6761 §6.4), so a request that lost its connect callback fails instead of going to a TCP port of this machine.
    /// </summary>
    public static readonly Uri BaseAddress = new("http://elm-server.invalid/");

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
    public static SocketsHttpHandler CreateHandler(CliHost host, UnixDomainSocketEndPoint endPoint)
    {
        Func<UnixDomainSocketEndPoint, CancellationToken, ValueTask<Stream>> connect = host.ConnectAdminSocket ?? ConnectAsync;
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = (_, cancellationToken) => connect(endPoint, cancellationToken),
        };
    }

    /// <summary>
    /// Creates the HTTP client of the requests to one server.
    /// </summary>
    /// <param name="host">The command's host.</param>
    /// <param name="endPoint">The server's socket file.</param>
    /// <param name="timeout">The HTTP client's own limit for one request.</param>
    public static HttpClient CreateClient(CliHost host, UnixDomainSocketEndPoint endPoint, TimeSpan timeout) =>
        new(CreateHandler(host, endPoint)) { BaseAddress = BaseAddress, Timeout = timeout };

    /// <summary>
    /// Returns the sentence for an answer of the administration API that is not the one asked for: its error code and
    /// detail when it has a code, or else its status code.
    /// </summary>
    /// <param name="status">The answer's status code.</param>
    /// <param name="answer">The answer's body as JSON, or the default value when it has none that can be read.</param>
    public static string Unexpected(System.Net.HttpStatusCode status, System.Text.Json.JsonElement answer) =>
        Text(answer, "code") is string code ? $"{code}: {Text(answer, "detail")}" : $"The server's administration API answered {(int)status}.";

    private static string? Text(System.Text.Json.JsonElement value, string name) =>
        value.ValueKind == System.Text.Json.JsonValueKind.Object
        && value.TryGetProperty(name, out System.Text.Json.JsonElement member)
        && member.ValueKind == System.Text.Json.JsonValueKind.String
            ? member.GetString()
            : null;

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
