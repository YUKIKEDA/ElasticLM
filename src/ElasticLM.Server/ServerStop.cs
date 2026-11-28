using Microsoft.Extensions.Hosting;

namespace ElasticLM.Server;

/// <summary>
/// The last step of the host's stop (design §13.4, V1d29a): it closes the server's records after every hosted service has
/// stopped, Kestrel's listeners included, and before the host raises <c>ApplicationStopped</c>. As a Windows service, the
/// service reports STOPPED only after <c>ApplicationStopped</c> (<c>WindowsServiceLifetime.OnStop</c>), so the records are
/// closed before the process may end.
/// </summary>
/// <remarks>
/// The host calls <see cref="IHostedService.StopAsync"/> in the reverse order of registration, and Kestrel's
/// <c>GenericWebHostService</c> is one of those services; <see cref="StoppedAsync"/> comes after all of them
/// (<c>Host.StopAsync</c>, dotnet/runtime release/10.0). The steps of the start do nothing, so no code of ours runs in
/// <c>app.StartAsync</c> (design §13.1, V1c9).
/// </remarks>
internal sealed class ServerStop : IHostedLifecycleService
{
    /// <summary>Gets or sets what closes the records; the server sets it before the host starts.</summary>
    public Func<Task>? Close { get; set; }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Close?.Invoke() ?? Task.CompletedTask;
}
