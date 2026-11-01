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
    /// The error code reference lists every error code (design §20.7, V1c2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - docs/guide/reference/error-codes.md and docs/guide.ja/reference/error-codes.md under the repository root.
    ///
    /// Steps:
    /// - Convert every value of <see cref="ErrorCode"/> to its wire name and look for it in code on both pages.
    ///
    /// Expected:
    /// - Every wire name, such as ELM_NO_SEATS, appears in a code span or a code block on both pages; the message lists every
    ///   one that does not.
    /// </remarks>
    [Fact]
    public void ToWireName_AllCodes_AppearOnReferencePage()
    {
        IReadOnlyList<string> missing = ReferencePages.FindMissing("error-codes.md", Enum.GetValues<ErrorCode>().Select(ErrorCodes.ToWireName));

        Assert.True(missing.Count == 0, ReferencePages.Message(missing));
    }

    /// <summary>
    /// The error code reference gives each error code its number, which never changes once published (design §20.7, V1c2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - docs/guide/reference/error-codes.md and docs/guide.ja/reference/error-codes.md under the repository root.
    ///
    /// Steps:
    /// - For every value of <see cref="ErrorCode"/>, look for a table row that holds its wire name and its number.
    ///
    /// Expected:
    /// - On both pages, one row holds each pair, such as ELM_NO_SEATS and 40; the message lists every pair that no row holds.
    /// </remarks>
    [Fact]
    public void ToWireName_AllCodes_ListedWithTheirNumbers()
    {
        IReadOnlyList<string> missing = ReferencePages.FindMissingRows(
            "error-codes.md",
            Enum.GetValues<ErrorCode>().Select(code => (ErrorCodes.ToWireName(code), (int)code))
        );

        Assert.True(missing.Count == 0, ReferencePages.Message(missing));
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
