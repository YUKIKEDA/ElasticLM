using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ElasticLM.Server.Tests;

/// <summary>
/// A root CA, an intermediate CA that the root signs, and a TLS certificate for 127.0.0.1 that the intermediate signs, for the
/// tests of the intermediates that elm-server and elm-service send (design §13.5, V1c12). Linked into ElasticLM.Service.Tests.
/// </summary>
/// <remarks>
/// The certificates are valid around the real time, not the tests' fake time, because TLS checks them with the real clock.
/// On Windows, .NET writes the intermediate into the CA store of the machine or of the user so that SChannel sends it
/// (design §13.5, R1); <see cref="Dispose"/> takes it out of both again.
/// </remarks>
internal sealed class TestCertificateChain : IDisposable
{
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    private readonly ECDsa _serverKey;

    public TestCertificateChain()
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DateTimeOffset notBefore = now.AddDays(-1);
        DateTimeOffset notAfter = now.AddDays(30);

        using ECDsa rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=ElasticLM test root", rootKey, HashAlgorithmName.SHA256);
        AddAuthority(rootRequest, pathLength: 1);
        Root = rootRequest.CreateSelfSigned(notBefore, notAfter);

        using ECDsa intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intermediateRequest = new CertificateRequest("CN=ElasticLM test intermediate", intermediateKey, HashAlgorithmName.SHA256);
        AddAuthority(intermediateRequest, pathLength: 0);
        intermediateRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Root, true, false));
        using (X509Certificate2 signed = intermediateRequest.Create(Root, notBefore, notAfter, RandomNumberGenerator.GetBytes(16)))
        {
            Intermediate = signed.CopyWithPrivateKey(intermediateKey);
        }

        _serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Server = Leaf("CN=127.0.0.1", _serverKey, Intermediate, notBefore, notAfter);
        Other = Leaf("CN=another server", null, Intermediate, notBefore, notAfter);
    }

    /// <summary>Gets the root CA, which only the client trusts.</summary>
    public X509Certificate2 Root { get; }

    /// <summary>Gets the intermediate CA, which the server must send.</summary>
    public X509Certificate2 Intermediate { get; }

    /// <summary>Gets the server's certificate, without its private key.</summary>
    public X509Certificate2 Server { get; }

    /// <summary>Gets another leaf certificate that the intermediate signed, which is not a CA certificate.</summary>
    public X509Certificate2 Other { get; }

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
    /// Writes the server's certificate with its key and the intermediate as one PKCS#12 file without a password.
    /// </summary>
    public string WritePkcs12(string path)
    {
        using X509Certificate2 withKey = Server.CopyWithPrivateKey(_serverKey);
        using X509Certificate2 intermediate = X509CertificateLoader.LoadCertificate(Intermediate.RawData);
        File.WriteAllBytes(path, new X509Certificate2Collection { withKey, intermediate }.Export(X509ContentType.Pkcs12)!);
        return path;
    }

    /// <summary>
    /// Connects to the server over TLS and returns whether it sent the intermediate, and whether a client that trusts only the
    /// root, downloads nothing, and checks no revocation then builds the chain.
    /// </summary>
    /// <remarks>
    /// The intermediate counts as sent only when it is among the certificates the server sent, which SslStream adds to the
    /// callback's ExtraStore; on Windows the CA store holds it too, and a chain built from the store would pass without it.
    /// </remarks>
    public async Task<(bool Sent, bool Builds)> ConnectAsync(Uri url, CancellationToken cancellationToken)
    {
        bool sent = false;
        bool builds = false;
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
                    using var check = new X509Chain();
                    check.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    check.ChainPolicy.CustomTrustStore.Add(Root);
                    check.ChainPolicy.DisableCertificateDownloads = true;
                    check.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    check.ChainPolicy.ExtraStore.AddRange(received);
                    builds = presented is X509Certificate2 leaf && check.Build(leaf);
                    return true;
                },
            },
            cancellationToken
        );
        return (sent, builds);
    }

    /// <summary>
    /// Takes the intermediate out of the CA stores that .NET may have written it to on Windows, and disposes the certificates.
    /// </summary>
    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (StoreLocation location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
            {
                try
                {
                    using var store = new X509Store(StoreName.CertificateAuthority, location);
                    store.Open(OpenFlags.ReadWrite);
                    foreach (X509Certificate2 found in store.Certificates.Find(X509FindType.FindByThumbprint, Intermediate.Thumbprint, false))
                    {
                        store.Remove(found);
                        found.Dispose();
                    }
                }
                catch (CryptographicException)
                {
                    // The machine's store needs an administrator; .NET then wrote to the user's store only.
                }
            }
        }

        Root.Dispose();
        Intermediate.Dispose();
        Server.Dispose();
        Other.Dispose();
        _serverKey.Dispose();
    }

    private static void AddAuthority(CertificateRequest request, int pathLength)
    {
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, pathLength, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
    }

    private static X509Certificate2 Leaf(string subject, ECDsa? key, X509Certificate2 issuer, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using ECDsa own = key is null ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null!;
        var request = new CertificateRequest(subject, key ?? own, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthentication)], critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        return request.Create(issuer, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
    }
}
