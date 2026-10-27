using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ElasticLM.Issuing;

/// <summary>
/// Reads an operator's TLS certificate for Kestrel (design §13.5 and §14.1), shared by <c>elm-server</c> and <c>elm-service</c>.
/// </summary>
public static class TlsCertificateFiles
{
    /// <summary>
    /// Reads a PKCS#12 file (with its password), or a PEM certificate with its PEM key in <paramref name="keyPath"/>.
    /// </summary>
    /// <param name="certificatePath">The certificate file.</param>
    /// <param name="keyPath">The PEM key file, or <see langword="null"/> for a PKCS#12 file.</param>
    /// <param name="password">The PKCS#12 password, or <see langword="null"/>.</param>
    /// <param name="certificate">The certificate with its private key, which the caller disposes.</param>
    /// <param name="problem">Why it cannot be read, otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a certificate with its private key was read.</returns>
    public static bool TryLoad(string certificatePath, string? keyPath, string? password, out X509Certificate2? certificate, out string? problem)
    {
        ArgumentException.ThrowIfNullOrEmpty(certificatePath);
        certificate = null;
        try
        {
            if (keyPath is null)
            {
                X509Certificate2 pfx = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, password);
                if (!pfx.HasPrivateKey)
                {
                    pfx.Dispose();
                    problem = $"The TLS certificate '{certificatePath}' has no private key.";
                    return false;
                }

                certificate = pfx;
                problem = null;
                return true;
            }

            using X509Certificate2 pem = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
            certificate = ForKestrel(pem);
            problem = null;
            return true;
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            problem = $"The TLS certificate cannot be read: {e.Message}";
            return false;
        }
    }

    /// <summary>
    /// Returns a copy of a certificate whose key Windows SChannel can use.
    /// </summary>
    /// <remarks>
    /// A key held only in memory fails in Windows SChannel ("No credentials are available in the security package",
    /// dotnet/runtime#23749, dotnet/aspnetcore#40020); a PKCS#12 round trip gives a key SChannel can use. It also gives the
    /// caller its own certificate object on every platform, so one path serves all three.
    /// </remarks>
    /// <param name="certificate">The certificate with its private key.</param>
    /// <returns>The copy, which the caller disposes.</returns>
    public static X509Certificate2 ForKestrel(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        byte[] pkcs12 = certificate.Export(X509ContentType.Pkcs12);
        try
        {
            return X509CertificateLoader.LoadPkcs12(pkcs12, null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }
}
