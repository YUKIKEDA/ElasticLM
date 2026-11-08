using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

// Makes a test certificate authority and a TLS certificate for elm-server that the authority signs. For trying a
// certificate of your own only: in real use, the server's certificate comes from your organization's certificate
// authority.
//
//   cert <out-folder>
//
// Writes test-ca.crt (the authority's certificate), server.crt (the server's certificate), and server.key (the server's
// private key). The authority's private key is never written, so nothing else can be signed with it.
const int ValidDays = 30;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: cert <out-folder>");
    return 2;
}

try
{
    string folder = args[0];
    Directory.CreateDirectory(folder);
    DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
    DateTimeOffset notAfter = notBefore.AddDays(ValidDays);

    // The authority: it may sign certificates of servers and other end entities, but no other authority (path length 0),
    // and nothing but certificates and lists of revoked certificates.
    using ECDsa authorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var authorityRequest = new CertificateRequest("CN=ElasticLM test CA (for trying only)", authorityKey, HashAlgorithmName.SHA256);
    authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, critical: true));
    authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
    authorityRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(authorityRequest.PublicKey, critical: false));
    using X509Certificate2 authority = authorityRequest.CreateSelfSigned(notBefore, notAfter);

    // The server: the names a client connects to, the IP address 127.0.0.1 and the DNS name localhost, for TLS servers only.
    using ECDsa serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256);
    var names = new SubjectAlternativeNameBuilder();
    names.AddIpAddress(IPAddress.Loopback);
    names.AddDnsName("localhost");
    serverRequest.CertificateExtensions.Add(names.Build());
    serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
    serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
    serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
    serverRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
    using X509Certificate2 server = serverRequest.Create(authority, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));

    Write(Path.Combine(folder, "test-ca.crt"), authority.ExportCertificatePem(), secret: false);
    Write(Path.Combine(folder, "server.crt"), server.ExportCertificatePem(), secret: false);
    Write(Path.Combine(folder, "server.key"), serverKey.ExportPkcs8PrivateKeyPem(), secret: true);
    Console.WriteLine($"test CA: {Path.Combine(folder, "test-ca.crt")}");
    Console.WriteLine($"server certificate: {Path.Combine(folder, "server.crt")}");
    Console.WriteLine($"server private key: {Path.Combine(folder, "server.key")}");
    Console.WriteLine($"valid for {ValidDays} days, for 127.0.0.1 and localhost; for trying only");
    return 0;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException)
{
    // A file that is already there, or a folder that cannot be written.
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

// Writes a new file and never overwrites one. The private key is readable by its owner only on Linux and macOS.
static void Write(string path, string pem, bool secret)
{
    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
    if (secret && !OperatingSystem.IsWindows())
    {
        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    }

    using var file = new FileStream(path, options);
    using var writer = new StreamWriter(file);
    writer.Write(pem);
}
