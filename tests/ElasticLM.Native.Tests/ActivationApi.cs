using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ElasticLM.Native.Tests;

/// <summary>
/// Calls the activation functions the way C does, through unmanaged function pointers (design §17.5). The async tests use
/// these synchronous helpers, since an async method cannot hold pointers.
/// </summary>
internal static unsafe class ActivationApi
{
    public static int SizeOfOptions => sizeof(ActivationOptions);

    public static ActivationOptions Options(NativeApi api, string product, string? url, string root, string directory) =>
        new()
        {
            StructSize = (uint)sizeof(ActivationOptions),
            ProductId = api.Text(product),
            ServiceUrl = api.Text(url),
            RootKeysPem = api.Texts([root]),
            RootKeyCount = 1,
            RevokedKeyIds = api.Texts([]),
            Directory = api.Text(directory),
            OnState = (nint)(delegate* unmanaged<nint, int, int, int, byte*, void>)&StateCallbacks.OnState,
            User = 42,
        };

    public static ActivationOptions WithAppVersion(NativeApi api, ActivationOptions options, string app)
    {
        options.AppVersion = api.Text(app);
        return options;
    }

    public static int Create(NativeApi api, ActivationOptions options, out nint activation, bool nullOut = false)
    {
        nint result = -1;
        int code = ((delegate* unmanaged<nint, nint*, int>)&Exports.ActivationCreate)((nint)api.Struct(options), nullOut ? null : &result);
        activation = result;
        return code;
    }

    public static int Activate(
        NativeApi api,
        nint activation,
        string key,
        out nint license,
        out long retry,
        bool nullRetry = false,
        bool nullOut = false
    )
    {
        nint result = -1;
        long wait = -1;
        int code = ((delegate* unmanaged<nint, byte*, nint*, long*, int>)&Exports.ActivationActivate)(
            activation,
            api.Text(key),
            nullOut ? null : &result,
            nullRetry ? null : &wait
        );
        license = result;
        retry = wait;
        return code;
    }

    public static int Refresh(nint activation, out nint license, out long retry)
    {
        nint result = -1;
        long wait = -1;
        int code = ((delegate* unmanaged<nint, nint*, long*, int>)&Exports.ActivationRefresh)(activation, &result, &wait);
        license = result;
        retry = wait;
        return code;
    }

    public static int Deactivate(nint activation) => ((delegate* unmanaged<nint, int>)&Exports.ActivationDeactivate)(activation);

    public static int Check(NativeApi api, nint activation, string? feature, out nint license)
    {
        nint result = -1;
        int code = ((delegate* unmanaged<nint, byte*, byte*, int, nint*, int>)&Exports.ActivationCheck)(
            activation,
            api.Text(feature),
            null,
            0,
            &result
        );
        license = result;
        return code;
    }

    public static int State(nint activation) => ((delegate* unmanaged<nint, int>)&Exports.ActivationState)(activation);

    public static int Id(nint activation, int size, out string id, out nuint needed)
    {
        byte* buffer = stackalloc byte[Math.Max(size, 1)];
        nuint required = 0;
        int code = ((delegate* unmanaged<nint, byte*, nuint, nuint*, int>)&Exports.ActivationId)(
            activation,
            size == 0 ? null : buffer,
            (nuint)size,
            &required
        );
        id = code == 0 ? Marshal.PtrToStringUTF8((nint)buffer)! : string.Empty;
        needed = required;
        return code;
    }

    public static int Request(NativeApi api, nint activation, string key, int renew, out string file, out int replaced)
    {
        nint text = -1;
        int changed = -1;
        int code = ((delegate* unmanaged<nint, byte*, int, nint*, int*, int>)&Exports.ActivationCreateActivationRequest)(
            activation,
            api.Text(key),
            renew,
            &text,
            &changed
        );
        file = text == 0 ? string.Empty : Marshal.PtrToStringUTF8(text)!;
        replaced = changed;
        ((delegate* unmanaged<byte*, void>)&Exports.StringFree)((byte*)text);
        return code;
    }

