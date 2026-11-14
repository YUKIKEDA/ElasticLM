using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ElasticLM.Client;
using ElasticLM.Issuing;
using ElasticLM.Issuing.Tests;
using ElasticLM.Licensing;
using Microsoft.Extensions.Logging;

namespace ElasticLM.Server.Tests;

/// <summary>
/// A start that cannot listen, or whose TLS certificate a server may not use, stops with a <see cref="ServerStartException"/>
/// (exit code 1), closes what it opened, and leaves the data folder ready for the next start (design §13.1, §13.5, V1c9).
/// </summary>
public sealed class ListenTests : IAsyncDisposable
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly IPAddress NotOnThisMachine = IPAddress.Parse("192.0.2.1");

    private readonly Deployment _deployment = new();

    public ValueTask DisposeAsync() => _deployment.DisposeAsync();

    /// <summary>
    /// A listen port that another socket holds stops the start with one message; the failed start signs and closes its
    /// files, so a start after the port is free continues both chains and keeps the borrows.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server that recorded a borrow of pro for a day and stopped; a TcpListener started on 127.0.0.1 at a port the OS
    ///   picked, the same IP and port the server is given as listen.
    ///
    /// Steps:
    /// - Start the server on that port; read the newest usage log and borrow record files; stop the listener; start the server
    ///   again on the same port; stop it; read and verify both logs.
    ///
    /// Expected:
    /// - ServerStartException whose message begins "elm-server cannot listen on https://127.0.0.1:{port}/ and " followed by
    ///   the absolute path of admin.sock in the data folder and ": "; the failed start's usage log file and borrow record file
    ///   both end with an auth line.
    /// - The second start runs with the borrow restored; its usage log file begins with chain.start whose prev.file names the
    ///   failed start's file; both logs verify as intact.
    /// </remarks>
    [Fact]
    public async Task Start_ListenPortHeld_ThrowsAndTheNextStartContinues()
    {
        string data = _deployment.NewFolder();
        await BorrowAndStopAsync(data);
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        int port = ((IPEndPoint)holder.LocalEndpoint).Port;

        var refused = await Assert.ThrowsAsync<ServerStartException>(() => _deployment.StartServerAsync(data, s => s with { Listen = Https(port) }));
        Assert.StartsWith(
            $"elm-server cannot listen on https://127.0.0.1:{port}/ and {Path.Combine(data, "admin.sock")}: ",
            refused.Message,
            StringComparison.Ordinal
        );
        string failedUsage = SignedLog.FindFiles(data, "usage")[^1];
        Assert.Equal("auth", LastType(failedUsage));
        Assert.Equal("auth", LastType(SignedLog.FindFiles(data, "borrows")[^1]));

        holder.Stop();
        ElmServer after = await _deployment.StartServerAsync(data, s => s with { Listen = Https(port) });
        Assert.Equal(1, Assert.Single(after.Engine.Usage()).Borrowed);
        await after.DisposeAsync();

        IReadOnlyList<string> usage = SignedLog.FindFiles(data, "usage");
        Assert.Equal(failedUsage, usage[^2]);
        using (JsonDocument first = JsonDocument.Parse(File.ReadLines(usage[^1]).First()))
        {
            Assert.Equal("chain.start", first.RootElement.GetProperty("type").GetString());
            Assert.Equal(Path.GetFileName(failedUsage), first.RootElement.GetProperty("prev").GetProperty("file").GetString());
        }

        Assert.True(VerifiedLog.Of(usage, _deployment.Trust, "acme-cad", "usage").Intact);
        Assert.True(VerifiedLog.Of(SignedLog.FindFiles(data, "borrows"), _deployment.Trust, "acme-cad", "borrows").Intact);
    }

    /// <summary>
    /// A start that cannot listen disposes the host before the exception reaches the caller, so the host's log, the
    /// console's included, is written out before elm-server prints its last line (design §13.1, V1c9 R2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A logger provider of the test that the host disposes with its services; a TcpListener on 127.0.0.1 holding the port
    ///   given as listen.
    ///
    /// Steps:
    /// - Start the server on the held port.
    ///
    /// Expected:
    /// - ServerStartException; by then the provider holds the host's error "Hosting failed to start" and is disposed.
    /// </remarks>
    [Fact]
    public async Task Start_ListenPortHeld_DisposesTheHost()
    {
        string data = _deployment.NewFolder();
        _deployment.WriteServerLicense(data);
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        int port = ((IPEndPoint)holder.LocalEndpoint).Port;
        using var log = new RecordingLoggerProvider();
        var host = new ServerHostOptions
        {
            Time = _deployment.Time,
            Environment = _ => null,
            MinimumLogLevel = LogLevel.Warning,
            LoggerProvider = log,
        };

        await Assert.ThrowsAsync<ServerStartException>(() =>
            ElmServer.StartAsync(_deployment.Settings(data, port), host, TestContext.Current.CancellationToken)
        );
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message == "Hosting failed to start");
        Assert.True(log.Disposed);
    }

    /// <summary>
    /// A start that cannot listen on its listen port fails after Kestrel has bound the administration socket, which is added
    /// first: Kestrel removes the socket file, the start releases the lock, and the administrator token is already the new
    /// one; the next start takes the folder again (design §13.1, §13.7, V1d2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A data folder in which a server ran and stopped, so it holds an administrator token; a TcpListener on 127.0.0.1
    ///   holding the port given as listen.
    /// - Between the listener's stop and the server's next start another process could take the port; the window is a few
    ///   milliseconds, and the test would then fail with that port in its message.
    ///
    /// Steps:
    /// - Read admin.token; start the server on the held port; look at the folder; stop the listener; start the server again
    ///   on the same port.
    ///
    /// Expected:
    /// - ServerStartException whose message begins "elm-server cannot listen on https://127.0.0.1:{port}/ and " followed by
    ///   the absolute path of admin.sock and ": ".
    /// - After it: no admin.sock in the folder; the test can open server.lock sharing it with nobody; admin.token differs from
    ///   the one read before, and no admin.token.new is left.
    /// - Then a running server whose public URL has the port.
    /// </remarks>
    [Fact]
    public async Task Start_ListenPortHeld_RemovesTheSocketFileAndReleasesTheLock()
    {
        string data = _deployment.NewFolder();
        await (await _deployment.StartServerAsync(data)).DisposeAsync();
        string socket = Path.Combine(data, "admin.sock");
        string? before = AdminToken.Read(data);
        Assert.NotNull(before);
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        int port = ((IPEndPoint)holder.LocalEndpoint).Port;

        var refused = await Assert.ThrowsAsync<ServerStartException>(() => _deployment.StartServerAsync(data, s => s with { Listen = Https(port) }));

        Assert.StartsWith($"elm-server cannot listen on https://127.0.0.1:{port}/ and {socket}: ", refused.Message, StringComparison.Ordinal);
        Assert.False(Path.Exists(socket));
        using (new FileStream(Path.Combine(data, "server.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        string? after = AdminToken.Read(data);
        Assert.NotNull(after);
        Assert.NotEqual(before, after);
        Assert.False(Path.Exists(Path.Combine(data, "admin.token.new")));

        holder.Stop();
        ElmServer server = await _deployment.StartServerAsync(data, s => s with { Listen = Https(port) });
        Assert.Equal(port, server.PublicUrls[0].Port);
    }

    /// <summary>
    /// A start with --set-aside-broken-records that sets a broken record aside and then cannot listen still writes
    /// records.set_aside to its usage log: the record is already renamed, and the next start does not set it aside again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server that recorded a borrow and stopped; a character of the borrow line changed (seats 2 to 9); a TcpListener on
    ///   127.0.0.1 holding the port given as listen.
    ///
    /// Steps:
    /// - Start with the set-aside option on the held port; stop the listener; start without the option on the same port; stop
    ///   it; verify the usage log.
    ///
    /// Expected:
    /// - ServerStartException "elm-server cannot listen on …", and a borrows-broken file; the newest usage log file of the
    ///   failed start holds records.set_aside naming that file; the second start runs with no borrowed seat; the usage log is
    ///   intact and holds one records.set_aside line.
    /// </remarks>
    [Fact]
    public async Task Start_SetAsideAndListenPortHeld_RecordsTheSetAside()
    {
        string data = _deployment.NewFolder();
        await BorrowAndStopAsync(data);
        string record = SignedLog.FindFiles(data, "borrows")[^1];
        string[] lines = File.ReadAllLines(record);
        int borrowLine = Array.FindIndex(lines, l => l.Contains("borrow.started", StringComparison.Ordinal));
        lines[borrowLine] = lines[borrowLine].Replace("\"seats\":2", "\"seats\":9", StringComparison.Ordinal);
        File.WriteAllText(record, string.Join('\n', lines) + "\n");
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        int port = ((IPEndPoint)holder.LocalEndpoint).Port;

        var refused = await Assert.ThrowsAsync<ServerStartException>(() =>
            _deployment.StartServerAsync(data, s => s with { Listen = Https(port) }, setAside: true)
        );
        Assert.StartsWith("elm-server cannot listen on ", refused.Message, StringComparison.Ordinal);
        string aside = Path.GetFileName(Assert.Single(Directory.GetFiles(data, "borrows-broken-*.jsonl")));
        string failedUsage = SignedLog.FindFiles(data, "usage")[^1];
        Assert.Contains(
            File.ReadLines(failedUsage),
            l => l.Contains("\"records.set_aside\"", StringComparison.Ordinal) && l.Contains(aside, StringComparison.Ordinal)
        );

        holder.Stop();
        ElmServer after = await _deployment.StartServerAsync(data, s => s with { Listen = Https(port) });
        Assert.Equal(0, Assert.Single(after.Engine.Usage()).Borrowed);
        await after.DisposeAsync();
        VerifiedLog usage = VerifiedLog.Of(SignedLog.FindFiles(data, "usage"), _deployment.Trust, "acme-cad", "usage");
        Assert.True(usage.Intact);
        Assert.Single(usage.Entries, e => e.Type == "records.set_aside");
    }

    /// <summary>
    /// A listen address this machine does not have is a SocketException in Kestrel, not an IOException, and stops the start
    /// with the same message (design §13.1, V1c9 U1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 192.0.2.1 (TEST-NET-1, RFC 5737), which no interface of the test machine has: a plain Socket.Bind to 192.0.2.1:0
    ///   throws SocketException first, or the test fails saying the machine allows that bind.
    ///
    /// Steps:
    /// - Start the server with listen https://192.0.2.1:0.
    ///
    /// Expected:
    /// - ServerStartException whose message begins "elm-server cannot listen on https://192.0.2.1:0/ and " followed by the
    ///   absolute path of admin.sock in the data folder and ": ".
    /// </remarks>
    [Fact]
    public async Task Start_AddressNotOnThisMachine_Throws()
    {
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            Exception? bind = Record.Exception(() => probe.Bind(new IPEndPoint(NotOnThisMachine, 0)));
            Assert.True(
                bind is SocketException,
                "This machine lets a socket bind 192.0.2.1, so the test cannot make the server fail to listen on an address it does not have."
            );
        }

        string data = _deployment.NewFolder();
        var refused = await Assert.ThrowsAsync<ServerStartException>(() =>
            _deployment.StartServerAsync(data, s => s with { Listen = new Uri("https://192.0.2.1:0") })
        );
        Assert.StartsWith(
            $"elm-server cannot listen on https://192.0.2.1:0/ and {Path.Combine(data, "admin.sock")}: ",
            refused.Message,
            StringComparison.Ordinal
        );
    }

    /// <summary>
    /// A TLS certificate whose Extended Key Usage leaves out server authentication stops the start with a reason before the
    /// server listens, for PEM and PKCS#12 files alike; one that includes it among others starts (design §13.5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A self-signed certificate for 127.0.0.1 whose Extended Key Usage is client authentication (1.3.6.1.5.5.7.3.2) only,
    ///   written as PEM files and as a PKCS#12 file; another whose Extended Key Usage is client and server authentication,
    ///   as PEM files.
    ///
    /// Steps:
    /// - Start with the PEM files of the first; with its PKCS#12 file; with the PEM files of the second.
    ///
    /// Expected:
    /// - ServerStartException "The TLS certificate '{path}' cannot be used by a server: its Extended Key Usage does not
    ///   include server authentication (1.3.6.1.5.5.7.3.1)." twice, naming each file; then a running server.
    /// </remarks>
    [Fact]
    public async Task Start_TlsCertificateWithoutServerAuthentication_Throws()
    {
        (string clientPem, string clientKey, string clientPfx) = WriteCertificate("client", "1.3.6.1.5.5.7.3.2");
        (string bothPem, string bothKey, _) = WriteCertificate("both", "1.3.6.1.5.5.7.3.2", "1.3.6.1.5.5.7.3.1");

        foreach ((string certificate, string? key) in new[] { (clientPem, clientKey), (clientPfx, (string?)null) })
        {
            var refused = await Assert.ThrowsAsync<ServerStartException>(() =>
                _deployment.StartServerAsync(change: s => s with { TlsCertificate = certificate, TlsKey = key })
            );
            Assert.Equal(
                $"The TLS certificate '{certificate}' cannot be used by a server: its Extended Key Usage does not include server "
                    + "authentication (1.3.6.1.5.5.7.3.1).",
                refused.Message
            );
        }

        ElmServer server = await _deployment.StartServerAsync(change: s => s with { TlsCertificate = bothPem, TlsKey = bothKey });
        Assert.Equal(Uri.UriSchemeHttps, server.PublicUrls[0].Scheme);
    }

    private static Uri Https(int port) => new($"https://127.0.0.1:{port}");

    private static string? LastType(string file)
    {
        using JsonDocument line = JsonDocument.Parse(File.ReadLines(file).Last());
        return line.RootElement.GetProperty("type").GetString();
    }

    private async Task BorrowAndStopAsync(string data)
    {
        ElmServer server = await _deployment.StartServerAsync(data);
        _deployment.Time.Advance(Window);
        LeaseResult borrowed = await _deployment
            .Client(server.PublicUrls[0])
            .BorrowLeaseAsync("pro", TimeSpan.FromDays(1), cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<LeaseResult.Acquired>(borrowed);
        await server.DisposeAsync();
    }

    // A self-signed certificate for 127.0.0.1 with the Extended Key Usages given, as a PEM certificate, a PEM key, and a
    // PKCS#12 file without a password.
    private (string Pem, string Key, string Pfx) WriteCertificate(string name, params string[] usages)
    {
        using ECDsa key = KeyFiles.CreateKey();
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        var oids = new OidCollection();
        foreach (string usage in usages)
        {
            oids.Add(new Oid(usage));
        }

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, critical: false));
        using X509Certificate2 certificate = request.CreateSelfSigned(Deployment.Start.AddDays(-1), Deployment.Start.AddDays(30));
        string pem = _deployment.FilePath(name + ".crt");
        string keyFile = _deployment.FilePath(name + ".key");
        string pfx = _deployment.FilePath(name + ".pfx");
        File.WriteAllText(pem, certificate.ExportCertificatePem() + "\n");
        File.WriteAllText(keyFile, key.ExportPkcs8PrivateKeyPem() + "\n");
        File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pkcs12));
        return (pem, keyFile, pfx);
    }
}
