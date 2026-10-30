using System.Runtime.InteropServices;

namespace ElasticLM.Native;

// The C structs of include/elasticlm.h (design §17.4), field for field in the same order. Sequential layout gives the
// same offsets as the C compiler on the 64-bit targets (design §4.1); the tests compare the sizes with the header's.

/// <summary><c>elm_installation_options</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct InstallationOptions
{
    public uint StructSize;
    public byte* ProductId;
    public int Scope;
    public byte* Directory;
    public int Create;
}

/// <summary><c>elm_verifier_options</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VerifierOptions
{
    public uint StructSize;
    public byte** RootKeysPem;
    public nuint RootKeyCount;
    public byte** RevokedKeyIds;
    public nuint RevokedKeyCount;
    public nint Installation;
    public byte* ClockGuardPath;
    public int ClockSkewSeconds;
    public int RejectTrialAsStandard;
}

/// <summary><c>elm_requirement</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Requirement
{
    public uint StructSize;
    public byte* ProductId;
    public int Kind;
    public byte* FeatureId;
    public byte* FeatureVersion;
    public nint Installation;
}

/// <summary><c>elm_feature</c>, written by the library.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Feature
{
    public uint StructSize;
    public byte* Id;
    public byte* VersionRange;
    public int HasSeats;
    public int Seats;
    public int HasExpiresAt;
    public long ExpiresAt;
}

/// <summary>
/// Checks a struct's <c>struct_size</c> (design §17.3). This is the first version of every struct, so the first and the
/// current sizes are the same, and any other value is refused.
/// </summary>
internal static unsafe class StructSizes
{
    public static void Check<T>(T* value, string name)
        where T : unmanaged
    {
        if (value is null)
        {
            throw NativeException.Invalid($"{name} is required.");
        }

        if (*(uint*)value != (uint)sizeof(T))
        {
            throw NativeException.Invalid($"{name}.struct_size must be {sizeof(T)}.");
        }
    }
}
