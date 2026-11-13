using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace ElasticLM.Licensing.Tests;

/// <summary>
/// The forms of a compact JWS that carries no valid signature, for the tests of every verifier (design §8.2 rules 3 and 5,
/// §20.7 V1d14, .cursor/rules/spec-checks.mdc). Each form keeps the payload of a token that a test made, so that only the
/// check of the signature or of <c>alg</c> can refuse it.
/// </summary>
/// <remarks>
/// The forms, by name:
/// - <c>empty</c>: the signature part is empty, so the token ends with its second dot.
/// - <c>63-bytes</c>: the first 63 bytes of the token's signature.
/// - <c>64-zero-bytes</c>: 64 zero bytes, the length of a signature with a value that is none.
/// - <c>65-bytes</c>: the token's signature and one more zero byte.
/// - <c>alg-none-empty</c>: <c>alg</c> is <c>none</c> and the signature part is empty.
/// - <c>alg-hs256-signed</c>: <c>alg</c> is <c>HS256</c>, with an ES256 signature of the token's own key over the changed
///   header and the payload, so that only <c>alg</c> is wrong.
/// </remarks>
internal static class SignatureForms
{
    private const string Es256Member = "\"alg\":\"ES256\"";

    /// <summary>Gets the name of every form.</summary>
    public static IReadOnlyList<string> Names { get; } = ["empty", "63-bytes", "64-zero-bytes", "65-bytes", "alg-none-empty", "alg-hs256-signed"];

    /// <summary>Gets every form, as the data of a theory.</summary>
    public static TheoryData<string> All => [.. Names];

    /// <summary>Returns every pair of one of the given values and a form, as the data of a theory.</summary>
    public static TheoryData<string, string> Cross(params string[] values)
    {
        var data = new TheoryData<string, string>();
        foreach (string value in values)
        {
            foreach (string form in Names)
            {
                data.Add(value, form);
            }
        }

        return data;
    }

    /// <summary>Returns whether a form changes <c>alg</c>, which a verifier refuses before it looks at the signature.</summary>
    public static bool ChangesAlgorithm(string form) => form.StartsWith("alg-", StringComparison.Ordinal);

    /// <summary>
    /// Returns a token in a form. <paramref name="sign"/> signs as the token's own key does, for the form that carries
    /// a signature over a changed header.
    /// </summary>
    public static string Change(string token, string form, Func<byte[], byte[]> sign)
    {
        int last = token.LastIndexOf('.');
        byte[] signature = Base64Url.DecodeFromChars(token.AsSpan(last + 1));
        Assert.Equal(64, signature.Length);
        return form switch
        {
            "empty" => token[..(last + 1)],
            "63-bytes" => token[..(last + 1)] + Base64Url.EncodeToString(signature.AsSpan(0, 63)),
            "64-zero-bytes" => token[..(last + 1)] + Base64Url.EncodeToString(new byte[64]),
            "65-bytes" => token[..(last + 1)] + Base64Url.EncodeToString([.. signature, 0]),
            "alg-none-empty" => WithAlgorithm(token, "none") + ".",
            "alg-hs256-signed" => Signed(WithAlgorithm(token, "HS256"), sign),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, "The form is not known."),
        };
    }

    /// <summary>Returns a function that signs as ES256 does (IEEE P1363, 64 bytes) with a key.</summary>
    public static Func<byte[], byte[]> Es256(ECDsa key) =>
        input => key.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    // The signing input of the token with another alg in its header: the header again in base64url, and the payload as it was.
    private static string WithAlgorithm(string token, string algorithm)
    {
        string[] parts = token.Split('.');
        string header = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[0]));
        Assert.Contains(Es256Member, header, StringComparison.Ordinal);
        string changed = header.Replace(Es256Member, $"\"alg\":\"{algorithm}\"", StringComparison.Ordinal);
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(changed)) + "." + parts[1];
    }

    private static string Signed(string signingInput, Func<byte[], byte[]> sign) =>
        signingInput + "." + Base64Url.EncodeToString(sign(Encoding.ASCII.GetBytes(signingInput)));
}
