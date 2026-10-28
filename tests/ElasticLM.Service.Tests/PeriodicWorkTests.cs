namespace ElasticLM.Service.Tests;

/// <summary>
/// A timer's work runs one at a time (design §14.8): a tick that comes while the last run still goes is skipped.
/// </summary>
public sealed class PeriodicWorkTests
{
    /// <summary>
    /// A second tick during a run does not start the work; after the run ends, the next tick does; waiting for idle waits
    /// for the run.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Work that counts its starts and ends when the test says.
    ///
    /// Steps:
    /// - Start; start again; ask for idle; end the run; start again; end that run.
    ///
    /// Expected:
    /// - true and one start; false and still one start; idle not done until the run ends; true and two starts.
    /// </remarks>
    [Fact]
    public async Task TryStart_DuringARun_IsSkipped()
    {
        int starts = 0;
        var gate = new TaskCompletionSource();
        var work = new PeriodicWork(() =>
        {
            starts++;
            return gate.Task;
        });

        Assert.Equal((true, 1), (work.TryStart(), starts));
        Assert.Equal((false, 1), (work.TryStart(), starts));
        Task idle = work.WhenIdleAsync();
        Assert.False(idle.IsCompleted);
        gate.SetResult();
        await idle;

        gate = new TaskCompletionSource();
        Assert.Equal((true, 2), (work.TryStart(), starts));
        gate.SetResult();
        await work.WhenIdleAsync();
    }
}
