using System.Security.Cryptography.X509Certificates;

namespace ElasticLM.Issuing;

/// <summary>
/// A server's TLS certificate with its private key, and the intermediate CA certificates that the server sends with it
/// (design §13.5, V1c12).
/// </summary>
public sealed class TlsServerCertificate : IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TlsServerCertificate"/> class, which owns the certificates from now on.
    /// </summary>
    /// <param name="certificate">The server's certificate with its private key.</param>
    /// <param name="intermediates">The intermediate CA certificates, in the order of the file; empty when there are none.</param>
    public TlsServerCertificate(X509Certificate2 certificate, X509Certificate2Collection intermediates)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(intermediates);
        Certificate = certificate;
        Intermediates = intermediates;
    }

    /// <summary>
    /// Gets the server's certificate with its private key.
    /// </summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>
    /// Gets the intermediate CA certificates, in the order of the file; empty for a certificate without any.
    /// </summary>
    public X509Certificate2Collection Intermediates { get; }

    /// <summary>
    /// Disposes the server's certificate and every intermediate certificate.
    /// </summary>
    public void Dispose()
    {
        Certificate.Dispose();
        foreach (X509Certificate2 intermediate in Intermediates)
        {
            intermediate.Dispose();
        }
    }
}
