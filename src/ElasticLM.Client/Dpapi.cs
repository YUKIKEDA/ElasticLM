using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ElasticLM.Client;

/// <summary>
/// Windows DPAPI through <c>CryptProtectData</c> and <c>CryptUnprotectData</c> in crypt32.dll (design §10.2).
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Dpapi
{
    private const uint UiForbidden = 0x1; // CRYPTPROTECT_UI_FORBIDDEN
    private const uint LocalMachine = 0x4; // CRYPTPROTECT_LOCAL_MACHINE

    // Optional entropy ties the protected blob to ElasticLM; another app using DPAPI for the same user cannot open it by accident.
    private static readonly byte[] Entropy = "ElasticLM installation key v1"u8.ToArray();

    public static byte[] Protect(byte[] data, bool machine)
    {
        DataBlob output;
        fixed (byte* input = data)
        {
            fixed (byte* entropy = Entropy)
            {
                var inBlob = new DataBlob { Size = (uint)data.Length, Data = input };
                var entropyBlob = new DataBlob { Size = (uint)Entropy.Length, Data = entropy };
                if (!CryptProtectData(&inBlob, null, &entropyBlob, null, null, UiForbidden | (machine ? LocalMachine : 0), out output))
                {
                    throw new IOException($"CryptProtectData failed with error {Marshal.GetLastPInvokeError()}.");
                }
            }
        }

        return CopyAndFree(output);
    }

    public static byte[]? Unprotect(byte[] data)
    {
        DataBlob output;
        fixed (byte* input = data)
        {
            fixed (byte* entropy = Entropy)
            {
                var inBlob = new DataBlob { Size = (uint)data.Length, Data = input };
                var entropyBlob = new DataBlob { Size = (uint)Entropy.Length, Data = entropy };
                if (!CryptUnprotectData(&inBlob, null, &entropyBlob, null, null, UiForbidden, out output))
                {
                    return null;
                }
            }
        }

        return CopyAndFree(output);
    }

    private static byte[] CopyAndFree(DataBlob blob)
    {
        try
        {
            return new ReadOnlySpan<byte>(blob.Data, (int)blob.Size).ToArray();
        }
        finally
        {
            new Span<byte>(blob.Data, (int)blob.Size).Clear();
            LocalFree(blob.Data);
        }
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        DataBlob* dataIn,
        char* description,
        DataBlob* entropy,
        void* reserved,
        void* prompt,
        uint flags,
        out DataBlob dataOut
    );

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        DataBlob* dataIn,
        char** description,
        DataBlob* entropy,
        void* reserved,
        void* prompt,
        uint flags,
        out DataBlob dataOut
    );

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(void* memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint Size;
        public byte* Data;
    }
}
