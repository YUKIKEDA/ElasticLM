using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ElasticLM.Licensing;

/// <summary>
/// Reads the OS machine IDs as 16-byte values (design §10.3). An ID that is missing, uninitialized, all zeros, or all ones
/// is left out.
/// </summary>
internal static partial class MachineIdSources
{
    public static IReadOnlyList<byte[]> ReadAll()
    {
        var ids = new List<byte[]>(2);
        if (OperatingSystem.IsWindows())
        {
            AddIfValid(ids, ReadWindowsMachineGuid());
            AddIfValid(ids, ReadWindowsSmbiosUuid());
        }
        else if (OperatingSystem.IsLinux())
        {
            AddIfValid(ids, ReadLinuxMachineId("/etc/machine-id"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            AddIfValid(ids, ReadMacPlatformUuid());
        }

        return ids;
    }

    /// <summary>
    /// Parses the content of <c>/etc/machine-id</c>: 32 lowercase hex characters and an optional newline (machine-id(5)).
    /// </summary>
    internal static byte[]? ParseLinuxMachineId(string content)
    {
        string trimmed = content.TrimEnd('\n');
        if (trimmed.Length != 32)
        {
            return null;
        }

        foreach (char c in trimmed)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return null;
            }
        }

        return Convert.FromHexString(trimmed);
    }

    /// <summary>
    /// Finds the 16-byte UUID of the SMBIOS System Information structure (type 1, offset 8) in a raw SMBIOS table.
    /// </summary>
    internal static byte[]? ParseSmbiosSystemUuid(ReadOnlySpan<byte> table)
    {
        int offset = 0;
        while (offset + 4 <= table.Length)
        {
            byte type = table[offset];
            byte length = table[offset + 1];
            if (length < 4 || offset + length > table.Length)
            {
                return null;
            }

            if (type == 1 && length >= 0x18)
            {
                return table.Slice(offset + 8, 16).ToArray();
            }

            if (type == 127)
            {
                return null;
            }

            // Skip the formatted area, then the string set, which ends with two zero bytes.
            int strings = offset + length;
            while (strings + 1 < table.Length && !(table[strings] == 0 && table[strings + 1] == 0))
            {
                strings++;
            }

            offset = strings + 2;
        }

        return null;
    }

    private static void AddIfValid(List<byte[]> ids, byte[]? id)
    {
        if (id is null || id.Length != 16)
        {
            return;
        }

        bool allZero = true;
        bool allOnes = true;
        foreach (byte b in id)
        {
            allZero &= b == 0x00;
            allOnes &= b == 0xFF;
        }

        if (!allZero && !allOnes)
        {
            ids.Add(id);
        }
    }

    private static byte[]? ReadLinuxMachineId(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= 64 ? ParseLinuxMachineId(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[]? ReadWindowsMachineGuid()
    {
        try
        {
            // A 32-bit process reads a redirected view of HKLM\SOFTWARE, so the 64-bit view is named explicitly.
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") is string text && Guid.TryParse(text, CultureInfo.InvariantCulture, out Guid guid)
                ? guid.ToByteArray(bigEndian: true)
                : null;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static unsafe byte[]? ReadWindowsSmbiosUuid()
    {
        const uint Rsmb = 0x52534D42; // 'RSMB'
        const int HeaderLength = 8; // RawSMBIOSData: four bytes of version fields, then a DWORD length.
        uint size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
        if (size is <= HeaderLength or > 1024 * 1024)
        {
            return null;
        }

        byte[] buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (GetSystemFirmwareTable(Rsmb, 0, p, size) != size)
            {
                return null;
            }
        }

        int tableLength = BitConverter.ToInt32(buffer, 4);
        if (tableLength <= 0 || tableLength > buffer.Length - HeaderLength)
        {
            return null;
        }

        return ParseSmbiosSystemUuid(buffer.AsSpan(HeaderLength, tableLength));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static unsafe byte[]? ReadMacPlatformUuid()
    {
        const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8
        nint matching = IOServiceMatching("IOPlatformExpertDevice");
        if (matching == 0)
        {
            return null;
        }

        // IOServiceGetMatchingService consumes the matching dictionary.
        uint service = IOServiceGetMatchingService(0, matching);
        if (service == 0)
        {
            return null;
        }

        nint key = 0;
        nint value = 0;
        try
        {
            key = CFStringCreateWithCString(0, "IOPlatformUUID", Utf8);
            if (key == 0)
            {
                return null;
            }

            value = IORegistryEntryCreateCFProperty(service, key, 0, 0);
            if (value == 0)
            {
                return null;
            }

            byte* text = stackalloc byte[64];
            if (CFStringGetCString(value, text, 64, Utf8) == 0)
            {
                return null;
            }

            string uuid = Marshal.PtrToStringUTF8((nint)text) ?? string.Empty;
            return Guid.TryParse(uuid, CultureInfo.InvariantCulture, out Guid guid) ? guid.ToByteArray(bigEndian: true) : null;
        }
        finally
        {
            if (value != 0)
            {
                CFRelease(value);
            }

            if (key != 0)
            {
                CFRelease(key);
            }

            _ = IOObjectRelease(service);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = false)]
    private static unsafe partial uint GetSystemFirmwareTable(
        uint firmwareTableProviderSignature,
        uint firmwareTableId,
        byte* buffer,
        uint bufferSize
    );

    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint IOServiceMatching(string name);

    [LibraryImport(IOKit)]
    private static partial uint IOServiceGetMatchingService(uint mainPort, nint matching);

    [LibraryImport(IOKit)]
    private static partial nint IORegistryEntryCreateCFProperty(uint entry, nint key, nint allocator, uint options);

    [LibraryImport(IOKit)]
    private static partial int IOObjectRelease(uint obj);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string text, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static unsafe partial byte CFStringGetCString(nint text, byte* buffer, nint bufferSize, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);
}
