using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ElasticLM.Issuing;

/// <summary>
/// Checks, on Windows and right after the https listener has started, that the server sends the intermediate CA certificates
/// of the operator's TLS certificate file (design §13.5, V1d3a), shared by <c>elm-server</c> and <c>elm-service</c>.
/// </summary>
/// <remarks>
/// On Windows, SChannel builds the chain it sends from the certificate stores at the first handshake of a credential and keeps
/// the result for as long as the process runs. When another program changes the same store at that moment, the result can be
/// the server's certificate alone (measured on Windows 11 with .NET 10.0.15: 8 of 1,500 starts of Kestrel while another process
/// added and removed certificates in the user's CA store; Issue #356). The other operating systems hand the chain to the TLS
/// library when the certificate is loaded, so the check does not run there.
/// </remarks>
public static class TlsChainSelfCheck
{
    /// <summary>
    /// The time allowed for the connection and the handshake together, counted on the caller's clock: the time Kestrel allows a
    /// client for a TLS handshake by default (<c>HttpsConnectionAdapterOptions.HandshakeTimeout</c>, 10 seconds).
    /// </summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Makes one TLS handshake with the server's own listener, without resuming a session and without sending anything after
    /// it, and compares the certificates the server sent with the intermediates of <paramref name="certificate"/> that belong
    /// to its chain. The handshake accepts any certificate: it reads what is sent and decides nothing about trust.
    /// </summary>
    /// <remarks>
    /// Nothing is checked, and no connection is made, on an operating system other than Windows or for a certificate without an
    /// intermediate in its chain. An intermediate counts as sent when that certificate was sent, or one with its subject and its
    /// public key that is valid at the time of <paramref name="time"/>.
    /// </remarks>
    /// <param name="certificatePath">The certificate file, which the returned sentence names.</param>
    /// <param name="certificate">The server's certificate and the intermediates of the same file.</param>
    /// <param name="listening">The endpoint the https listener is bound to; a wildcard address is reached through loopback.</param>
    /// <param name="time">The clock that counts <see cref="Limit"/> and against which a certificate sent in place of the file's is
    /// checked for its period.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>
    /// <see langword="null"/> when every such intermediate was sent or nothing was checked; otherwise why the server must not go
    /// on: the intermediates that were not sent, or that the handshake did not get as far as the server's certificates.
    /// </returns>
    public static async Task<string?> RunAsync(
        string certificatePath,
        TlsServerCertificate certificate,
        IPEndPoint listening,
        TimeProvider time,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(certificatePath);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(listening);
        ArgumentNullException.ThrowIfNull(time);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        List<X509Certificate2> expected = ExpectedIntermediates(certificate);
        if (expected.Count == 0)
        {
            return null;
        }

        IPEndPoint target = Target(listening);
        List<X509Certificate2>? sent = null;
        string? unfinished = null;
        using var limit = new CancellationTokenSource(Limit, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            using var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(target, linked.Token).ConfigureAwait(false);
            var tls = new SslStream(new NetworkStream(socket, ownsSocket: false));
            await using (tls.ConfigureAwait(false))
            {
                await tls.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions
                        {
                            // An IP address is not sent as a server name, and Kestrel's listener has one certificate for every name.
                            TargetHost = target.Address.ToString(),

                            // A full handshake: the check reads what this process sends at a first handshake, not what a
                            // session kept from an earlier connection.
                            AllowTlsResume = false,

                            // SslStream builds a chain before it calls back; this keeps that from fetching the certificates
                            // the server left out, or a revocation list, from the network.
                            CertificateChainPolicy = new X509ChainPolicy
                            {
                                DisableCertificateDownloads = true,
                                RevocationMode = X509RevocationMode.NoCheck,
                            },
                            RemoteCertificateValidationCallback = (_, _, chain, _) =>
                            {
                                // SslStream puts the certificates the server sent into the chain's ExtraStore
                                // (CertificateValidationPal.GetRemoteCertificate, dotnet/runtime v10.0.15).
                                // Copies: the chain and its certificates belong to SslStream, which disposes them.
                                sent = [.. (chain?.ChainPolicy.ExtraStore ?? []).Select(c => X509CertificateLoader.LoadCertificate(c.RawData))];
                                return true;
                            },
                        },
                        linked.Token
                    )
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            unfinished = $"did not finish within {Limit.TotalSeconds:0} seconds";
        }
        catch (SocketException e)
        {
            unfinished = $"failed (socket error {e.SocketErrorCode})";
        }
        catch (Exception e) when (e is IOException or AuthenticationException)
        {
            unfinished = "failed";
        }

        List<X509Certificate2> missing;
        try
        {
            // The servers' certificates arrive before the handshake ends; the check reads them whenever they arrived.
            if (sent is null)
            {
                return $"The check that Windows sends the intermediate CA certificates of the TLS certificate '{certificatePath}' did "
                    + $"not get an answer: the TLS handshake with {target} {unfinished ?? "failed"}.";
            }

            DateTimeOffset now = time.GetUtcNow();
            missing = [.. expected.Where(e => !sent.Exists(s => StandsFor(s, e, now)))];
        }
        finally
        {
            foreach (X509Certificate2 copy in sent ?? [])
            {
                copy.Dispose();
            }
        }

        if (missing.Count == 0)
        {
            return null;
        }

        // One sentence for both causes. A first handshake that came while the store was being changed is cured by a new process.
        // An account that can write neither store (design §13.5, V1c12 R1) gets the same result at every start, and the cure
        // is the certificate in the machine's store. Which of the two it is cannot be read from the store here: while another
        // process changed the store, a read of it right after such a start missed the intermediate in 3 of 206 starts (design
        // §20.7, the V1d3a Grill).
        string names = string.Join(" and ", missing.Select(c => $"the intermediate CA certificate {c.Subject} (thumbprint {c.Thumbprint})"));
        string these = missing.Count == 1 ? "the certificate" : "these certificates";
        return $"Windows left {names} of the TLS certificate '{certificatePath}' out of the first TLS handshake, and what that handshake "
            + $"sent stays for as long as this process runs. Start again; if this happens at every start, add {these} to the "
            + "machine's Intermediate Certification Authorities store.";
    }

