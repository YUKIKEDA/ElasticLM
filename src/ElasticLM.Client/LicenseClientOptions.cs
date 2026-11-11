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
    /// <see cref="LicenseKey"/>, of <c>elm-service</c> (https, or http on a loopback address only, for tests and for trying it
    /// out locally; production uses https).
    /// </summary>
    public required Uri ServerUrl { get; init; }

    // design §12.7.6, §12.7.5 steps 1, 2, and 6.

    /// <summary>
    /// Gets the license key that leases from <c>elm-service</c>, or <see langword="null"/> for an
    /// on-premises server. With a key, the client checks TLS with the OS's trust, keeps no server pin, and verifies leases
    /// with the certificates the service sends. The key is held in memory only and sent only
    /// with an acquisition.
    /// </summary>
    public string? LicenseKey { get; init; }

    // design §13.5 step 2, §12.7.5 step 1.

    /// <summary>Gets the vendor's trust store, from which the server license or, with
    /// <see cref="LicenseKey"/>, the certificates of the service's leases are verified.</summary>
    public required TrustStore Trust { get; init; }

    // design §13.5 step 3.

    /// <summary>
    /// Gets the server license ID the server must hold, or <see langword="null"/> to accept the first one seen and require it
    /// afterwards. With an ID, a server moved to another machine is accepted under the same ID. Not
    /// with <see cref="LicenseKey"/>.
    /// </summary>
    public string? ExpectedServerLicenseId { get; init; }

    // design §10.2.

    /// <summary>Gets where the installation key and the server pins are kept.</summary>
    public KeyScope KeyScope { get; init; } = KeyScope.User;

    // design §10.2.

    /// <summary>Gets the folder of the installation key and the pins, or <see langword="null"/> for the default folder of the scope.</summary>
    public string? DataDirectory { get; init; }

    // design §4.4.

    /// <summary>
    /// Gets a value indicating whether the host name and OS user name are sent with each new lease
    /// (<c>ClientInfoStrategy: HostAndUser</c>). Off by default.
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
