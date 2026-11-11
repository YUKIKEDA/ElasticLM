using System.Net;
using System.Text;
using System.Text.Json;
using ElasticLM.Cli;
using ElasticLM.Client;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Server.Tests;

/// <summary>
/// The start of <c>elm-server</c> around the socket file of its administration API (design §13.1, §13.7, V1d2): the limit of
/// the socket's path, the lock file that keeps a second server off the data folder, the probe of a socket file that is left,
/// and the administrator token made anew at every start.
/// </summary>
public sealed class AdminSocketTests : IAsyncDisposable
{
    private readonly Deployment _deployment = new();
    private readonly List<IAsyncDisposable> _owned = [];

    public async ValueTask DisposeAsync()
    {
        foreach (IAsyncDisposable owned in Enumerable.Reverse(_owned))
        {
            await owned.DisposeAsync();
        }

        await _deployment.DisposeAsync();
    }

    /// <summary>
    /// The server listens on two things only: its https address for the lease protocol, and the socket file admin.sock in its
    /// data folder for the administration API. It has no other TCP listener (design §5.3 T20, §13.7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A running server with the test settings: listen on 127.0.0.1 at a port the OS picks.
    ///
    /// Steps:
    /// - Read the addresses that the host's server bound.
    ///
    /// Expected:
    /// - Exactly two: one that begins "https://127.0.0.1:", and "http://unix:" followed by the absolute path of admin.sock,
    ///   which is in the data folder.
    /// </remarks>
    [Fact]
    public async Task Start_Listens_OnTheHttpsAddressAndTheSocketFileOnly()
    {
        ElmServer server = await _deployment.StartServerAsync();

        ICollection<string> addresses = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;

        Assert.Equal(Path.Combine(server.DataDirectory, "admin.sock"), server.AdminSocketPath);
        Assert.Equal(2, addresses.Count);
        Assert.Single(addresses, a => a.StartsWith("https://127.0.0.1:", StringComparison.Ordinal));
        Assert.Single(addresses, a => a == "http://unix:" + server.AdminSocketPath);
    }

    /// <summary>
    /// A socket path of exactly the system's limit starts the server and elm reaches it; one byte more stops the server and
    /// elm with the line that gives the length and the limit, and the server leaves nothing behind.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The limit of this system, written here from the design: 107 bytes on Windows and Linux, 103 on macOS.
    /// - Two data folders under the test's folder whose names are padded so that the absolute path of admin.sock in them is
    ///   exactly the limit, and the limit plus one, in UTF-8 bytes. Each name holds "é", which is two bytes, so a count of
    ///   characters would be one short.
    ///
    /// Steps:
    /// - Start a server in the first folder and run elm server status with --data. Start a server in the second, which does
    ///   not exist yet, and run elm server status with --data.
    ///
    /// Expected:
    /// - The product's limit is the design's. The first server starts, and status exits 0.
    /// - The second start throws ServerStartException "The path of the administration socket '{path}' is {limit + 1} bytes in
    ///   UTF-8, and this system allows {limit}. Use a data folder with a shorter path."; the folder does not exist afterwards.
    ///   status exits 2 with the same sentence after "error: ".
    /// </remarks>
    [Fact]
    public async Task Start_SocketPathAtTheLimitAndOneByteOver_StartsAndStops()
    {
        int limit = OperatingSystem.IsMacOS() ? 103 : 107;
        Assert.Equal(limit, AdminSocket.MaxPathBytes);
        string at = PaddedFolder(limit);
        string over = PaddedFolder(limit + 1);
        _deployment.WriteServerLicense(at);

        ElmServer server = await _deployment.StartServerAsync(at);
        Assert.Equal(limit, Encoding.UTF8.GetByteCount(server.AdminSocketPath));
        (int code, _, string statusError) = Run(["server", "status", "--data", at]);
        Assert.True(code == 0, statusError);

        string line = TooLong(over, limit);
        var refused = await Assert.ThrowsAsync<ServerStartException>(() =>
            ElmServer.StartAsync(_deployment.Settings(over), _deployment.HostOptions(), TestContext.Current.CancellationToken)
        );
        Assert.Equal(line, refused.Message);
        Assert.False(Path.Exists(over));
        Assert.Equal((2, "error: " + line), Trimmed(Run(["server", "status", "--data", over])));
    }

