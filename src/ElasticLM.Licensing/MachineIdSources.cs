using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ElasticLM.Licensing;

// design §10.3.

/// <summary>
/// Reads the OS machine IDs as 16-byte values. An ID that is missing, uninitialized, all zeros, or all ones
/// is left out.
/// </summary>
internal static partial class MachineIdSources
{
    /// <summary>
    /// Copies the raw SMBIOS data into <paramref name="buffer"/> and returns its size. Given an empty buffer, returns the
    /// size only.
    /// </summary>
    internal delegate uint FirmwareTableReader(Span<byte> buffer);

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
    /// Parses the content of <c>/etc/machine-id</c>: 32 lowercase hex characters (machine-id(5)), with any line feeds at the
    /// end removed. A carriage return is not removed, so a CRLF line end is refused.
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

    /// <summary>
    /// Adds <paramref name="id"/> to <paramref name="ids"/> unless it is <see langword="null"/>, all zeros, or all ones. Every
    /// reader returns 16 bytes or null, and <see cref="MachineFingerprint.Derive"/> refuses any other length.
    /// </summary>
    internal static void AddIfValid(List<byte[]> ids, byte[]? id)
    {
        if (id is null)
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

    /// <summary>
    /// Reads and parses the machine-id file at <paramref name="path"/>, skipping a UTF-8 byte order mark. Returns
    /// <see langword="null"/> when the file is missing, a folder, larger than 64 bytes, unreadable, or not in the format of
    /// <see cref="ParseLinuxMachineId"/>.
    /// </summary>
    internal static byte[]? ReadLinuxMachineId(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);

            // One byte past the limit tells a file of 64 bytes from a larger one without trusting FileStream.Length.
            Span<byte> buffer = stackalloc byte[65];
            int length = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (length > 64)
            {
                return null;
            }

            ReadOnlySpan<byte> content = buffer[..length];
            if (content.StartsWith(Encoding.UTF8.Preamble))
            {
                content = content[Encoding.UTF8.Preamble.Length..];
            }

            return ParseLinuxMachineId(Encoding.UTF8.GetString(content));
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

    /// <summary>
    /// Parses a UUID string into its 16 bytes in the order they are written. Accepts every form that
    /// <see cref="Guid.TryParse(string?, IFormatProvider?, out Guid)"/> accepts, and returns <see langword="null"/> for
    /// <see langword="null"/> and for any other text.
    /// </summary>
    internal static byte[]? ParseGuid(string? text) =>
        Guid.TryParse(text, CultureInfo.InvariantCulture, out Guid guid) ? guid.ToByteArray(bigEndian: true) : null;

    /// <summary>
    /// Reads the raw SMBIOS data (<c>RawSMBIOSData</c>) through <paramref name="read"/> and returns the System Information
    /// UUID. Returns <see langword="null"/> when the size first returned is 8 bytes or less or over 1 MiB, when the second
    /// call returns another size, or when the table length in the header is not positive or exceeds the bytes after it.
    /// </summary>
    internal static byte[]? ReadSmbiosUuid(FirmwareTableReader read)
    {
        const int HeaderLength = 8; // RawSMBIOSData: four bytes of version fields, then a DWORD length.
        uint size = read([]);
        if (size is <= HeaderLength or > 1024 * 1024)
        {
            return null;
        }

        byte[] buffer = new byte[size];
        if (read(buffer) != size)
        {
            return null;
        }

        int tableLength = BitConverter.ToInt32(buffer, 4);
        if (tableLength <= 0 || tableLength > buffer.Length - HeaderLength)
        {
            return null;
        }

        return ParseSmbiosSystemUuid(buffer.AsSpan(HeaderLength, tableLength));
    }

    /// <summary>
    /// Reads the MachineGuid value of the 64-bit registry view. Returns <see langword="null"/> when the key or the value is
    /// missing, the value is not a string, the string is not a UUID, or the registry cannot be read.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static byte[]? ReadWindowsMachineGuid()
    {
        try
        {
            // A 32-bit process reads a redirected view of HKLM\SOFTWARE, so the 64-bit view is named explicitly.
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return ParseGuid(key?.GetValue("MachineGuid") as string);
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
    private static byte[]? ReadWindowsSmbiosUuid() => ReadSmbiosUuid(ReadRawSmbiosData);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static unsafe uint ReadRawSmbiosData(Span<byte> buffer)
    {
        const uint Rsmb = 0x52534D42; // 'RSMB'

        // An empty span pins to a null pointer, so the first call asks for the size.
        fixed (byte* p = buffer)
        {
            return GetSystemFirmwareTable(Rsmb, 0, p, (uint)buffer.Length);
        }
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

            return ParseGuid(Marshal.PtrToStringUTF8((nint)text));
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
