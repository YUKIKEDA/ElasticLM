using System.Reflection;
using System.Text.Json.Serialization;

namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Tests of the small parts: <see cref="Base64UrlCodec"/>, <see cref="ErrorCodes"/>, and <see cref="TrustStore"/>; and of the
/// reference pages for the package's public types and its token formats.
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

    /// <summary>
    /// The .NET API reference lists every public top-level type of ElasticLM.Licensing in the package's section (design §20.7,
    /// V1c2c).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - docs/guide/reference/dotnet-api.md and docs/guide.ja/reference/dotnet-api.md under the repository root.
    ///
    /// Steps:
    /// - Take the exported types of the assembly of <see cref="LicenseVerifier"/> that are not nested, without the generic arity
    ///   suffix.
    /// - Look for them in the section whose level-2 heading holds `ElasticLM.Licensing`.
    ///
    /// Expected:
    /// - The assembly has types.
    /// - On both pages, a code span in the section is each type's name exactly; the message lists every name that is not.
    /// </remarks>
    [Fact]
    public void PublicTypes_AllTopLevel_AppearInPackageSection()
    {
        IReadOnlyList<string> types = ReferencePages.TypeNames(typeof(LicenseVerifier).Assembly);
        Assert.NotEmpty(types);

        IReadOnlyList<string> missing = ReferencePages.FindMissingInSections(
            "dotnet-api.md",
            2,
            new Dictionary<string, IReadOnlyList<string>> { ["ElasticLM.Licensing"] = types }
        );

        Assert.True(missing.Count == 0, ReferencePages.Message(missing));
    }

    /// <summary>
    /// The .NET API reference gives each entry type of ElasticLM.Licensing every public method and every property of its options
    /// type (design §20.7, V1c2c).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - docs/guide/reference/dotnet-api.md and docs/guide.ja/reference/dotnet-api.md under the repository root.
    /// - The entry types and their options types: <see cref="TrustStore"/> with none, <see cref="LicenseVerifier"/> and
    ///   <see cref="LeaseVerifier"/> with <see cref="LicenseVerifierOptions"/>, which both constructors take.
    ///
    /// Steps:
    /// - Take the public methods each entry type declares, static ones included, without property accessors, event add and
    ///   remove, operators, members the compiler makes, and overrides of object's methods; and the public properties of its
    ///   options type.
    /// - Look for them in the section whose level-3 heading holds the entry type's name.
    ///
    /// Expected:
    /// - Each entry type has methods.
    /// - On both pages, a code span in each section is each name exactly; the message lists every name that is not.
    /// </remarks>
    [Fact]
    public void EntryTypes_MethodsAndOptions_AppearInTheirSections()
    {
        (Type Entry, Type? Options)[] entries =
        [
            (typeof(TrustStore), null),
            (typeof(LicenseVerifier), typeof(LicenseVerifierOptions)),
            (typeof(LeaseVerifier), typeof(LicenseVerifierOptions)),
        ];
        Assert.All(entries, entry => Assert.NotEmpty(ReferencePages.MethodNames(entry.Entry)));

        IReadOnlyList<string> missing = ReferencePages.FindMissingInSections(
            "dotnet-api.md",
            3,
            entries.ToDictionary(entry => entry.Entry.Name, entry => ReferencePages.EntryNames(entry.Entry, entry.Options))
        );

        Assert.True(missing.Count == 0, ReferencePages.Message(missing));
    }

    /// <summary>
    /// The formats reference lists every token type (design §8.6, §20.7, V1c2c).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - docs/guide/reference/formats.md and docs/guide.ja/reference/formats.md under the repository root.
    ///
    /// Steps:
    /// - Take the values of the constants of <see cref="TokenTypes"/>, such as elm-license+jws, and look for them on both pages.
    ///
    /// Expected:
    /// - <see cref="TokenTypes"/> has values.
    /// - On both pages, a code span is each value exactly; the message lists every value that is not.
    /// </remarks>
    [Fact]
    public void TokenTypes_AllValues_AppearOnFormatsPage()
    {
        string[] types =
        [
            .. typeof(TokenTypes)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral)
                .Select(field => (string)field.GetRawConstantValue()!),
        ];
        Assert.NotEmpty(types);

        IReadOnlyList<string> missing = ReferencePages.FindMissingOnPage("formats.md", types);

        Assert.True(missing.Count == 0, ReferencePages.Message(missing));
    }

    /// <summary>
    /// The formats reference lists every member of each payload in its format's section (design §7, §8, §20.7, V1c2c).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - docs/guide/reference/formats.md and docs/guide.ja/reference/formats.md under the repository root.
    /// - The payload types and the code span of the level-2 heading of their format: <see cref="KeyCertificatePayload"/> and
    ///   elm-cert+jws, <see cref="LicensePayload"/> and elm-license+jws (a server license is a license token too),
    ///   <see cref="LicenseFileEnvelope"/> and .elmlic, <see cref="LeasePayload"/> and elm-lease+jws. The offline request and
    ///   deactivation proof have no payload type: the client writes them member by member.
    /// - Only the payloads' own members count. The members of the objects inside them (the items of feat, cnf, jwk) are not
    ///   compared, since the page may name them by a path such as cnf.jkt.
    ///
    /// Steps:
    /// - Take the JSON names of the public properties of each payload type, such as lic and feat.
    /// - Look for them in the section whose level-2 heading holds the format's code span.
    ///
    /// Expected:
    /// - On both pages, a code span in each section is each JSON name exactly; the message lists every name that is not.
    /// </remarks>
    [Fact]
    public void Payloads_AllMembers_AppearInTheirFormatSections()
    {
        (string Format, Type Payload)[] payloads =
        [
            (TokenTypes.KeyCertificate, typeof(KeyCertificatePayload)),
            (TokenTypes.License, typeof(LicensePayload)),
            (".elmlic", typeof(LicenseFileEnvelope)),
            (TokenTypes.Lease, typeof(LeasePayload)),
        ];

        IReadOnlyList<string> missing = ReferencePages.FindMissingInSections(
            "formats.md",
            2,
            payloads.ToDictionary(payload => payload.Format, payload => JsonNames(payload.Payload))
        );

        Assert.True(missing.Count == 0, ReferencePages.Message(missing));
    }

    private static IReadOnlyList<string> JsonNames(Type type) =>
        [.. type.GetProperties().Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name)];
}
