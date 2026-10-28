using ElasticLM.Licensing;

namespace ElasticLM.Client;

/// <summary>
/// The settings of an <see cref="ActivationClient"/> (design §12.6.2).
/// </summary>
public sealed class ActivationClientOptions
{
    /// <summary>Gets the product ID (design §9.1): the product's <c>code</c>, which also names the installation key's folder.</summary>
    public required string ProductId { get; init; }

    /// <summary>Gets the URL of <c>elm-service</c>: https, or http on a loopback address only, for tests.</summary>
    public required Uri ServiceUrl { get; init; }

    /// <summary>Gets the vendor's root keys, which the license files are checked against.</summary>
    public required TrustStore Trust { get; init; }

    /// <summary>Gets where the installation key and the activation are kept (design §10.2). The default is the user.</summary>
    public KeyScope KeyScope { get; init; } = KeyScope.User;

    /// <summary>Gets the folder of the installation key and the activation, or <see langword="null"/> for the default of
    /// design §10.2.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>
    /// Gets a value indicating whether the host name and OS user name are sent (design §4.4); the service keeps them only
    /// under <c>ClientInfoStrategy: HostAndUser</c>. Off by default.
    /// </summary>
    public bool SendClientInfo { get; init; }

    /// <summary>Gets the app's version, sent for the audit log; at most 64 bytes.</summary>
    public string? AppVersion { get; init; }

    /// <summary>Gets the clock guard (design §11.4), or <see langword="null"/> for a <see cref="FileClockGuard"/> beside the
    /// installation key.</summary>
    public IClockGuard? ClockGuard { get; init; }

    /// <summary>Gets the verifier's settings.</summary>
    public LicenseVerifierOptions? VerifierOptions { get; init; }

    /// <summary>Gets the clock.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Gets a wrapper around the HTTP handler, so tests can lose an answer after the service acted on it.</summary>
    internal Func<HttpMessageHandler, HttpMessageHandler>? WrapHandler { get; init; }
}
