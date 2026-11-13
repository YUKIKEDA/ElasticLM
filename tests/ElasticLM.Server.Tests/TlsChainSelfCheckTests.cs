using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ElasticLM.Issuing;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Server.Tests;

/// <summary>
/// The check that elm-server and elm-service make on Windows right after they start to listen: one TLS handshake with their own
/// listener, which reads whether the intermediates of the certificate file are sent (design §13.5, V1d3a). Here the check runs
/// against listeners of the test, which send what the test decides; the servers' own starts are in <see cref="TlsChainTests"/>.
/// </summary>
public sealed class TlsChainSelfCheckTests : IDisposable
{
    private const string Path = "chain.crt";

    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    private readonly List<IDisposable> _owned = [];

    // The real time: a certificate sent in place of the file's is checked for its period against this clock, and the test
    // certificates are valid around the real time.
    private readonly FakeTimeProvider _time = new(TimeProvider.System.GetUtcNow());

    public void Dispose()
    {
        _stop.Cancel();
        foreach (IDisposable owned in Enumerable.Reverse(_owned))
        {
            owned.Dispose();
        }

        _stop.Dispose();
    }

    /// <summary>
    /// A listener that sends the certificate alone gives the sentence that names the intermediate left out; a wildcard listen
    /// address is reached through loopback.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows. A chain; a listener of the test on 127.0.0.1 (for the case "::", on ::1) that answers TLS handshakes with the
    ///   chain's certificate alone; the certificate with the intermediate as the intermediates of its file.
    ///
    /// Steps:
    /// - Run the check with the listener's own endpoint, with 0.0.0.0 and the listener's port, and with :: and the port of the
    ///   listener on ::1.
    ///
    /// Expected:
    /// - "Windows left the intermediate CA certificate {subject} (thumbprint {thumbprint}) of the TLS certificate 'chain.crt'
    ///   out of the first TLS handshake, and what that handshake sent stays for as long as this process runs. Start again; if
    ///   this happens at every start, add the certificate to the machine's Intermediate Certification Authorities store." with
    ///   the intermediate's subject and thumbprint, in every case.
    /// </remarks>
    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("127.0.0.1", "0.0.0.0")]
    [InlineData("::1", "::")]
    public async Task Run_IntermediateNotSent_NamesIt(string listenOn, string configured)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), TestCertificateChain.SelfCheckIsWindowsOnly);
        Assert.SkipUnless(listenOn != "::1" || Socket.OSSupportsIPv6, "This machine has no IPv6.");
        TestCertificateChain chain = Own(new TestCertificateChain());
        (IPEndPoint endPoint, _) = chain.Listen(IPAddress.Parse(listenOn), [], _stop.Token);
        TlsServerCertificate certificate = Own(chain.ServerWith(chain.Intermediate));

        string? problem = await TlsChainSelfCheck.RunAsync(
            Path,
            certificate,
            new IPEndPoint(IPAddress.Parse(configured), endPoint.Port),
            _time,
            _stop.Token
        );

        Assert.Equal(LeftOut(chain), problem);
    }

    /// <summary>
    /// After earlier checks of the same listener in the same process, each check still makes a full handshake and reads the
    /// certificates sent, with TLS 1.3 and with TLS 1.2; it sends no server name and nothing after the handshake.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows. A chain that a server sends (it holds the mutex of the tests that change the CA stores); a listener of the
    ///   test that sends the chain's certificate with its intermediate, with the TLS versions the system chooses or with TLS
    ///   1.2 only, and records, for each handshake, the bytes read after it and the server name the client sent; the
    ///   certificate with the intermediate as the intermediates of its file.
    ///
    /// Steps:
    /// - Connect to the listener twice (with a new chain, at most three in all, until it sends the intermediate), run the check with
    ///   the listener's endpoint three times in a row, then connect once more, which ends after the listener has recorded the
    ///   third check.
    ///
    /// Expected:
    /// - Null each time; after every recorded handshake the listener read no byte (the client closed), and it saw no server
    ///   name.
    /// </remarks>
    [Theory]
    [InlineData(SslProtocols.None)]
    [InlineData(SslProtocols.Tls12)]
    public async Task Run_SameListenerAgain_MakesAFullHandshake(SslProtocols protocols)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), TestCertificateChain.SelfCheckIsWindowsOnly);
        var seen = new ConcurrentQueue<(int Read, string ServerName)>();
        (TestCertificateChain chain, IPEndPoint endPoint, _, _) = await ListenSendingAsync(c => c.Intermediate, protocols, seen);
        TlsServerCertificate certificate = Own(chain.ServerWith(chain.Intermediate));

        string?[] problems = new string?[3];
        for (int i = 0; i < problems.Length; i++)
        {
            problems[i] = await TlsChainSelfCheck.RunAsync(Path, certificate, endPoint, _time, _stop.Token);
        }

        _ = await chain.ConnectAsync(new Uri($"https://127.0.0.1:{endPoint.Port}/"), _stop.Token);

        Assert.Equal(new string?[3], problems);
        Assert.True(seen.Count >= 4, $"The listener recorded {seen.Count} handshakes.");
        Assert.All(
            seen,
            s =>
            {
                Assert.True(s.Read <= 0, $"The listener read {s.Read} bytes after a handshake.");
                Assert.Equal(string.Empty, s.ServerName);
            }
        );
    }

    /// <summary>
    /// Two intermediates that the listener leaves out are both named, in the order of the chain from the server's certificate.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows. A chain root, upper intermediate, intermediate, server certificate; a listener of the test that answers with
    ///   the server's certificate alone; the certificate with the intermediate and the upper intermediate as the intermediates
    ///   of its file.
    ///
    /// Steps:
    /// - Run the check with the listener's endpoint.
    ///
    /// Expected:
    /// - "Windows left the intermediate CA certificate {intermediate} (thumbprint …) and the intermediate CA certificate {upper}
    ///   (thumbprint …) of the TLS certificate 'chain.crt' out of the first TLS handshake, … add these certificates to the
    ///   machine's Intermediate Certification Authorities store."
    /// </remarks>
    [Fact]
    public async Task Run_TwoIntermediatesNotSent_NamesBoth()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), TestCertificateChain.SelfCheckIsWindowsOnly);
        TestCertificateChain chain = Own(new TestCertificateChain(upper: true));
        (IPEndPoint endPoint, _) = chain.Listen(IPAddress.Loopback, [], _stop.Token);
        TlsServerCertificate certificate = Own(chain.ServerWith(chain.Intermediate, chain.UpperIntermediate!));

        string? problem = await TlsChainSelfCheck.RunAsync(Path, certificate, endPoint, _time, _stop.Token);

        Assert.Equal(
            $"Windows left the intermediate CA certificate {chain.Intermediate.Subject} (thumbprint {chain.Intermediate.Thumbprint}) and the "
                + $"intermediate CA certificate {chain.UpperIntermediate!.Subject} (thumbprint {chain.UpperIntermediate.Thumbprint}) of the TLS "
                + $"certificate '{Path}' out of the first TLS handshake, and what that handshake sent stays for as long as this process runs. "
                + "Start again; if this happens at every start, add these certificates to the machine's Intermediate Certification "
                + "Authorities store.",
            problem
        );
    }

    /// <summary>
    /// An intermediate counts as sent when the listener sends another certificate of the same CA: the same name and key.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows. A chain that a server sends (it holds the mutex of the tests that change the CA stores); a listener of the
    ///   test that sends the chain's certificate with the renewed certificate of the intermediate CA (the same name and key,
    ///   another serial number); the certificate with the first certificate of the intermediate CA as the intermediates of
    ///   its file.
    ///
    /// Steps:
    /// - Connect to the listener and read what it sends; when it does not send the renewed certificate (its first handshake
    ///   lost it), try a new chain, at most three in all. Run the check with the listener's endpoint.
    ///
    /// Expected:
    /// - The listener sends a certificate with the intermediate's subject, and not the certificate of the file; the check
    ///   returns null.
    /// </remarks>
    [Fact]
    public async Task Run_RenewedIntermediateSent_Passes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), TestCertificateChain.SelfCheckIsWindowsOnly);
        (TestCertificateChain chain, IPEndPoint endPoint, bool sentTheFiles, bool builds) = await ListenSendingAsync(c => c.IntermediateRenewed);
        TlsServerCertificate certificate = Own(chain.ServerWith(chain.Intermediate));
        string? problem = await TlsChainSelfCheck.RunAsync(Path, certificate, endPoint, _time, _stop.Token);

        Assert.False(sentTheFiles);
        Assert.True(builds);
        Assert.Null(problem);
    }

    /// <summary>
    /// A renewed certificate of the intermediate CA does not count as sent at a time outside its period.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows. The chain and the listener of the renewed test; a clock one day after the end of the renewed certificate.
    ///
    /// Steps:
    /// - Run the check with the listener's endpoint and that clock.
    ///
    /// Expected:
    /// - The sentence that names the file's intermediate as left out.
    /// </remarks>
    [Fact]
    public async Task Run_RenewedIntermediateOutsideItsPeriod_NamesTheFilesIntermediate()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), TestCertificateChain.SelfCheckIsWindowsOnly);
        (TestCertificateChain chain, IPEndPoint endPoint, _, bool builds) = await ListenSendingAsync(c => c.IntermediateRenewed);
        TlsServerCertificate certificate = Own(chain.ServerWith(chain.Intermediate));
        var later = new FakeTimeProvider(new DateTimeOffset(chain.IntermediateRenewed.NotAfter.ToUniversalTime()).AddDays(1));

        string? problem = await TlsChainSelfCheck.RunAsync(Path, certificate, endPoint, later, _stop.Token);

        Assert.True(builds);
        Assert.Equal(LeftOut(chain), problem);
    }

    /// <summary>
    /// A check cancelled through the caller's token ends with the cancellation, not with the sentence of the time limit.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows. A chain; the certificate with the intermediate as the intermediates of its file; a TCP listener of the test
    ///   that accepts the connection and sends nothing.
    ///
    /// Steps:
    /// - Start the check with a token of the test; once the listener has accepted, cancel the token.
    ///
    /// Expected:
    /// - The check throws OperationCanceledException.
    /// </remarks>
    [Fact]
    public async Task Run_CallerCancels_ThrowsTheCancellation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), TestCertificateChain.SelfCheckIsWindowsOnly);
        TestCertificateChain chain = Own(new TestCertificateChain());
        TlsServerCertificate certificate = Own(chain.ServerWith(chain.Intermediate));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);

        Task<string?> check = TlsChainSelfCheck.RunAsync(Path, certificate, (IPEndPoint)listener.LocalEndpoint, _time, caller.Token);
        using TcpClient accepted = await listener.AcceptTcpClientAsync(_stop.Token);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.WaitAsync(TimeSpan.FromSeconds(30), _stop.Token));
    }

    /// <summary>
    /// A certificate without an intermediate in its chain is not checked, and no connection is made: a file with the
    /// certificate alone, with the root after it, and with a CA certificate of another chain after it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows. Two chains; a TCP listener of the test that accepts nothing.
    ///
    /// Steps:
    /// - Run the check with the listener's endpoint and the first chain's certificate: alone; with its root as the file's
    ///   second certificate; with the second chain's intermediate as the file's second certificate.
    ///
    /// Expected:
    /// - Null each time, and the listener has no connection waiting.
    /// </remarks>
    [Fact]
    public async Task Run_NoIntermediateInTheChain_MakesNoConnection()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), TestCertificateChain.SelfCheckIsWindowsOnly);
        TestCertificateChain chain = Own(new TestCertificateChain());
        TestCertificateChain unrelated = Own(new TestCertificateChain());
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endPoint = (IPEndPoint)listener.LocalEndpoint;

        foreach (X509Certificate2[] after in new X509Certificate2[][] { [], [chain.Root], [unrelated.Intermediate] })
        {
            TlsServerCertificate certificate = Own(chain.ServerWith(after));

            Assert.Null(await TlsChainSelfCheck.RunAsync(Path, certificate, endPoint, _time, _stop.Token));
            Assert.False(listener.Pending());
        }
    }

    /// <summary>
    /// On Linux and macOS the check does nothing: it makes no connection and returns null.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux or macOS. A chain; the certificate with the intermediate as the intermediates of its file; a TCP listener of
    ///   the test that accepts nothing.
    ///
    /// Steps:
    /// - Run the check with the listener's endpoint.
    ///
    /// Expected:
    /// - Null, and the listener has no connection waiting.
    /// </remarks>
    [Fact]
    public async Task Run_NotWindows_MakesNoConnection()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "On Windows the check runs; the other tests of this class cover it.");
        TestCertificateChain chain = Own(new TestCertificateChain());
        TlsServerCertificate certificate = Own(chain.ServerWith(chain.Intermediate));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        Assert.Null(await TlsChainSelfCheck.RunAsync(Path, certificate, (IPEndPoint)listener.LocalEndpoint, _time, _stop.Token));
        Assert.False(listener.Pending());
    }

    /// <summary>
    /// A listener that accepts the connection and never answers ends the check when its clock has moved 10 seconds, not before.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows. A chain; the certificate with the intermediate as the intermediates of its file; a TCP listener of the test
    ///   that accepts connections into its backlog and sends nothing; a fake clock.
    ///
    /// Steps:
    /// - Start the check with the listener's endpoint; move the clock 10 seconds less one millisecond; then one millisecond.
    ///
    /// Expected:
    /// - After the first move the check has not ended. After the second it returns "The check that Windows sends the
    ///   intermediate CA certificates of the TLS certificate 'chain.crt' did not get an answer: the TLS handshake with
    ///   127.0.0.1:{port} did not finish within 10 seconds."
    /// </remarks>
    [Fact]
    public async Task Run_ListenerNeverAnswers_EndsAtTheLimit()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), TestCertificateChain.SelfCheckIsWindowsOnly);
        TestCertificateChain chain = Own(new TestCertificateChain());
        TlsServerCertificate certificate = Own(chain.ServerWith(chain.Intermediate));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endPoint = (IPEndPoint)listener.LocalEndpoint;

        Task<string?> check = TlsChainSelfCheck.RunAsync(Path, certificate, endPoint, _time, _stop.Token);
        using TcpClient accepted = await listener.AcceptTcpClientAsync(_stop.Token);
        _time.Advance(TlsChainSelfCheck.Limit - TimeSpan.FromMilliseconds(1));
        bool endedEarly = check.IsCompleted;
        _time.Advance(TimeSpan.FromMilliseconds(1));

        // The bound of the real clock only ends a run in which the check does not end at its limit.
        string? problem = await check.WaitAsync(TimeSpan.FromSeconds(30), _stop.Token);

        Assert.False(endedEarly);
        Assert.Equal(TimeSpan.FromSeconds(10), TlsChainSelfCheck.Limit);
        Assert.Equal(NoAnswer(endPoint, "did not finish within 10 seconds"), problem);
    }

    /// <summary>
    /// A listener that closes the connection without a TLS answer, and a port where nothing listens, each give the sentence of
    /// a handshake without an answer.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows. A chain; the certificate with the intermediate as the intermediates of its file; a TCP listener of the test.
    /// - The closed port: a listener of the test takes a port the system chooses and stops, and the test assumes that nothing
    ///   listens there afterwards. Another program that took the port in between would get the request instead (the V1d3b
    ///   Grill, 3b-5): on Windows the port that a bind to port 0 gets moves on across the machine, and in 2,000 binds none
    ///   came back at once.
    ///
    /// Steps:
    /// - Start the check with the listener's endpoint, accept its connection, and close it. Then stop the listener and run the
    ///   check with the same endpoint.
    ///
    /// Expected:
    /// - "… did not get an answer: the TLS handshake with 127.0.0.1:{port} failed." for the closed connection, and
    ///   "… failed (socket error ConnectionRefused)." for the port where nothing listens.
    /// </remarks>
    [Fact]
    public async Task Run_NoTlsAnswer_SaysTheHandshakeFailed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), TestCertificateChain.SelfCheckIsWindowsOnly);
        TestCertificateChain chain = Own(new TestCertificateChain());
        TlsServerCertificate certificate = Own(chain.ServerWith(chain.Intermediate));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endPoint = (IPEndPoint)listener.LocalEndpoint;

        Task<string?> check = TlsChainSelfCheck.RunAsync(Path, certificate, endPoint, _time, _stop.Token);
        (await listener.AcceptTcpClientAsync(_stop.Token)).Dispose();
        string? closed = await check;
        listener.Dispose();
        string? refused = await TlsChainSelfCheck.RunAsync(Path, certificate, endPoint, _time, _stop.Token);

        Assert.Equal(NoAnswer(endPoint, "failed"), closed);
        Assert.Equal(NoAnswer(endPoint, "failed (socket error ConnectionRefused)"), refused);
    }

    private static string LeftOut(TestCertificateChain chain) =>
        $"Windows left the intermediate CA certificate {chain.Intermediate.Subject} (thumbprint {chain.Intermediate.Thumbprint}) of the "
        + $"TLS certificate '{Path}' out of the first TLS handshake, and what that handshake sent stays for as long as this process runs. "
        + "Start again; if this happens at every start, add the certificate to the machine's Intermediate Certification Authorities store.";

    private static string NoAnswer(IPEndPoint endPoint, string how) =>
        $"The check that Windows sends the intermediate CA certificates of the TLS certificate '{Path}' did not get an answer: the TLS "
        + $"handshake with {endPoint} {how}.";

    // A chain that a server sends, with a listener of the test that sends the chain's certificate and the given intermediate,
    // and what a connection to it got. The listener's own first handshake can lose the intermediate to another program
    // that changes the store at that moment, as a server's can (design §20.7, the V1d3a Grill); then a new chain is tried, at
    // most StartAttempts in all. A chain holds the mutex until it is disposed, so the chain of a failed try is disposed before
    // the next is made.
    private async Task<(TestCertificateChain Chain, IPEndPoint EndPoint, bool SentTheFiles, bool Builds)> ListenSendingAsync(
        Func<TestCertificateChain, X509Certificate2> intermediate,
        SslProtocols protocols = SslProtocols.None,
        ConcurrentQueue<(int Read, string ServerName)>? seen = null
    )
    {
        for (int attempt = 1; ; attempt++)
        {
            var chain = new TestCertificateChain(sentByServer: true);
            var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            (IPEndPoint endPoint, Task served) = chain.Listen(IPAddress.Loopback, [intermediate(chain)], stop.Token, protocols, seen);
            var url = new Uri($"https://127.0.0.1:{endPoint.Port}/");

            // Whether the listener sends this intermediate is read by its thumbprint: a chain can build from the store's copy.
            bool sendsIt = await chain.SendsAsync(url, intermediate(chain), _stop.Token);
            (bool sent, bool builds, _) = await chain.ConnectAsync(url, _stop.Token);
            if (sendsIt || attempt == TestCertificateChain.StartAttempts)
            {
                Own(stop);
                return (Own(chain), endPoint, sent, builds);
            }

            // The listener of a try that is given up stops before its chain sweeps the stores and gives the mutex back.
            await stop.CancelAsync();
            await served;
            stop.Dispose();
            chain.Dispose();
            seen?.Clear();
        }
    }

    private T Own<T>(T owned)
        where T : IDisposable
    {
        _owned.Add(owned);
        return owned;
    }
}
