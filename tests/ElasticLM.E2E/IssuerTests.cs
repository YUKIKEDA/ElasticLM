using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.E2E;

/// <summary>
/// The issuer's offline flow with the published elm (design §16): keys, a certificate, a license, and its checks, and an
/// app that verifies the license with ElasticLM.Licensing (design §11).
/// </summary>
public sealed class IssuerTests
{
    /// <summary>
    /// A license issued with the published elm verifies with elm and with the library, and a changed byte breaks it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A vendor folder whose root key, signing key, and certificate (use license) the published elm made.
    ///
    /// Steps:
    /// - elm license issue (unbound, product acme-cad, feature pro ver &gt;=2.0 &lt;3.0, 30 days); elm license inspect; elm
    ///   license verify --json for acme-cad pro 2.5; LicenseVerifier with the root key for acme-cad pro 2.5 and for acme-cam;
    ///   change one byte of the license's payload and verify with elm and the library again.
    ///
    /// Expected:
    /// - issue and inspect exit 0, inspect shows the license ID and NOT VERIFIED; verify exits 0 with valid true; the library
    ///   gives Valid for acme-cad and ELM_WRONG_PRODUCT for acme-cam; after the change elm exits 1 and the library gives
    ///   ELM_BAD_SIGNATURE.
    /// </remarks>
    [Fact]
    public async Task IssuedLicense_VerifiesWithElmAndTheLibrary()
    {
        await using Vendor vendor = await Vendor.CreateAsync("license");
        string expires = DateTimeOffset.UtcNow.AddDays(30).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        await vendor.ElmOkAsync(
            "license",
            "issue",
            "--signing-key",
            vendor.SigningKey,
            "--certificate",
            vendor.Certificate,
            "--product",
            "acme-cad",
            "--license-id",
            "e2e-issued",
            "--feature",
            "pro;ver=>=2.0 <3.0",
            "--expires",
            expires,
            "--trust",
            vendor.RootPublicKey,
            "--out",
            "issued.elmlic"
        );

        string inspected = await vendor.ElmOkAsync("license", "inspect", "issued.elmlic");
        Assert.Contains("\"lic\": \"e2e-issued\"", inspected, StringComparison.Ordinal);
        Assert.Contains("NOT VERIFIED", inspected, StringComparison.Ordinal);

        string[] verify =
        [
            "license",
            "verify",
            "issued.elmlic",
            "--trust",
            vendor.RootPublicKey,
            "--product",
            "acme-cad",
            "--feature",
            "pro",
            "--version",
            "2.5",
        ];
        using (JsonDocument json = JsonDocument.Parse(await vendor.ElmOkAsync([.. verify, "--json"])))
        {
            Assert.True(json.RootElement.GetProperty("valid").GetBoolean());
        }

        var verifier = new LicenseVerifier(
            TrustStore.Create([await File.ReadAllTextAsync(vendor.RootPublicKey, TestContext.Current.CancellationToken)], []),
            TimeProvider.System,
            StatelessClockGuard.Instance
        );
        byte[] file = await File.ReadAllBytesAsync(vendor.File("issued.elmlic"), TestContext.Current.CancellationToken);
        Assert.IsType<LicenseVerificationResult.Valid>(verifier.Verify(file, Requirement("acme-cad")));
        Assert.Equal(ErrorCode.WrongProduct, Invalid(verifier.Verify(file, Requirement("acme-cam"))));

        // One byte of the license's payload, the part after its first dot; the file also holds the key certificate.
        int index = Array.IndexOf(file, (byte)'.') + 5;
        file[index] = file[index] == (byte)'A' ? (byte)'B' : (byte)'A';
        await File.WriteAllBytesAsync(vendor.File("issued.elmlic"), file, TestContext.Current.CancellationToken);
        Assert.Equal(1, (await vendor.ElmAsync(verify)).Code);
        Assert.Equal(ErrorCode.BadSignature, Invalid(verifier.Verify(file, Requirement("acme-cad"))));
    }

    private static LicenseRequirement Requirement(string product) => new(product, LicenseKind.Standard, "pro", "2.5", null);

    private static ErrorCode Invalid(LicenseVerificationResult result) => Assert.IsType<LicenseVerificationResult.Invalid>(result).Error.Code;
}
