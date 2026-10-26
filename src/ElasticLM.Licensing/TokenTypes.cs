namespace ElasticLM.Licensing;

/// <summary>
/// The <c>typ</c> header values (design §8.4).
/// </summary>
public static class TokenTypes
{
    /// <summary>A key certificate signed by a root key.</summary>
    public const string KeyCertificate = "elm-cert+jws";

    /// <summary>A license token.</summary>
    public const string License = "elm-license+jws";

    /// <summary>A lease token.</summary>
    public const string Lease = "elm-lease+jws";

    /// <summary>A request proof.</summary>
    public const string Proof = "elm-proof+jws";

    /// <summary>An offline activation or refresh request.</summary>
    public const string ActivationRequest = "elm-actreq+jws";

    /// <summary>An offline deactivation proof.</summary>
    public const string DeactivationProof = "elm-deact+jws";

    /// <summary>A log signature.</summary>
    public const string Log = "elm-log+jws";
}
