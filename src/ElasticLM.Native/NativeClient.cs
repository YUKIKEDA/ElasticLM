using System.Runtime.InteropServices;
using ElasticLM.Client;
using ElasticLM.Licensing;

namespace ElasticLM.Native;

/// <summary><c>elm_client_options</c> (design §17.6).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClientOptions
{
    public uint StructSize;
    public byte* ProductId;
    public byte* ServerUrl;
    public byte* LicenseKey;
    public byte** RootKeysPem;
    public nuint RootKeyCount;
    public byte** RevokedKeyIds;
    public nuint RevokedKeyCount;
    public byte* ExpectedServerLicenseId;
    public int Scope;
    public byte* Directory;
    public int SendClientInfo;
    public byte* AppVersion;
    public int ClockSkewSeconds;
    public int RejectTrialAsStandard;
    public nint OnLease;
    public nint User;
}

/// <summary>
/// <c>elm_client</c>: a <see cref="LicenseClient"/> and the C callback of its lease state changes (design §17.6).
/// </summary>
internal sealed unsafe class NativeClient(nint callback, nint user) : NativeCallbackHandle
{
    public LicenseClient? Client { get; set; }

    /// <summary>The handler of <see cref="LicenseClient.LeaseStateChanged"/>; it passes the lease by its ID.</summary>
    public void OnLeaseChanged(object? sender, LeaseStateChangedEventArgs change)
    {
        if (callback == 0)
        {
            return;
        }

        Call(() =>
        {
            (nint id, _) = NativeStrings.Allocate(change.Lease.Id);
            (nint text, _) = NativeStrings.Allocate(change.Error?.Message ?? string.Empty);
            try
            {
                ((delegate* unmanaged<nint, byte*, int, int, int, byte*, void>)callback)(
                    user,
                    (byte*)id,
                    (int)change.Previous,
                    (int)change.Current,
                    (int)(change.Error?.Code ?? ErrorCode.Ok),
                    (byte*)text
                );
            }
            finally
            {
                NativeStrings.Free(text);
                NativeStrings.Free(id);
            }
        });
    }

    /// <summary>
    /// Stops new callbacks, waits for the running ones, then disposes the client, which returns its leases within 5 seconds
    /// (design §17.6).
    /// </summary>
    public void Close() => Close(() => Client?.DisposeAsync().AsTask().GetAwaiter().GetResult());
}

/// <summary>
/// <c>elm_lease</c>: a <see cref="Lease"/> with its ID and feature in native memory (design §17.6). The state, the expiry,
/// and the last error are read from the lease each time, since renewals change them.
/// </summary>
internal sealed class NativeLease : IDisposable
{
    public NativeLease(Lease lease)
    {
        Lease = lease;
        Id = NativeStrings.Allocate(lease.Id).Pointer;
        FeatureId = NativeStrings.Allocate(lease.FeatureId).Pointer;
    }

    public Lease Lease { get; }

    public nint Id { get; }

    public nint FeatureId { get; }

    public void Dispose()
    {
        NativeStrings.Free(Id);
        NativeStrings.Free(FeatureId);
    }
}
