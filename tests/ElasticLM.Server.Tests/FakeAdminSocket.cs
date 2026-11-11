using System.Net.Sockets;
using System.Text;
using ElasticLM.Client;

namespace ElasticLM.Server.Tests;

/// <summary>
/// What a test puts at the path of a server's administration socket (<c>admin.sock</c> in its data folder, design §13.7): a
/// listener of its own that counts the connections and the bytes it gets and may answer each request, or a socket file that
/// nobody listens on, as a killed server leaves it.
/// </summary>
internal sealed class FakeAdminSocket : IDisposable
{
    // What ConnectionsBeforeNowAsync sends on its own connection; no peer under test sends it.
    private static readonly byte[] Mark = "\0fake-admin-socket-mark\0"u8.ToArray();

    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly Func<int, string?> _answer;
    private readonly TaskCompletionSource _stopped = new();
    private readonly Task _accepting;
    private readonly List<Socket> _accepted = [];
    private int _connections;
    private int _bytes;
    private int _requests;
    private int _open;
    private int _marks;

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeAdminSocket"/> class, which listens at once.
    /// </summary>
    /// <param name="folder">The data folder; the listener binds <c>admin.sock</c> in it.</param>
    /// <param name="answer">The bytes that answer the request of each connection, by the connection's number from 0, once
    /// the request's head has come; <see langword="null"/> gives no answer and keeps the connection open until the fake is
    /// disposed.</param>
    /// <param name="onRequest">Runs when the head of a request has come, before its answer.</param>
    public FakeAdminSocket(string folder, Func<int, string?>? answer = null, Action? onRequest = null)
    {
        Path = AdminSocket.PathIn(folder);
        _answer = answer ?? (_ => null);
        _listener.Bind(new UnixDomainSocketEndPoint(Path));
        _listener.Listen(16);
        _accepting = Task.Run(() => AcceptAsync(onRequest));
    }

    /// <summary>Gets the path of the socket file.</summary>
    public string Path { get; }

    /// <summary>Gets the number of connections the listener accepted.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Gets the number of bytes the listener received on all its connections.</summary>
    public int BytesReceived => Volatile.Read(ref _bytes);

    /// <summary>Gets the number of requests whose head (up to the empty line) has come.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>
    /// Leaves a socket file at <c>admin.sock</c> of a data folder that nobody listens on, so a connection to it is refused:
    /// what a server that was killed leaves. A listener binds another name, the file is renamed, and the listener closes;
    /// .NET then deletes only the name it bound (renaming a bound socket file and the refusal afterwards: tried on Windows 11
    /// with .NET 10.0.15).
    /// </summary>
    /// <param name="folder">The data folder.</param>
    /// <returns>The path of the socket file.</returns>
    public static string LeaveFile(string folder)
    {
        string path = AdminSocket.PathIn(folder);
        string bound = System.IO.Path.Combine(System.IO.Path.GetFullPath(folder), "left.sock");
        using (var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            listener.Bind(new UnixDomainSocketEndPoint(bound));
            listener.Listen(1);
            File.Move(bound, path);
        }

        Assert.True(System.IO.Path.Exists(path), "The socket file that was left is gone.");
        return path;
    }

    /// <summary>
    /// Returns the number of connections that came before this call, once every one of them has been read to its end, so that
    /// <see cref="BytesReceived"/> holds every byte a peer sent before it closed. The listener accepts on its own thread, so
    /// <see cref="Connections"/> read right after a peer's connection may not count it yet. This method connects once
    /// itself and sends a mark that the listener knows: the listener takes connections in the order they were made, so when
    /// it has taken this one, it has taken every earlier one. The method's own connections and their bytes are not counted.
    /// </summary>
    public async Task<int> ConnectionsBeforeNowAsync()
    {
        int marks = Volatile.Read(ref _marks);
        using (var own = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            await own.ConnectAsync(new UnixDomainSocketEndPoint(Path), TestContext.Current.CancellationToken);
            await own.SendAsync(Mark, TestContext.Current.CancellationToken);
        }

        for (int i = 0; i < 1_000 && (Volatile.Read(ref _marks) == marks || Volatile.Read(ref _open) > 0); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(marks + 1, Volatile.Read(ref _marks));
        Assert.Equal(0, Volatile.Read(ref _open));
        return Connections;
    }

    public void Dispose()
    {
        _stopped.TrySetResult();
        _listener.Dispose();
        lock (_accepted)
        {
            // A peer that keeps its connection open would otherwise keep its reader waiting.
            foreach (Socket connection in _accepted)
            {
                connection.Dispose();
            }
        }

        _accepting.GetAwaiter().GetResult();
    }

    private async Task AcceptAsync(Action? onRequest)
    {
        var served = new List<Task>();
        try
        {
            while (true)
            {
                Socket connection = await _listener.AcceptAsync();
                lock (_accepted)
                {
                    _accepted.Add(connection);
                }

                Interlocked.Increment(ref _open);
                served.Add(Task.Run(() => ServeAsync(connection, onRequest)));
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // The fake is disposed.
        }

        await Task.WhenAll(served);
    }

    // A connection is counted when its first bytes, or its end, show whether it is the fake's own mark.
    private async Task ServeAsync(Socket connection, Action? onRequest)
    {
        using (connection)
        {
            try
            {
                var head = new StringBuilder();
                byte[] buffer = new byte[4096];
                bool answered = false;
                int number = -1;
                while (true)
                {
                    int read = await connection.ReceiveAsync(buffer);
                    if (number < 0 && buffer.AsSpan(0, read).SequenceEqual(Mark))
                    {
                        Interlocked.Increment(ref _marks);
                        return;
                    }

                    if (number < 0)
                    {
                        number = Interlocked.Increment(ref _connections) - 1;
                    }

                    if (read == 0)
                    {
                        return;
                    }

                    Interlocked.Add(ref _bytes, read);
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    if (answered || !head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    answered = true;
                    Interlocked.Increment(ref _requests);
                    onRequest?.Invoke();
                    if (_answer(number) is string answer)
                    {
                        await connection.SendAsync(Encoding.ASCII.GetBytes(answer));
                        return;
                    }

                    // No answer: the connection stays open until the test ends the fake. A peer that does not give up by then
                    // gets an answer after ten seconds of the real clock, so that its test fails on an assertion and does
                    // not hang.
                    if (await Task.WhenAny(_stopped.Task, Task.Delay(TimeSpan.FromSeconds(10))) != _stopped.Task)
                    {
                        await connection.SendAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
                    }

                    return;
                }
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException)
            {
                // The peer closed the connection, or the fake is disposed.
            }
            finally
            {
                Interlocked.Decrement(ref _open);
            }
        }
    }
}
