using System.Security.Cryptography;
using ElasticLM.Licensing;

namespace ElasticLM.Client.Tests;

/// <summary>
/// Tests of <see cref="InstallationKey.CreateClockGuard"/> and <see cref="InstallationClockGuard"/> (design §11.4, §17.4).
/// </summary>
public sealed class InstallationClockGuardTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "elm-guard-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// The guard signs its state with the installation key: a guard of the same key honors the state, a guard of another
    /// key treats it as missing.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A created installation key; its guard observed T0 + 30 days in <c>clock</c> in the key's folder.
    ///
    /// Steps:
    /// - Observe T0 + 10 days (a token issued at T0) with a new guard of the same key, and with a guard of another key on
    ///   the same file.
    ///
    /// Expected:
    /// - The same key: rejected (the signed state is honored). Another key: accepted (the signature fails, so the floor is
    ///   the token's iat).
    /// </remarks>
    [Fact]
    public void CreateClockGuard_SignsStateWithTheKey()
    {
        var store = new InstallationKeyStore("acme-cad", KeyScope.User, _dir);
        Assert.True(store.TryLoadOrCreate(out InstallationKey? key, out _));
        string path = Path.Combine(store.Directory, "clock");
        using (key)
        {
            using (InstallationClockGuard first = key!.CreateClockGuard(path))
            {
                Assert.True(first.Observe(T0.AddDays(30), T0, Skew));
            }

            using InstallationClockGuard second = key.CreateClockGuard(path);
            Assert.False(second.Observe(T0.AddDays(10), T0, Skew));
        }

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.True(new FileClockGuard(path, other).Observe(T0.AddDays(10), T0, Skew));
    }

    /// <summary>
    /// Disposing a guard destroys its own copy only: the installation key and other guards keep working.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A created installation key and a guard of it, disposed.
    ///
    /// Steps:
    /// - Make another guard of the key and observe T0 + 1 day; observe with the disposed guard.
    ///
    /// Expected:
    /// - The new guard accepts; the disposed guard throws <see cref="ObjectDisposedException"/> when it signs its state.
    /// </remarks>
    [Fact]
    public void Dispose_DestroysOnlyTheGuardsCopy()
    {
        var store = new InstallationKeyStore("acme-cad", KeyScope.User, _dir);
        Assert.True(store.TryLoadOrCreate(out InstallationKey? key, out _));
        string path = Path.Combine(store.Directory, "clock");
        using (key)
        {
            InstallationClockGuard disposed = key!.CreateClockGuard(path);
            disposed.Dispose();

            using InstallationClockGuard fresh = key.CreateClockGuard(path);
            Assert.True(fresh.Observe(T0.AddDays(1), T0, Skew));
            Assert.Throws<ObjectDisposedException>(() => disposed.Observe(T0.AddDays(2), T0, Skew));
        }
    }

    /// <summary>
    /// An empty path is refused.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A created installation key.
    ///
    /// Steps:
    /// - CreateClockGuard with an empty path.
    ///
    /// Expected:
    /// - <see cref="ArgumentException"/>.
    /// </remarks>
    [Fact]
    public void CreateClockGuard_EmptyPath_Throws()
    {
        var store = new InstallationKeyStore("acme-cad", KeyScope.User, _dir);
        Assert.True(store.TryLoadOrCreate(out InstallationKey? key, out _));
        using (key)
        {
            Assert.Throws<ArgumentException>(() => key!.CreateClockGuard(string.Empty));
        }
    }
}
