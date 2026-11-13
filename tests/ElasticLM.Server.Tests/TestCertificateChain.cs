using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ElasticLM.Issuing;

namespace ElasticLM.Server.Tests;

/// <summary>
/// A root CA, an intermediate CA that the root signs, and a TLS certificate for 127.0.0.1 that the intermediate signs, for the
/// tests of the intermediates that elm-server and elm-service send (design §13.5, V1c12 and V1d3a). Linked into
/// ElasticLM.Service.Tests.
/// </summary>
/// <remarks>
/// The certificates are valid around the real time, not the tests' fake time, because TLS checks them with the real clock.
/// On Windows, .NET writes the intermediate, and a root the machine does not trust, into the CA store of the machine or of the
/// user so that SChannel sends it (design §13.5, R1). A server whose first handshake comes while another program changes that
/// store sends its certificate alone for as long as its process runs (design §20.7, the V1d3a Grill), so:
/// - Each chain has names of its own, which begin with <see cref="SubjectPrefix"/>, and <see cref="Dispose"/> of a chain that
///   a server sent takes every certificate whose subject begins with it out of both stores, the ones an earlier run left
///   included.
/// - A chain that a server sends (<c>sentByServer</c>) holds a mutex that every test process on the machine shares, from
///   before the server starts until the stores are swept, so no two such tests change the stores at the same time.
/// </remarks>
internal sealed class TestCertificateChain : IDisposable
{
    /// <summary>The beginning of the subject of every root and intermediate of the tests, and of nothing else.</summary>
    public const string SubjectPrefix = "CN=ElasticLM test chain ";

    /// <summary>
    /// How many chains <see cref="StartAsync"/> tries: under a program that rewrote the store all the time, 8 of 1,500 starts
    /// stopped at the server's check (design §20.7, the V1d3a Grill), so three in a row is about one run in 6.6 million.
    /// </summary>
    public const int StartAttempts = 3;

    /// <summary>The reason the tests of the servers' check at the start are skipped on Linux and macOS.</summary>
    public const string SelfCheckIsWindowsOnly =
        "Only Windows builds the chain it sends from a certificate store at the first handshake; Linux and macOS get it when the "
        + "certificate is loaded, and the servers make no check there.";

    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    // The beginning of the sentence with which a server stops when Windows left an intermediate out of its first handshake
    // (TlsChainSelfCheck).
    private const string LeftOut = "Windows left the intermediate CA certificate ";

    private readonly ECDsa _serverKey;
    private readonly ECDsa _otherKey;
    private readonly StoreLock? _storeLock;

