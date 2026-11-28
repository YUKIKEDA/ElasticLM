using ElasticLM.Issuing;
using ElasticLM.Issuing.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ElasticLM.Server.Tests;

/// <summary>
/// A stop of the host closes the server's records before the host raises ApplicationStopped, after which a Windows service
/// reports STOPPED and its process may end (design §13.4, V1d29a).
/// </summary>
public sealed class ServerStopTests : IAsyncDisposable
{
    private readonly Deployment _deployment = new();

    public ValueTask DisposeAsync() => _deployment.DisposeAsync();

    /// <summary>
    /// When the host is asked to stop, as Ctrl+C, SIGTERM, and a service stop ask it, the usage log and the borrow record
    /// already end with server.stopped when ApplicationStopped is raised.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A server with a license in a new data folder, started in process.
    /// - A callback on the host's ApplicationStopped that reads the usage log and the newest borrow record as they are then.
    ///
    /// Steps:
    /// - Ask the host to stop (IHostApplicationLifetime.StopApplication), and wait for the stop as Program does
    ///   (WaitForShutdownAsync).
    ///
    /// Expected:
    /// - At ApplicationStopped, the usage log verifies, and its last entry is server.stopped with 0 leases; the newest borrow
    ///   record has a server.stopped line.
    /// </remarks>
    [Fact]
    public async Task HostStop_AtApplicationStopped_RecordsEndWithServerStopped()
    {
        ElmServer server = await _deployment.StartServerAsync();
        IHostApplicationLifetime lifetime = server.Services.GetRequiredService<IHostApplicationLifetime>();
        string? usageLast = null;
        bool usageIntact = false;
        bool borrowStopped = false;
        lifetime.ApplicationStopped.Register(() =>
        {
            VerifiedLog usage = VerifiedLog.Of(SignedLog.FindFiles(server.DataDirectory, "usage"), _deployment.Trust, "acme-cad", "usage");
            usageIntact = usage.Intact;
            usageLast = usage.Entries[^1].Type + ":" + usage.Entries[^1].Value.GetProperty("leases").GetInt64();
            string borrows = SignedLog.FindFiles(server.DataDirectory, BorrowJournal.BaseName)[^1];
            borrowStopped = File.ReadAllText(borrows).Contains("\"type\":\"server.stopped\"", StringComparison.Ordinal);
        });

        lifetime.StopApplication();
        await server.WaitForShutdownAsync();

        Assert.True(usageIntact, "The usage log did not verify at ApplicationStopped.");
        Assert.Equal("server.stopped:0", usageLast);
        Assert.True(borrowStopped, "The borrow record had no server.stopped line at ApplicationStopped.");
    }
}
