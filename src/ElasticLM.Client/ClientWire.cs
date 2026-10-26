using System.Text.Json.Serialization;

namespace ElasticLM.Client;

/// <summary><c>POST /v1/leases</c> (design §12.3, §12.7.1).</summary>
internal sealed record AcquireRequestBody(
    [property: JsonPropertyName("feature")] string Feature,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("fingerprints")] IReadOnlyList<string>? Fingerprints,
    [property: JsonPropertyName("client")] ClientInfoBody? Client,
    [property: JsonPropertyName("app")] string? App
);

/// <summary>The host name and OS user name, sent only when the app enables it (design §4.4).</summary>
internal sealed record ClientInfoBody([property: JsonPropertyName("host")] string Host, [property: JsonPropertyName("user")] string User);

/// <summary><c>POST /v1/leases/{id}/renew</c>.</summary>
internal sealed record RenewRequestBody(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("retry")] IReadOnlyList<string>? Retry
);

/// <summary><c>POST /v1/leases/reclaim</c> (design §13.4).</summary>
internal sealed record ReclaimRequestBody([property: JsonPropertyName("leases")] IReadOnlyList<string> Leases);

/// <summary>The answer to an acquire or a renewal.</summary>
internal sealed class LeaseAnswerBody
{
    [JsonPropertyName("lease")]
    [JsonRequired]
    public string Lease { get; set; } = string.Empty;
}

/// <summary>The answer to a reclaim.</summary>
internal sealed class ReclaimAnswerBody
{
    [JsonPropertyName("results")]
    [JsonRequired]
    public List<ReclaimItemBody> Results { get; set; } = [];
}

/// <summary>One reclaim result.</summary>
internal sealed class ReclaimItemBody
{
    [JsonPropertyName("lease")]
    public string? Lease { get; set; }

    [JsonPropertyName("code")]
    [JsonRequired]
    public string Code { get; set; } = string.Empty;
}

/// <summary><c>GET /v1/nonce</c>.</summary>
internal sealed class NonceAnswerBody
{
    [JsonPropertyName("nonce")]
    [JsonRequired]
    public string Nonce { get; set; } = string.Empty;
}

/// <summary>An RFC 9457 problem with the §19 code; only the members the client reads.</summary>
internal sealed class ProblemBody
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}

/// <summary>A server pin (design §13.5): the server license ID and the server key thumbprint for one server URL.</summary>
internal sealed class PinFile
{
    [JsonPropertyName("url")]
    [JsonRequired]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("lic")]
    [JsonRequired]
    public string Lic { get; set; } = string.Empty;

    [JsonPropertyName("jkt")]
    [JsonRequired]
    public string Jkt { get; set; } = string.Empty;
}

/// <summary>
/// Source-generated JSON for what the client reads from a server and from its pin files, which are untrusted: nullable
/// annotations are enforced, duplicate members are refused, and the depth is limited (design §8.6).
/// </summary>
[JsonSourceGenerationOptions(
    RespectNullableAnnotations = true,
    AllowDuplicateProperties = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    MaxDepth = 8
)]
[JsonSerializable(typeof(AcquireRequestBody))]
[JsonSerializable(typeof(RenewRequestBody))]
[JsonSerializable(typeof(ReclaimRequestBody))]
[JsonSerializable(typeof(LeaseAnswerBody))]
[JsonSerializable(typeof(ReclaimAnswerBody))]
[JsonSerializable(typeof(NonceAnswerBody))]
[JsonSerializable(typeof(ProblemBody))]
[JsonSerializable(typeof(PinFile))]
internal sealed partial class ClientWireContext : JsonSerializerContext;