    public static int Proof(nint activation, out string file, out int replaced)
    {
        nint text = -1;
        int changed = -1;
        int code = ((delegate* unmanaged<nint, nint*, int*, int>)&Exports.ActivationCreateDeactivationProof)(activation, &text, &changed);
        file = text == 0 ? string.Empty : Marshal.PtrToStringUTF8(text)!;
        replaced = changed;
        ((delegate* unmanaged<byte*, void>)&Exports.StringFree)((byte*)text);
        return code;
    }

    public static int Forget(nint activation) => ((delegate* unmanaged<nint, int>)&Exports.ActivationForgetDeactivationProof)(activation);

    public static int Import(NativeApi api, nint activation, byte[]? response, out nint license)
    {
        nint result = -1;
        int code = ((delegate* unmanaged<nint, byte*, nuint, nint*, int>)&Exports.ActivationImportResponse)(
            activation,
            response is null ? null : api.Bytes(response),
            (nuint)(response?.Length ?? 0),
            &result
        );
        license = result;
        return code;
    }

    public static void FreeNullString() => ((delegate* unmanaged<byte*, void>)&Exports.StringFree)(null);

    public static void Destroy(nint activation) => ((delegate* unmanaged<nint, void>)&Exports.ActivationDestroy)(activation);

    public static int Kind(nint license) => ((delegate* unmanaged<nint, int>)&Exports.LicenseKindOf)(license);

    public static int InGrace(nint license) => ((delegate* unmanaged<nint, int>)&Exports.LicenseInGracePeriod)(license);
}

/// <summary>The state callback the activation tests pass, recording what it sees (design §17.5).</summary>
internal static unsafe class StateCallbacks
{
    public static ConcurrentQueue<(int Previous, int Current, int Error, string Message, nint User)> Seen { get; private set; } = new();

    public static ConcurrentQueue<string> Inside { get; private set; } = new();

    public static ManualResetEventSlim Entered { get; private set; } = new(false);

    public static ManualResetEventSlim? Hold { get; set; }

    public static nint Reenter { get; set; }

    public static int LastThread { get; private set; }

    public static (int Previous, int Current, int Error, string Message, nint User)[] Changes() => [.. Seen];

    public static void Reset()
    {
        Seen = new();
        Inside = new();
        Entered = new(false);
        Hold = null;
        Reenter = 0;
    }

    [UnmanagedCallersOnly]
    public static void OnState(nint user, int previous, int current, int error, byte* message)
    {
        LastThread = Environment.CurrentManagedThreadId;
        Seen.Enqueue((previous, current, error, Marshal.PtrToStringUTF8((nint)message) ?? "(null)", user));
        if (Reenter != 0)
        {
            nint a = Reenter;
            nint license;
            Inside.Enqueue("refresh " + ((delegate* unmanaged<nint, nint*, long*, int>)&Exports.ActivationRefresh)(a, &license, null));
            Inside.Enqueue("deactivate " + ((delegate* unmanaged<nint, int>)&Exports.ActivationDeactivate)(a));
            ((delegate* unmanaged<nint, void>)&Exports.ActivationDestroy)(a);
            Inside.Enqueue("state " + ((delegate* unmanaged<nint, int>)&Exports.ActivationState)(a));
            byte* buffer = stackalloc byte[128];
            nuint needed;
            Inside.Enqueue(
                "id "
                    + (((delegate* unmanaged<nint, byte*, nuint, nuint*, int>)&Exports.ActivationId)(a, buffer, 128, &needed) == 0 ? "ok" : "failed")
            );
            nint checkedLicense;
            Inside.Enqueue(
                "check " + ((delegate* unmanaged<nint, byte*, byte*, int, nint*, int>)&Exports.ActivationCheck)(a, null, null, 0, &checkedLicense)
            );
            ((delegate* unmanaged<nint, void>)&Exports.LicenseFree)(checkedLicense);
        }

        Entered.Set();
        Hold?.Wait();
    }
}
