using System.Text.Json.Serialization;

namespace ElasticLM.Licensing;

/// <summary>
/// The payload of a lease token (<c>elm-lease+jws</c>, design §8.4.3).
/// </summary>
internal sealed class LeasePayload
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
    [JsonRequired]
    public long Exp { get; set; }

    [JsonPropertyName("jti")]
    [JsonRequired]
    public string Jti { get; set; } = string.Empty;

    [JsonPropertyName("lse")]
    [JsonRequired]
    public string Lse { get; set; } = string.Empty;

    [JsonPropertyName("prd")]
    [JsonRequired]
    public string Prd { get; set; } = string.Empty;

    [JsonPropertyName("lic")]
    [JsonRequired]
    public string Lic { get; set; } = string.Empty;

    [JsonPropertyName("feat")]
    [JsonRequired]
    public string Feat { get; set; } = string.Empty;

    [JsonPropertyName("n")]
    [JsonRequired]
    public int N { get; set; }

    [JsonPropertyName("cnf")]
    [JsonRequired]
    public ConfirmationClaim Cnf { get; set; } = new();

    [JsonPropertyName("brw")]
    [JsonRequired]
    public bool Brw { get; set; }

    [JsonPropertyName("srv")]
    [JsonRequired]
    public string Srv { get; set; } = string.Empty;

    [JsonPropertyName("slic")]
    public string? Slic { get; set; }

    [JsonPropertyName("seq")]
    [JsonRequired]
    public long Seq { get; set; }
}

/// <summary>
/// The payload of a request proof (<c>elm-proof+jws</c>, design §12.2).
/// </summary>
internal sealed class ProofPayload
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

    [JsonPropertyName("htm")]
    [JsonRequired]
    public string Htm { get; set; } = string.Empty;

    [JsonPropertyName("htu")]
    [JsonRequired]
    public string Htu { get; set; } = string.Empty;

    [JsonPropertyName("nonce")]
    [JsonRequired]
    public string Nonce { get; set; } = string.Empty;

    [JsonPropertyName("bdy")]
    [JsonRequired]
    public string Bdy { get; set; } = string.Empty;

    [JsonPropertyName("jwk")]
    public EcPublicJwk? Jwk { get; set; }
}

/// <summary>
/// The payload of a log signature (<c>elm-log+jws</c>, design §8.4.6): it closes the lines written since the previous one.
/// </summary>
internal sealed class LogPayload
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

    [JsonPropertyName("stream")]
    [JsonRequired]
    public string Stream { get; set; } = string.Empty;

    [JsonPropertyName("n")]
    [JsonRequired]
    public long N { get; set; }

    [JsonPropertyName("prev")]
    [JsonRequired]
    public string Prev { get; set; } = string.Empty;

    [JsonPropertyName("digest")]
    [JsonRequired]
    public string Digest { get; set; } = string.Empty;

    [JsonPropertyName("lines")]
    [JsonRequired]
    public int Lines { get; set; }
}
