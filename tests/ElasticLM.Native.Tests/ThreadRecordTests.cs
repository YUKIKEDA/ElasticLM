using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ElasticLM.Native.Tests;

/// <summary>
/// Whether the error message array of a thread is collected after the thread ends (design §17.3, §17.6): for a thread the
/// OS made, as C makes it, and, to compare, for a thread .NET made. The test runs under the JIT, so it does not show the
/// NativeAOT runtime directly.
/// </summary>
public sealed unsafe partial class ThreadRecordTests
{
    private static WeakReference? _record;
    private static ManualResetEventSlim _go = new(true);

    /// <summary>
    /// The error message array of a thread the OS made is collected after the thread ends, as one of a .NET thread is.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - On a thread made with CreateThread (Windows) or pthread_create, call elm_client_release_lease with NULL, which
    ///   writes this thread's message, hold its array by a weak reference, and wait until the test lets the thread go.
    ///   Collect (collect, wait for the finalizers, collect) while the thread waits; let it go, wait for it to end, and
    ///   collect again. Do the same on a thread made by .NET.
    ///
    /// Expected:
    /// - The call returned ELM_INVALID_ARGUMENT and the array was held; it is alive while its thread waits, so the check can
    ///   see a live array; after the thread ends, neither array is alive.
    /// </remarks>
    [Fact]
    public void ErrorRecord_OfAThreadThatEnded_IsCollected()
    {
        RunOnOsThread();
        Assert.False(StillAlive(_record!), "The message array of an OS thread was not collected after it ended.");

        _record = null;
        _go = new ManualResetEventSlim(false);
        var thread = new Thread(Record);
        thread.Start();
        WaitForRecord();
        Assert.True(StillAlive(_record!), "The message array of a waiting .NET thread was collected.");
        _go.Set();
        thread.Join();
        Assert.False(StillAlive(_record!), "The message array of a .NET thread was not collected after it ended.");
    }

    // Collects and tells whether the target is still alive.
    private static bool StillAlive(WeakReference record)
    {
        for (int i = 0; i < 3 && record.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        return record.IsAlive;
    }

    // Writes this thread's message and holds its array weakly. NoInlining keeps no strong reference in the caller's frame.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Record()
    {
        if (((delegate* unmanaged<nint, nint, int>)&Exports.ClientReleaseLease)(0, 0) == 1 && NativeErrors.Current is byte[] array)
        {
            _record = new WeakReference(array);
        }

        _go.Wait();
    }

    private static void WaitForRecord()
    {
        for (int i = 0; i < 1_000 && Volatile.Read(ref _record) is null; i++)
        {
            Thread.Sleep(10);
        }

        Assert.True(_record is not null, "The thread did not hold its message.");
    }

    private static void RunOnOsThread()
    {
        _record = null;
        _go = new ManualResetEventSlim(false);
        if (OperatingSystem.IsWindows())
        {
            nint handle = CreateThread(0, 0, &WindowsStart, 0, 0, 0);
            Assert.NotEqual(0, handle);
            WaitForRecord();
            Assert.True(StillAlive(_record!), "The message array of a waiting OS thread was collected.");
            _go.Set();
            Assert.Equal(0u, WaitForSingleObject(handle, uint.MaxValue));
            CloseHandle(handle);
        }
        else
        {
            nint thread;
            Assert.Equal(0, PThreadCreate(&thread, 0, &UnixStart, 0));
            WaitForRecord();
            Assert.True(StillAlive(_record!), "The message array of a waiting OS thread was collected.");
            _go.Set();
            Assert.Equal(0, PThreadJoin(thread, 0));
        }
    }

    [UnmanagedCallersOnly]
    private static uint WindowsStart(nint argument)
    {
        Record();
        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint UnixStart(nint argument)
    {
        Record();
        return 0;
    }

    [LibraryImport("kernel32", EntryPoint = "CreateThread")]
    private static partial nint CreateThread(
        nint attributes,
        nuint stackSize,
        delegate* unmanaged<nint, uint> start,
        nint argument,
        uint flags,
        nint id
    );

    [LibraryImport("kernel32", EntryPoint = "WaitForSingleObject")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32", EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("libc", EntryPoint = "pthread_create")]
    private static partial int PThreadCreate(nint* thread, nint attributes, delegate* unmanaged<nint, nint> start, nint argument);

    [LibraryImport("libc", EntryPoint = "pthread_join")]
    private static partial int PThreadJoin(nint thread, nint result);
}
