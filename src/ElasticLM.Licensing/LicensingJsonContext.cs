using System.Text.Json;
using System.Text.Json.Serialization;

namespace ElasticLM.Licensing;

/// <summary>
/// Source-generated JSON metadata for every type the library reads or writes (NativeAOT, design §0.5).
/// </summary>
[JsonSourceGenerationOptions(MaxDepth = Limits.MaxJsonDepth, AllowDuplicateProperties = false, ReadCommentHandling = JsonCommentHandling.Disallow)]
[JsonSerializable(typeof(JwsHeader))]
[JsonSerializable(typeof(KeyCertificatePayload))]
[JsonSerializable(typeof(LicensePayload))]
[JsonSerializable(typeof(LicenseFileEnvelope))]
[JsonSerializable(typeof(EmbeddedJwkPayload))]
[JsonSerializable(typeof(ClockState))]
internal sealed partial class LicensingJsonContext : JsonSerializerContext;
