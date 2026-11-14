using System.Text.Json.Serialization;

namespace ElasticLM.Licensing;

// design §8.2.

/// <summary>
/// The JWS protected header: exactly <c>alg</c>, <c>kid</c>, and <c>typ</c>.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class JwsHeader
{
    [JsonPropertyName("alg")]
    [JsonRequired]
    public string Alg { get; set; } = string.Empty;

    [JsonPropertyName("kid")]
    [JsonRequired]
    public string Kid { get; set; } = string.Empty;

    [JsonPropertyName("typ")]
    [JsonRequired]
    public string Typ { get; set; } = string.Empty;
}

// design §8.4.1.

/// <summary>
/// The payload of a key certificate (<c>elm-cert+jws</c>).
/// </summary>
internal sealed class KeyCertificatePayload
{
    [JsonPropertyName("ver")]
    [JsonRequired]
    public int Ver { get; set; }

    [JsonPropertyName("iat")]
    [JsonRequired]
    public long Iat { get; set; }

    [JsonPropertyName("jti")]
    [JsonRequired]
    public string Jti { get; set; } = string.Empty;

    [JsonPropertyName("sub")]
    [JsonRequired]
    public string Sub { get; set; } = string.Empty;

    [JsonPropertyName("jwk")]
    [JsonRequired]
    public EcPublicJwk Jwk { get; set; } = new();

    [JsonPropertyName("use")]
    [JsonRequired]
    public List<string> Use { get; set; } = [];

    [JsonPropertyName("nbf")]
    [JsonRequired]
    public long Nbf { get; set; }

    [JsonPropertyName("exp")]
    [JsonRequired]
    public long Exp { get; set; }
}

// design §8.4.2.

/// <summary>
/// A feature in a license token.
/// </summary>
internal sealed class FeatureClaim
{
    [JsonPropertyName("id")]
    [JsonRequired]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("ver")]
    public string? Ver { get; set; }

    [JsonPropertyName("seats")]
    public int? Seats { get; set; }

    [JsonPropertyName("exp")]
    public long? Exp { get; set; }
}

// design §8.4.2.

/// <summary>
/// The confirmation claim that binds a token to an installation key.
/// </summary>
internal sealed class ConfirmationClaim
{
    [JsonPropertyName("jkt")]
    [JsonRequired]
    public string Jkt { get; set; } = string.Empty;
}

// design §8.4.2.

/// <summary>
/// The payload of a license token (<c>elm-license+jws</c>).
/// </summary>
internal sealed class LicensePayload
{
    [JsonPropertyName("ver")]
    [JsonRequired]
    public int Ver { get; set; }

    [JsonPropertyName("iss")]
    [JsonRequired]
    public string Iss { get; set; } = string.Empty;

    [JsonPropertyName("iat")]
    [JsonRequired]
    public long Iat { get; set; }

    [JsonPropertyName("exp")]
    public long? Exp { get; set; }

    [JsonPropertyName("jti")]
    [JsonRequired]
    public string Jti { get; set; } = string.Empty;

    [JsonPropertyName("lic")]
    [JsonRequired]
    public string Lic { get; set; } = string.Empty;

    [JsonPropertyName("prd")]
    [JsonRequired]
    public string Prd { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    [JsonRequired]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("feat")]
    [JsonRequired]
    public List<FeatureClaim> Feat { get; set; } = [];

    [JsonPropertyName("cnf")]
    public ConfirmationClaim? Cnf { get; set; }

    [JsonPropertyName("fp")]
    public List<string>? Fp { get; set; }

    [JsonPropertyName("fpm")]
    public string? Fpm { get; set; }

    [JsonPropertyName("grace")]
    public long? Grace { get; set; }

    [JsonPropertyName("refresh")]
    public long? Refresh { get; set; }

    [JsonPropertyName("seq")]
    public long? Seq { get; set; }

    [JsonPropertyName("areq")]
    public string? Areq { get; set; }

    [JsonPropertyName("act")]
    public string? Act { get; set; }

    [JsonPropertyName("lease")]
    public long? Lease { get; set; }

    [JsonPropertyName("borrow")]
    public long? Borrow { get; set; }

    [JsonPropertyName("overage")]
    public int? Overage { get; set; }

    [JsonPropertyName("clone")]
    public string? Clone { get; set; }

    [JsonPropertyName("brl")]
    public int? Brl { get; set; }

    [JsonPropertyName("sbrl")]
    public int? Sbrl { get; set; }

    [JsonPropertyName("meta")]
    public Dictionary<string, string>? Meta { get; set; }
}

// design §8.5.

/// <summary>
/// The license file envelope.
/// </summary>
internal sealed class LicenseFileEnvelope
{
    [JsonPropertyName("format")]
    [JsonRequired]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    [JsonRequired]
    public int Version { get; set; }

    [JsonPropertyName("license")]
    [JsonRequired]
    public string License { get; set; } = string.Empty;

    [JsonPropertyName("certificates")]
    [JsonRequired]
    public List<string> Certificates { get; set; } = [];
}

// design §8.2 rule 6, exception.

/// <summary>
/// Reads only the <c>jwk</c> member of a payload, for a token that is verified with the key its own payload carries.
/// </summary>
internal sealed class EmbeddedJwkPayload
{
    [JsonPropertyName("jwk")]
    public EcPublicJwk? Jwk { get; set; }
}

// design §11.4.

/// <summary>
/// The persisted clock guard state.
/// </summary>
internal sealed class ClockState
{
    [JsonPropertyName("v")]
    [JsonRequired]
    public int V { get; set; }

    [JsonPropertyName("lastSeen")]
    [JsonRequired]
    public long LastSeen { get; set; }

    [JsonPropertyName("sig")]
    public string? Sig { get; set; }
}
