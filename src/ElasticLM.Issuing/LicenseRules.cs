using ElasticLM.Licensing;

namespace ElasticLM.Issuing;

/// <summary>
/// The rules of a license token's values that a service checks when it stores a license, before it issues a token from it
/// (design §8.6, §9.2): the same checks <see cref="LicenseIssuer"/> applies.
/// </summary>
public static class LicenseRules
{
    /// <summary>The most features one license grants.</summary>
    public const int MaxFeatures = Limits.MaxFeatures;

    /// <summary>The most metadata entries.</summary>
    public const int MaxMetaEntries = Limits.MaxMetaEntries;

    /// <summary>The longest metadata key, in UTF-8 bytes.</summary>
    public const int MaxMetaKeyBytes = Limits.MaxMetaKeyBytes;

    /// <summary>The longest metadata value, in UTF-8 bytes.</summary>
    public const int MaxMetaValueBytes = Limits.MaxMetaValueBytes;

    /// <summary>The last second a time claim can hold: 9999-12-31T23:59:59Z.</summary>
    public const long MaxUnixSeconds = Limits.MaxUnixSeconds;

    /// <summary>
    /// Returns whether a text is a feature version range such as <c>&gt;=2.0 &lt;3.0</c> (design §9.2).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> when it parses.</returns>
    public static bool IsVersionRange(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return FeatureVersionRange.TryParse(text, out _);
    }
}
