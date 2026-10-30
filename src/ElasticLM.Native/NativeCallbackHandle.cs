namespace ElasticLM.Native;

/// <summary>
/// A handle whose .NET client calls a C callback (design §17.5, §17.6): <c>elm_activation</c> and <c>elm_client</c>.
/// </summary>
/// <remarks>
/// The callback runs on the thread that ends an operation or on a .NET thread of the automatic renewal. While it runs, the
/// thread is marked, so the blocking calls and destroy of the same handle can be refused from inside it. The caller's error
/// message is saved before the callback and put back after it. <see cref="Close"/> stops new callbacks and waits for the
/// running ones under the same lock that keeps new ones from starting.
/// </remarks>
internal abstract class NativeCallbackHandle
{
    [ThreadStatic]
    private static NativeCallbackHandle? _inCallback;

    private readonly object _lock = new();
    private bool _closed;
    private int _running;

    /// <summary>Gets a value indicating whether this thread runs a callback of this handle.</summary>
    public bool InOwnCallback => ReferenceEquals(_inCallback, this);

    /// <summary>
    /// Stops new callbacks, waits for the running ones, then runs <paramref name="dispose"/>, which disposes the client.
    /// </summary>
    /// <param name="dispose">Disposes the client.</param>
    public void Close(Action dispose)
    {
        lock (_lock)
        {
            _closed = true;
            while (_running > 0)
            {
                Monitor.Wait(_lock);
            }
        }

        dispose();
    }

    /// <summary>Runs one callback, unless the handle is closing.</summary>
    /// <param name="invoke">Calls the C function.</param>
    protected void Call(Action invoke)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _running++;
        }

        byte[] message = NativeErrors.Save();
        NativeCallbackHandle? outer = _inCallback;
        try
        {
            _inCallback = this;
            invoke();
        }
        finally
        {
            _inCallback = outer;
            NativeErrors.Restore(message);
            lock (_lock)
            {
                _running--;
                Monitor.PulseAll(_lock);
            }
        }
    }
}
