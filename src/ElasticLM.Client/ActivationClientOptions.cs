using ElasticLM.Licensing;

namespace ElasticLM.Client;

// design §12.6.2.

/// <summary>
/// The settings of an <see cref="ActivationClient"/>.
/// </summary>
public sealed class ActivationClientOptions
{
    // design §9.1.

    /// <summary>Gets the product ID: the product's <c>code</c>, which also names the installation key's folder.</summary>
    public required string ProductId { get; init; }

    // design §12.8.5.

    /// <summary>
    /// Gets the URL of <c>elm-service</c>: https, or http on a loopback address only, for tests and for trying it out locally
    /// (production uses https); or <see langword="null"/> for a device that never reaches the service, which activates,
    /// refreshes, and deactivates by offline files only. Without it the online operations answer
    /// <c>ELM_INVALID_STATE</c> and nothing is refreshed on its own.
    /// </summary>
    public Uri? ServiceUrl { get; init; }

    /// <summary>Gets the vendor's root keys, which the license files are checked against.</summary>
    public required TrustStore Trust { get; init; }

    // design §10.2.

    /// <summary>Gets where the installation key and the activation are kept. The default is the user.</summary>
    public KeyScope KeyScope { get; init; } = KeyScope.User;

    // design §10.2.

    /// <summary>Gets the folder of the installation key and the activation, or <see langword="null"/> for the default folder
    /// of the scope.</summary>
    public string? DataDirectory { get; init; }

    // design §4.4.

    /// <summary>
    /// Gets a value indicating whether the host name and OS user name are sent; the service keeps them only
    /// under <c>ClientInfoStrategy: HostAndUser</c>. Off by default.
    /// </summary>
    public bool SendClientInfo { get; init; }

    /// <summary>Gets the app's version, sent for the audit log; at most 64 bytes.</summary>
    public string? AppVersion { get; init; }

    // design §11.4.

    /// <summary>Gets the clock guard, or <see langword="null"/> for a <see cref="FileClockGuard"/> beside the
    /// installation key.</summary>
    public IClockGuard? ClockGuard { get; init; }

    /// <summary>Gets the verifier's settings.</summary>
    public LicenseVerifierOptions? VerifierOptions { get; init; }

    /// <summary>Gets the clock.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Gets a wrapper around the HTTP handler, so tests can lose an answer after the service acted on it.</summary>
    internal Func<HttpMessageHandler, HttpMessageHandler>? WrapHandler { get; init; }

    // design §13.7, M4c3.

    /// <summary>
    /// Gets the only kind of license kept, or <see langword="null"/> for any (standard, trial, server). <c>elm-server</c> and
    /// <c>elm server</c> keep only server licenses.
    /// </summary>
    internal LicenseKind? RequiredKind { get; init; }

    // design §13.7.

    /// <summary>
    /// Gets a value indicating whether an activation or an activation request makes the installation key when there is none.
    /// <c>elm server</c> does not: the server makes its key at its first start.
    /// </summary>
    internal bool CreateKey { get; init; } = true;

    // design §13.7.

    /// <summary>
    /// Gets what runs after a new license file is kept, with the file's bytes: <c>elm-server</c> writes it to its license path
    /// and reloads. An exception it throws is ignored.
    /// </summary>
    internal Action<byte[]>? LicenseKept { get; init; }

    // design §17.5.

    /// <summary>
    /// Gets a handler attached to <see cref="ActivationClient.StateChanged"/> before the client starts, so the change of a
    /// refresh that is already due at the start is not missed. libelasticlm passes its callback here.
    /// </summary>
    internal EventHandler<ActivationStateChangedEventArgs>? StateChangedFromStart { get; init; }
}
