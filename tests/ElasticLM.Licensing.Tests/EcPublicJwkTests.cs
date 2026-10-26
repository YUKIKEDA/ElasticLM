using System.Security.Cryptography;

namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Tests of <see cref="EcPublicJwk"/>.
/// </summary>
public sealed class EcPublicJwkTests
{
    /// <summary>
    /// The thumbprint equals the jkt that RFC 9449 gives for its example key, an independent reference for RFC 7638.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The x and y of the RFC 9449 example DPoP key, and the jkt of its figures 8, 9, and 11.
    ///
    /// Steps:
    /// - Compute the thumbprint.
    ///
    /// Expected:
    /// - "0ZcOCORZNYy-DWpqq30jZyJGHTN0d2HglBV3uiguA4I".
    /// </remarks>
    [Fact]
    public void Thumbprint_Rfc9449Example_MatchesJkt()
    {
        var jwk = new EcPublicJwk
        {
            Kty = "EC",
            Crv = "P-256",
            X = "l8tFrhx-34tV3hRICRDY9zCkDlpBhF42UQUfWVAWBFs",
            Y = "9VE4jf_Ok_o64zbTTlcuNJajHmt6v9TDVrU0CdvGRDA",
        };

        Assert.True(jwk.TryGetParameters(out ECParameters parameters));
        Assert.Equal("0ZcOCORZNYy-DWpqq30jZyJGHTN0d2HglBV3uiguA4I", EcPublicJwk.Thumbprint(parameters));
    }

    /// <summary>
    /// TryGetParameters rejects another key type, another curve, and coordinates that are not 32 bytes.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A valid P-256 JWK, changed in one member.
    ///
    /// Steps:
    /// - Call TryGetParameters.
    ///
    /// Expected:
    /// - False for each change.
    /// </remarks>
    [Theory]
    [InlineData("kty")]
    [InlineData("crv")]
    [InlineData("short-x")]
    [InlineData("padded-y")]
    public void TryGetParameters_WrongShape_ReturnsFalse(string kind)
    {
        var jwk = new EcPublicJwk
        {
            Kty = "EC",
            Crv = "P-256",
            X = "l8tFrhx-34tV3hRICRDY9zCkDlpBhF42UQUfWVAWBFs",
            Y = "9VE4jf_Ok_o64zbTTlcuNJajHmt6v9TDVrU0CdvGRDA",
        };
        switch (kind)
        {
            case "kty":
                jwk.Kty = "RSA";
                break;
            case "crv":
                jwk.Crv = "P-384";
                break;
            case "short-x":
                jwk.X = TestIssuer.B64(new byte[31]);
                break;
            default:
                jwk.Y += "=";
                break;
        }

        Assert.False(jwk.TryGetParameters(out _));
    }
}
