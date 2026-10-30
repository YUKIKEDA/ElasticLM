using System.Runtime.InteropServices;
using ElasticLM.Client;
using ElasticLM.Licensing;

namespace ElasticLM.Native;

/// <summary>
/// The opaque handles of the C ABI (design §17.4): each is the value of a <see cref="GCHandle"/> to one of the classes
/// below. A handle of another type is refused where the type can be told; a freed or made-up handle is the caller's error.
/// </summary>
internal static class NativeHandles
{
    public static nint Allocate(object target) => GCHandle.ToIntPtr(GCHandle.Alloc(target));

    /// <summary>Returns the object behind a handle, or <see langword="null"/> for zero or a handle of another type.</summary>
    public static T? Find<T>(nint handle)
        where T : class => handle == 0 ? null : GCHandle.FromIntPtr(handle).Target as T;

    /// <summary>Returns the object behind a required handle.</summary>
    /// <exception cref="NativeException">The handle is zero or of another type (<c>ELM_INVALID_ARGUMENT</c>).</exception>
    public static T Get<T>(nint handle, string name)
        where T : class => Find<T>(handle) ?? throw NativeException.Invalid($"{name} is not a valid handle.");

    /// <summary>Frees a handle and returns its object, or <see langword="null"/> for zero or a handle of another type.</summary>
    public static T? Free<T>(nint handle)
        where T : class
    {
        if (Find<T>(handle) is not T target)
        {
            return null;
        }

        GCHandle.FromIntPtr(handle).Free();
        return target;
    }
}

/// <summary>
/// <c>elm_installation</c>: an installation key from <see cref="InstallationKeyStore"/> and this machine's fingerprints for
/// its product (design §17.4).
/// </summary>
internal sealed class NativeInstallation : IDisposable
{
    public NativeInstallation(string productId, InstallationKeyStore store, InstallationKey key, IReadOnlyList<string> fingerprints)
    {
        ProductId = productId;
        Directory = store.Directory;
        Key = key;
        Fingerprints = fingerprints;
        Thumbprint = NativeStrings.Allocate(key.Thumbprint).Pointer;
    }

    public string ProductId { get; }

    public string Directory { get; }

    public InstallationKey Key { get; }

    public IReadOnlyList<string> Fingerprints { get; }

    /// <summary>Gets the key's thumbprint as a NUL-terminated UTF-8 string in native memory.</summary>
    public nint Thumbprint { get; private set; }

    public InstallationIdentity Identity() => Key.Identity(Fingerprints);

    public void Dispose()
    {
        Key.Dispose();
        NativeStrings.Free(Thumbprint);
        Thumbprint = 0;
    }
}

/// <summary>
/// <c>elm_verifier</c>: a <see cref="LicenseVerifier"/> and the clock guard it owns (design §17.4).
/// </summary>
internal sealed class NativeVerifier(LicenseVerifier verifier, IDisposable? guard) : IDisposable
{
    public LicenseVerifier Verifier { get; } = verifier;

    public void Dispose() => guard?.Dispose();
}
