namespace ElasticLM.Service;

/// <summary>
/// One piece of a timer's work (design §14.8): a run starts only when the last one has ended, so a tick that comes while the
/// work still runs is skipped. A clock that jumps (a fake one in tests, a resumed machine) fires many ticks at once, and each
/// would otherwise take its own database connection.
/// </summary>
internal sealed class PeriodicWork(Func<Task> work)
{
    private readonly Lock _lock = new();
    private Task? _running;

    /// <summary>Starts a run unless one is running.</summary>
    /// <returns><see langword="false"/> when a run was still going and this tick is skipped.</returns>
    public bool TryStart()
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: false })
            {
                return false;
            }

            // The work catches its own failures (it writes them to the service's log), so the task does not fault.
            _running = work();
            return true;
        }
    }

    /// <summary>Waits for the run that has started, if any, to end.</summary>
    public Task WhenIdleAsync()
    {
        lock (_lock)
        {
            return _running ?? Task.CompletedTask;
        }
    }
}
