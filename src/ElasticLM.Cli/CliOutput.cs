using System.Text.Json;
using System.Text.Json.Serialization;

namespace ElasticLM.Cli;

internal sealed record KeyOutput(string KeyId, string? PrivateKeyFile, string? PublicKeyFile);

internal sealed record CertificateOutput(
    string File,
    string SubjectKeyId,
    string RootKeyId,
    string[] Uses,
    DateTimeOffset NotBefore,
    DateTimeOffset ExpiresAt
);

internal sealed record IssueOutput(string File, string LicenseId, string TokenId, string[] Warnings);

internal sealed record TokenView(JsonElement Header, JsonElement Payload);

internal sealed record InspectOutput(bool Verified, TokenView License, TokenView[] Certificates);

internal sealed record VerifyOutput(
    bool Valid,
    string? Code,
    string? Message,
    string? LicenseId,
    string? ProductId,
    string? Kind,
    DateTimeOffset? ExpiresAt,
    bool? InGracePeriod,
    string[]? Features
);

/// <summary>
/// Source-generated JSON for <c>--json</c> output (NativeAOT).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(KeyOutput))]
[JsonSerializable(typeof(CertificateOutput))]
[JsonSerializable(typeof(IssueOutput))]
[JsonSerializable(typeof(InspectOutput))]
[JsonSerializable(typeof(VerifyOutput))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
