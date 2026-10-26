namespace ElasticLM.Licensing;

/// <summary>
/// Detects a clock set back past the last time a token was accepted (design §11.4).
/// </summary>
public interface IClockGuard
{
    /// <summary>
    /// Checks the current time against the last time seen, and records the new time when the check passes.
    /// </summary>
    /// <param name="now">The current time from the verifier's <see cref="TimeProvider"/>.</param>
    /// <param name="tokenIssuedAt">The <c>iat</c> of the verified token. It is a floor the clock cannot go below.</param>
    /// <param name="allowedSkew">The allowed clock skew.</param>
    /// <returns><see langword="true"/> when the clock has not been set back; otherwise <see langword="false"/>.</returns>
    bool Observe(DateTimeOffset now, DateTimeOffset tokenIssuedAt, TimeSpan allowedSkew);
}

/// <summary>
/// A clock guard without state. It accepts every time; the verifier still rejects a token issued in the future (design
/// §11.2 step 6).
/// </summary>
public sealed class StatelessClockGuard : IClockGuard
{
    /// <summary>Gets the shared instance.</summary>
    public static StatelessClockGuard Instance { get; } = new();

    /// <inheritdoc />
    public bool Observe(DateTimeOffset now, DateTimeOffset tokenIssuedAt, TimeSpan allowedSkew) => true;
}
