namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Tests of the small parts: <see cref="Base64UrlCodec"/>, <see cref="ErrorCodes"/>, and <see cref="TrustStore"/>.
/// </summary>
public sealed class SmallPartsTests
{
    /// <summary>
    /// The decoder accepts only canonical unpadded base64url.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Texts: canonical, padded, standard-alphabet characters, whitespace, a non-zero unused trailing bit, length 4n+1.
    ///
    /// Steps:
    /// - Decode each.
    ///
    /// Expected:
    /// - Only the canonical text decodes.
    /// </remarks>
    [Theory]
    [InlineData("AQI", true)]
    [InlineData("AQI=", false)]
    [InlineData("A+I", false)]
    [InlineData("A/I", false)]
    [InlineData("AQ I", false)]
    [InlineData("AQJ", false)]
    [InlineData("AQIDB", false)]
    public void TryDecode_Forms_AcceptsCanonicalOnly(string text, bool valid)
    {
        Assert.Equal(valid, Base64UrlCodec.TryDecode(text, out _));
    }

    /// <summary>
    /// Every defined error code has a distinct ELM_ wire name.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - All values of <see cref="ErrorCode"/>.
    ///
    /// Steps:
    /// - Convert each to its wire name.
    ///
    /// Expected:
    /// - No exception, every name starts with ELM_, and all names differ; an undefined value throws.
    /// </remarks>
    [Fact]
    public void ToWireName_AllCodes_AreDistinct()
    {
        string[] names = [.. Enum.GetValues<ErrorCode>().Select(ErrorCodes.ToWireName)];

        Assert.All(names, n => Assert.StartsWith("ELM_", n, StringComparison.Ordinal));
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => ErrorCodes.ToWireName((ErrorCode)12345));
    }

    /// <summary>
    /// The trust store refuses no keys, keys that are not P-256 public keys in one PEM PUBLIC KEY block, private keys, and a
    /// key given twice.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty list, a non-PEM string, a P-384 key, a P-256 private key as PKCS#8 and as SEC1 PEM, two PUBLIC KEY blocks in
    ///   one text, and the same public key twice.
    ///
    /// Steps:
    /// - Create a trust store from each.
    ///
    /// Expected:
    /// - ArgumentException each time; the public key alone is accepted.
    /// </remarks>
    [Fact]
    public void Create_InvalidRoots_Throws()
    {
        using var p384 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP384);
        using var p256 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        string publicPem = p256.ExportSubjectPublicKeyInfoPem();

        Assert.Throws<ArgumentException>(() => TrustStore.Create([], []));
        Assert.Throws<ArgumentException>(() => TrustStore.Create(["not a key"], []));
        Assert.Throws<ArgumentException>(() => TrustStore.Create([p384.ExportSubjectPublicKeyInfoPem()], []));
        Assert.Throws<ArgumentException>(() => TrustStore.Create([p256.ExportPkcs8PrivateKeyPem()], []));
        Assert.Throws<ArgumentException>(() => TrustStore.Create([p256.ExportECPrivateKeyPem()], []));
        Assert.Throws<ArgumentException>(() => TrustStore.Create([publicPem + "\n" + p384.ExportSubjectPublicKeyInfoPem()], []));
        Assert.Throws<ArgumentException>(() => TrustStore.Create([publicPem, publicPem], []));
        Assert.Single(TrustStore.Create([publicPem], []).RootKeyIds);
    }
}
