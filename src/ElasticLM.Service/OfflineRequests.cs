using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.Service;

/// <summary>The claims of an <c>elm-actreq+jws</c> (design §8.4.4): an activation (<see cref="LicenseKey"/> and
/// <see cref="Product"/>) or a refresh (<see cref="ActivationId"/>).</summary>
internal sealed record OfflineActivation(
    bool Refresh,
    string? LicenseKey,
    string? Product,
    Guid? ActivationId,
    long? Seq,
    IReadOnlyList<string> Retry,
    IReadOnlyList<string> Fingerprints,
    string Nonce,
    long IssuedAt,
    long ExpiresAt
);

/// <summary>The claims of an <c>elm-deact+jws</c> (design §8.4.5).</summary>
internal sealed record OfflineDeactivation(Guid ActivationId, long Seq, IReadOnlyList<string> Retry, long IssuedAt);

/// <summary>
/// The rules of offline request files (design §8.4.4, §8.4.5, §12.8.4): their claims, the request's lifetime, and how far
/// ahead of the service's clock a request may be made.
/// </summary>
internal static class OfflineRequests
{
    public const string Malformed = "The file is not a valid offline request.";

    /// <summary>The lifetime of a request: <c>exp</c> is <c>iat</c> plus 7 days exactly.</summary>
    public const long Lifetime = 7 * 24 * 60 * 60;

    /// <summary>The length of a 128-bit nonce in base64url.</summary>
    public const int NonceLength = 22;

    /// <summary>How far ahead of the service's clock a request may be made: the default ClockSkew of design §11.3.</summary>
    public static readonly TimeSpan FutureSkew = TimeSpan.FromMinutes(5);

    // design §8.3: times are seconds since 1970, up to the end of year 9999.
    private const long MaxTime = 253_402_300_799;

    /// <summary>Reads the claims of an activation request.</summary>
    /// <exception cref="BodyException">A claim is missing, of the wrong type, or past its limit (<c>ELM_MALFORMED</c>).</exception>
    public static OfflineActivation ReadActivation(ReadOnlyMemory<byte> payload)
    {
        using RequestBody body = RequestBody.Parse(payload.ToArray(), Malformed);
        Version(body);
        bool refresh = body.RequiredText("op") switch
        {
            "activate" => false,
            "refresh" => true,
            _ => throw new BodyException(ErrorCode.Malformed, Malformed),
        };
        string? key = refresh ? null : body.RequiredText("key");
        string? product = refresh ? null : Activations.ProductCode(body, "prd", Malformed);
        Guid? activation = refresh ? ActivationId(body) : null;
        long? seq = body.TryGet("seq", out JsonElement seqValue) ? Activations.Sequence(body, seqValue, Malformed) : null;
        if (refresh && seq is null)
        {
            throw new BodyException(ErrorCode.Malformed, Malformed);
        }

        IReadOnlyList<string> retry = Activations.Retry(body, Malformed);
        IReadOnlyList<string> fingerprints = Activations.Fingerprints(body, "fp", Malformed);
        string nonce = body.RequiredText("nonce");
        if (nonce.Length != NonceLength || !nonce.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
        {
            throw new BodyException(ErrorCode.Malformed, Malformed);
        }

        long issued = Time(body, "iat");
        long expires = Time(body, "exp");
        return expires != issued + Lifetime
            ? throw new BodyException(ErrorCode.Malformed, Malformed)
            : new OfflineActivation(refresh, key, product, activation, seq, retry, fingerprints, nonce, issued, expires);
    }

    /// <summary>Reads the activation ID of a deactivation proof, before its signature is checked with that activation's key.</summary>
    /// <exception cref="BodyException">The payload or its <c>act</c> is not valid (<c>ELM_MALFORMED</c>).</exception>
    public static Guid ReadDeactivationTarget(ReadOnlyMemory<byte> payload)
    {
        using RequestBody body = RequestBody.Parse(payload.ToArray(), Malformed);
        return ActivationId(body);
    }

    /// <summary>Reads the claims of a deactivation proof.</summary>
    /// <exception cref="BodyException">A claim is not valid (<c>ELM_MALFORMED</c>).</exception>
    public static OfflineDeactivation ReadDeactivation(ReadOnlyMemory<byte> payload)
    {
        using RequestBody body = RequestBody.Parse(payload.ToArray(), Malformed);
        Version(body);
        Guid activation = ActivationId(body);
        long seq = body.TryGet("seq", out JsonElement value)
            ? Activations.Sequence(body, value, Malformed) ?? throw new BodyException(ErrorCode.Malformed, Malformed)
            : throw new BodyException(ErrorCode.Malformed, Malformed);
        return new OfflineDeactivation(activation, seq, Activations.Retry(body, Malformed), Time(body, "iat"));
    }

    // design §8.3: ver is 1.
    private static void Version(RequestBody body)
    {
        if (!body.TryGet("ver", out JsonElement value) || body.Integer(value) != 1)
        {
            throw new BodyException(ErrorCode.Malformed, Malformed);
        }
    }

    private static Guid ActivationId(RequestBody body) =>
        Guid.TryParseExact(body.RequiredText("act"), "D", out Guid id) ? id : throw new BodyException(ErrorCode.Malformed, Malformed);

    // design §8.6: a time in seconds since 1970, 0 or more.
    private static long Time(RequestBody body, string name) =>
        body.TryGet(name, out JsonElement value) && body.Integer(value) is long seconds && seconds is >= 0 and <= MaxTime
            ? seconds
            : throw new BodyException(ErrorCode.Malformed, Malformed);
}
