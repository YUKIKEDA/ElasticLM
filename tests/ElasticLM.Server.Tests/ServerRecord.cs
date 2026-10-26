using ElasticLM.Client;
using ElasticLM.Issuing;
using ElasticLM.Licensing;

namespace ElasticLM.Server.Tests;

/// <summary>
/// A borrow record written by hand with the key and license of a server data folder, for tests of how the server reads it.
/// </summary>
internal sealed class ServerRecord : IDisposable
{
    private readonly InstallationKey _key;

    private ServerRecord(InstallationKey key, SignedLog log)
    {
        _key = key;
        Log = log;
    }

    /// <summary>Gets the open record, stream <c>borrows</c>.</summary>
    public SignedLog Log { get; }

    /// <summary>
    /// Opens a new record file signed with the key and license of <paramref name="serverData"/>, in <paramref name="target"/>
    /// or in the same folder.
    /// </summary>
    public static ServerRecord Open(string serverData, TimeProvider time, string? target = null)
    {
        var store = new InstallationKeyStore("acme-cad", KeyScope.Machine, serverData);
        Assert.True(store.TryLoad(out InstallationKey? key, out _));
        byte[] license = File.ReadAllBytes(Path.Combine(serverData, "server.elmlic"));
        byte[] jwk = key!.WithPrivateKey(KeyFiles.ExportPublicJwk);
        SignedLog log = SignedLog.Open(target ?? serverData, "borrows", "borrows", use => key.WithPrivateKey(use), license, jwk, time);
        return new ServerRecord(key, log);
    }

    public void Dispose()
    {
        Log.Dispose();
        _key.Dispose();
    }
}
