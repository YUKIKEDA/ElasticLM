using System.Numerics;
using System.Security.Cryptography;

namespace ElasticLM.Licensing.Tests;

public sealed class EcKeysTests
{
    /// <summary>
    /// The import of a P-256 public key refuses a point that is not on the curve on every OS, so no verification key is made
    /// from it (design §5.3 T1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The public point of a generated P-256 key, and the same point with y + 1, which is not on the curve.
    ///
    /// Steps:
    /// - Import both with <see cref="ECDsa.ImportParameters"/> directly, then make a verification key from each with
    ///   <see cref="EcKeys.TryCreateVerifier"/> and <see cref="VerificationKey.TryCreate"/>.
    ///
    /// Expected:
    /// - The direct import of the off-curve point throws: <see cref="PlatformNotSupportedException"/> on Windows,
    ///   <see cref="CryptographicException"/> on Linux and macOS (dotnet/runtime release/10.0: OpenSSL's
    ///   <c>EC_KEY_check_key</c> in <c>pal_ecc_import_export.c</c>, Apple's <c>SecKeyCreateWithData</c> in <c>pal_seckey.c</c>).
    /// - Both makers return <see langword="false"/> and no key for it, and <see langword="true"/> with a key for the point on
    ///   the curve.
    /// </remarks>
    [Fact]
    public void TryCreateVerifier_PointOffTheCurve_IsRefusedOnEveryOs()
    {
        using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters good = generated.ExportParameters(false);
        ECParameters off = good;
        off.Q.Y = PlusOne(good.Q.Y!);

        using (var direct = ECDsa.Create())
        {
            Exception thrown = Assert.ThrowsAny<Exception>(() => direct.ImportParameters(off));
            Assert.True(
                OperatingSystem.IsWindows() ? thrown is PlatformNotSupportedException : thrown is CryptographicException,
                $"The import threw {thrown.GetType().FullName}."
            );
        }

        Assert.False(EcKeys.TryCreateVerifier(off, out ECDsa? offKey));
        Assert.Null(offKey);
        Assert.False(VerificationKey.TryCreate(off, out VerificationKey? offVerifier));
        Assert.Null(offVerifier);

        Assert.True(EcKeys.TryCreateVerifier(good, out ECDsa? goodKey));
        goodKey!.Dispose();
        Assert.True(VerificationKey.TryCreate(good, out VerificationKey? goodVerifier));
        goodVerifier!.Dispose();
    }

    private static byte[] PlusOne(byte[] coordinate)
    {
        BigInteger p = BigInteger.Parse(
            "0FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF",
            System.Globalization.NumberStyles.HexNumber
        );
        BigInteger value = (new BigInteger(coordinate, isUnsigned: true, isBigEndian: true) + 1) % p;
        byte[] bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        return [.. new byte[32 - bytes.Length], .. bytes];
    }
}
