using System.Text.Json.Serialization;

namespace ElasticLM.Client;

// design §12.3, §12.7.1, §12.7.6.

/// <summary><c>POST /v1/leases</c>; the license key and the product only to <c>elm-service</c>.</summary>
internal sealed record AcquireRequestBody(
    [property: JsonPropertyName("feature")] string Feature,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("fingerprints")] IReadOnlyList<string>? Fingerprints,
    [property: JsonPropertyName("client")] ClientInfoBody? Client,
    [property: JsonPropertyName("app")] string? App,
    [property: JsonPropertyName("borrow")] long? Borrow,
    [property: JsonPropertyName("licenseKey")] string? LicenseKey,
    [property: JsonPropertyName("product")] string? Product
);

// design §4.4.

/// <summary>The host name and OS user name, sent only when the app enables it.</summary>
internal sealed record ClientInfoBody([property: JsonPropertyName("host")] string Host, [property: JsonPropertyName("user")] string User);

/// <summary><c>POST /v1/leases/{id}/renew</c>.</summary>
internal sealed record RenewRequestBody(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("retry")] IReadOnlyList<string>? Retry
);

// design §13.4.

/// <summary><c>POST /v1/leases/reclaim</c>.</summary>
internal sealed record ReclaimRequestBody([property: JsonPropertyName("leases")] IReadOnlyList<string> Leases);

/// <summary>The answer to an acquire or a renewal.</summary>
internal sealed class LeaseAnswerBody
{
    [JsonPropertyName("lease")]
    [JsonRequired]
    public string Lease { get; set; } = string.Empty;

    // design §12.7.1: the certificates of the service's signing key (use: lease); empty from an on-premises server.
    [JsonPropertyName("certificates")]
    public List<string>? Certificates { get; set; }
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

    // design §19: an item without a code loses its lease alone, so the member is not required.
    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

/// <summary><c>GET /v1/nonce</c>.</summary>
internal sealed class NonceAnswerBody
{
    [JsonPropertyName("nonce")]
    [JsonRequired]
    public string Nonce { get; set; } = string.Empty;
}

// design §19.

/// <summary>An RFC 9457 problem with the ElasticLM error code; only the members the client reads.</summary>
internal sealed class ProblemBody
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}

// design §13.5.

/// <summary>A server pin: the server license ID and the server key thumbprint for one server URL.</summary>
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

// design §8.6.

/// <summary>
/// Source-generated JSON for what the client reads from a server and from its pin files, which are untrusted: nullable
/// annotations are enforced, duplicate members are refused, and the depth is limited.
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
