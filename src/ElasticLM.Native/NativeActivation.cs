using System.Runtime.InteropServices;
using ElasticLM.Client;
using ElasticLM.Licensing;

namespace ElasticLM.Native;

/// <summary><c>elm_activation_options</c> (design §17.5).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ActivationOptions
{
    public uint StructSize;
    public byte* ProductId;
    public byte* ServiceUrl;
    public byte** RootKeysPem;
    public nuint RootKeyCount;
    public byte** RevokedKeyIds;
    public nuint RevokedKeyCount;
    public int Scope;
    public byte* Directory;
    public int SendClientInfo;
    public byte* AppVersion;
    public int ClockSkewSeconds;
    public int RejectTrialAsStandard;
    public nint OnState;
    public nint User;
}

/// <summary>
/// <c>elm_activation</c>: an <see cref="ActivationClient"/> and the C callback of its state changes (design §17.5).
/// </summary>
/// <remarks>
/// The callback runs on the thread that ends an operation or on a thread pool thread of the automatic refresh. While it
/// runs, this thread is marked, so the blocking calls and destroy of the same handle can be refused from inside it. Destroy
/// waits for running callbacks under the same lock that keeps new ones from starting.
/// </remarks>
internal sealed unsafe class NativeActivation(nint callback, nint user)
{
    [ThreadStatic]
    private static NativeActivation? _inCallback;

    private readonly object _lock = new();
    private bool _closed;
    private int _running;

    public ActivationClient? Client { get; set; }

    /// <summary>Gets a value indicating whether this thread runs a callback of this handle.</summary>
    public bool InOwnCallback => ReferenceEquals(_inCallback, this);

    /// <summary>The handler attached before the client starts (<c>ActivationClientOptions.StateChangedFromStart</c>).</summary>
    public void OnStateChanged(object? sender, ActivationStateChangedEventArgs change)
    {
        if (callback == 0)
        {
            return;
        }

        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _running++;
        }

        byte[] message = NativeErrors.Save();
        NativeActivation? outer = _inCallback;
        (nint text, _) = NativeStrings.Allocate(change.Error?.Message ?? string.Empty);
        try
        {
            _inCallback = this;
            ((delegate* unmanaged<nint, int, int, int, byte*, void>)callback)(
                user,
                (int)change.Previous,
                (int)change.Current,
                (int)(change.Error?.Code ?? ErrorCode.Ok),
                (byte*)text
            );
        }
        finally
        {
            _inCallback = outer;
            NativeStrings.Free(text);
            NativeErrors.Restore(message);
            lock (_lock)
            {
                _running--;
                Monitor.PulseAll(_lock);
            }
        }
    }

    /// <summary>
    /// Stops new callbacks, waits for the running ones, then disposes the client, which waits up to 5 seconds for the timer's
    /// work (design §17.5).
    /// </summary>
    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
            while (_running > 0)
            {
                Monitor.Wait(_lock);
            }
        }

        Client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
