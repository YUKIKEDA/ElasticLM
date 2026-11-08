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
        (bool sent, bool builds, _) = await _chain.ConnectAsync(server.PublicUrls[0], TestContext.Current.CancellationToken);

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
        (string certificate, string key) = _chain.WritePem(
            _deployment.FilePath("root.crt"),
            _deployment.FilePath("root.key"),
            _chain.Intermediate,
            _chain.Root
        );

        ElmServer server = await _deployment.StartServerAsync(change: s => s with { TlsCertificate = certificate, TlsKey = key });
        (bool sent, bool builds, _) = await _chain.ConnectAsync(server.PublicUrls[0], TestContext.Current.CancellationToken);

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
        string pfx = _chain.WritePkcs12(_deployment.FilePath("chain.pfx"), [_chain.Intermediate]);

        ElmServer server = await _deployment.StartServerAsync(change: s => s with { TlsCertificate = pfx, TlsKey = null });
        (bool sent, bool builds, _) = await _chain.ConnectAsync(server.PublicUrls[0], TestContext.Current.CancellationToken);

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
        string pfx = _chain.WritePkcs12(_deployment.FilePath("keys.pfx"), [_chain.Intermediate], otherWithKey: true);

        ElmServer server = await _deployment.StartServerAsync(change: s => s with { TlsCertificate = pfx, TlsKey = null });
        (bool sent, _, string? presented) = await _chain.ConnectAsync(server.PublicUrls[0], TestContext.Current.CancellationToken);

        Assert.Equal(_chain.Server.Thumbprint, presented);
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
        (string certificate, string key) = _chain.WritePem(_deployment.FilePath("leaf.crt"), _deployment.FilePath("leaf.key"));

        ElmServer server = await _deployment.StartServerAsync(change: s => s with { TlsCertificate = certificate, TlsKey = key });
        (bool sent, bool builds, string? presented) = await _chain.ConnectAsync(server.PublicUrls[0], TestContext.Current.CancellationToken);

        Assert.Equal(_chain.Server.Thumbprint, presented);
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
        string key = _chain.WritePem(_deployment.FilePath("other.crt"), _deployment.FilePath("other.key"), _chain.Other).Key;
        string other = _deployment.FilePath("other.crt");
        string unconstrained = _chain
            .WritePem(_deployment.FilePath("unconstrained.crt"), _deployment.FilePath("unconstrained.key"), _chain.Unconstrained)
            .Certificate;
        string pfx = _chain.WritePkcs12(_deployment.FilePath("other.pfx"), [_chain.Intermediate, _chain.Other]);

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
        string key = _chain.WritePem(_deployment.FilePath("none.crt"), _deployment.FilePath("none.key")).Key;
        string certificate = _deployment.FilePath("none.crt");
        File.Copy(key, certificate, overwrite: true);

        var refused = await Assert.ThrowsAsync<ServerStartException>(() =>
            _deployment.StartServerAsync(change: s => s with { TlsCertificate = certificate, TlsKey = key })
        );

        Assert.StartsWith("The TLS certificate cannot be read: ", refused.Message, StringComparison.Ordinal);
    }
}
