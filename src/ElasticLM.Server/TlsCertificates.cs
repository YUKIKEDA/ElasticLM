using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ElasticLM.Client;
using ElasticLM.Issuing;

namespace ElasticLM.Server;

/// <summary>
/// The server's TLS certificate (design §13.5): the operator's PEM or PKCS#12 file, or by default a certificate the server
/// signs itself with its installation key. Clients pin the installation key, not the certificate, so the self-signed
/// certificate is made again at each start.
/// </summary>
internal static class TlsCertificates
{
    private static readonly TimeSpan SelfSignedValidity = TimeSpan.FromDays(365);

    public static X509Certificate2 SelfSigned(InstallationKey key, IReadOnlyList<Uri> publicUrls, TimeProvider time)
    {
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        foreach (string host in publicUrls.Select(u => u.IdnHost).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IPAddress.TryParse(host, out IPAddress? address))
            {
                names.AddIpAddress(address);
            }
            else if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                names.AddDnsName(host);
            }
        }

        DateTimeOffset now = time.GetUtcNow();
        using X509Certificate2 created = key.WithPrivateKey(ecdsa =>
        {
            var request = new CertificateRequest("CN=elm-server", ecdsa, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
            return request.CreateSelfSigned(now.AddDays(-1), now + SelfSignedValidity);
        });
        return ForKestrel(created);
    }

    /// <summary>
    /// Reads the operator's certificate: a PKCS#12 file (password in <see cref="ServerSettings.TlsPasswordVariable"/>), or a
    /// PEM certificate with its PEM key in <paramref name="keyPath"/>.
    /// </summary>
    /// <exception cref="ServerStartException">The files cannot be read or do not hold a certificate with its private key.</exception>
    public static X509Certificate2 FromFiles(string certificatePath, string? keyPath, string? password) =>
        TlsCertificateFiles.TryLoad(certificatePath, keyPath, password, out X509Certificate2? certificate, out string? problem)
            ? certificate!
            : throw new ServerStartException(problem!);

    private static X509Certificate2 ForKestrel(X509Certificate2 certificate) => TlsCertificateFiles.ForKestrel(certificate);
}