    /// <summary>
    /// The length of the socket's path is checked before the trust store is read: a start with a path that is too long and no
    /// root key stops with the path's line (design §13.1 step 1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder whose socket path is one byte over the limit; settings without trust.rootKeys, and no
    ///   ELM_TRUST_ROOT_KEYS.
    ///
    /// Steps:
    /// - Start the server.
    ///
    /// Expected:
    /// - ServerStartException with the line of the path's length, not the one that asks for the root keys.
    /// </remarks>
    [Fact]
    public async Task Start_SocketPathTooLongAndNoRootKey_StopsWithThePathLine()
    {
        int limit = AdminSocket.MaxPathBytes;
        string over = PaddedFolder(limit + 1);
        ServerSettings settings = _deployment.Settings(over) with { TrustRootKeyFiles = [] };

        var refused = await Assert.ThrowsAsync<ServerStartException>(() =>
            ElmServer.StartAsync(settings, _deployment.HostOptions(), TestContext.Current.CancellationToken)
        );

        Assert.Equal(TooLong(over, limit), refused.Message);
    }

    /// <summary>
    /// A second server on the data folder of a running one stops at the lock file, before it reads the license, and changes
    /// nothing: the first server keeps its socket file, its token, and its logs.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A running server; the names of its usage log and borrow record files, and the bytes of its admin.token.
    ///
    /// Steps:
    /// - Start a second server with the same settings. Start a third with the same data folder and a license path that
    ///   names no file. Run elm server status with --data.
    ///
    /// Expected:
    /// - Both starts throw ServerStartException "Another elm-server is running on the data folder '{folder}': it holds the
    ///   lock file '{folder}/server.lock'."; the third does not get as far as the missing license.
    /// - admin.sock is still there and status exits 0 with the first server's license; the folder holds the same usage log
    ///   and borrow record files as before, and admin.token has the same bytes.
    /// </remarks>
    [Fact]
    public async Task Start_SecondServerOnTheDataFolder_StopsAtTheLockAndChangesNothing()
    {
        string data = _deployment.NewFolder();
        ElmServer first = await _deployment.StartServerAsync(data);
        string[] logs = Logs(data);
        byte[] token = File.ReadAllBytes(Path.Combine(data, AdminToken.FileName));
        string line = $"Another elm-server is running on the data folder '{data}': it holds the lock file '{Path.Combine(data, "server.lock")}'.";

        var second = await Assert.ThrowsAsync<ServerStartException>(() => _deployment.StartServerAsync(data));
        var third = await Assert.ThrowsAsync<ServerStartException>(() =>
            _deployment.StartServerAsync(data, s => s with { LicensePath = Path.Combine(data, "none.elmlic") })
        );

        Assert.Equal(line, second.Message);
        Assert.Equal(line, third.Message);
        Assert.True(Path.Exists(first.AdminSocketPath));
        (int code, string output, string error) = Run(["server", "status", "--data", data]);
        Assert.True(code == 0, error);
        Assert.Contains("license: lic-srv", output, StringComparison.Ordinal);
        Assert.Equal(logs, Logs(data));
        Assert.Equal(token, File.ReadAllBytes(Path.Combine(data, AdminToken.FileName)));
    }

