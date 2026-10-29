using ElasticLM.Licensing;

namespace ElasticLM.Client;

/// <summary>
/// The settings of a <see cref="LicenseClient"/>. <see cref="LicenseClient.TryCreate"/> takes a copy.
/// </summary>
public sealed class LicenseClientOptions
{
    /// <summary>Gets the product ID of the app.</summary>
    public required string ProductId { get; init; }

    /// <summary>
    /// Gets the URL of the on-premises license server, for example <c>https://licenses.example:7070/</c>; or, with
    /// <see cref="LicenseKey"/>, of <c>elm-service</c> (https, or http on a loopback address only, for tests).
    /// </summary>
    public required Uri ServerUrl { get; init; }

    /// <summary>
    /// Gets the license key that leases from <c>elm-service</c> (design §12.7.6), or <see langword="null"/> for an
    /// on-premises server. With a key, the client checks TLS with the OS's trust, keeps no server pin, and verifies leases
    /// with the certificates the service sends (§12.7.5 steps 1, 2, and 6). The key is held in memory only and sent only
    /// with an acquisition.
    /// </summary>
    public string? LicenseKey { get; init; }

    /// <summary>Gets the vendor's trust store, from which the server license (design §13.5 step 2) or, with
    /// <see cref="LicenseKey"/>, the certificates of the service's leases (§12.7.5 step 1) are verified.</summary>
    public required TrustStore Trust { get; init; }

    /// <summary>
    /// Gets the server license ID the server must hold, or <see langword="null"/> to accept the first one seen and require it
    /// afterwards (design §13.5 step 3). With an ID, a server moved to another machine is accepted under the same ID. Not
    /// with <see cref="LicenseKey"/>.
    /// </summary>
    public string? ExpectedServerLicenseId { get; init; }

    /// <summary>Gets where the installation key and the server pins are kept (design §10.2).</summary>
    public KeyScope KeyScope { get; init; } = KeyScope.User;

    /// <summary>Gets the folder of the installation key and the pins, or <see langword="null"/> for the default of design §10.2.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>
    /// Gets a value indicating whether the host name and OS user name are sent with each new lease
    /// (<c>ClientInfoStrategy: HostAndUser</c>, design §4.4). Off by default.
    /// </summary>
    public bool SendClientInfo { get; init; }

    /// <summary>Gets the app version sent with each new lease (at most 64 bytes), or <see langword="null"/>.</summary>
    public string? AppVersion { get; init; }

    /// <summary>Gets the clock guard for the server license and the leases, or <see langword="null"/> for a
    /// <see cref="FileClockGuard"/> next to the installation key.</summary>
    public IClockGuard? ClockGuard { get; init; }

    /// <summary>Gets the verifier options (clock skew).</summary>
    public LicenseVerifierOptions? VerifierOptions { get; init; }

    /// <summary>Gets the time provider; tests pass a fake one.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Gets a wrapper around the HTTP handler, so tests can lose an answer after the server acted on it.</summary>
    internal Func<HttpMessageHandler, HttpMessageHandler>? WrapHandler { get; init; }
}
