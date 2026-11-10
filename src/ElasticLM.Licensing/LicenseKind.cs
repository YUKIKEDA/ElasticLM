namespace ElasticLM.Licensing;

// design §9.3, §9.6.

/// <summary>
/// The kind of a license.
/// </summary>
public enum LicenseKind
{
    /// <summary>A standard license.</summary>
    Standard,

    /// <summary>A trial license.</summary>
    Trial,

    /// <summary>A server license for an on-premises license server.</summary>
    Server,
}

// design §9.3 CloneStrategy, §10.4.

/// <summary>
/// What a server does when it detects a cloned installation.
/// </summary>
public enum CloneStrategy
{
    /// <summary>Stop the activation or lease and answer <c>ELM_CLONE_DETECTED</c>.</summary>
    Deny,

    /// <summary>Record the detection and answer as usual.</summary>
    Flag,
}
