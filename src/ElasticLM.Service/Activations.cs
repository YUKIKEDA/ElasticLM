using System.Text;
using System.Text.Json;
using ElasticLM.Licensing;

namespace ElasticLM.Service;

/// <summary>The host name and OS user a client sends under <c>ClientInfoStrategy: HostAndUser</c> (design §12.3, §4.4).</summary>
internal sealed record ClientInfo(string Host, string User);

/// <summary>The body of <c>POST /v1/activations</c> (design §12.3, §12.4).</summary>
internal sealed record ActivationRequest(
    string LicenseKey,
    string Product,
    IReadOnlyList<string> Fingerprints,
    long? Seq,
    IReadOnlyList<string> Retry,
    ClientInfo? Client,
    string? App
);

/// <summary>The body of a refresh or a deactivation (design §12.5, §12.6).</summary>
internal sealed record SequenceRequest(long Seq, IReadOnlyList<string> Retry);

/// <summary>The body of <c>POST /v1/trials</c> (design §12.9).</summary>
internal sealed record TrialRequest(string Product, IReadOnlyList<string> Fingerprints, IReadOnlyList<string> Retry, ClientInfo? Client, string? App);

/// <summary>How a request's sequence number stands to an activation's (design §10.4 step 3).</summary>
internal enum SequenceJudgement
{
    /// <summary>The sequence number of the last token: a regular request.</summary>
    Current,

    /// <summary>One behind, naming the request that got the last token: the answer was lost, so it is sent again.</summary>
    Retry,

    /// <summary>Anything else: another copy of the installation went ahead.</summary>
    Clone,

    /// <summary>The activation was already stopped as a clone (<c>CloneStrategy: Deny</c>).</summary>
    Blocked,
}

/// <summary>
/// The rules of the requests devices send (design §8.6, §10.4, §12.3 to §12.6, §12.9): body limits, the sequence judgement,
/// and the stored form of fingerprints.
/// </summary>
internal static class Activations
{
    public const string Malformed = "The request body is not a valid activation request.";
    public const string TrialMalformed = "The request body is not a valid trial request.";

    /// <summary>The limits of design §8.6.</summary>
    public const int MaxFingerprints = 8;

    public const int FingerprintLength = 32;
    public const int MaxClientBytes = 255;
    public const int MaxAppBytes = 64;
    public const int MaxRetry = 16;
    public const int MaxRetryLength = 64;

    /// <summary>Reads the body of an activation.</summary>
    /// <exception cref="BodyException">A member is missing, of the wrong type, or past its limit (<c>ELM_MALFORMED</c>).</exception>
    public static ActivationRequest ReadActivation(RequestBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        string key = body.RequiredText("licenseKey");
        string product = ProductCode(body, "product", Malformed);
        IReadOnlyList<string> fingerprints = Fingerprints(body, "fingerprints", Malformed);
        long? seq = body.TryGet("seq", out JsonElement seqValue) ? Sequence(body, seqValue, Malformed) : null;
        return new ActivationRequest(key, product, fingerprints, seq, Retry(body, Malformed), Client(body, Malformed), App(body, Malformed));
    }

    /// <summary>Reads the body of a refresh or a deactivation (design §12.6.1): <c>seq</c> and the optional <c>retry</c>.</summary>
    /// <exception cref="BodyException">The body is not valid (<c>ELM_MALFORMED</c>).</exception>
    public static SequenceRequest ReadSequence(RequestBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        long seq = body.TryGet("seq", out JsonElement value)
            ? Sequence(body, value, Malformed) ?? throw new BodyException(ErrorCode.Malformed, Malformed)
            : throw new BodyException(ErrorCode.Malformed, Malformed);
        return new SequenceRequest(seq, Retry(body, Malformed));
    }

    /// <summary>Reads the body of a trial.</summary>
    /// <exception cref="BodyException">The body is not valid (<c>ELM_MALFORMED</c>).</exception>
    public static TrialRequest ReadTrial(RequestBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        string product = ProductCode(body, "product", TrialMalformed);
        return new TrialRequest(
            product,
            Fingerprints(body, "fingerprints", TrialMalformed),
            Retry(body, Malformed),
            Client(body, TrialMalformed),
            App(body, TrialMalformed)
        );
    }

