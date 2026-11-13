using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using ElasticLM.Client;

namespace ElasticLM.Server.Tests;

/// <summary>
/// elm-server sends the intermediate CA certificates of its TLS certificate file with the certificate, from a PEM file and from
/// a PKCS#12 file, and refuses to start when one of them is not a CA certificate (design §13.5, V1c12). On Windows it checks
/// at each start that they are sent, and stops when one is not (design §13.5, V1d3a).
/// </summary>
/// <remarks>
/// A test whose server sends an intermediate starts it through <see cref="StartWithChainAsync"/>: the chain holds the mutex of
/// the tests that change the Windows CA stores, and a start that stops at the server's own check is made again with a new
/// chain (<see cref="TestCertificateChain.StartAsync"/>). The servers stop before the chains are disposed.
/// </remarks>
public sealed class TlsChainTests : IAsyncDisposable
{
    private readonly Deployment _deployment = new();
    private readonly List<TestCertificateChain> _chains = [];

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _deployment.DisposeAsync();
        }
        finally
        {
            foreach (TestCertificateChain chain in _chains)
            {
                chain.Dispose();
            }
        }
    }

    /// <summary>
    /// A PEM file with the server's certificate and then the intermediate makes the server send the intermediate.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A root CA, an intermediate CA it signs, and a certificate for 127.0.0.1 the intermediate signs, valid around the real
    ///   time; a PEM file with the certificate and then the intermediate, and a PEM file with the certificate's key.
    ///
    /// Steps:
    /// - Start the server with the two files; connect with SslStream, and in the validation callback read the certificates the
    ///   server sent (the chain's ExtraStore), then build the chain trusting only the root, with downloads off and no
    ///   revocation check.
    ///
    /// Expected:
    /// - The intermediate is among the certificates the server sent, and the chain builds.
    /// </remarks>
    [Fact]
    public async Task Start_PemWithIntermediate_SendsTheIntermediate()
    {
        (TestCertificateChain chain, ElmServer server) = await StartWithChainAsync(c =>
            c.WritePem(_deployment.FilePath("chain.crt"), _deployment.FilePath("chain.key"), c.Intermediate)
        );
        (bool sent, bool builds, _) = await chain.ConnectAsync(server.PublicUrls[0], TestContext.Current.CancellationToken);

        Assert.True(sent);
        Assert.True(builds);
    }

    /// <summary>
    /// A PEM file that also holds the root CA after the intermediate starts, and the server sends the intermediate.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The chain of the PEM test; a PEM file with the certificate, the intermediate, and the root, and the key.
    ///
    /// Steps:
    /// - Start the server with the two files; connect and check as in the PEM test.
    ///
    /// Expected:
    /// - The server starts, the intermediate is among the certificates it sent, and the chain builds.
    /// </remarks>
    [Fact]
    public async Task Start_PemWithRoot_SendsTheIntermediate()
    {
        (TestCertificateChain chain, ElmServer server) = await StartWithChainAsync(c =>
            c.WritePem(_deployment.FilePath("root.crt"), _deployment.FilePath("root.key"), c.Intermediate, c.Root)
        );
        (bool sent, bool builds, _) = await chain.ConnectAsync(server.PublicUrls[0], TestContext.Current.CancellationToken);

        Assert.True(sent);
        Assert.True(builds);
    }

    /// <summary>
    /// A PKCS#12 file with the server's certificate, its key, and the intermediate makes the server send the intermediate.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The chain of the PEM test; a PKCS#12 file without a password that holds the certificate with its key and the
    ///   intermediate without a key.
    ///
    /// Steps:
    /// - Start the server with the PKCS#12 file and no key file; connect and check as in the PEM test.
    ///
    /// Expected:
    /// - The intermediate is among the certificates the server sent, and the chain builds.
    /// </remarks>
    [Fact]
    public async Task Start_Pkcs12WithIntermediate_SendsTheIntermediate()
    {
        (TestCertificateChain chain, ElmServer server) = await StartWithChainAsync(c =>
            (c.WritePkcs12(_deployment.FilePath("chain.pfx"), [c.Intermediate]), null)
        );
        (bool sent, bool builds, _) = await chain.ConnectAsync(server.PublicUrls[0], TestContext.Current.CancellationToken);

        Assert.True(sent);
        Assert.True(builds);
    }

    /// <summary>
    /// In a PKCS#12 file with two certificates that have a private key, the server uses the first one and still sends the
    /// intermediate.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The chain of the PEM test; a PKCS#12 file without a password that holds the certificate with its key, the intermediate
    ///   without a key, and then another server's certificate with its key.
    ///
    /// Steps:
    /// - Start the server with the PKCS#12 file and no key file; connect and read the certificate the server presented and the
    ///   certificates it sent.
    ///
    /// Expected:
    /// - The presented certificate is the server's (the first with a key), and the intermediate is among the certificates sent.
    /// </remarks>
    [Fact]
    public async Task Start_Pkcs12WithTwoKeys_UsesTheFirst()
    {
        (TestCertificateChain chain, ElmServer server) = await StartWithChainAsync(c =>
            (c.WritePkcs12(_deployment.FilePath("keys.pfx"), [c.Intermediate], otherWithKey: true), null)
        );
        (bool sent, _, string? presented) = await chain.ConnectAsync(server.PublicUrls[0], TestContext.Current.CancellationToken);

        Assert.Equal(chain.Server.Thumbprint, presented);
        Assert.True(sent);
    }

    /// <summary>
    /// A PEM file with the server's certificate only starts and serves as before, and sends no intermediate.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The chain of the PEM test; a PEM file with the certificate only, and its key.
    ///
    /// Steps:
    /// - Start the server with the two files; connect and read the certificates the server sent.
    ///
    /// Expected:
    /// - The TLS handshake completes with the server's certificate, the intermediate is not among the certificates the server
    ///   sent, and a client that trusts only the root and downloads nothing cannot build the chain.
    /// </remarks>
    [Fact]
    public async Task Start_PemWithoutIntermediate_ServesWithoutIt()
    {
        TestCertificateChain chain = NewChain();
        (string certificate, string key) = chain.WritePem(_deployment.FilePath("leaf.crt"), _deployment.FilePath("leaf.key"));

        ElmServer server = await _deployment.StartServerAsync(change: s => s with { TlsCertificate = certificate, TlsKey = key });
        (bool sent, bool builds, string? presented) = await chain.ConnectAsync(server.PublicUrls[0], TestContext.Current.CancellationToken);

        Assert.Equal(chain.Server.Thumbprint, presented);
        Assert.False(sent);
        Assert.False(builds);
    }

    /// <summary>
    /// A certificate after the server's certificate that is not a CA certificate stops the start with a reason that names its
    /// place in the file, in a PEM file and in a PKCS#12 file, and so does one without the basic constraints extension.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The chain of the PEM test; another server's certificate (basic constraints without CA), and a certificate without the
    ///   basic constraints extension, both signed by the intermediate.
    /// - A PEM file with the server's certificate and then the other server's; one with the server's and then the one without
    ///   basic constraints; and a PKCS#12 file with the server's certificate and key, the intermediate, and then the other
    ///   server's certificate without a key.
    ///
    /// Steps:
    /// - Start the server with each file.
    ///
    /// Expected:
    /// - ServerStartException "The TLS certificate '{path}' cannot be used by a server: its certificate {n} ({subject}) is not a
    ///   CA certificate.", with 2 and CN=another server, 2 and CN=no basic constraints, and 3 and CN=another server.
    /// </remarks>
    [Fact]
    public async Task Start_NonAuthorityAfterTheCertificate_Throws()
    {
        TestCertificateChain chain = NewChain();
        string key = chain.WritePem(_deployment.FilePath("other.crt"), _deployment.FilePath("other.key"), chain.Other).Key;
        string other = _deployment.FilePath("other.crt");
        string unconstrained = chain
            .WritePem(_deployment.FilePath("unconstrained.crt"), _deployment.FilePath("unconstrained.key"), chain.Unconstrained)
            .Certificate;
        string pfx = chain.WritePkcs12(_deployment.FilePath("other.pfx"), [chain.Intermediate, chain.Other]);

        foreach (
            (string certificate, string? keyFile, int position, string subject) in new[]
            {
                (other, (string?)key, 2, "CN=another server"),
                (unconstrained, key, 2, "CN=no basic constraints"),
                (pfx, null, 3, "CN=another server"),
            }
        )
        {
            var refused = await Assert.ThrowsAsync<ServerStartException>(() =>
                _deployment.StartServerAsync(change: s => s with { TlsCertificate = certificate, TlsKey = keyFile })
            );
            Assert.Equal(
                $"The TLS certificate '{certificate}' cannot be used by a server: its certificate {position} ({subject}) is not a CA certificate.",
                refused.Message
            );
        }
    }

    /// <summary>
    /// A PEM certificate file that holds no certificate stops the start as a file that cannot be read.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The chain of the PEM test; its key file, and a certificate file whose only block is that private key.
    ///
    /// Steps:
    /// - Start the server with the key's text as the certificate file and the key file.
    ///
    /// Expected:
    /// - ServerStartException whose message begins "The TLS certificate cannot be read: ".
    /// </remarks>
    [Fact]
    public async Task Start_PemWithoutCertificate_Throws()
    {
        string key = NewChain().WritePem(_deployment.FilePath("none.crt"), _deployment.FilePath("none.key")).Key;
        string certificate = _deployment.FilePath("none.crt");
        File.Copy(key, certificate, overwrite: true);

        var refused = await Assert.ThrowsAsync<ServerStartException>(() =>
            _deployment.StartServerAsync(change: s => s with { TlsCertificate = certificate, TlsKey = key })
        );

        Assert.StartsWith("The TLS certificate cannot be read: ", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CA certificate in the file that does not belong to the chain of the server's certificate is not sent, and the server
    /// starts without it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two chains with names of their own; a PEM file with the first chain's certificate, its intermediate, and then the
    ///   intermediate of the second chain, and the key.
    ///
    /// Steps:
    /// - Start the server with the two files; connect and check as in the PEM test.
    ///
    /// Expected:
    /// - The server starts (on Windows, its check at the start expects only the intermediate of its own chain), the first
    ///   chain's intermediate is among the certificates sent, and the chain builds.
    /// </remarks>
    [Fact]
    public async Task Start_PemWithAnUnrelatedAuthority_StartsAndSendsItsOwnIntermediate()
    {
        TestCertificateChain unrelated = NewChain();
        (TestCertificateChain chain, ElmServer server) = await StartWithChainAsync(c =>
            c.WritePem(_deployment.FilePath("two.crt"), _deployment.FilePath("two.key"), c.Intermediate, unrelated.Intermediate)
        );
        (bool sent, bool builds, _) = await chain.ConnectAsync(server.PublicUrls[0], TestContext.Current.CancellationToken);

        Assert.True(sent);
        Assert.True(builds);
    }

    /// <summary>
    /// On Windows, a server whose first handshake cannot have the intermediate stops at the start with a sentence that names
    /// the certificate left out, leaves nothing listening, and does not record a start (design §13.5, V1d3a).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows. A chain; a listener of the test that answers TLS handshakes with the chain's certificate alone, with the
    ///   options Kestrel uses, while the intermediate is in no store; one handshake with it, which sends no intermediate. From
    ///   then on Windows sends this certificate alone in this process, whatever the store holds (design §20.7, the V1d3a Grill).
    /// - A PEM file with the certificate and then the intermediate, and the key.
    ///
    /// Steps:
    /// - Start the server with the two files on a port of the test's choice and a data folder of the test; then connect to
    ///   that port, and read the data folder.
    ///
    /// Expected:
    /// - ServerStartException "Windows left the intermediate CA certificate {subject} (thumbprint {thumbprint}) of the TLS
    ///   certificate '{path}' out of the first TLS handshake, and what that handshake sent stays for as long as this process
    ///   runs. Start again; if this happens at every start, add the certificate to the machine's Intermediate Certification
    ///   Authorities store." with the intermediate's subject and thumbprint; the connection afterwards is refused; the data
    ///   folder holds no admin.sock, and its log files hold no server.started.
    /// </remarks>
    [Fact]
    public async Task Start_FirstHandshakeWithoutTheIntermediate_Throws()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), TestCertificateChain.SelfCheckIsWindowsOnly);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        TestCertificateChain chain = NewChain(sentByServer: true);
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            (IPEndPoint alone, Task served) = chain.Listen(IPAddress.Loopback, [], stop.Token);
            (bool primed, _, _) = await chain.ConnectAsync(new Uri($"https://127.0.0.1:{alone.Port}/"), cancel);
            Assert.False(primed);
            await stop.CancelAsync();
            await served;
        }

        (string certificate, string key) = chain.WritePem(_deployment.FilePath("first.crt"), _deployment.FilePath("first.key"), chain.Intermediate);
        int port = FreePort();
        string data = _deployment.NewFolder();

        var refused = await Assert.ThrowsAsync<ServerStartException>(() =>
            _deployment.StartServerAsync(
                data,
                s => s with { Listen = new Uri($"https://127.0.0.1:{port}"), TlsCertificate = certificate, TlsKey = key }
            )
        );

        Assert.Equal(
            $"Windows left the intermediate CA certificate {chain.Intermediate.Subject} (thumbprint {chain.Intermediate.Thumbprint}) of the TLS "
                + $"certificate '{certificate}' out of the first TLS handshake, and what that handshake sent stays for as long as this "
                + "process runs. Start again; if this happens at every start, add the certificate to the machine's Intermediate "
                + "Certification Authorities store.",
            refused.Message
        );
        using var client = new TcpClient();
        SocketException closed = await Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port, cancel).AsTask());
        Assert.Equal(SocketError.ConnectionRefused, closed.SocketErrorCode);
        Assert.False(File.Exists(AdminSocket.PathIn(data)));
        string[] logs = Directory.GetFiles(data, "*.jsonl");
        Assert.NotEmpty(logs);
        Assert.DoesNotContain("server.started", string.Concat(logs.Select(File.ReadAllText)), StringComparison.Ordinal);
    }

    /// <summary>
    /// The tests start a server again, with a new chain, only after the sentence of the server's own check, and at most three
    /// chains in all; any other failure of the start is the test's at once.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Three start functions that count their calls: one throws ServerStartException with the sentence of the check for a
    ///   certificate left out; one throws ServerStartException "The TLS certificate cannot be read: x"; one throws the first
    ///   sentence twice and then returns 7.
    ///
    /// Steps:
    /// - Call TestCertificateChain.StartAsync with each.
    ///
    /// Expected:
    /// - The first throws its exception after 3 calls, each with another chain; the second after 1 call; the third returns 7
    ///   after 3 calls.
    /// </remarks>
    [Fact]
    public async Task StartAsync_OnlyAfterTheCheckSentence_TriesAnotherChain()
    {
        const string leftOut =
            "Windows left the intermediate CA certificate CN=x (thumbprint 00) of the TLS certificate 'x' out of the first TLS handshake, "
            + "and what that handshake sent stays for as long as this process runs. Start again; if this happens at every start, add the "
            + "certificate to the machine's Intermediate Certification Authorities store.";
        var subjects = new HashSet<string>();
        int other = 0;
        int third = 0;

        ServerStartException always = await Assert.ThrowsAsync<ServerStartException>(() =>
            TestCertificateChain.StartAsync<int>(chain =>
            {
                subjects.Add(chain.Intermediate.Subject);
                throw new ServerStartException(leftOut);
            })
        );
        ServerStartException unread = await Assert.ThrowsAsync<ServerStartException>(() =>
            TestCertificateChain.StartAsync<int>(_ =>
            {
                other++;
                throw new ServerStartException("The TLS certificate cannot be read: x");
            })
        );
        (TestCertificateChain kept, int started) = await TestCertificateChain.StartAsync(_ =>
            ++third < TestCertificateChain.StartAttempts ? throw new ServerStartException(leftOut) : Task.FromResult(7)
        );
        _chains.Add(kept);

        Assert.Equal(leftOut, always.Message);
        Assert.Equal(3, TestCertificateChain.StartAttempts);
        Assert.Equal(3, subjects.Count);
        Assert.Equal("The TLS certificate cannot be read: x", unread.Message);
        Assert.Equal(1, other);
        Assert.Equal((7, 3), (started, third));
    }

    /// <summary>
    /// On Windows, disposing a chain that a server sent takes the certificates .NET wrote for it out of the CA stores.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows. A chain that a server sends; a listener of the test that sends the chain's certificate with its intermediate,
    ///   for which .NET writes the intermediate to the CA store of the machine or of the user; one handshake that receives the
    ///   intermediate.
    ///
    /// Steps:
    /// - Stop the listener, dispose the chain, and read the CA store of the user, which also shows the machine's.
    ///
    /// Expected:
    /// - The handshake received the intermediate, and after the disposal no certificate of the store has the subject of the
    ///   chain's intermediate or of its root.
    /// </remarks>
    [Fact]
    public async Task Dispose_ChainAServerSent_LeavesNoneOfItsCertificatesInTheStores()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows keeps the intermediates a server sends in a certificate store.");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        var chain = new TestCertificateChain(sentByServer: true);
        string[] subjects = [chain.Intermediate.Subject, chain.Root.Subject];
        bool sent;
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            (IPEndPoint endPoint, Task served) = chain.Listen(IPAddress.Loopback, [chain.Intermediate], stop.Token);
            (sent, _, _) = await chain.ConnectAsync(new Uri($"https://127.0.0.1:{endPoint.Port}/"), cancel);
            await stop.CancelAsync();
            await served;
        }
        finally
        {
            chain.Dispose();
        }

        using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        string[] left = [.. store.Certificates.Select(c => c.Subject).Where(subjects.Contains)];

        Assert.True(sent);
        Assert.Empty(left);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private TestCertificateChain NewChain(bool sentByServer = false)
    {
        var chain = new TestCertificateChain(sentByServer);
        _chains.Add(chain);
        return chain;
    }

    // Starts the server with the files that write makes from a new chain; see the remarks of the class.
    private async Task<(TestCertificateChain Chain, ElmServer Server)> StartWithChainAsync(
        Func<TestCertificateChain, (string Certificate, string? Key)> write
    )
    {
        (TestCertificateChain chain, ElmServer server) = await TestCertificateChain.StartAsync(c =>
        {
            (string certificate, string? key) = write(c);
            return _deployment.StartServerAsync(change: s => s with { TlsCertificate = certificate, TlsKey = key });
        });
        _chains.Add(chain);
        return (chain, server);
    }
}
