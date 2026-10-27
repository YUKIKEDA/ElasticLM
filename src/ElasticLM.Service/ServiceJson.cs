using System.Text.Json.Serialization;

namespace ElasticLM.Service;

/// <summary>
/// The settings file (design §14.8). Unknown members are errors, so a misspelt key is not silently ignored.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SettingsFile
{
    [JsonPropertyName("listen")]
    public string? Listen { get; set; }

    [JsonPropertyName("tls")]
    public TlsSection? Tls { get; set; }

    [JsonPropertyName("database")]
    public DatabaseSection? Database { get; set; }

    [JsonPropertyName("signing")]
    public SigningSection? Signing { get; set; }

    [JsonPropertyName("trust")]
    public TrustSection? Trust { get; set; }

    [JsonPropertyName("bootstrapOwner")]
    public OwnerSection? BootstrapOwner { get; set; }

    [JsonPropertyName("rateLimit")]
    public RateLimitSection? RateLimit { get; set; }

    [JsonPropertyName("audit")]
    public AuditSection? Audit { get; set; }
}

/// <summary><c>tls</c>: PEM files of the certificate and its key.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class TlsSection
{
    [JsonPropertyName("certificate")]
    public string? Certificate { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }
}

/// <summary><c>database</c>: the engine, and the file that holds the connection string.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class DatabaseSection
{
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("connectionFile")]
    public string? ConnectionFile { get; set; }
}

/// <summary><c>signing</c>: the signing key file and its certificate files.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SigningSection
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("certificates")]
    public List<string>? Certificates { get; set; }
}

/// <summary><c>trust</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class TrustSection
{
    [JsonPropertyName("rootKeys")]
    public List<string>? RootKeys { get; set; }

    [JsonPropertyName("revokedKeyIds")]
    public List<string>? RevokedKeyIds { get; set; }
}

/// <summary><c>bootstrapOwner</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class OwnerSection
{
    [JsonPropertyName("iss")]
    public string? Iss { get; set; }

    [JsonPropertyName("sub")]
    public string? Sub { get; set; }
}

/// <summary><c>rateLimit</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class RateLimitSection
{
    [JsonPropertyName("perIp")]
    public int? PerIp { get; set; }

    [JsonPropertyName("perKey")]
    public int? PerKey { get; set; }
}

/// <summary><c>audit</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class AuditSection
{
    [JsonPropertyName("retentionDays")]
    public int? RetentionDays { get; set; }
}

/// <summary>
/// <c>POST /v1/admin/api-keys</c> (design §14.5). Unknown members are ignored, so a newer client does not break.
/// </summary>
internal sealed class CreateApiKeyBody
{
    [JsonPropertyName("name")]
    [JsonRequired]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("permissions")]
    [JsonRequired]
    public List<string> Permissions { get; set; } = [];

    [JsonPropertyName("expiresInDays")]
    public int? ExpiresInDays { get; set; }
}

