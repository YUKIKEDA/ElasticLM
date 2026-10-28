using System.Security.Cryptography;

namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Tests of <see cref="FileClockGuard"/> (design §11.4).
/// </summary>
public sealed class FileClockGuardTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "elm-clock-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));

    public FileClockGuardTests() => Directory.CreateDirectory(_directory);

    private string StatePath => Path.Combine(_directory, "clock.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// A clock set back past the last time seen minus the skew is rejected, also by a new guard reading the same file.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A guard that observed T0 + 1 day.
    ///
    /// Steps:
    /// - Observe T0 + 1 day - 300 s and T0 + 1 day - 301 s with a token issued at T0, in the same guard and in a new guard on
    ///   the same file.
    ///
    /// Expected:
    /// - -300 s: accepted. -301 s: rejected, in both guards.
    /// </remarks>
    [Fact]
    public void Observe_ClockSetBack_RejectsBeyondSkew()
    {
        var guard = new FileClockGuard(StatePath);
        DateTimeOffset seen = T0.AddDays(1);
        Assert.True(guard.Observe(seen, T0, Skew));

        Assert.True(guard.Observe(seen.AddSeconds(-300), T0, Skew));
        Assert.False(guard.Observe(seen.AddSeconds(-301), T0, Skew));
        Assert.False(new FileClockGuard(StatePath).Observe(seen.AddSeconds(-301), T0, Skew));
    }

    /// <summary>
    /// The service's time after an online refresh replaces the last time seen, also by an earlier time, in this guard and in
    /// the file (design §11.4 step 4); a guard without state ignores it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A guard that observed T0 + 10 days (the clock was set forward).
    ///
    /// Steps:
    /// - Observe T0 with a token issued at T0; accept T0 as the service's time; observe T0 again in this guard and in a new
    ///   guard on the same file; accept a time on the stateless guard.
    ///
    /// Expected:
    /// - Rejected; accepted twice; no exception.
    /// </remarks>
    [Fact]
    public void AcceptServerTime_ReplacesTheLastTimeSeen()
    {
        var guard = new FileClockGuard(StatePath);
        Assert.True(guard.Observe(T0.AddDays(10), T0, Skew));
        Assert.False(guard.Observe(T0, T0, Skew));

        guard.AcceptServerTime(T0);
        Assert.True(guard.Observe(T0, T0, Skew));
        Assert.True(new FileClockGuard(StatePath).Observe(T0, T0, Skew));
        ((IClockGuard)StatelessClockGuard.Instance).AcceptServerTime(T0);
    }

    /// <summary>
    /// Without a state file, the token's iat is the floor: a clock before iat minus the skew is rejected.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - No state file.
    ///
    /// Steps:
    /// - Observe iat - 301 s, then iat - 300 s in a new guard.
    ///
    /// Expected:
    /// - -301 s: rejected. -300 s: accepted.
    /// </remarks>
    [Fact]
    public void Observe_NoState_UsesIssuedAtAsFloor()
    {
        Assert.False(new FileClockGuard(StatePath).Observe(T0.AddSeconds(-301), T0, Skew));
        Assert.True(new FileClockGuard(Path.Combine(_directory, "other.json")).Observe(T0.AddSeconds(-300), T0, Skew));
    }

    /// <summary>
    /// With an installation key, a state file whose time was edited fails its signature and falls back to the token's iat.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A signed state at T0 + 30 days.
    ///
    /// Steps:
    /// - Rewrite lastSeen to T0 + 60 days keeping the signature; observe T0 + 10 days with a token issued at T0.
    ///
    /// Expected:
    /// - Accepted: the edited value is ignored, and the floor is the token's iat, not T0 + 60 days.
    /// </remarks>
    [Fact]
    public void Observe_EditedSignedState_FallsBackToIssuedAt()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.True(new FileClockGuard(StatePath, key).Observe(T0.AddDays(30), T0, Skew));
        string text = File.ReadAllText(StatePath);
        long original = T0.AddDays(30).ToUnixTimeSeconds();
        long edited = T0.AddDays(60).ToUnixTimeSeconds();
        File.WriteAllText(
            StatePath,
            text.Replace(
                original.ToString(System.Globalization.CultureInfo.InvariantCulture),
                edited.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal
            )
        );

        Assert.True(new FileClockGuard(StatePath, key).Observe(T0.AddDays(10), T0, Skew));
    }

    /// <summary>
    /// With an installation key, an untouched signed state is honored across guards.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A signed state at T0 + 30 days.
    ///
    /// Steps:
    /// - Observe T0 + 10 days in a new guard with the same key.
    ///
    /// Expected:
    /// - Rejected.
    /// </remarks>
    [Fact]
    public void Observe_SignedState_IsHonored()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.True(new FileClockGuard(StatePath, key).Observe(T0.AddDays(30), T0, Skew));

        Assert.False(new FileClockGuard(StatePath, key).Observe(T0.AddDays(10), T0, Skew));
    }

    /// <summary>
    /// When the state cannot be written, the guard keeps going on its in-memory value and the token's iat, without an
    /// exception.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A state path inside a directory that does not exist.
    ///
    /// Steps:
    /// - Observe T0 + 1 day; observe T0 + 1 day - 301 s in the same guard.
    ///
    /// Expected:
    /// - The first is accepted and no file appears; the second is rejected from the in-memory value.
    /// </remarks>
    [Fact]
    public void Observe_UnwritableState_KeepsGoingInMemory()
    {
        string path = Path.Combine(_directory, "missing", "clock.json");
        var guard = new FileClockGuard(path);

        Assert.True(guard.Observe(T0.AddDays(1), T0, Skew));
        Assert.False(File.Exists(path));
        Assert.False(guard.Observe(T0.AddDays(1).AddSeconds(-301), T0, Skew));
    }

    /// <summary>
    /// After a failed write, the guard does not try again until the time advanced by 60 seconds.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A state path in a missing directory; the first write fails.
    ///
    /// Steps:
    /// - Create the directory; observe T0 + 59 s; check the file; observe T0 + 60 s; check the file.
    ///
    /// Expected:
    /// - No file after +59 s; the file exists after +60 s.
    /// </remarks>
    [Fact]
    public void Observe_AfterFailedWrite_WaitsBeforeRetrying()
    {
        string directory = Path.Combine(_directory, "later");
        string path = Path.Combine(directory, "clock.json");
        var guard = new FileClockGuard(path);
        Assert.True(guard.Observe(T0, T0, Skew));
        Directory.CreateDirectory(directory);

        Assert.True(guard.Observe(T0.AddSeconds(59), T0, Skew));
        Assert.False(File.Exists(path));
        Assert.True(guard.Observe(T0.AddSeconds(60), T0, Skew));
        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// The state is written only when the time advanced by 60 seconds or more since the last write.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A guard that observed T0.
    ///
    /// Steps:
    /// - Observe T0 + 59 s, read the file; observe T0 + 60 s, read the file.
    ///
    /// Expected:
    /// - After +59 s the file still holds T0; after +60 s it holds T0 + 60 s.
    /// </remarks>
    [Fact]
    public void Observe_SmallAdvance_DoesNotWrite()
    {
        var guard = new FileClockGuard(StatePath);
        Assert.True(guard.Observe(T0, T0, Skew));
        string seconds0 = T0.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        string seconds60 = T0.AddSeconds(60).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(guard.Observe(T0.AddSeconds(59), T0, Skew));
        Assert.Contains(seconds0, File.ReadAllText(StatePath), StringComparison.Ordinal);
        Assert.True(guard.Observe(T0.AddSeconds(60), T0, Skew));
        Assert.Contains(seconds60, File.ReadAllText(StatePath), StringComparison.Ordinal);
    }
}
