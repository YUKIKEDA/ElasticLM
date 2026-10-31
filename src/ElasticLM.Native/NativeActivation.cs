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
internal sealed unsafe class NativeActivation(nint callback, nint user) : NativeCallbackHandle
{
    public ActivationClient? Client { get; set; }

    /// <summary>The handler attached before the client starts (<c>ActivationClientOptions.StateChangedFromStart</c>).</summary>
    public void OnStateChanged(object? sender, ActivationStateChangedEventArgs change)
    {
        if (callback == 0)
        {
            return;
        }

        Call(() =>
        {
            (nint text, _) = NativeStrings.Allocate(change.Error?.Message ?? string.Empty);
            try
            {
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
                NativeStrings.Free(text);
            }
        });
    }

    /// <summary>
    /// Stops new callbacks, waits for the running ones, then disposes the client, which waits up to 5 seconds for the timer's
    /// work (design §17.5).
    /// </summary>
    public void Close() => Close(() => Client?.DisposeAsync().AsTask().GetAwaiter().GetResult());
}
