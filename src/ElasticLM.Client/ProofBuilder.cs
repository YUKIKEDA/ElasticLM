using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ElasticLM.Licensing;

namespace ElasticLM.Client;

/// <summary>
/// Creates the <c>ELM-Proof</c> of a request (design §12.2): an <c>elm-proof+jws</c> signed by the installation key over
/// the method, the normalized target URI, the server's nonce, and the SHA-256 of the body.
/// </summary>
public static class ProofBuilder
{
    /// <summary>The name of the request header that carries the proof.</summary>
    public const string HeaderName = "ELM-Proof";

    /// <summary>
    /// Creates a proof.
    /// </summary>
    /// <param name="key">The installation key.</param>
    /// <param name="method">The request method, such as <c>POST</c>.</param>
    /// <param name="target">The absolute request URI. It is normalized (design §12.1).</param>
    /// <param name="nonce">The nonce the server gave.</param>
    /// <param name="body">The exact request body bytes; empty when there is none.</param>
    /// <param name="includePublicKey"><see langword="true"/> for a first request, when the server does not know the key yet.</param>
    /// <param name="time">The time provider, for <c>iat</c>.</param>
    /// <returns>The proof.</returns>
    /// <exception cref="ArgumentException">The method or nonce is empty, or the target is not an absolute http or https URI.</exception>
    public static string Create(
        InstallationKey key,
        string method,
        Uri target,
        string nonce,
        ReadOnlySpan<byte> body,
        bool includePublicKey,
        TimeProvider time
    ) => Create(key, method, target, nonce, body, includePublicKey, time, out _);

    /// <summary>
    /// Creates a proof and returns its <c>jti</c>, which the client names when it retries a renewal that got no answer
    /// (design §10.4).
    /// </summary>
    internal static string Create(
        InstallationKey key,
        string method,
        Uri target,
        string nonce,
        ReadOnlySpan<byte> body,
        bool includePublicKey,
        TimeProvider time,
        out string proofId
    )
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentException.ThrowIfNullOrEmpty(nonce);
        ArgumentNullException.ThrowIfNull(time);

        ProofJwk? jwk = null;
        if (includePublicKey)
        {
            ECParameters parameters = key.PublicParameters();
            jwk = new ProofJwk("EC", "P-256", Base64Url.EncodeToString(parameters.Q.X), Base64Url.EncodeToString(parameters.Q.Y));
        }

        var payload = new ProofClaims(
            1,
            time.GetUtcNow().ToUnixTimeSeconds(),
            proofId = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)),
            method,
            HttpTargetUri.Normalize(target),
            nonce,
            Base64Url.EncodeToString(SHA256.HashData(body)),
            jwk
        );
        var header = new ProofHeader("ES256", key.Thumbprint, TokenTypes.Proof);
        string signingInput =
            Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(header, ClientJsonContext.Default.ProofHeader))
            + "."
            + Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload, ClientJsonContext.Default.ProofClaims));
        return signingInput + "." + Base64Url.EncodeToString(key.Sign(Encoding.ASCII.GetBytes(signingInput)));
    }
}

internal sealed record ProofHeader(
    [property: JsonPropertyName("alg")] string Alg,
    [property: JsonPropertyName("kid")] string Kid,
    [property: JsonPropertyName("typ")] string Typ
);

internal sealed record ProofJwk(
    [property: JsonPropertyName("kty")] string Kty,
    [property: JsonPropertyName("crv")] string Crv,
    [property: JsonPropertyName("x")] string X,
    [property: JsonPropertyName("y")] string Y
);

internal sealed record ProofClaims(
    [property: JsonPropertyName("ver")] int Ver,
    [property: JsonPropertyName("iat")] long Iat,
    [property: JsonPropertyName("jti")] string Jti,
    [property: JsonPropertyName("htm")] string Htm,
    [property: JsonPropertyName("htu")] string Htu,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("bdy")] string Bdy,
    [property: JsonPropertyName("jwk")] ProofJwk? Jwk
);

/// <summary>
/// Source-generated JSON for what the client writes (NativeAOT).
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProofHeader))]
[JsonSerializable(typeof(ProofClaims))]
internal sealed partial class ClientJsonContext : JsonSerializerContext;
