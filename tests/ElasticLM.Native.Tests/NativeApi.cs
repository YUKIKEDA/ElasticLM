using System.Runtime.InteropServices;
using System.Text;

namespace ElasticLM.Native.Tests;

/// <summary>
/// Calls the exported functions the way C does: through unmanaged function pointers to the
/// <see cref="UnmanagedCallersOnlyAttribute"/> methods, with arguments in native memory (design §17.3, §17.4).
/// </summary>
internal sealed unsafe class NativeApi : IDisposable
{
    private readonly List<nint> _memory = [];

    public static string LastError => Marshal.PtrToStringUTF8(((delegate* unmanaged<nint>)&Exports.LastErrorMessage)()) ?? "(null)";

    /// <summary>Copies bytes into native memory freed with this object, with a NUL after them.</summary>
    public byte* Bytes(byte[] bytes)
    {
        byte* memory = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        bytes.CopyTo(new Span<byte>(memory, bytes.Length));
        memory[bytes.Length] = 0;
        _memory.Add((nint)memory);
        return memory;
    }

    /// <summary>Copies a string into native memory as UTF-8 with a NUL, or returns null.</summary>
    public byte* Text(string? text) => text is null ? null : Bytes(Encoding.UTF8.GetBytes(text));

    /// <summary>Copies strings into a native array of pointers.</summary>
    public byte** Texts(IReadOnlyList<string> texts)
    {
        var array = (byte**)NativeMemory.Alloc((nuint)Math.Max(texts.Count, 1), (nuint)sizeof(nint));
        _memory.Add((nint)array);
        for (int i = 0; i < texts.Count; i++)
        {
            array[i] = Text(texts[i]);
        }

        return array;
    }

    public T* Struct<T>(T value)
        where T : unmanaged
    {
        var memory = (T*)NativeMemory.Alloc((nuint)sizeof(T));
        *memory = value;
        _memory.Add((nint)memory);
        return memory;
    }

    public InstallationOptions InstallationOptions(string? product, string? directory, int create = 1, int scope = 0) =>
        new()
        {
            StructSize = (uint)sizeof(InstallationOptions),
            ProductId = Text(product),
            Scope = scope,
            Directory = Text(directory),
            Create = create,
        };

    public static int Open(InstallationOptions* options, nint* result) =>
        ((delegate* unmanaged<nint, nint*, int>)&Exports.InstallationOpen)((nint)options, result);

    public nint OpenInstallation(string product, string directory)
    {
        nint result;
        Assert.Equal(0, Open(Struct(InstallationOptions(product, directory)), &result));
        return result;
    }

    public VerifierOptions VerifierOptions(IReadOnlyList<string> roots, IReadOnlyList<string>? revoked = null) =>
        new()
        {
            StructSize = (uint)sizeof(VerifierOptions),
            RootKeysPem = Texts(roots),
            RootKeyCount = (nuint)roots.Count,
            RevokedKeyIds = Texts(revoked ?? []),
            RevokedKeyCount = (nuint)(revoked?.Count ?? 0),
        };

    public static int Create(VerifierOptions* options, nint* result) =>
        ((delegate* unmanaged<nint, nint*, int>)&Exports.VerifierCreate)((nint)options, result);

    public nint CreateVerifier(VerifierOptions options)
    {
        nint result;
        Assert.Equal(0, Create(Struct(options), &result));
        return result;
    }

    public Requirement Requirement(
        string? product = "acme-cad",
        int kind = 0,
        string? feature = null,
        string? version = null,
        nint installation = 0
    ) =>
        new()
        {
            StructSize = (uint)sizeof(Requirement),
            ProductId = Text(product),
            Kind = kind,
            FeatureId = Text(feature),
            FeatureVersion = Text(version),
            Installation = installation,
        };

    public static int Verify(nint verifier, byte* file, nuint length, Requirement* requirement, nint* result) =>
        ((delegate* unmanaged<nint, byte*, nuint, nint, nint*, int>)&Exports.VerifierVerify)(verifier, file, length, (nint)requirement, result);

    /// <summary>Verifies a file with a requirement and returns the number and the license handle.</summary>
    public (int Code, nint License) Verify(nint verifier, byte[] file, Requirement requirement)
    {
        nint result = -1;
        int code = Verify(verifier, Bytes(file), (nuint)file.Length, Struct(requirement), &result);
        return (code, result);
    }

    public static string? Utf8(nint text) => text == 0 ? null : Marshal.PtrToStringUTF8(text);

    public static string Utf8(nint text, nuint length) => Encoding.UTF8.GetString((byte*)text, (int)length);

    public static void FreeInstallation(nint handle) => ((delegate* unmanaged<nint, void>)&Exports.InstallationFree)(handle);

    public static void DestroyVerifier(nint handle) => ((delegate* unmanaged<nint, void>)&Exports.VerifierDestroy)(handle);

    public static void FreeLicense(nint handle) => ((delegate* unmanaged<nint, void>)&Exports.LicenseFree)(handle);

    public void Dispose()
    {
        foreach (nint pointer in _memory)
        {
            NativeMemory.Free((void*)pointer);
        }
    }
}
