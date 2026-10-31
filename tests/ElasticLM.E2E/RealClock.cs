namespace ElasticLM.E2E;

/// <summary>
/// Every wait of the end-to-end tests on the real clock (design §20.7). The published binaries have no clock to replace, so a
/// flow that needs time to pass waits for it; the waits of one run add up, and a wait that would pass the run's budget fails.
/// </summary>
internal static class RealClock
{
    /// <summary>The real-clock waits one run may make on one machine (design §20.7, Issue #178 Q8).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);
    private static long _waitedTicks;

    /// <summary>Gets how long this run's waits have taken so far.</summary>
    public static TimeSpan Waited => TimeSpan.FromTicks(Interlocked.Read(ref _waitedTicks));

    /// <summary>Waits until <paramref name="condition"/> holds, at most <paramref name="timeout"/>, and counts the time.</summary>
    /// <param name="what">What the wait is for, for the failure message.</param>
    /// <param name="condition">The condition to wait for.</param>
    /// <param name="timeout">The longest this wait may take.</param>
    public static async Task WaitUntilAsync(string what, Func<Task<bool>> condition, TimeSpan timeout)
    {
        long start = TimeProvider.System.GetTimestamp();
        try
        {
            while (!await condition())
            {
                TimeSpan spent = TimeProvider.System.GetElapsedTime(start);
                Assert.True(spent < timeout, $"{what}: the condition did not hold within {timeout.TotalSeconds} seconds.");
                Assert.True(Waited + spent < Budget, $"{what}: the run's real-clock waits passed {Budget.TotalSeconds} seconds.");
                await Task.Delay(Poll, TimeProvider.System, TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            Interlocked.Add(ref _waitedTicks, TimeProvider.System.GetElapsedTime(start).Ticks);
        }
    }

    /// <summary>Waits for <paramref name="delay"/>, as a server's <c>Retry-After</c> asks, and counts the time.</summary>
    /// <param name="what">What the wait is for, for the failure message.</param>
    /// <param name="delay">How long to wait.</param>
    public static async Task DelayAsync(string what, TimeSpan delay)
    {
        Assert.True(Waited + delay < Budget, $"{what}: the run's real-clock waits would pass {Budget.TotalSeconds} seconds.");
        long start = TimeProvider.System.GetTimestamp();
        try
        {
            await Task.Delay(delay, TimeProvider.System, TestContext.Current.CancellationToken);
        }
        finally
        {
            Interlocked.Add(ref _waitedTicks, TimeProvider.System.GetElapsedTime(start).Ticks);
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds, at most <paramref name="timeout"/>, and counts the time.</summary>
    /// <param name="what">What the wait is for, for the failure message.</param>
    /// <param name="condition">The condition to wait for.</param>
    /// <param name="timeout">The longest this wait may take.</param>
    public static Task WaitUntilAsync(string what, Func<bool> condition, TimeSpan timeout) =>
        WaitUntilAsync(what, () => Task.FromResult(condition()), timeout);
}
