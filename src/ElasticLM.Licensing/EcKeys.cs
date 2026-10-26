using System.Security.Cryptography;

namespace ElasticLM.Licensing;

/// <summary>
/// Creates ECDSA P-256 objects from public key material.
/// </summary>
internal static class EcKeys
{
    /// <summary>
    /// Creates an <see cref="ECDsa"/> for verification. Import fails for a point that is not on the curve, so a
    /// <see langword="false"/> result also covers invalid points.
    /// </summary>
    public static bool TryCreateVerifier(ECParameters parameters, out ECDsa? key)
    {
        key = null;
        ECDsa candidate = ECDsa.Create();
        try
        {
            candidate.ImportParameters(parameters);
        }
        catch (CryptographicException)
        {
            candidate.Dispose();
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            // Windows CNG on .NET 10.0.13 reports a point that is not on the curve this way, wrapping a CryptographicException.
            candidate.Dispose();
            return false;
        }

        key = candidate;
        return true;
    }

    /// <summary>
    /// Reads a P-256 public key from a PEM text with exactly one block holding a SubjectPublicKeyInfo (<c>PUBLIC KEY</c>).
    /// </summary>
    public static bool TryReadPublicKeyPem(string pem, out ECParameters parameters)
    {
        parameters = default;

        // One PEM block, read as SubjectPublicKeyInfo. ImportFromPem would also read PRIVATE KEY and EC PRIVATE KEY blocks, so a
        // root's private key passed by mistake would be accepted silently (.cursor/rules/security.mdc). The DER of a private key
        // is not a SubjectPublicKeyInfo, so ImportSubjectPublicKeyInfo rejects it whatever its label says
        // (Create_InvalidRoots_Throws).
        if (!PemEncoding.TryFind(pem, out PemFields fields))
        {
            return false;
        }

        if (PemEncoding.TryFind(pem.AsSpan(fields.Location.End.GetOffset(pem.Length)), out _))
        {
            return false;
        }

        byte[] der = new byte[fields.DecodedDataLength];
        if (!Convert.TryFromBase64Chars(pem.AsSpan()[fields.Base64Data], der, out int written) || written != der.Length)
        {
            return false;
        }

        using ECDsa key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(der, out int read);
            if (read != der.Length)
            {
                return false;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }

        ECParameters exported = key.ExportParameters(includePrivateParameters: false);
        if (!IsP256(exported.Curve))
        {
            return false;
        }

        parameters = exported;
        return true;
    }

    /// <summary>
    /// Returns whether a curve is NIST P-256. Platforms fill either the OID value or the friendly name.
    /// </summary>
    public static bool IsP256(ECCurve curve) =>
        curve.IsNamed && (curve.Oid.Value == "1.2.840.10045.3.1.7" || curve.Oid.FriendlyName is "nistP256" or "ECDSA_P256");
}