    /// <summary>
    /// Judges a request's sequence number against an activation (design §10.4 step 3). A sequence number left out (a
    /// reactivation without one) is a clone.
    /// </summary>
    public static SequenceJudgement Judge(ActivationRow activation, long? seq, IReadOnlyList<string> retry)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(retry);
        if (activation.BlockedAt is not null)
        {
            return SequenceJudgement.Blocked;
        }

        if (seq == activation.Seq)
        {
            return SequenceJudgement.Current;
        }

        return seq == activation.Seq - 1 && retry.Contains(activation.LastProof, StringComparer.Ordinal)
            ? SequenceJudgement.Retry
            : SequenceJudgement.Clone;
    }

    /// <summary>Returns fingerprints as stored: a JSON array of strings.</summary>
    public static string Store(IReadOnlyList<string> fingerprints) =>
        JsonSerializer.Serialize([.. fingerprints], ServiceJsonContext.Default.StringArray);

    /// <summary>Reads stored fingerprints.</summary>
    public static string[] Load(string stored) => JsonSerializer.Deserialize(stored, ServiceJsonContext.Default.StringArray) ?? [];

    // design §8.6: at most 8, each 32 lower-case hexadecimal characters (§10.3). The same one twice counts once.
    internal static List<string> Fingerprints(RequestBody body, string name, string malformed)
    {
        if (!body.TryGet(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxFingerprints)
        {
            throw new BodyException(ErrorCode.Malformed, malformed);
        }

        var fingerprints = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            string text = body.Text(item) ?? throw new BodyException(ErrorCode.Malformed, malformed);
            if (text.Length != FingerprintLength || !text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                throw new BodyException(ErrorCode.Malformed, malformed);
            }

            if (!fingerprints.Contains(text, StringComparer.Ordinal))
            {
                fingerprints.Add(text);
            }
        }

        return fingerprints;
    }

    // design §9.1: a product code, 1 to 64 of a-z, 0-9, and '-'.
    internal static string ProductCode(RequestBody body, string name, string malformed)
    {
        string product = body.RequiredText(name);
        return product.Length is 0 or > 64 || !product.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
            ? throw new BodyException(ErrorCode.Malformed, malformed)
            : product;
    }

    internal static long? Sequence(RequestBody body, JsonElement value, string malformed) =>
        body.Integer(value) is long seq ? (seq >= 0 ? seq : throw new BodyException(ErrorCode.Malformed, malformed)) : null;

    // design §10.4 step 2, §8.6: the jti of requests that got no answer, at most 16, each 1 to 64 characters.
    internal static List<string> Retry(RequestBody body, string malformed)
    {
        if (!body.TryGet("retry", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxRetry)
        {
            throw new BodyException(ErrorCode.Malformed, malformed);
        }

        var retry = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            retry.Add(
                body.Text(item) is { Length: >= 1 and <= MaxRetryLength } text ? text : throw new BodyException(ErrorCode.Malformed, malformed)
            );
        }

        return retry;
    }

    // design §12.3, §8.6: {host, user}, each at most 255 bytes in UTF-8.
    private static ClientInfo? Client(RequestBody body, string malformed)
    {
        if (!body.TryGet("client", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new BodyException(ErrorCode.Malformed, malformed);
        }

        string Part(string name) =>
            value.TryGetProperty(name, out JsonElement part) && body.Text(part) is string text && Encoding.UTF8.GetByteCount(text) <= MaxClientBytes
                ? text
                : throw new BodyException(ErrorCode.Malformed, malformed);
        return new ClientInfo(Part("host"), Part("user"));
    }

    // design §12.3, §8.6: the app's version, at most 64 bytes in UTF-8.
    private static string? App(RequestBody body, string malformed) =>
        !body.TryGet("app", out JsonElement value) ? null
        : body.Text(value) is not string text ? null
        : Encoding.UTF8.GetByteCount(text) <= MaxAppBytes ? text
        : throw new BodyException(ErrorCode.Malformed, malformed);
}
