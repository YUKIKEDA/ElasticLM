using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ElasticLM.Licensing;

namespace ElasticLM.Issuing;

/// <summary>
/// Writes JWS compact tokens in the restricted form of design §8.2: a header of exactly <c>alg</c>, <c>kid</c>, and
/// <c>typ</c>, ES256 in IEEE P1363 form.
/// </summary>
internal static class JwsWriter
{
    public static string Sign<T>(ECDsa key, string typ, T payload, JsonTypeInfo<T> typeInfo)
    {
        var header = new JwsHeader
        {
            Alg = CompactJws.Es256,
            Kid = KeyFiles.KeyId(key),
            Typ = typ,
        };
        string signingInput =
            Base64UrlCodec.Encode(JsonSerializer.SerializeToUtf8Bytes(header, LicensingJsonContext.Default.JwsHeader))
            + "."
            + Base64UrlCodec.Encode(JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo));
        byte[] signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );
        return signingInput + "." + Base64UrlCodec.Encode(signature);
    }

    public static string NewTokenId() => Base64UrlCodec.Encode(RandomNumberGenerator.GetBytes(16));
}
