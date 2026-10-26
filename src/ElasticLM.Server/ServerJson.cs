using System.Text.Json.Serialization;

namespace ElasticLM.Server;

/// <summary>
/// <c>POST /v1/leases</c> (design §12.3, §12.7.1).
/// </summary>
internal sealed class AcquireBody
{
    [JsonPropertyName("feature")]
    [JsonRequired]
    public string Feature { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("count")]
    [JsonRequired]
    public int Count { get; set; }

    [JsonPropertyName("borrow")]
    public long? Borrow { get; set; }

    [JsonPropertyName("fingerprints")]
    public List<string>? Fingerprints { get; set; }

    [JsonPropertyName("client")]
    public ClientInfoBody? Client { get; set; }

    [JsonPropertyName("app")]
    public string? App { get; set; }
}

/// <summary>
/// The host name and OS user name a client sends under <c>ClientInfoStrategy: HostAndUser</c> (design §12.3).
/// </summary>
internal sealed class ClientInfoBody
{
    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }
}

/// <summary>
/// <c>POST /v1/leases/{id}/renew</c>.
/// </summary>
internal sealed class RenewBody
{
    [JsonPropertyName("seq")]
    [JsonRequired]
    public long Seq { get; set; }

    /// <summary>Gets or sets the jti of the client's earlier attempts that got no answer (design §10.4).</summary>
    [JsonPropertyName("retry")]
    public List<string>? Retry { get; set; }
}

/// <summary>
/// <c>POST /v1/leases/reclaim</c> (design §13.4).
/// </summary>
internal sealed class ReclaimBody
{
    [JsonPropertyName("leases")]
    [JsonRequired]
    public List<string> Leases { get; set; } = [];
}

/// <summary>
/// The answer to an acquire or a renewal (design §12.7.1). On premises the certificates are empty.
/// </summary>
internal sealed record LeaseAnswer(
    [property: JsonPropertyName("lease")] string Lease,
    [property: JsonPropertyName("certificates")] string[] Certificates
);

/// <summary>
/// The answer to a reclaim: one result per token, in order.
/// </summary>
internal sealed record ReclaimAnswer([property: JsonPropertyName("results")] ReclaimItem[] Results);

/// <summary>
/// One reclaim result.
/// </summary>
internal sealed record ReclaimItem([property: JsonPropertyName("lease")] string? Lease, [property: JsonPropertyName("code")] string Code);

/// <summary>
/// <c>GET /v1/nonce</c>.
/// </summary>
internal sealed record NonceAnswer([property: JsonPropertyName("nonce")] string Nonce);

/// <summary>
/// An RFC 9457 problem with the §19 code (design §12.1).
/// </summary>
internal sealed record Problem(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("code")] string Code
);

/// <summary>
/// The settings file (design §13.8). Unknown members are errors, so a misspelt key is not silently ignored.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SettingsFile
{
    [JsonPropertyName("product")]
    public string? Product { get; set; }

    [JsonPropertyName("listen")]
    public string? Listen { get; set; }

    [JsonPropertyName("publicUrls")]
    public List<string>? PublicUrls { get; set; }

    [JsonPropertyName("rateLimit")]
    public RateLimitSection? RateLimit { get; set; }

    [JsonPropertyName("license")]
    public string? License { get; set; }

    [JsonPropertyName("trust")]
    public TrustSection? Trust { get; set; }

    [JsonPropertyName("tls")]
    public TlsSection? Tls { get; set; }

    [JsonPropertyName("allow")]
    public List<string>? Allow { get; set; }

    [JsonPropertyName("deny")]
    public List<string>? Deny { get; set; }

    [JsonPropertyName("dataDirectory")]
    public string? DataDirectory { get; set; }
}

/// <summary>
/// <c>rateLimit</c>.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class RateLimitSection
{
    [JsonPropertyName("perIp")]
    public int? PerIp { get; set; }
}

/// <summary>
/// <c>trust</c>.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class TrustSection
{
    [JsonPropertyName("rootKeys")]
    public List<string>? RootKeys { get; set; }

    [JsonPropertyName("revokedKeyIds")]
    public List<string>? RevokedKeyIds { get; set; }
}

/// <summary>
/// <c>tls</c>.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class TlsSection
{
    [JsonPropertyName("certificate")]
    public string? Certificate { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }
}

/// <summary>
/// Source-generated JSON for the server (NativeAOT). Requests are untrusted: nullable annotations are enforced, duplicate
/// members are refused, and the depth is limited (design §8.6).
/// </summary>
[JsonSourceGenerationOptions(
    RespectNullableAnnotations = true,
    AllowDuplicateProperties = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    MaxDepth = 8
)]
[JsonSerializable(typeof(AcquireBody))]
[JsonSerializable(typeof(RenewBody))]
[JsonSerializable(typeof(ReclaimBody))]
[JsonSerializable(typeof(LeaseAnswer))]
[JsonSerializable(typeof(ReclaimAnswer))]
[JsonSerializable(typeof(NonceAnswer))]
[JsonSerializable(typeof(Problem))]
[JsonSerializable(typeof(SettingsFile))]
internal sealed partial class ServerJsonContext : JsonSerializerContext;
