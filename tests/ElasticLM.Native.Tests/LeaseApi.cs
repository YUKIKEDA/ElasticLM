using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ElasticLM.Native.Tests;

/// <summary>
/// Calls the lease functions the way C does, through unmanaged function pointers (design §17.6). The async tests use these
/// synchronous helpers, since an async method cannot hold pointers.
/// </summary>
internal static unsafe class LeaseApi
{
    public static int SizeOfOptions => sizeof(ClientOptions);

    public static ClientOptions Options(NativeApi api, string root, string? url, string directory, string? licenseKey = null) =>
        new()
        {
            StructSize = (uint)sizeof(ClientOptions),
            ProductId = api.Text("acme-cad"),
            ServerUrl = api.Text(url),
            LicenseKey = api.Text(licenseKey),
            RootKeysPem = api.Texts([root]),
            RootKeyCount = 1,
            RevokedKeyIds = api.Texts([]),
            Directory = api.Text(directory),
            OnLease = (nint)(delegate* unmanaged<nint, byte*, int, int, int, byte*, void>)&LeaseCallbacks.OnLease,
            User = 7,
        };

    public static ClientOptions With(NativeApi api, ClientOptions options, string? appVersion = null, string? expected = null)
    {
        options.AppVersion = api.Text(appVersion);
        options.ExpectedServerLicenseId = api.Text(expected);
        return options;
    }

    public static int Create(NativeApi api, ClientOptions options, out nint client, bool nullOut = false)
    {
        nint result = -1;
        int code = ((delegate* unmanaged<nint, nint*, int>)&Exports.ClientCreate)((nint)api.Struct(options), nullOut ? null : &result);
        client = result;
        return code;
    }

    public static int Acquire(NativeApi api, nint client, string? feature, int count, out nint lease, out long retry, bool nullRetry = false)
    {
        nint result = -1;
        long wait = -1;
        int code = ((delegate* unmanaged<nint, byte*, byte*, int, nint*, long*, int>)&Exports.ClientAcquireLease)(
            client,
            api.Text(feature),
            null,
            count,
            &result,
            nullRetry ? null : &wait
        );
        lease = result;
        retry = wait;
        return code;
    }

    public static int Borrow(NativeApi api, nint client, string feature, long period, out nint lease, out long retry)
    {
        nint result = -1;
        long wait = -1;
        int code = ((delegate* unmanaged<nint, byte*, long, byte*, int, nint*, long*, int>)&Exports.ClientBorrowLease)(
            client,
            api.Text(feature),
            period,
            null,
            1,
            &result,
            &wait
        );
        lease = result;
        retry = wait;
        return code;
    }

    public static int Release(nint client, nint lease) => ((delegate* unmanaged<nint, nint, int>)&Exports.ClientReleaseLease)(client, lease);

    public static int Borrowed(nint client, out string[] ids)
    {
        nint list = -1;
        nuint count = 99;
        int code = ((delegate* unmanaged<nint, nint*, nuint*, int>)&Exports.ClientGetBorrowedLeases)(client, &list, &count);
        ids = new string[(int)count];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = Id(((nint*)list)[i]);
        }

        ((delegate* unmanaged<nint*, nuint, void>)&Exports.LeaseListFree)((nint*)list, count);
        return code;
    }

    public static int ResetPin(NativeApi api, nint client, string? url, out int deleted)
    {
        int result = -1;
        int code = ((delegate* unmanaged<nint, byte*, int*, int>)&Exports.ClientResetServerPin)(client, api.Text(url), &result);
        deleted = result;
        return code;
    }

    public static string Id(nint lease)
    {
        nint text = -1;
        int code = ((delegate* unmanaged<nint, nint*, int>)&Exports.LeaseId)(lease, &text);
        return code == 0 ? Marshal.PtrToStringUTF8(text)! : $"error {code}";
    }

    public static string Feature(nint lease)
    {
        nint text = -1;
        int code = ((delegate* unmanaged<nint, nint*, int>)&Exports.LeaseFeatureId)(lease, &text);
        return code == 0 ? Marshal.PtrToStringUTF8(text)! : $"error {code}";
    }

    public static long ExpiresAt(nint lease)
    {
        long value = -1;
        int code = ((delegate* unmanaged<nint, long*, int>)&Exports.LeaseExpiresAt)(lease, &value);
        return code == 0 ? value : -code;
    }

    public static int Seats(nint lease) => ((delegate* unmanaged<nint, int>)&Exports.LeaseSeats)(lease);

    public static int IsBorrow(nint lease) => ((delegate* unmanaged<nint, int>)&Exports.LeaseIsBorrow)(lease);

    public static int State(nint lease) => ((delegate* unmanaged<nint, int>)&Exports.LeaseStateOf)(lease);

    public static int LastError(nint lease) => ((delegate* unmanaged<nint, int>)&Exports.LeaseLastError)(lease);

    public static void Free(nint lease) => ((delegate* unmanaged<nint, void>)&Exports.LeaseFree)(lease);

    // Each function whose output is required, called with that output NULL (design §17.6).
    public static int[] NullOutputs(NativeApi api, nint client, nint lease)
    {
        nint list;
        nuint count;
        long retry;
        byte* pro = api.Text("pro");
        return
        [
            ((delegate* unmanaged<nint, byte*, byte*, int, nint*, long*, int>)&Exports.ClientAcquireLease)(client, pro, null, 1, null, &retry),
            ((delegate* unmanaged<nint, byte*, long, byte*, int, nint*, long*, int>)&Exports.ClientBorrowLease)(
                client,
                pro,
                60,
                null,
                1,
                null,
                &retry
            ),
            ((delegate* unmanaged<nint, nint*, nuint*, int>)&Exports.ClientGetBorrowedLeases)(client, null, &count),
            ((delegate* unmanaged<nint, nint*, nuint*, int>)&Exports.ClientGetBorrowedLeases)(client, &list, null),
            ((delegate* unmanaged<nint, byte*, int*, int>)&Exports.ClientResetServerPin)(client, api.Text("https://x/"), null),
            ((delegate* unmanaged<nint, nint*, int>)&Exports.LeaseId)(lease, null),
            ((delegate* unmanaged<nint, long*, int>)&Exports.LeaseExpiresAt)(lease, null),
        ];
    }

    public static void FreeNullList() => ((delegate* unmanaged<nint*, nuint, void>)&Exports.LeaseListFree)(null, 3);

    public static void Destroy(nint client) => ((delegate* unmanaged<nint, void>)&Exports.ClientDestroy)(client);
}

