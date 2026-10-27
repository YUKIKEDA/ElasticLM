using System.Text.Json;
using System.Text.Json.Serialization;

namespace ElasticLM.Licensing;

/// <summary>
/// Source-generated JSON metadata for every type the library reads or writes (NativeAOT, design §0.5).
/// </summary>
// RespectNullableAnnotations turns a JSON null in a non-nullable member into a JsonException. [JsonRequired] alone lets a null
// through as a present value. Collection elements are not covered and are checked where the payloads are read.
[JsonSourceGenerationOptions(
    MaxDepth = Limits.MaxJsonDepth,
    AllowDuplicateProperties = false,
    RespectNullableAnnotations = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Disallow
)]
[JsonSerializable(typeof(JwsHeader))]
[JsonSerializable(typeof(KeyCertificatePayload))]
[JsonSerializable(typeof(LicensePayload))]
[JsonSerializable(typeof(LicenseFileEnvelope))]
[JsonSerializable(typeof(EmbeddedJwkPayload))]
[JsonSerializable(typeof(ClockState))]
[JsonSerializable(typeof(LeasePayload))]
[JsonSerializable(typeof(ProofPayload))]
[JsonSerializable(typeof(EcPublicJwk))]
[JsonSerializable(typeof(LogPayload))]
internal sealed partial class LicensingJsonContext : JsonSerializerContext;
