namespace ElasticLM.E2E;

/// <summary>
/// Every wait of the end-to-end tests on the real clock (design §20.7). The published binaries have no clock to replace, so a
/// flow that needs time to pass waits for it. The waits of one run add up, those running side by side in other tests
/// included, and a wait fails its test once the sum passes the run's budget.
/// </summary>
internal static class RealClock
{
    /// <summary>The real-clock waits one run may make on one machine (design §20.7, Issue #178 Q8).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);
    private static readonly Lock Gate = new();

    // The start of every wait still running, so that the sum counts the waits of tests that run side by side (PR #179's
    // review): a sum of the finished waits only would let two tests wait 70 seconds each at once.
    private static readonly List<long> Running = [];
    private static long _finishedTicks;

    /// <summary>Gets how long this run's waits have taken so far, the finished ones and the time the running ones have spent.</summary>
    public static TimeSpan Waited
    {
        get
        {
            lock (Gate)
            {
                long ticks = _finishedTicks;
                foreach (long start in Running)
                {
                    ticks += TimeProvider.System.GetElapsedTime(start).Ticks;
                }

                return TimeSpan.FromTicks(ticks);
            }
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds, at most <paramref name="timeout"/>, and counts the time.</summary>
    /// <param name="what">What the wait is for, for the failure message.</param>
    /// <param name="condition">The condition to wait for.</param>
    /// <param name="timeout">The longest this wait may take.</param>
    public static async Task WaitUntilAsync(string what, Func<Task<bool>> condition, TimeSpan timeout)
    {
        long start = Begin();
        try
        {
            while (!await condition())
            {
                Assert.True(
                    TimeProvider.System.GetElapsedTime(start) < timeout,
                    $"{what}: the condition did not hold within {timeout.TotalSeconds} seconds."
                );
                Assert.True(Waited < Budget, $"{what}: the run's real-clock waits passed {Budget.TotalSeconds} seconds.");
                await Task.Delay(Poll, TimeProvider.System, TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            End(start);
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds, at most <paramref name="timeout"/>, and counts the time.</summary>
    /// <param name="what">What the wait is for, for the failure message.</param>
    /// <param name="condition">The condition to wait for.</param>
    /// <param name="timeout">The longest this wait may take.</param>
    public static Task WaitUntilAsync(string what, Func<bool> condition, TimeSpan timeout) =>
        WaitUntilAsync(what, () => Task.FromResult(condition()), timeout);

    /// <summary>Waits for <paramref name="delay"/>, as a server's <c>Retry-After</c> asks, and counts the time.</summary>
    /// <param name="what">What the wait is for, for the failure message.</param>
    /// <param name="delay">How long to wait.</param>
    public static async Task DelayAsync(string what, TimeSpan delay)
    {
        Assert.True(Waited + delay < Budget, $"{what}: the run's real-clock waits would pass {Budget.TotalSeconds} seconds.");
        long start = Begin();
        try
        {
            await Task.Delay(delay, TimeProvider.System, TestContext.Current.CancellationToken);
        }
        finally
        {
            End(start);
        }

        Assert.True(Waited < Budget, $"{what}: the run's real-clock waits passed {Budget.TotalSeconds} seconds.");
    }

    private static long Begin()
    {
        long start = TimeProvider.System.GetTimestamp();
        lock (Gate)
        {
            Running.Add(start);
        }

        return start;
    }

    private static void End(long start)
    {
        lock (Gate)
        {
            Running.Remove(start);
            _finishedTicks += TimeProvider.System.GetElapsedTime(start).Ticks;
        }
    }
}