/// <summary>
/// An API key as the administration API shows it (design §14.5). The key itself is only in <see cref="Key"/>, at creation.
/// </summary>
internal sealed record ApiKeyItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("prefix")] string Prefix,
    [property: JsonPropertyName("permissions")] string[] Permissions,
    [property: JsonPropertyName("createdBy")] string CreatedBy,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("expiresAt")] string ExpiresAt,
    [property: JsonPropertyName("revokedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RevokedAt
)
{
    [JsonPropertyName("key")]
    public string? Key { get; init; }
}

/// <summary>A page of API keys.</summary>
internal sealed record ApiKeyPage([property: JsonPropertyName("items")] ApiKeyItem[] Items, [property: JsonPropertyName("next")] string? Next);

/// <summary>
/// An audit row as the administration API shows it (design §14.5): the stored <c>detail</c> string is returned as it is, so
/// a reader can hash the row again (design §18.2).
/// </summary>
internal sealed record AuditItem(
    [property: JsonPropertyName("n")] long N,
    [property: JsonPropertyName("ts")] string Ts,
    [property: JsonPropertyName("actorType")] string ActorType,
    [property: JsonPropertyName("actorId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ActorId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("target"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Target,
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("ip"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Ip,
    [property: JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Detail,
    [property: JsonPropertyName("prev")] string Prev,
    [property: JsonPropertyName("hash")] string Hash
);

/// <summary>A page of audit rows.</summary>
internal sealed record AuditPage([property: JsonPropertyName("items")] AuditItem[] Items, [property: JsonPropertyName("next")] long? Next);

/// <summary><c>POST /v1/admin/audit/verify</c>.</summary>
internal sealed record VerifyAnswer(
    [property: JsonPropertyName("intact")] bool Intact,
    [property: JsonPropertyName("checked")] long Checked,
    [property: JsonPropertyName("firstBroken"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? FirstBroken
);

/// <summary>A product (design §14.5, M4a2).</summary>
internal sealed record ProductItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("trialPolicyId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? TrialPolicyId,
    [property: JsonPropertyName("createdAt")] string CreatedAt
);

/// <summary>A page of products.</summary>
internal sealed record ProductPage([property: JsonPropertyName("items")] ProductItem[] Items, [property: JsonPropertyName("next")] string? Next);

/// <summary>A feature of a product (design §14.5, M4a2).</summary>
internal sealed record FeatureItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("productId")] string ProductId,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("createdAt")] string CreatedAt
);

/// <summary>A page of features.</summary>
internal sealed record FeaturePage([property: JsonPropertyName("items")] FeatureItem[] Items, [property: JsonPropertyName("next")] string? Next);

/// <summary>A policy (design §14.5, M4a2): every member, defaults included; <c>null</c> is the design's "none".</summary>
internal sealed record PolicyItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("productId")] string ProductId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("licenseKind")] string LicenseKind,
    [property: JsonPropertyName("binding")] string Binding,
    [property: JsonPropertyName("maxActivations")] long MaxActivations,
    [property: JsonPropertyName("deactivationLimit"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? DeactivationLimit,
    [property: JsonPropertyName("expirationBasis")] string ExpirationBasis,
    [property: JsonPropertyName("duration"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Duration,
    [property: JsonPropertyName("tokenLifetime")] long TokenLifetime,
    [property: JsonPropertyName("refreshWindow")] long RefreshWindow,
    [property: JsonPropertyName("gracePeriod")] long GracePeriod,
    [property: JsonPropertyName("fingerprintMatching")] string FingerprintMatching,
    [property: JsonPropertyName("cloneStrategy")] string CloneStrategy,
    [property: JsonPropertyName("floating")] string Floating,
    [property: JsonPropertyName("leaseDuration")] long LeaseDuration,
    [property: JsonPropertyName("overage"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Overage,
    [property: JsonPropertyName("borrowLimit"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? BorrowLimit,
    [property: JsonPropertyName("borrowReturnLimit")] long BorrowReturnLimit,
    [property: JsonPropertyName("clientInfoStrategy")] string ClientInfoStrategy,
    [property: JsonPropertyName("trialDuration")] long TrialDuration,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt
);

/// <summary>A page of policies.</summary>
internal sealed record PolicyPage([property: JsonPropertyName("items")] PolicyItem[] Items, [property: JsonPropertyName("next")] string? Next);

/// <summary><c>GET /v1/admin/signing-key</c> (design §14.5, decision 0.75).</summary>
internal sealed record SigningKeyAnswer(
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("uses")] string[] Uses,
    [property: JsonPropertyName("expiresAt")] string ExpiresAt,
    [property: JsonPropertyName("expiresSoon")] bool ExpiresSoon
);

/// <summary>A feature of a license as the API shows it (design §14.5).</summary>
internal sealed record LicenseFeatureItem(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("ver"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Ver,
    [property: JsonPropertyName("seats"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Seats,
    [property: JsonPropertyName("expiresAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ExpiresAt
);

/// <summary>A feature of a license as the <c>features</c> column stores it (times in UNIX seconds).</summary>
internal sealed record StoredLicenseFeature(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("ver")] string? Ver,
    [property: JsonPropertyName("seats")] long? Seats,
    [property: JsonPropertyName("expiresAt")] long? ExpiresAt
);

/// <summary>
/// A license as the administration API shows it (design §14.5, M4a3). The key itself is only in <see cref="Key"/>, at
/// creation.
/// </summary>
internal sealed record LicenseItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("productId")] string ProductId,
    [property: JsonPropertyName("policyId")] string PolicyId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("features")] LicenseFeatureItem[] Features,
    [property: JsonPropertyName("maxActivations"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? MaxActivations,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("expiresAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ExpiresAt,
    [property: JsonPropertyName("suspendedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SuspendedAt,
    [property: JsonPropertyName("revokedAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RevokedAt,
    [property: JsonPropertyName("meta")] Dictionary<string, string> Meta
)
{
    [JsonPropertyName("key")]
    public string? Key { get; init; }
}

/// <summary>A page of licenses.</summary>
internal sealed record LicensePage([property: JsonPropertyName("items")] LicenseItem[] Items, [property: JsonPropertyName("next")] string? Next);

/// <summary>
/// An error answer (RFC 9457) with the ElasticLM code.
/// </summary>
internal sealed record Problem(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("code")] string Code
);

/// <summary>
/// Source-generated JSON for the service (NativeAOT). Requests are untrusted: nullable annotations are enforced, duplicate
/// members are refused, and the depth is limited to 8 (design §14.5).
/// </summary>
[JsonSourceGenerationOptions(
    RespectNullableAnnotations = true,
    AllowDuplicateProperties = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    MaxDepth = 8
)]
[JsonSerializable(typeof(SettingsFile))]
[JsonSerializable(typeof(CreateApiKeyBody))]
[JsonSerializable(typeof(ApiKeyItem))]
[JsonSerializable(typeof(ApiKeyPage))]
[JsonSerializable(typeof(AuditPage))]
[JsonSerializable(typeof(VerifyAnswer))]
[JsonSerializable(typeof(Problem))]
[JsonSerializable(typeof(ProductItem))]
[JsonSerializable(typeof(ProductPage))]
[JsonSerializable(typeof(FeatureItem))]
[JsonSerializable(typeof(FeaturePage))]
[JsonSerializable(typeof(PolicyItem))]
[JsonSerializable(typeof(PolicyPage))]
[JsonSerializable(typeof(SigningKeyAnswer))]
[JsonSerializable(typeof(LicenseItem))]
[JsonSerializable(typeof(LicensePage))]
[JsonSerializable(typeof(StoredLicenseFeature[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class ServiceJsonContext : JsonSerializerContext;