    /// <summary>
    /// Returns whether a certificate that the server sent stands for an intermediate of the file: it is that certificate, or it
    /// has the same subject and public key and is valid at <paramref name="now"/>, as a renewed certificate of the same CA that
    /// Windows takes from a store is.
    /// </summary>
    /// <remarks>
    /// Such a certificate lets a client build a chain to the same root through the same CA. That the client accepts it is not
    /// decided here: the client checks the chain, with its own time and rules.
    /// </remarks>
    internal static bool StandsFor(X509Certificate2 sent, X509Certificate2 expected, DateTimeOffset now) =>
        string.Equals(sent.Thumbprint, expected.Thumbprint, StringComparison.Ordinal)
        || (
            SameNameAndKey(sent, expected)
            && sent.NotBefore.ToUniversalTime() <= now.UtcDateTime
            && now.UtcDateTime <= sent.NotAfter.ToUniversalTime()
        );

    private static bool SameNameAndKey(X509Certificate2 one, X509Certificate2 other) =>
        one.SubjectName.RawData.AsSpan().SequenceEqual(other.SubjectName.RawData)
        && one.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(other.PublicKey.ExportSubjectPublicKeyInfo());

    // The intermediates of the file that stand in the chain of the server's certificate, without a self-issued one (a root,
    // which SChannel does not send). .NET decides what goes with the certificate the same way: a chain built with the file's
    // certificates at hand (SslStreamCertificateContext.Create, dotnet/runtime v10.0.15). A CA certificate of the file that
    // does not link to the server's certificate is not expected, because nothing sends it (design §13.5, V1c12 Q3).
    private static List<X509Certificate2> ExpectedIntermediates(TlsServerCertificate certificate)
    {
        if (certificate.Intermediates.Count == 0)
        {
            return [];
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ExtraStore.AddRange(certificate.Intermediates);

        // The result of Build is not read: a chain that ends in a root this machine does not trust still names its links.
        _ = chain.Build(certificate.Certificate);
        var expected = new List<X509Certificate2>();

        // The server's own certificate is never matched: the file's other certificates are CA certificates (V1c12 Q3).
        foreach (X509ChainElement element in chain.ChainElements)
        {
            using X509Certificate2 link = element.Certificate;
            if (link.SubjectName.RawData.AsSpan().SequenceEqual(link.IssuerName.RawData))
            {
                continue;
            }

            // The chain may take the link from a store, where a renewed certificate of the same CA can stand.
            if (certificate.Intermediates.FirstOrDefault(c => SameNameAndKey(c, link)) is X509Certificate2 fromFile)
            {
                expected.Add(fromFile);
            }
        }

        return expected;
    }

    private static IPEndPoint Target(IPEndPoint listening) =>
        listening.Address.Equals(IPAddress.Any) ? new IPEndPoint(IPAddress.Loopback, listening.Port)
        : listening.Address.Equals(IPAddress.IPv6Any) ? new IPEndPoint(IPAddress.IPv6Loopback, listening.Port)
        : listening;
}
