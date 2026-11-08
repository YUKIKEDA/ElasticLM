namespace ElasticLM.Server.Tests;

/// <summary>
/// elm-server sends the intermediate CA certificates of its TLS certificate file with the certificate, from a PEM file and from
/// a PKCS#12 file, and refuses to start when one of them is not a CA certificate (design §13.5, V1c12).
/// </summary>
public sealed class TlsChainTests : IAsyncDisposable
{
    private readonly Deployment _deployment = new();
    private readonly TestCertificateChain _chain = new();

    public async ValueTask DisposeAsync()
    {
        await _deployment.DisposeAsync();
        _chain.Dispose();
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
        (string certificate, string key) = _chain.WritePem(_deployment.FilePath("chain.crt"), _deployment.FilePath("chain.key"), _chain.Intermediate);

        ElmServer server = await _deployment.StartServerAsync(change: s => s with { TlsCertificate = certificate, TlsKey = key });
        (bool sent, bool builds) = await _chain.ConnectAsync(server.PublicUrls[0], TestContext.Current.CancellationToken);

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
        string pfx = _chain.WritePkcs12(_deployment.FilePath("chain.pfx"));

        ElmServer server = await _deployment.StartServerAsync(change: s => s with { TlsCertificate = pfx, TlsKey = null });
        (bool sent, bool builds) = await _chain.ConnectAsync(server.PublicUrls[0], TestContext.Current.CancellationToken);

        Assert.True(sent);
        Assert.True(builds);
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
    /// - The TLS handshake completes, the intermediate is not among the certificates the server sent, and a client that trusts
    ///   only the root and downloads nothing cannot build the chain.
    /// </remarks>
    [Fact]
    public async Task Start_PemWithoutIntermediate_ServesWithoutIt()
    {
        (string certificate, string key) = _chain.WritePem(_deployment.FilePath("leaf.crt"), _deployment.FilePath("leaf.key"));

        ElmServer server = await _deployment.StartServerAsync(change: s => s with { TlsCertificate = certificate, TlsKey = key });
        (bool sent, bool builds) = await _chain.ConnectAsync(server.PublicUrls[0], TestContext.Current.CancellationToken);

        Assert.False(sent);
        Assert.False(builds);
    }

    /// <summary>
    /// A certificate after the server's certificate that is not a CA certificate stops the start with a reason that names its
    /// place in the file.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The chain of the PEM test; a PEM file with the server's certificate and then another server's certificate (basic
    ///   constraints without CA), and the key.
    ///
    /// Steps:
    /// - Start the server with the two files.
    ///
    /// Expected:
    /// - ServerStartException "The TLS certificate '{path}' cannot be used by a server: its certificate 2 (CN=another server) is
    ///   not a CA certificate."
    /// </remarks>
    [Fact]
    public async Task Start_PemWithNonAuthority_Throws()
    {
        (string certificate, string key) = _chain.WritePem(_deployment.FilePath("bad.crt"), _deployment.FilePath("bad.key"), _chain.Other);

        var refused = await Assert.ThrowsAsync<ServerStartException>(() =>
            _deployment.StartServerAsync(change: s => s with { TlsCertificate = certificate, TlsKey = key })
        );

        Assert.Equal(
            $"The TLS certificate '{certificate}' cannot be used by a server: its certificate 2 (CN=another server) is not a CA certificate.",
            refused.Message
        );
    }
}
