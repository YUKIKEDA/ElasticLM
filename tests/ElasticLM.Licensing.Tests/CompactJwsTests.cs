using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Tests of <see cref="CompactJws"/> against independent references and design §8.2.
/// </summary>
public sealed class CompactJwsTests
{
    /// <summary>
    /// The ES256 check verifies the example of RFC 7515 Appendix A.3, an independent reference for the IEEE P1363 form.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The public key, encoded header, encoded payload, and signature copied from RFC 7515 Appendix A.3.
    ///
    /// Steps:
    /// - Verify the signature over "header.payload"; then flip one signature bit and verify again.
    ///
    /// Expected:
    /// - The RFC signature verifies; the changed one does not.
    /// </remarks>
    [Fact]
    public void VerifyEs256_Rfc7515AppendixA3_Verifies()
    {
        var jwk = new EcPublicJwk
        {
            Kty = "EC",
            Crv = "P-256",
            X = "f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU",
            Y = "x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0",
        };
        Assert.True(jwk.TryGetParameters(out ECParameters parameters));
        using var key = ECDsa.Create(parameters);
        byte[] input = Encoding.ASCII.GetBytes(
            "eyJhbGciOiJFUzI1NiJ9.eyJpc3MiOiJqb2UiLA0KICJleHAiOjEzMDA4MTkzODAsDQogImh0dHA6Ly9leGFtcGxlLmNvbS9pc19yb290Ijp0cnVlfQ"
        );
        Assert.True(
            Base64UrlCodec.TryDecode("DtEhU3ljbEg8L38VWAfUAqOyKAM6-Xx-F4GawxaepmXFCgfTjDxw5djxLa8ISlSApmWQxfKTUJqPP3-Kg6NU1Q", out byte[] signature)
        );

        Assert.True(CompactJws.VerifyEs256(input, signature, key));
        signature[10] ^= 0x01;
        Assert.False(CompactJws.VerifyEs256(input, signature, key));
    }

    /// <summary>
    /// A token of exactly 16 KiB passes the length check; one character more is rejected for its length (design §8.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Compact-shaped texts of 16,384 and 16,385 characters whose segments are canonical base64url but whose header is
    ///   not valid JSON.
    ///
    /// Steps:
    /// - Parse both.
    ///
    /// Expected:
    /// - Both fail, 16,384 for its header and 16,385 for its length (the error message names the limit).
    /// </remarks>
    [Fact]
    public void TryParse_LengthBoundary_RejectsOverLimitForLength()
    {
        static string Text(int length) => "AAAA." + new string('A', length - 10) + ".AAAA";

        Assert.False(CompactJws.TryParse(Text(16 * 1024), TokenTypes.License, out _, out LicenseError? atLimit));
        Assert.False(CompactJws.TryParse(Text((16 * 1024) + 1), TokenTypes.License, out _, out LicenseError? overLimit));

        Assert.DoesNotContain("limit", atLimit!.Message, StringComparison.Ordinal);
        Assert.Contains("limit", overLimit!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The embedded-key exception verifies a proof whose payload jwk matches the header kid, and rejects a mismatched kid,
    /// a jwk that is not the signer, and a point off the curve.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A key pair; proofs carrying the public key in the payload.
    ///
    /// Steps:
    /// - Parse each proof as elm-proof+jws and call TryVerifyWithEmbeddedKey.
    ///
    /// Expected:
    /// - Matching proof: true and the key returned. Others: false.
    /// </remarks>
    [Theory]
    [InlineData("match", true)]
    [InlineData("kid-mismatch", false)]
    [InlineData("other-signer", false)]
    [InlineData("off-curve", false)]
    [InlineData("no-jwk", false)]
    public void TryVerifyWithEmbeddedKey_Cases_MatchDesign(string kind, bool expected)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string kid = TestIssuer.Thumbprint(key);
        var payload = new JsonObject
        {
            ["ver"] = 1,
            ["iat"] = 1,
            ["jti"] = "j",
            ["jwk"] = TestIssuer.Jwk(key),
        };
        ECDsa signer = key;
        switch (kind)
        {
            case "kid-mismatch":
                kid = TestIssuer.Thumbprint(other);
                break;
            case "other-signer":
                signer = other;
                break;
            case "off-curve":
                JsonObject bad = TestIssuer.Jwk(key);
                bad["y"] = TestIssuer.B64(new byte[32]);
                payload["jwk"] = bad;
                kid = Base64UrlCodec.Encode(
                    SHA256.HashData(Encoding.UTF8.GetBytes($"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{bad["x"]}\",\"y\":\"{bad["y"]}\"}}"))
                );
                break;
            case "no-jwk":
                payload.Remove("jwk");
                break;
            default:
                break;
        }

        string token = TestIssuer.Sign(signer, kid, TokenTypes.Proof, payload);
        Assert.True(CompactJws.TryParse(token, TokenTypes.Proof, out CompactJws? jws, out _));

        Assert.Equal(expected, jws!.TryVerifyWithEmbeddedKey(out ECParameters found));
        if (expected)
        {
            Assert.Equal(kid, EcPublicJwk.Thumbprint(found));
        }
    }
}