/// <summary>The lease callback the tests pass, recording what it sees (design §17.6).</summary>
internal static unsafe class LeaseCallbacks
{
    public static ConcurrentQueue<(string Id, int Previous, int Current, int Error, string Message, nint User)> Seen { get; private set; } = new();

    public static ConcurrentQueue<string> Inside { get; private set; } = new();

    public static ManualResetEventSlim Entered { get; private set; } = new(false);

    public static ManualResetEventSlim? Hold { get; set; }

    // The client the callback calls into, and a lease of it to read, while it runs (Reenter) or after it is let go (AfterHold).
    public static (nint Client, nint Lease) Reenter { get; set; }

    public static (nint Client, nint Lease) AfterHold { get; set; }

    public static int LastThread { get; private set; }

    public static (string Id, int Previous, int Current, int Error, string Message, nint User)[] Changes() => [.. Seen];

    public static void Reset()
    {
        Seen = new();
        Inside = new();
        Entered = new(false);
        Hold = null;
        Reenter = default;
        AfterHold = default;
    }

    [UnmanagedCallersOnly]
    public static void OnLease(nint user, byte* id, int previous, int current, int error, byte* message)
    {
        LastThread = Environment.CurrentManagedThreadId;
        Seen.Enqueue(
            (Marshal.PtrToStringUTF8((nint)id) ?? "(null)", previous, current, error, Marshal.PtrToStringUTF8((nint)message) ?? "(null)", user)
        );
        if (Reenter.Client != 0)
        {
            (nint c, nint l) = Reenter;
            nint lease;
            long retry;
            Inside.Enqueue(
                "acquire "
                    + ((delegate* unmanaged<nint, byte*, byte*, int, nint*, long*, int>)&Exports.ClientAcquireLease)(c, null, null, 1, &lease, &retry)
            );
            Inside.Enqueue("release " + ((delegate* unmanaged<nint, nint, int>)&Exports.ClientReleaseLease)(c, l));
            nint list;
            nuint count;
            Inside.Enqueue("borrowed " + ((delegate* unmanaged<nint, nint*, nuint*, int>)&Exports.ClientGetBorrowedLeases)(c, &list, &count));
            ((delegate* unmanaged<nint, void>)&Exports.ClientDestroy)(c);
            Inside.Enqueue("state " + LeaseApi.State(l));
            int deleted;
            byte* url = stackalloc byte[]
            {
                (byte)'h',
                (byte)'t',
                (byte)'t',
                (byte)'p',
                (byte)'s',
                (byte)':',
                (byte)'/',
                (byte)'/',
                (byte)'x',
                (byte)'/',
                0,
            };
            Inside.Enqueue("pin " + ((delegate* unmanaged<nint, byte*, int*, int>)&Exports.ClientResetServerPin)(c, url, &deleted));

            // The callback ends with a failure, so the caller's message must be put back.
            Inside.Enqueue(
                "acquire "
                    + ((delegate* unmanaged<nint, byte*, byte*, int, nint*, long*, int>)&Exports.ClientAcquireLease)(c, null, null, 1, &lease, &retry)
            );
        }

        Entered.Set();
        Hold?.Wait();
        if (AfterHold.Client != 0)
        {
            Inside.Enqueue("after state " + LeaseApi.State(AfterHold.Lease) + " id " + LeaseApi.Id(AfterHold.Lease));
        }
    }
}
