using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ElasticLM.Licensing;

/// <summary>
/// An EC P-256 public key as a JWK with only <c>kty</c>, <c>crv</c>, <c>x</c>, and <c>y</c> (design §8.4.1).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class EcPublicJwk
{
    private const int CoordinateLength = 32;

    [JsonPropertyName("kty")]
    [JsonRequired]
    public string Kty { get; set; } = string.Empty;

    [JsonPropertyName("crv")]
    [JsonRequired]
    public string Crv { get; set; } = string.Empty;

    [JsonPropertyName("x")]
    [JsonRequired]
    public string X { get; set; } = string.Empty;

    [JsonPropertyName("y")]
    [JsonRequired]
    public string Y { get; set; } = string.Empty;

    public static EcPublicJwk FromParameters(ECParameters parameters) =>
        new()
        {
            Kty = "EC",
            Crv = "P-256",
            X = Base64UrlCodec.Encode(parameters.Q.X),
            Y = Base64UrlCodec.Encode(parameters.Q.Y),
        };

    /// <summary>
    /// Returns the RFC 7638 JWK thumbprint (SHA-256, base64url), which is the key ID (design §7.4).
    /// </summary>
    public static string Thumbprint(ECParameters parameters)
    {
        // RFC 7638 §3.2: the required members in lexicographic order, no whitespace. The coordinates are base64url, so no
        // character needs JSON escaping.
        string canonical =
            "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\""
            + Base64UrlCodec.Encode(parameters.Q.X)
            + "\",\"y\":\""
            + Base64UrlCodec.Encode(parameters.Q.Y)
            + "\"}";
        return Base64UrlCodec.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// Converts the JWK to curve parameters, checking the key type, the curve, and the coordinate lengths. Whether the
    /// point is on the curve is checked when the parameters are imported (<see cref="EcKeys.TryCreateVerifier"/>).
    /// </summary>
    public bool TryGetParameters(out ECParameters parameters)
    {
        parameters = default;
        if (Kty != "EC" || Crv != "P-256")
        {
            return false;
        }

        if (!Base64UrlCodec.TryDecode(X, out byte[] x) || !Base64UrlCodec.TryDecode(Y, out byte[] y))
        {
            return false;
        }

        if (x.Length != CoordinateLength || y.Length != CoordinateLength)
        {
            return false;
        }

        parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x, Y = y },
        };
        return true;
    }
}
