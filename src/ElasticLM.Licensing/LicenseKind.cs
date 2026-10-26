namespace ElasticLM.Licensing;

/// <summary>
/// The kind of a license (design §9.3, §9.6).
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