    /// <summary>
    /// While something holds server.lock, the start stops with the lock's line and does not go on to the socket file; once
    /// the lock is free, the same folder starts.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a server license; server.lock opened by the test, shared with nobody; a listener of the test on
    ///   admin.sock that counts its connections.
    ///
    /// Steps:
    /// - Start the server. Close the listener and the lock. Start the server again.
    ///
    /// Expected:
    /// - ServerStartException "Another elm-server is running on the data folder '{folder}': it holds the lock file '{path}'.";
    ///   the listener got no connection, so the start did not probe the socket file. Then a running server.
    /// </remarks>
    [Fact]
    public async Task Start_LockFileHeld_StopsBeforeTheSocketProbe()
    {
        string data = _deployment.NewFolder();
        _deployment.WriteServerLicense(data);
        string lockFile = Path.Combine(data, "server.lock");
        using (var held = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        using (var listener = new FakeAdminSocket(data))
        {
            var refused = await Assert.ThrowsAsync<ServerStartException>(() => _deployment.StartServerAsync(data));

            Assert.Equal($"Another elm-server is running on the data folder '{data}': it holds the lock file '{lockFile}'.", refused.Message);
            Assert.Equal(0, await listener.ConnectionsBeforeNowAsync());
        }

        ElmServer server = await _deployment.StartServerAsync(data);
        Assert.True(Path.Exists(server.AdminSocketPath));
    }

    /// <summary>
    /// With the lock free, a socket file that accepts a connection stops the start: another server runs where the file lock
    /// does not hold. The probe sends nothing, and the start changes nothing.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder in which a server ran and stopped; nothing holds server.lock; a listener of the test on admin.sock that
    ///   accepts connections and counts the bytes it receives. The names of the usage log and borrow record files, and the
    ///   bytes of admin.token.
    ///
    /// Steps:
    /// - Start the server; ask the listener for its connections once it has read them to their end.
    ///
    /// Expected:
    /// - ServerStartException "Another elm-server is running on the data folder '{folder}': its administration socket '{path}'
    ///   accepts connections.".
    /// - The listener got one connection and no byte; its socket file is still there; the folder holds the same log files,
    ///   and admin.token has the same bytes.
    /// </remarks>
    [Fact]
    public async Task Start_SocketFileAcceptsAConnection_StopsWithoutSendingAByte()
    {
        string data = await StoppedServerFolderAsync();
        string[] logs = Logs(data);
        byte[] token = File.ReadAllBytes(Path.Combine(data, AdminToken.FileName));
        using var listener = new FakeAdminSocket(data);

        var refused = await Assert.ThrowsAsync<ServerStartException>(() => _deployment.StartServerAsync(data));
        int connections = await listener.ConnectionsBeforeNowAsync();

        Assert.Equal(
            $"Another elm-server is running on the data folder '{data}': its administration socket '{listener.Path}' accepts connections.",
            refused.Message
        );
        Assert.Equal((1, 0), (connections, listener.BytesReceived));
        Assert.True(Path.Exists(listener.Path));
        Assert.Equal(logs, Logs(data));
        Assert.Equal(token, File.ReadAllBytes(Path.Combine(data, AdminToken.FileName)));
    }

    /// <summary>
    /// A socket file that a killed server left, on which nobody listens, is deleted and the server listens in its place.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder in which a server ran and stopped; a socket file at admin.sock whose connections are refused.
    ///
    /// Steps:
    /// - Start the server; get the status over the socket.
    ///
    /// Expected:
    /// - The server starts and answers 200 on admin.sock.
    /// </remarks>
    [Fact]
    public async Task Start_SocketFileRefusesTheConnection_DeletesItAndListens()
    {
        string data = await StoppedServerFolderAsync();
        FakeAdminSocket.LeaveFile(data);

        ElmServer server = await _deployment.StartServerAsync(data);

        using HttpClient admin = Deployment.AdminHttp(server);
        using HttpResponseMessage status = await admin.GetAsync("admin/v1/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
    }

    /// <summary>
    /// An ordinary file at admin.sock: where its connection is refused the file is deleted and the server listens; where the
    /// connection fails otherwise the start stops and the file stays. The test writes which of the two this system gives.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a server license; a file admin.sock that holds one byte.
    ///
    /// Steps:
    /// - Start the server.
    ///
    /// Expected:
    /// - On Windows the connection is refused (measured on Windows 11): the server starts and answers on admin.sock.
    /// - On Linux and macOS either that, or ServerStartException "The administration socket '{path}' cannot be used: …" with
    ///   the file still there and unchanged. The test's output names the result.
    /// </remarks>
    [Fact]
    public async Task Start_OrdinaryFileAtTheSocketPath_IsDeletedWhereItsConnectionIsRefused()
    {
        string data = _deployment.NewFolder();
        _deployment.WriteServerLicense(data);
        string socket = Path.Combine(data, "admin.sock");
        File.WriteAllText(socket, "x");

        Exception? stopped = await Record.ExceptionAsync(() => _deployment.StartServerAsync(data));

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"An ordinary file at admin.sock on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}: "
                + (stopped is null ? "the connection was refused; the file was deleted." : stopped.Message)
        );
        if (stopped is null)
        {
            using HttpClient admin = Deployment.SocketHttp(socket);
            using HttpResponseMessage answer = await admin.GetAsync("admin/v1/status", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
            return;
        }

        Assert.False(OperatingSystem.IsWindows(), "On Windows the connection to an ordinary file is refused: " + stopped.Message);
        Assert.StartsWith(
            $"The administration socket '{socket}' cannot be used: ",
            Assert.IsType<ServerStartException>(stopped).Message,
            StringComparison.Ordinal
        );
        Assert.Equal("x", File.ReadAllText(socket));
    }

    /// <summary>
    /// A symbolic link at admin.sock: where the connection through it is refused, only the link is deleted, and the file it
    /// names stays; elsewhere the start stops and both stay.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a server license; a file outside it; admin.sock as a symbolic link to that file. A user who may
    ///   not make symbolic links (Windows without the privilege) skips the test and says so.
    ///
    /// Steps:
    /// - Start the server.
    ///
    /// Expected:
    /// - The file the link named still holds its text.
    /// - On Windows the server starts, and admin.sock is no link any more. On Linux and macOS either that, or
    ///   ServerStartException "The administration socket '{path}' cannot be used: …" with the link still there.
    /// </remarks>
    [Fact]
    public async Task Start_SymbolicLinkAtTheSocketPath_DeletesOnlyTheLink()
    {
        string data = _deployment.NewFolder();
        _deployment.WriteServerLicense(data);
        string socket = Path.Combine(data, "admin.sock");
        string target = _deployment.FilePath("target.txt");
        File.WriteAllText(target, "kept");
        try
        {
            File.CreateSymbolicLink(socket, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This user cannot make symbolic links here: " + e.Message);
        }

        Exception? stopped = await Record.ExceptionAsync(() => _deployment.StartServerAsync(data));

        Assert.Equal("kept", File.ReadAllText(target));
        if (stopped is null)
        {
            Assert.Null(new FileInfo(socket).LinkTarget);
            return;
        }

        Assert.False(OperatingSystem.IsWindows(), "On Windows the connection through a link to an ordinary file is refused: " + stopped.Message);
        Assert.StartsWith(
            $"The administration socket '{socket}' cannot be used: ",
            Assert.IsType<ServerStartException>(stopped).Message,
            StringComparison.Ordinal
        );
        Assert.Equal(target, new FileInfo(socket).LinkTarget);
    }

    /// <summary>
    /// A symbolic link at admin.sock that names nothing: on Windows its connection is refused, the link is deleted, and the
    /// server listens; on Linux and macOS the connection fails otherwise, and the start stops and leaves the link.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a server license; admin.sock as a symbolic link to a file that does not exist. A user who may
    ///   not make symbolic links skips the test and says so.
    /// - On Linux and macOS the link is seen (it is there by lstat), and a connection through it fails with ENOENT, which
    ///   .NET gives as SocketError.AddressNotAvailable, not as a refusal (read in dotnet/runtime, not run before CI).
    ///
    /// Steps:
    /// - Start the server.
    ///
    /// Expected:
    /// - Windows: the server starts, and admin.sock is no link any more.
    /// - Linux and macOS: ServerStartException whose message begins "The administration socket '{path}' cannot be used: ",
    ///   and admin.sock is still the link.
    /// - Nothing was made at the path the link names.
    /// </remarks>
    [Fact]
    public async Task Start_DanglingSymbolicLinkAtTheSocketPath_FollowsTheSystemsAnswer()
    {
        string data = _deployment.NewFolder();
        _deployment.WriteServerLicense(data);
        string socket = Path.Combine(data, "admin.sock");
        string target = _deployment.FilePath("nothing.txt");
        try
        {
            File.CreateSymbolicLink(socket, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This user cannot make symbolic links here: " + e.Message);
        }

        Exception? stopped = await Record.ExceptionAsync(() => _deployment.StartServerAsync(data));

        Assert.False(Path.Exists(target));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(stopped is null, "On Windows the start stopped: " + stopped?.Message);
            Assert.Null(new FileInfo(socket).LinkTarget);
            return;
        }

        Assert.True(stopped is not null, "On this system the start took the folder with a dangling link at admin.sock.");
        Assert.StartsWith(
            $"The administration socket '{socket}' cannot be used: ",
            Assert.IsType<ServerStartException>(stopped).Message,
            StringComparison.Ordinal
        );
        Assert.Equal(target, new FileInfo(socket).LinkTarget);
    }

    /// <summary>
    /// What cannot be deleted at admin.sock, a folder of that name, stops the start, and the folder stays.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder in which a server ran and stopped, and a folder named admin.sock in it; the names of its usage log
    ///   and borrow record files, and the bytes of admin.token.
    ///
    /// Steps:
    /// - Start the server.
    ///
    /// Expected:
    /// - ServerStartException whose message begins "The administration socket '{path}' cannot be used: "; the folder is
    ///   still there, the data folder holds the same log files, and admin.token has the same bytes.
    /// </remarks>
    [Fact]
    public async Task Start_FolderAtTheSocketPath_StopsAndLeavesIt()
    {
        string data = await StoppedServerFolderAsync();
        string socket = Path.Combine(data, "admin.sock");
        Directory.CreateDirectory(socket);
        string[] logs = Logs(data);
        byte[] token = File.ReadAllBytes(Path.Combine(data, AdminToken.FileName));

        var refused = await Assert.ThrowsAsync<ServerStartException>(() => _deployment.StartServerAsync(data));

        Assert.StartsWith($"The administration socket '{socket}' cannot be used: ", refused.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(socket));
        Assert.Equal(logs, Logs(data));
        Assert.Equal(token, File.ReadAllBytes(Path.Combine(data, AdminToken.FileName)));
    }

    /// <summary>
    /// A lock file that cannot be opened, a folder of that name, stops the start with its own line.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder in which a server ran and stopped, with a folder named server.lock in place of the lock file; the
    ///   names of its usage log and borrow record files, and the bytes of admin.token.
    /// - Opening a folder is UnauthorizedAccessException on Windows; on Linux and macOS it is the error of the open itself,
    ///   which is not the lock's EWOULDBLOCK, so it must not read as "Another elm-server is running".
    ///
    /// Steps:
    /// - Start the server.
    ///
    /// Expected:
    /// - ServerStartException whose message begins "The lock file '{path}' cannot be opened: "; the data folder holds the
    ///   same log files, and admin.token has the same bytes.
    /// </remarks>
    [Fact]
    public async Task Start_FolderAtTheLockPath_StopsWithTheLockFileLine()
    {
        string data = await StoppedServerFolderAsync();
        string lockFile = Path.Combine(data, "server.lock");
        File.Delete(lockFile);
        Directory.CreateDirectory(lockFile);
        string[] logs = Logs(data);
        byte[] token = File.ReadAllBytes(Path.Combine(data, AdminToken.FileName));

        var refused = await Assert.ThrowsAsync<ServerStartException>(() => _deployment.StartServerAsync(data));

        Assert.StartsWith($"The lock file '{lockFile}' cannot be opened: ", refused.Message, StringComparison.Ordinal);
        Assert.Equal(logs, Logs(data));
        Assert.Equal(token, File.ReadAllBytes(Path.Combine(data, AdminToken.FileName)));
    }

    /// <summary>
    /// A start that stops after it took the lock, here for want of a license, releases the lock.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with the server's key and no license file.
    ///
    /// Steps:
    /// - Start the server; open server.lock, shared with nobody.
    ///
    /// Expected:
    /// - ServerStartException that names the missing license; the test opens the lock file.
    /// </remarks>
    [Fact]
    public async Task Start_StopsAtTheLicense_ReleasesTheLock()
    {
        string data = _deployment.NewFolder();
        Deployment.ServerKeyId(data);

        var refused = await Assert.ThrowsAsync<ServerStartException>(() =>
            ElmServer.StartAsync(_deployment.Settings(data), _deployment.HostOptions(), TestContext.Current.CancellationToken)
        );

        Assert.Contains("does not exist", refused.Message, StringComparison.Ordinal);
        using var free = new FileStream(Path.Combine(data, "server.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>
    /// A server that stops normally leaves no socket file and no lock, and keeps its token file.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A running server.
    ///
    /// Steps:
    /// - Stop it; look at its data folder.
    ///
    /// Expected:
    /// - admin.sock is gone; the test opens server.lock, shared with nobody; admin.token still reads as a token.
    /// </remarks>
    [Fact]
    public async Task Stop_RemovesTheSocketFile_ReleasesTheLock_KeepsTheToken()
    {
        ElmServer server = await _deployment.StartServerAsync();
        string data = server.DataDirectory;
        Assert.True(Path.Exists(server.AdminSocketPath));

        await server.DisposeAsync();

        Assert.False(Path.Exists(server.AdminSocketPath));
        using var free = new FileStream(Path.Combine(data, "server.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.NotNull(AdminToken.Read(data));
    }

    /// <summary>
    /// On Windows the lock file of a running server can be neither opened, nor deleted, nor copied.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows; a running server.
    ///
    /// Steps:
    /// - Open server.lock for reading with every sharing allowed; delete it; copy it.
    ///
    /// Expected:
    /// - IOException each time, and the file is still there.
    /// </remarks>
    [Fact]
    public async Task LockFile_WhileTheServerRuns_CannotBeOpenedDeletedOrCopied()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Linux and macOS lock with flock, which is advisory: the file can be deleted there.");
        ElmServer server = await _deployment.StartServerAsync();
        string lockFile = Path.Combine(server.DataDirectory, "server.lock");

        Assert.Throws<IOException>(() => new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        Assert.Throws<IOException>(() => File.Delete(lockFile));
        Assert.Throws<IOException>(() => File.Copy(lockFile, lockFile + ".copy"));
        Assert.True(File.Exists(lockFile));
    }

    /// <summary>
    /// Every start makes a new administrator token: the token of the start before is refused, and elm server, which reads
    /// the file at each call, reaches the new server without any other option.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder in which a server ran and stopped; the token it wrote.
    ///
    /// Steps:
    /// - Start a server in the same folder; read admin.token; get the status with the earlier token and with the new one;
    ///   run elm server status with --data.
    ///
    /// Expected:
    /// - The two tokens differ. The earlier one gets 401 with the code ELM_UNAUTHORIZED, the new one 200; status exits 0.
    /// </remarks>
    [Fact]
    public async Task Start_Again_MakesANewTokenAndRefusesTheEarlierOne()
    {
        string data = await StoppedServerFolderAsync();
        string earlier = AdminToken.Read(data)!;

        ElmServer server = await _deployment.StartServerAsync(data);

        string now = AdminToken.Read(data)!;
        Assert.NotEqual(earlier, now);
        using (HttpClient old = Deployment.AdminHttp(server, token: earlier))
        using (HttpResponseMessage refused = await old.GetAsync("admin/v1/status", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            using JsonDocument problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("ELM_UNAUTHORIZED", problem.RootElement.GetProperty("code").GetString());
        }

        using (HttpClient current = Deployment.AdminHttp(server, token: now))
        using (HttpResponseMessage answered = await current.GetAsync("admin/v1/status", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        }

        (int code, _, string error) = Run(["server", "status", "--data", data]);
        Assert.True(code == 0, error);
    }

    /// <summary>
    /// The token is replaced through a new file: a new file that an earlier start left does not stop the start, none is left
    /// after it, and the token file holds a whole token that only its owner can read.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a server license, and a file admin.token.new of three bytes, as a start that ended between
    ///   writing and replacing leaves one.
    ///
    /// Steps:
    /// - Start the server; look at the folder.
    ///
    /// Expected:
    /// - The server starts; admin.token.new is gone; admin.token reads as a token of 32 bytes; on Linux and macOS its mode is
    ///   0600.
    /// </remarks>
    [Fact]
    public async Task Start_NewTokenFileLeft_ReplacesItAndLeavesNone()
    {
        string data = _deployment.NewFolder();
        _deployment.WriteServerLicense(data);
        string fresh = Path.Combine(data, "admin.token.new");
        File.WriteAllBytes(fresh, [1, 2, 3]);

        await _deployment.StartServerAsync(data);

        Assert.False(Path.Exists(fresh));
        Assert.Equal(32, System.Buffers.Text.Base64Url.DecodeFromChars(AdminToken.Read(data)).Length);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(data, AdminToken.FileName)));
        }
    }

    /// <summary>
    /// A token that cannot be made stops the start: with a folder where the new token file goes, the start says so.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder with a server license, and a folder named admin.token.new in it.
    ///
    /// Steps:
    /// - Start the server.
    ///
    /// Expected:
    /// - ServerStartException "The administrator token in '{folder}' cannot be created or read.".
    /// </remarks>
    [Fact]
    public async Task Start_FolderAtTheNewTokenPath_StopsWithTheTokenLine()
    {
        string data = _deployment.NewFolder();
        _deployment.WriteServerLicense(data);
        Directory.CreateDirectory(Path.Combine(data, "admin.token.new"));

        var refused = await Assert.ThrowsAsync<ServerStartException>(() => _deployment.StartServerAsync(data));

        Assert.Equal($"The administrator token in '{data}' cannot be created or read.", refused.Message);
    }

    /// <summary>
    /// On Windows, while a reader holds the old token file open, the start tries the replacement again every 100
    /// milliseconds of its clock and succeeds once the reader has closed, up to the try at 2 seconds.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows; a data folder in which a server ran and stopped; admin.token opened by the test for reading, shared for
    ///   reading, as elm server reads it; a fake clock that counts the timers of 100 milliseconds made on it.
    /// - t0 is the first try of the replacement. Each failed try makes one timer due 100 milliseconds later, and nothing
    ///   else makes such a timer during the start.
    ///
    /// Steps:
    /// - Start the server. 19 times: wait for the next timer, then move the clock 100 milliseconds (the tries at t0 + 0.1 s to
    ///   t0 + 1.9 s fail). Wait for the 20th timer; close the reader; move the clock 100 milliseconds (the try at t0 + 2 s).
    ///
    /// Expected:
    /// - The start has not ended while the reader holds the file; after the last move it returns a running server whose
    ///   token differs from the one before, and exactly 20 timers were made.
    /// </remarks>
    [Fact]
    public async Task Start_ReaderHoldsTheTokenFileUntilTwoSeconds_Succeeds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file that a reader holds open.");
        string data = await StoppedServerFolderAsync();
        string earlier = AdminToken.Read(data)!;
        var clock = new CountingTime(Deployment.Start);
        var reader = new FileStream(Path.Combine(data, AdminToken.FileName), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<ElmServer> start = StartOnAsync(data, clock, cancel.Token);
        try
        {
            for (int timer = 1; timer <= 19; timer++)
            {
                await clock.WaitForTimersAsync(timer);
                Assert.False(start.IsCompleted);
                clock.Advance(TimeSpan.FromMilliseconds(100));
            }

            await clock.WaitForTimersAsync(20);
            Assert.False(start.IsCompleted);
            reader.Dispose();
            clock.Advance(TimeSpan.FromMilliseconds(100));

            ElmServer server = await start.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            _owned.Add(server);
            Assert.NotEqual(earlier, AdminToken.Read(data));
            Assert.Equal(20, clock.Timers);
        }
        finally
        {
            reader.Dispose();
            await EndAsync(start, cancel);
        }
    }

    /// <summary>
    /// On Windows, a reader that still holds the old token file at the try 2 seconds after the first stops the start.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - As the test before: admin.token held open for reading, and the counting fake clock. t0 is the first try.
    ///
    /// Steps:
    /// - Start the server. 20 times: wait for the next timer, then move the clock 100 milliseconds. The last move brings the
    ///   try at t0 + 2 s, which is the last one.
    ///
    /// Expected:
    /// - The start has not ended before the 20th move; after it, ServerStartException "The administrator token in '{folder}'
    ///   cannot be created or read.", with exactly 20 timers made; admin.token is still the earlier one, and admin.token.new
    ///   is gone.
    /// </remarks>
    [Fact]
    public async Task Start_ReaderHoldsTheTokenFilePastTwoSeconds_StopsWithTheTokenLine()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file that a reader holds open.");
        string data = await StoppedServerFolderAsync();
        string earlier = AdminToken.Read(data)!;
        var clock = new CountingTime(Deployment.Start);
        using var reader = new FileStream(Path.Combine(data, AdminToken.FileName), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<ElmServer> start = StartOnAsync(data, clock, cancel.Token);
        try
        {
            for (int timer = 1; timer <= 20; timer++)
            {
                await clock.WaitForTimersAsync(timer);
                Assert.False(start.IsCompleted);
                clock.Advance(TimeSpan.FromMilliseconds(100));
            }

            var refused = await Assert.ThrowsAsync<ServerStartException>(() =>
                start.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
            );

            Assert.Equal($"The administrator token in '{data}' cannot be created or read.", refused.Message);
            Assert.Equal(20, clock.Timers);
            Assert.Equal(earlier, AdminToken.Read(data));
            Assert.False(Path.Exists(Path.Combine(data, "admin.token.new")));
        }
        finally
        {
            await EndAsync(start, cancel);
        }
    }

    /// <summary>
    /// A start that is cancelled while it waits for a reader of the old token file deletes the new token file and releases
    /// the lock.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows; admin.token held open for reading, and the counting fake clock, as in the two tests before.
    ///
    /// Steps:
    /// - Start the server with a cancellation token; wait for the first timer, so the first try has failed and
    ///   admin.token.new is written; cancel.
    ///
    /// Expected:
    /// - The start throws OperationCanceledException; admin.token.new is gone; the test opens server.lock, shared with nobody;
    ///   admin.token is still the earlier one.
    /// </remarks>
    [Fact]
    public async Task Start_CancelledWhileAReaderHoldsTheTokenFile_DeletesTheNewTokenFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file that a reader holds open.");
        string data = await StoppedServerFolderAsync();
        string earlier = AdminToken.Read(data)!;
        var clock = new CountingTime(Deployment.Start);
        using var reader = new FileStream(Path.Combine(data, AdminToken.FileName), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<ElmServer> start = StartOnAsync(data, clock, cancel.Token);
        try
        {
            await clock.WaitForTimersAsync(1);
            Assert.True(File.Exists(Path.Combine(data, "admin.token.new")));
            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                start.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
            );

            Assert.False(Path.Exists(Path.Combine(data, "admin.token.new")));
            using var free = new FileStream(Path.Combine(data, "server.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(earlier, AdminToken.Read(data));
        }
        finally
        {
            await EndAsync(start, cancel);
        }
    }

    // The sentence of a socket path one byte over the limit, for the data folder that PaddedFolder(limit + 1) returns.
    private static string TooLong(string folder, int limit) =>
        $"The path of the administration socket '{Path.Combine(folder, "admin.sock")}' is {limit + 1} bytes in UTF-8, and this system allows "
        + $"{limit}. Use a data folder with a shorter path.";

    private static (int Code, string Error) Trimmed((int Code, string Out, string Error) result) => (result.Code, result.Error.Trim());

    // The usage log and borrow record files of a data folder, by name.
    private static string[] Logs(string data) => [.. Directory.GetFiles(data, "*.jsonl").Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    // The path of a data folder, not made yet, in which admin.sock has an absolute path of exactly the given UTF-8 bytes.
    private string PaddedFolder(int socketPathBytes)
    {
        int name = socketPathBytes - Encoding.UTF8.GetByteCount(_deployment.Directory) - 1 - "/admin.sock".Length;
        Assert.True(name >= 3, "The temporary folder is too long to pad a data folder's name to the socket path limit.");
        string folder = Path.Combine(_deployment.Directory, "é" + new string('a', name - 2));
        Assert.Equal(socketPathBytes, Encoding.UTF8.GetByteCount(Path.Combine(folder, "admin.sock")));
        return folder;
    }

    // A data folder in which a server ran with its license and stopped: it holds a key, a license, a token, and the logs.
    private async Task<string> StoppedServerFolderAsync()
    {
        ElmServer server = await _deployment.StartServerAsync();
        await server.DisposeAsync();
        return server.DataDirectory;
    }

    // Starts a server on a clock of the test's own.
    private Task<ElmServer> StartOnAsync(string data, TimeProvider clock, CancellationToken cancellationToken)
    {
        var host = new ServerHostOptions
        {
            Time = clock,
            Environment = _ => null,
            MinimumLogLevel = LogLevel.Warning,
        };
        return ElmServer.StartAsync(_deployment.Settings(data), host, cancellationToken);
    }

    // Ends a start that a test left waiting on its fake clock, so that it does not keep server.lock: the start is
    // cancelled, and a server that did start is stopped with the test's other servers.
    private async Task EndAsync(Task<ElmServer> start, CancellationTokenSource cancel)
    {
        await cancel.CancelAsync();
        try
        {
            ElmServer server = await start.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            if (!_owned.Contains(server))
            {
                _owned.Add(server);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ServerStartException)
        {
            // The start ended without a server.
        }
    }

    private (int Code, string Out, string Error) Run(string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var host = new CliHost
        {
            Out = output,
            Error = error,
            Time = _deployment.Time,
            GetEnvironmentVariable = _ => null,
        };
        int code = ElmApp.Run(args, host);
        return (code, output.ToString(), error.ToString());
    }

    // A fake clock that counts the timers due in 100 milliseconds, which is how the start waits between two tries to
    // replace the token file.
    private sealed class CountingTime(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private int _timers;

        public int Timers => Volatile.Read(ref _timers);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromMilliseconds(100))
            {
                Interlocked.Increment(ref _timers);
            }

            return timer;
        }

        // Waits, in real time and with a bound, until the start has made its next timer: only then does a move of the clock
        // reach it.
        public async Task WaitForTimersAsync(int count)
        {
            for (int i = 0; i < 3_000 && Timers < count; i++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.Equal(count, Timers);
        }
    }
}
