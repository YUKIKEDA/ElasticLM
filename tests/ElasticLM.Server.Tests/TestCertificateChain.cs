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
    private readonly ECDsa _otherKey;

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
        Server = Leaf("CN=127.0.0.1", _serverKey, Intermediate, notBefore, notAfter, basicConstraints: true);
        _otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Other = Leaf("CN=another server", _otherKey, Intermediate, notBefore, notAfter, basicConstraints: true);
        using ECDsa unconstrainedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Unconstrained = Leaf("CN=no basic constraints", unconstrainedKey, Intermediate, notBefore, notAfter, basicConstraints: false);
    }

    /// <summary>Gets the root CA, which only the client trusts.</summary>
    public X509Certificate2 Root { get; }

    /// <summary>Gets the intermediate CA, which the server must send.</summary>
    public X509Certificate2 Intermediate { get; }

    /// <summary>Gets the server's certificate, without its private key.</summary>
    public X509Certificate2 Server { get; }

    /// <summary>Gets another leaf certificate that the intermediate signed, which is not a CA certificate.</summary>
    public X509Certificate2 Other { get; }

    /// <summary>Gets a certificate that the intermediate signed without the basic constraints extension.</summary>
    public X509Certificate2 Unconstrained { get; }

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
    /// Connects to the server over TLS and returns whether it sent the intermediate, whether a client that trusts only the
    /// root, downloads nothing, and checks no revocation then builds the chain, and the thumbprint of the certificate it presented.
    /// </summary>
    /// <remarks>
    /// The intermediate counts as sent only when it is among the certificates the server sent, which SslStream adds to the
    /// callback's ExtraStore; on Windows the CA store holds it too, and a chain built from the store would pass without it.
    /// </remarks>
    public async Task<(bool Sent, bool Builds, string? Presented)> ConnectAsync(Uri url, CancellationToken cancellationToken)
    {
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
        return (sent, builds, thumbprint);
    }

    /// <summary>
    /// Takes the intermediate and the root out of the CA stores that .NET may have written them to on Windows, and disposes the
    /// certificates.
    /// </summary>
    /// <exception cref="CryptographicException">The user's CA store cannot be opened or changed.</exception>
    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            // The machine's store first: the user's store also lists the machine's certificates, and removing one of those
            // through the user's store is refused ("Access is denied" on the windows-latest runner, whose user is an
            // administrator, so .NET wrote to the machine's store there).
            try
            {
                RemoveFromAuthorities(StoreLocation.LocalMachine, Intermediate.Thumbprint, Root.Thumbprint);
            }
            catch (CryptographicException)
            {
                // The machine's store needs an administrator, and .NET then wrote to the user's store only.
            }

            RemoveFromAuthorities(StoreLocation.CurrentUser, Intermediate.Thumbprint, Root.Thumbprint);
        }

        Root.Dispose();
        Intermediate.Dispose();
        Server.Dispose();
        Other.Dispose();
        Unconstrained.Dispose();
        _serverKey.Dispose();
        _otherKey.Dispose();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RemoveFromAuthorities(StoreLocation location, params string[] thumbprints)
    {
        using var store = new X509Store(StoreName.CertificateAuthority, location);
        store.Open(OpenFlags.ReadWrite);
        foreach (string thumbprint in thumbprints)
        {
            X509Certificate2Collection found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
            try
            {
                store.RemoveRange(found);
            }
            finally
            {
                foreach (X509Certificate2 certificate in found)
                {
                    certificate.Dispose();
                }
            }
        }
    }

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
}
