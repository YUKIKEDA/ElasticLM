using System.Security.Cryptography;
using ElasticLM.Licensing;

namespace ElasticLM.Client;

/// <summary>
/// A clock guard that keeps its state in a file and signs it with its own copy of an installation key (design §11.4), made
/// by <see cref="InstallationKey.CreateClockGuard"/>. Disposing it destroys the copy.
/// </summary>
public sealed class InstallationClockGuard : IClockGuard, IDisposable
{
    private readonly ECDsa _key;
    private readonly FileClockGuard _guard;

    internal InstallationClockGuard(string path, ECDsa key)
    {
        _key = key;
        _guard = new FileClockGuard(path, key);
    }

    /// <inheritdoc />
    public bool Observe(DateTimeOffset now, DateTimeOffset tokenIssuedAt, TimeSpan allowedSkew) => _guard.Observe(now, tokenIssuedAt, allowedSkew);

    /// <inheritdoc />
    public void AcceptServerTime(DateTimeOffset serverTime) => _guard.AcceptServerTime(serverTime);

    /// <inheritdoc />
    public void Dispose() => _key.Dispose();
}