    /// <summary>
    /// Initializes a new instance of the <see cref="TestCertificateChain"/> class.
    /// </summary>
    /// <param name="sentByServer">
    /// Whether a test starts a server that sends the intermediate, which changes the CA store on Windows: the chain then waits
    /// for the mutex of such tests and holds it until <see cref="Dispose"/> has swept the stores.
    /// </param>
    /// <param name="upper">
    /// Whether another intermediate CA stands between the root and <see cref="Intermediate"/>: <see cref="UpperIntermediate"/>.
    /// </param>
    public TestCertificateChain(bool sentByServer = false, bool upper = false)
    {
        // The mutex first, so no key or certificate is made by a chain that waits for it in vain; a constructor that throws
        // after it gives it back.
        _storeLock = sentByServer && OperatingSystem.IsWindows() ? new StoreLock() : null;
        try
        {
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            DateTimeOffset notBefore = now.AddDays(-1);
            DateTimeOffset notAfter = now.AddDays(30);
            string name = SubjectPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

            using ECDsa rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var rootRequest = new CertificateRequest(name + " root", rootKey, HashAlgorithmName.SHA256);
            AddAuthority(rootRequest, pathLength: upper ? 2 : 1);
            Root = rootRequest.CreateSelfSigned(notBefore, notAfter);

            X509Certificate2 issuer = Root;
            using ECDsa upperKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            if (upper)
            {
                var upperRequest = new CertificateRequest(name + " upper intermediate", upperKey, HashAlgorithmName.SHA256);
                AddAuthority(upperRequest, pathLength: 1);
                upperRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Root, true, false));
                using X509Certificate2 signed = upperRequest.Create(Root, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
                UpperIntermediate = signed.CopyWithPrivateKey(upperKey);
                issuer = UpperIntermediate;
            }

            using ECDsa intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var intermediateRequest = new CertificateRequest(name + " intermediate", intermediateKey, HashAlgorithmName.SHA256);
            AddAuthority(intermediateRequest, pathLength: 0);
            intermediateRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
            using (X509Certificate2 signed = intermediateRequest.Create(issuer, notBefore, notAfter, RandomNumberGenerator.GetBytes(16)))
            {
                Intermediate = signed.CopyWithPrivateKey(intermediateKey);
            }

            IntermediateRenewed = intermediateRequest.Create(issuer, notBefore.AddHours(1), notAfter, RandomNumberGenerator.GetBytes(16));

            _serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Server = Leaf("CN=127.0.0.1", _serverKey, Intermediate, notBefore, notAfter, basicConstraints: true);
            _otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Other = Leaf("CN=another server", _otherKey, Intermediate, notBefore, notAfter, basicConstraints: true);
            using ECDsa unconstrainedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Unconstrained = Leaf("CN=no basic constraints", unconstrainedKey, Intermediate, notBefore, notAfter, basicConstraints: false);
        }
        catch
        {
            _storeLock?.Dispose();
            throw;
        }
    }

    /// <summary>Gets the root CA, which only the client trusts.</summary>
    public X509Certificate2 Root { get; }

    /// <summary>Gets the intermediate CA, which the server must send.</summary>
    public X509Certificate2 Intermediate { get; }

    /// <summary>Gets the intermediate CA between the root and <see cref="Intermediate"/>, for a chain made with one.</summary>
    public X509Certificate2? UpperIntermediate { get; }

    /// <summary>
    /// Gets another certificate of the intermediate CA, as a CA's renewed certificate is: the same name and key, another serial
    /// number and start.
    /// </summary>
    public X509Certificate2 IntermediateRenewed { get; }

    /// <summary>Gets the server's certificate, without its private key.</summary>
    public X509Certificate2 Server { get; }

    /// <summary>Gets another leaf certificate that the intermediate signed, which is not a CA certificate.</summary>
    public X509Certificate2 Other { get; }

    /// <summary>Gets a certificate that the intermediate signed without the basic constraints extension.</summary>
    public X509Certificate2 Unconstrained { get; }

    /// <summary>
    /// Makes a chain that a server sends and starts the server with it. When the server stops because Windows left an
    /// intermediate out of its first handshake, and only then, the chain is thrown away and a new one is tried, at most
    /// <see cref="StartAttempts"/> chains in all: within one process a start with the same certificate ends the same way
    /// (design §20.7, the V1d3a Grill). Any other failure is the test's.
    /// </summary>
    /// <param name="start">Writes the chain's files and starts the server.</param>
    /// <returns>The chain, which the caller disposes after the server has stopped, and what <paramref name="start"/> returned.</returns>
    public static async Task<(TestCertificateChain Chain, T Started)> StartAsync<T>(Func<TestCertificateChain, Task<T>> start)
    {
        for (int attempt = 1; ; attempt++)
        {
            var chain = new TestCertificateChain(sentByServer: true);
            try
            {
                return (chain, await start(chain));
            }
            catch (Exception e)
            {
                chain.Dispose();
                if (attempt == StartAttempts || !e.Message.StartsWith(LeftOut, StringComparison.Ordinal))
                {
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Writes the server's certificate followed by the given certificates as one PEM file, and the server's key as another.
    /// </summary>
    public (string Certificate, string Key) WritePem(string certificatePath, string keyPath, params X509Certificate2[] after)
    {
        File.WriteAllText(certificatePath, string.Concat(new[] { Server }.Concat(after).Select(c => c.ExportCertificatePem() + "\n")));
        File.WriteAllText(keyPath, _serverKey.ExportPkcs8PrivateKeyPem() + "\n");
        return (certificatePath, keyPath);
    }

    /// <summary>
    /// Writes the server's certificate with its key, then the given certificates without a key, as one PKCS#12 file without a
    /// password; with <paramref name="otherWithKey"/>, another server's certificate with its key comes after them.
    /// </summary>
    public string WritePkcs12(string path, X509Certificate2[] after, bool otherWithKey = false)
    {
        var all = new X509Certificate2Collection { Server.CopyWithPrivateKey(_serverKey) };
        foreach (X509Certificate2 certificate in after)
        {
            all.Add(X509CertificateLoader.LoadCertificate(certificate.RawData));
        }

        if (otherWithKey)
        {
            all.Add(Other.CopyWithPrivateKey(_otherKey));
        }

        try
        {
            File.WriteAllBytes(path, all.Export(X509ContentType.Pkcs12)!);
        }
        finally
        {
            foreach (X509Certificate2 certificate in all)
            {
                certificate.Dispose();
            }
        }

        return path;
    }

    /// <summary>
    /// Returns the server's certificate with its key and no intermediate, as a server reads it from a file without them.
    /// </summary>
    public TlsServerCertificate ServerAlone()
    {
        using X509Certificate2 withKey = Server.CopyWithPrivateKey(_serverKey);
        return new TlsServerCertificate(TlsCertificateFiles.ForKestrel(withKey), []);
    }

    /// <summary>
    /// Returns the server's certificate with its key and the given certificates as the intermediates of its file.
    /// </summary>
    public TlsServerCertificate ServerWith(params X509Certificate2[] intermediates)
    {
        using X509Certificate2 withKey = Server.CopyWithPrivateKey(_serverKey);
        return new TlsServerCertificate(
            TlsCertificateFiles.ForKestrel(withKey),
            [.. intermediates.Select(c => X509CertificateLoader.LoadCertificate(c.RawData))]
        );
    }

    /// <summary>
    /// Listens on a port the system chooses and answers every TLS handshake with the server's certificate and the given
    /// intermediates; with none, with the certificate alone, as a server does whose certificate file holds no intermediate.
    /// </summary>
    /// <remarks>
    /// The options are those Kestrel gives SslStream for a listener with one certificate (no protocol named, no revocation
    /// check), so on Windows the handshake uses, and fills, the credential that a server started later in this process with the
    /// same certificate gets (design §20.7, the V1d3a Grill). Without intermediates nothing is written to a certificate store;
    /// with them, on Windows, .NET writes them to the CA store, so the chain is one that a server sends.
    /// </remarks>
    /// <param name="address">The address to listen on.</param>
    /// <param name="intermediates">The certificates to send after the server's.</param>
    /// <param name="cancellationToken">Stops the listener.</param>
    /// <param name="protocols">The TLS versions the listener allows; <see cref="SslProtocols.None"/> lets the system choose.</param>
    /// <param name="seen">
    /// Gets, for each handshake that ended, the bytes read after it (up to 16; 0 when the client sent nothing more and closed,
    /// -1 when the read failed because the client had closed) and the server name the client sent (empty when it sent none).
    /// </param>
    /// <returns>The endpoint, and the listener's work, which ends when <paramref name="cancellationToken"/> is cancelled.</returns>
    public (IPEndPoint EndPoint, Task Served) Listen(
        IPAddress address,
        X509Certificate2[] intermediates,
        CancellationToken cancellationToken,
        SslProtocols protocols = SslProtocols.None,
        ConcurrentQueue<(int Read, string ServerName)>? seen = null
    )
    {
        var listener = new TcpListener(address, 0);
        listener.Start();
        var endPoint = (IPEndPoint)listener.LocalEndpoint;
        return (endPoint, ServeAsync());

        async Task ServeAsync()
        {
            using TlsServerCertificate alone = ServerAlone();
            SslStreamCertificateContext? context =
                intermediates.Length == 0 ? null : SslStreamCertificateContext.Create(alone.Certificate, [.. intermediates], offline: true);
            try
            {
                while (true)
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
                    await using var tls = new SslStream(client.GetStream());
                    try
                    {
                        await tls.AuthenticateAsServerAsync(
                            new SslServerAuthenticationOptions
                            {
                                ServerCertificate = context is null ? alone.Certificate : null,
                                ServerCertificateContext = context,
                                EnabledSslProtocols = protocols,
                                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                            },
                            cancellationToken
                        );

                        // A client that sends nothing after the handshake closes first; the read returns 0 then, or, when the
                        // client closed while the listener's session tickets were on their way (TLS 1.3), throws: -1.
                        int read;
                        try
                        {
                            read = await tls.ReadAsync(new byte[16], cancellationToken);
                        }
                        catch (IOException)
                        {
                            read = -1;
                        }

                        seen?.Enqueue((read, tls.TargetHostName));
                    }
                    catch (Exception e) when (e is IOException or AuthenticationException)
                    {
                        // A client that left in the middle.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The test is over.
            }
            finally
            {
                listener.Dispose();
            }
        }
    }

    /// <summary>
    /// Connects to the server over TLS and returns whether it sent the intermediate, whether a client that trusts only the
    /// root, downloads nothing, and checks no revocation then builds the chain, and the thumbprint of the certificate it presented.
    /// </summary>
    /// <remarks>
    /// The intermediate counts as sent only when it is among the certificates the server sent, which SslStream adds to the
    /// callback's ExtraStore; on Windows the CA store holds it too, and a chain built from the store would pass without it.
    /// </remarks>
    public async Task<(bool Sent, bool Builds, string? Presented)> ConnectAsync(Uri url, CancellationToken cancellationToken)
    {
        (bool sent, bool builds, string? presented, _) = await HandshakeAsync(url, null, cancellationToken);
        return (sent, builds, presented);
    }

    /// <summary>
    /// Connects to the server over TLS and returns whether it sent the given certificate (by thumbprint).
    /// </summary>
    public async Task<bool> SendsAsync(Uri url, X509Certificate2 certificate, CancellationToken cancellationToken) =>
        (await HandshakeAsync(url, certificate, cancellationToken)).SentThat;

    private async Task<(bool Sent, bool Builds, string? Presented, bool SentThat)> HandshakeAsync(
        Uri url,
        X509Certificate2? that,
        CancellationToken cancellationToken
    )
    {
        bool sentThat = false;
        bool sent = false;
        bool builds = false;
        string? thumbprint = null;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, url.Port, cancellationToken);
        await using var tls = new SslStream(client.GetStream());
        await tls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "127.0.0.1",
                RemoteCertificateValidationCallback = (_, presented, chain, _) =>
                {
                    X509Certificate2Collection received = chain?.ChainPolicy.ExtraStore ?? [];
                    sent = received.Cast<X509Certificate2>().Any(c => c.Thumbprint == Intermediate.Thumbprint);
                    sentThat = that is not null && received.Cast<X509Certificate2>().Any(c => c.Thumbprint == that.Thumbprint);
                    using var check = new X509Chain();
                    check.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    check.ChainPolicy.CustomTrustStore.Add(Root);
                    check.ChainPolicy.DisableCertificateDownloads = true;
                    check.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    check.ChainPolicy.ExtraStore.AddRange(received);
                    thumbprint = (presented as X509Certificate2)?.Thumbprint;
                    builds = presented is X509Certificate2 leaf && check.Build(leaf);
                    return true;
                },
            },
            cancellationToken
        );
        return (sent, builds, thumbprint, sentThat);
    }

    /// <summary>
    /// Takes every root and intermediate of the tests out of the CA stores that .NET may have written them to on Windows,
    /// gives the mutex back, and disposes the certificates.
    /// </summary>
    /// <exception cref="CryptographicException">The user's CA store cannot be opened or changed.</exception>
    public void Dispose()
    {
        try
        {
            // Only under the mutex, which is taken on Windows alone: a chain without it was not sent by a server, so .NET wrote
            // nothing for it, and its sweep would change the stores while another test's server starts.
            if (_storeLock is not null && OperatingSystem.IsWindows())
            {
                // The machine's store first: the user's store also lists the machine's certificates, and removing one of those
                // through the user's store is refused ("Access is denied" on the windows-latest runner, whose user is an
                // administrator, so .NET wrote to the machine's store there).
                try
                {
                    SweepAuthorities(StoreLocation.LocalMachine);
                }
                catch (CryptographicException)
                {
                    // The machine's store needs an administrator, and .NET then wrote to the user's store only.
                }

                SweepAuthorities(StoreLocation.CurrentUser);
            }
        }
        finally
        {
            _storeLock?.Dispose();
            Root.Dispose();
            UpperIntermediate?.Dispose();
            Intermediate.Dispose();
            IntermediateRenewed.Dispose();
            Server.Dispose();
            Other.Dispose();
            Unconstrained.Dispose();
            _serverKey.Dispose();
            _otherKey.Dispose();
        }
    }

    // By the names, not by this chain's thumbprints: a removal by thumbprint left certificates behind when the store was
    // changed by others at the same time (3 of about 464 in the measurement of design §20.7, the V1d3a Grill), and the sweep
    // takes those with it at the next test. Only the chain that holds the mutex has certificates in the stores at that time.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SweepAuthorities(StoreLocation location)
    {
        using var store = new X509Store(StoreName.CertificateAuthority, location);
        store.Open(OpenFlags.ReadWrite);

        // A removal can stay without effect while another program writes the store (1 test run in 50 of the measurement), so
        // the sweep reads the store again and removes what is still there, three passes at most.
        for (int pass = 0; pass < 3; pass++)
        {
            X509Certificate2Collection all = store.Certificates;
            try
            {
                X509Certificate2[] ours = [.. all.Where(IsOurs)];
                int removed = 0;
                foreach (X509Certificate2 certificate in ours)
                {
                    try
                    {
                        store.Remove(certificate);
                        removed++;
                    }
                    catch (CryptographicException)
                    {
                        // The user's store also lists the machine's certificates, and removing one of those through it is
                        // refused. They are the machine's to remove, which the sweep of the machine's store did or could not.
                    }
                }

                if (removed == 0)
                {
                    return;
                }
            }
            finally
            {
                foreach (X509Certificate2 certificate in all)
                {
                    certificate.Dispose();
                }
            }
        }
    }

    // The names of this run's chains, and the two names that every chain had before V1d3a, whose certificates earlier runs
    // left in the stores (21 of them in the measurement of design §20.7, the V1d3a Grill).
    private static bool IsOurs(X509Certificate2 certificate) =>
        certificate.Subject.StartsWith(SubjectPrefix, StringComparison.Ordinal)
        || certificate.Subject is "CN=ElasticLM test root" or "CN=ElasticLM test intermediate";

    private static void AddAuthority(CertificateRequest request, int pathLength)
    {
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, pathLength, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
    }

    private static X509Certificate2 Leaf(
        string subject,
        ECDsa key,
        X509Certificate2 issuer,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        bool basicConstraints
    )
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        if (basicConstraints)
        {
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        }

        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthentication)], critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        return request.Create(issuer, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>
    /// The mutex of the tests that change the Windows CA stores, taken on a thread of its own because a mutex belongs to the
    /// thread that took it and a test goes on on other threads after each await.
    /// </summary>
    private sealed class StoreLock : IDisposable
    {
        // Global: one name for every session of the machine, since the machine's store is shared by all of them.
        private const string Name = @"Global\ElasticLM.Tests.TlsChainStore";

        // A holder keeps the mutex for one server start, a few handshakes, and the sweep, about a second; the bound only ends
        // a run in which a holder hangs.
        private static readonly TimeSpan Wait = TimeSpan.FromMinutes(5);

        private readonly ManualResetEventSlim _decided = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _holder;
        private bool _taken;
        private Exception? _failure;

        public StoreLock()
        {
            _holder = new Thread(Hold) { IsBackground = true, Name = "TLS chain store lock" };
            _holder.Start();
            _decided.Wait();
            if (!_taken)
            {
                _holder.Join();
                _decided.Dispose();
                _release.Dispose();
                throw _failure is null
                    ? new TimeoutException($"The mutex '{Name}' of the tests that change the Windows CA stores was not free within {Wait}.")
                    : new InvalidOperationException(
                        $"The mutex '{Name}' of the tests that change the Windows CA stores cannot be opened or taken, for example because "
                            + "a process that runs as administrator made it.",
                        _failure
                    );
            }
        }

        public void Dispose()
        {
            _release.Set();
            _holder.Join();
            _decided.Dispose();
            _release.Dispose();
        }

        // Every exception is kept for the constructor: one that left this thread would end the test host.
        private void Hold()
        {
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(initiallyOwned: false, Name);
                try
                {
                    _taken = mutex.WaitOne(Wait);
                }
                catch (AbandonedMutexException)
                {
                    // A test process ended while it held the mutex; this thread holds it now.
                    _taken = true;
                }
            }
            catch (Exception e)
            {
                _failure = e;
            }

            _decided.Set();
            try
            {
                if (_taken)
                {
                    _release.Wait();
                    mutex!.ReleaseMutex();
                }
            }
            catch (Exception e) when (e is ApplicationException or ObjectDisposedException)
            {
                // Released already, or the process is ending; nothing waits for this thread's answer any more.
            }
            finally
            {
                mutex?.Dispose();
            }
        }
    }
}
