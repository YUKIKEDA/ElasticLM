using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The test's fake clock for a service, which can make one read of the timestamp or of the time wait until the test lets it
/// go on. The service reads the timestamp in its rate limits and the time in its operations, so a held read keeps a request
/// between two of its steps.
/// </summary>
internal sealed class HeldClock(TimeProvider inner) : TimeProvider, IDisposable
{
    private readonly ManualResetEventSlim _release = new(true);
    private readonly Lock _lock = new();
    private TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _timestampsUntilHeld;
    private int _timesUntilHeld;
    private string? _heldIn;

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    /// <summary>Holds the given read of the timestamp, counted from this call; the task ends once that read waits.</summary>
    public Task HoldAsync(int read) => HoldAsync(read, 0, null);

    /// <summary>Holds the given read of the time, counted from this call; the task ends once that read waits.</summary>
    public Task HoldTimeAsync(int read) => HoldAsync(0, read, null);

    /// <summary>
    /// Holds the next read of the time that the named async method of the service makes itself, not through a method it
    /// calls; the task ends once that read waits. The compiler names an async method's state machine after the method.
    /// </summary>
    public Task HoldTimeReadInAsync(string method) => HoldAsync(0, 0, "<" + method + ">");

    public void Release() => _release.Set();

    // Not inlined, so that the frame above this one is the method that reads the time.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public override DateTimeOffset GetUtcNow()
    {
        string? heldIn = Volatile.Read(ref _heldIn);
        bool caller =
            heldIn is not null && new StackFrame(1, false).GetMethod()?.DeclaringType?.Name.StartsWith(heldIn, StringComparison.Ordinal) == true;
        Wait(ref _timesUntilHeld, caller);
        return inner.GetUtcNow();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        inner.CreateTimer(callback, state, dueTime, period);

    public override long GetTimestamp()
    {
        Wait(ref _timestampsUntilHeld, false);
        return inner.GetTimestamp();
    }

    public void Dispose() => _release.Dispose();

    private Task HoldAsync(int timestamp, int time, string? heldIn)
    {
        lock (_lock)
        {
            _release.Reset();
            _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            (_timestampsUntilHeld, _timesUntilHeld) = (timestamp, time);
            Volatile.Write(ref _heldIn, heldIn);
            return _held.Task;
        }
    }

    private void Wait(ref int readsUntilHeld, bool caller)
    {
        bool hold;
        lock (_lock)
        {
            hold = (readsUntilHeld > 0 && --readsUntilHeld == 0) || (caller && _heldIn is not null);
            if (hold)
            {
                _heldIn = null;
                _held.SetResult();
            }
        }

        if (hold)
        {
            // The limit only ends a test that never lets the read go on.
            _release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
    }
}
