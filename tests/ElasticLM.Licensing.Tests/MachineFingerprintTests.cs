using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Tests of <see cref="MachineFingerprint"/> and <see cref="MachineIdSources"/> (design §10.3).
/// </summary>
public sealed class MachineFingerprintTests : IDisposable
{
    private const string Id = "00112233445566778899aabbccddeeff";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "elm-machine-id-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));

    public MachineFingerprintTests() => Directory.CreateDirectory(_directory);

    private string MachineIdPath => Path.Combine(_directory, "machine-id");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// The derivation matches a value computed with Python's hmac and hashlib, an independent implementation.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - OS ID 00112233445566778899aabbccddeeff and product acme-cad. The expected value came from:
    ///   hmac.new(osid, sha256(b"acme-cad").digest()[:16], sha256).digest()[:16] with the UUID v4 bits set.
    ///
    /// Steps:
    /// - Derive the fingerprint.
    ///
    /// Expected:
    /// - "2b9b86465b5d4fbda9c050845613fa89".
    /// </remarks>
    [Fact]
    public void Derive_KnownInput_MatchesIndependentValue()
    {
        byte[] osId = Convert.FromHexString("00112233445566778899aabbccddeeff");

        Assert.Equal("2b9b86465b5d4fbda9c050845613fa89", MachineFingerprint.Derive(osId, "acme-cad"));
    }

    /// <summary>
    /// The derivation refuses an OS ID that is not 16 bytes.
    /// </summary>
    /// <remarks>
    /// Design §10.3: every reader returns 16 bytes, and the derivation refuses any other length.
    ///
    /// Preconditions:
    /// - OS IDs of 15 and 17 bytes.
    ///
    /// Steps:
    /// - Derive for acme-cad.
    ///
    /// Expected:
    /// - ArgumentException "The OS ID must be 16 bytes." for the parameter osId.
    /// </remarks>
    /// <param name="length">The length of the OS ID.</param>
    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    public void Derive_IdNot16Bytes_Throws(int length)
    {
        var e = Assert.Throws<ArgumentException>(() => MachineFingerprint.Derive(new byte[length], "acme-cad"));

        Assert.Equal("osId", e.ParamName);
        Assert.StartsWith("The OS ID must be 16 bytes.", e.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Fingerprints differ between products for the same OS ID.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - One OS ID.
    ///
    /// Steps:
    /// - Derive for acme-cad and acme-cam.
    ///
    /// Expected:
    /// - Different values.
    /// </remarks>
    [Fact]
    public void Derive_DifferentProducts_Differ()
    {
        byte[] osId = Convert.FromHexString("00112233445566778899aabbccddeeff");

        Assert.NotEqual(MachineFingerprint.Derive(osId, "acme-cad"), MachineFingerprint.Derive(osId, "acme-cam"));
    }

    /// <summary>
    /// This machine yields at least one fingerprint on Windows, Linux, and macOS (the CI runners), each 32 lowercase hex characters.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A Windows, Linux, or macOS machine with an OS machine ID. Skipped on Linux when /etc/machine-id is missing or empty.
    ///
    /// Steps:
    /// - Compute the fingerprints twice.
    ///
    /// Expected:
    /// - At least one, the same both times, each 32 lowercase hex characters.
    /// </remarks>
    [Fact]
    public void Compute_ThisMachine_ReturnsStableFingerprints()
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            Assert.Skip("The machine ID sources cover Windows, Linux, and macOS.");
        }

        // An image ships /etc/machine-id empty (machine-id(5)); the .NET SDK image does.
        if (OperatingSystem.IsLinux() && !(File.Exists("/etc/machine-id") && new FileInfo("/etc/machine-id").Length > 0))
        {
            Assert.Skip("This Linux machine has no /etc/machine-id, or an empty one (for example, a container).");
        }

        IReadOnlyList<string> first = MachineFingerprint.Compute("acme-cad");
        IReadOnlyList<string> second = MachineFingerprint.Compute("acme-cad");

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
        Assert.All(first, f => Assert.Matches("^[0-9a-f]{32}$", f));
    }

    /// <summary>
    /// The machine-id parser accepts 32 lowercase hex characters with any line feeds at the end and rejects everything else.
    /// </summary>
    /// <remarks>
    /// Design §10.3: 32 lowercase hex characters; line feeds at the end are removed, a carriage return is not.
    ///
    /// Preconditions:
    /// - Contents as machine-id(5) describes, two line feeds, a CRLF line end, and broken ones.
    ///
    /// Steps:
    /// - Parse each.
    ///
    /// Expected:
    /// - A 16-byte value for the valid forms and two line feeds; null for CRLF, uppercase, "uninitialized", a short ID, and
    ///   an empty file.
    /// </remarks>
    [Theory]
    [InlineData("00112233445566778899aabbccddeeff\n", true)]
    [InlineData("00112233445566778899aabbccddeeff", true)]
    [InlineData("00112233445566778899aabbccddeeff\n\n", true)]
    [InlineData("00112233445566778899aabbccddeeff\r\n", false)]
    [InlineData("00112233445566778899AABBCCDDEEFF\n", false)]
    [InlineData("uninitialized\n", false)]
    [InlineData("0011223344556677\n", false)]
    [InlineData("", false)]
    public void ParseLinuxMachineId_Contents_FollowFormat(string content, bool valid)
    {
        byte[]? id = MachineIdSources.ParseLinuxMachineId(content);

        Assert.Equal(valid, id is { Length: 16 });
    }

    /// <summary>
    /// The SMBIOS walker skips other structures and their string sets and returns the type 1 UUID.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A synthetic table: a type 0 structure with two strings, then a type 1 structure of length 0x1B whose UUID is 01..10,
    ///   then the end-of-table structure.
    ///
    /// Steps:
    /// - Parse the table; parse a table with only type 0 and the end marker.
    ///
    /// Expected:
    /// - The UUID bytes 01..10; null when there is no type 1.
    /// </remarks>
    [Fact]
    public void ParseSmbiosSystemUuid_SyntheticTable_FindsTypeOneUuid()
    {
        byte[] type0 = [0, 4, 0, 0, (byte)'A', (byte)'B', 0, (byte)'C', 0, 0];
        byte[] uuid = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        var type1 = new List<byte> { 1, 0x1B, 1, 0, 0, 0, 0, 0 };
        type1.AddRange(uuid);
        type1.AddRange([0, 0, 0]);
        type1.AddRange([0, 0]);
        byte[] end = [127, 4, 2, 0, 0, 0];

        byte[] table = [.. type0, .. type1, .. end];

        Assert.Equal(uuid, MachineIdSources.ParseSmbiosSystemUuid(table));
        Assert.Null(MachineIdSources.ParseSmbiosSystemUuid([.. type0, .. end]));
    }

    /// <summary>
    /// The filter keeps an ID and leaves out a missing ID and an all-zero or all-one ID.
    /// </summary>
    /// <remarks>
    /// Design §10.3: no fingerprint is made from an ID that is all zeros or all ones.
    ///
    /// Preconditions:
    /// - null; 16 bytes of 00 and of FF; 16 bytes of 00 with 01 first, of FF with FE first, and of 00 then FF;
    ///   00112233445566778899aabbccddeeff.
    ///
    /// Steps:
    /// - Pass each to AddIfValid with an empty list.
    ///
    /// Expected:
    /// - The list holds the same ID for the last four inputs and is empty for the others.
    /// </remarks>
    /// <param name="hex">The ID in hex, or null for a missing ID.</param>
    /// <param name="kept">Whether the ID is kept.</param>
    [Theory]
    [InlineData(null, false)]
    [InlineData("00000000000000000000000000000000", false)]
    [InlineData("ffffffffffffffffffffffffffffffff", false)]
    [InlineData("01000000000000000000000000000000", true)]
    [InlineData("feffffffffffffffffffffffffffffff", true)]
    [InlineData("0000000000000000ffffffffffffffff", true)]
    [InlineData(Id, true)]
    public void AddIfValid_EachId_KeepsOnlyAnIdThatIsNeitherAllZerosNorAllOnes(string? hex, bool kept)
    {
        byte[]? id = hex is null ? null : Convert.FromHexString(hex);
        var ids = new List<byte[]>();

        MachineIdSources.AddIfValid(ids, id);

        if (kept)
        {
            Assert.Equal(id, Assert.Single(ids));
        }
        else
        {
            Assert.Empty(ids);
        }
    }

    /// <summary>
    /// The machine-id reader reads a file of at most 64 bytes, a UTF-8 byte order mark counted, and leaves out a larger one.
    /// </summary>
    /// <remarks>
    /// Design §10.3: /etc/machine-id over 64 bytes is not read, a UTF-8 byte order mark is skipped, and the line feeds at the
    /// end are removed. The ID therefore parses with each count of line feeds below, and only the size decides.
    ///
    /// Preconditions:
    /// - A file in the test folder with the 32 hex characters of the ID and n line feeds: 1 (33 bytes), 31 (63 bytes),
    ///   32 (64 bytes), 33 (65 bytes); with the 3-byte UTF-8 byte order mark first: 1 (36 bytes), 29 (64 bytes),
    ///   30 (65 bytes).
    ///
    /// Steps:
    /// - Read the file with ReadLinuxMachineId.
    ///
    /// Expected:
    /// - The 16 bytes of the ID for the files of at most 64 bytes; null for 65 bytes.
    /// </remarks>
    /// <param name="byteOrderMark">Whether the file starts with the UTF-8 byte order mark.</param>
    /// <param name="newlines">The number of line feeds after the ID.</param>
    /// <param name="read">Whether the ID is read.</param>
    [Theory]
    [InlineData(false, 1, true)]
    [InlineData(false, 31, true)]
    [InlineData(false, 32, true)]
    [InlineData(false, 33, false)]
    [InlineData(true, 1, true)]
    [InlineData(true, 29, true)]
    [InlineData(true, 30, false)]
    public void ReadLinuxMachineId_FileSize_ReadsAtMost64Bytes(bool byteOrderMark, int newlines, bool read)
    {
        byte[] text = System.Text.Encoding.ASCII.GetBytes(Id + new string('\n', newlines));
        File.WriteAllBytes(MachineIdPath, byteOrderMark ? [0xEF, 0xBB, 0xBF, .. text] : text);

        byte[]? id = MachineIdSources.ReadLinuxMachineId(MachineIdPath);

        Assert.Equal(read ? Convert.FromHexString(Id) : null, id);
    }

    /// <summary>
    /// The machine-id reader returns null when the file is missing, is a folder, or cannot be read.
    /// </summary>
    /// <remarks>
    /// Design §10.3: no fingerprint is made from an OS ID that cannot be read: a missing file, a folder, a file held open, or a
    /// file without the right to read.
    ///
    /// Preconditions:
    /// - "missing": nothing at the path. "folder": a folder at the path. "locked": a valid file held open with FileShare.None
    ///   (IOException). "denied": a valid file the test's user may not read (a deny entry for reading data on Windows, mode 000
    ///   on Linux and macOS; UnauthorizedAccessException); skipped for root on Linux and macOS, which reads a file of any mode.
    ///
    /// Steps:
    /// - Read the path with ReadLinuxMachineId.
    ///
    /// Expected:
    /// - null, with no exception.
    /// </remarks>
    /// <param name="state">The state of the path.</param>
    [Theory]
    [InlineData("missing")]
    [InlineData("folder")]
    [InlineData("locked")]
    [InlineData("denied")]
    public void ReadLinuxMachineId_FileNotReadable_ReturnsNull(string state)
    {
        if (state == "folder")
        {
            Directory.CreateDirectory(MachineIdPath);
        }
        else if (state != "missing")
        {
            File.WriteAllText(MachineIdPath, Id + "\n");
        }

        if (state == "denied")
        {
            Deny(MachineIdPath);
        }

        using FileStream? holder = state == "locked" ? new FileStream(MachineIdPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;

        Assert.Null(MachineIdSources.ReadLinuxMachineId(MachineIdPath));
    }

    /// <summary>
    /// The SMBIOS walker skips a type 1 structure too short to hold the UUID, stops at the end-of-table structure, and returns
    /// null for a structure whose length is under 4 or runs past the table.
    /// </summary>
    /// <remarks>
    /// Design §10.3: the walk of the SMBIOS table and the cases in which the UUID cannot be read.
    ///
    /// Preconditions:
    /// - A type 1 structure of length 0x17 with UUID bytes AA, then a type 1 structure of length 0x1B with UUID 01..10.
    /// - That full type 1 structure alone, cut to 0x1B bytes (it ends at the table's end) and to 0x1A bytes (it runs past it).
    /// - The end-of-table structure, then the full type 1 structure.
    /// - A type 0 structure of length 2, then the full type 1 structure.
    /// - The full type 1 structure, then another of length 0x1B with UUID bytes BB.
    /// - A type 0 structure alone, so the table ends before a type 1 structure; the same with 3 more bytes (1, 0x1B, 1).
    /// - A type 0 structure whose string set has no two zero bytes before the table ends.
    ///
    /// Steps:
    /// - Parse each table.
    ///
    /// Expected:
    /// - 01..10 for the first two and for the two type 1 structures (the first one counts); null for the others.
    /// </remarks>
    [Fact]
    public void ParseSmbiosSystemUuid_ShortOrMalformedStructures_FollowTheirRules()
    {
        byte[] uuid = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        byte[] full = [1, 0x1B, 1, 0, 0, 0, 0, 0, .. uuid, 0, 0, 0, 0, 0];
        byte[] second = [1, 0x1B, 2, 0, 0, 0, 0, 0, .. Enumerable.Repeat((byte)0xBB, 16), 0, 0, 0, 0, 0];
        byte[] shortType1 = [1, 0x17, 1, 0, 0, 0, 0, 0, .. Enumerable.Repeat((byte)0xAA, 15), 0, 0];
        byte[] end = [127, 4, 2, 0, 0, 0];
        byte[] lengthUnderFour = [0, 2, 0, 0];
        byte[] type0 = [0, 4, 0, 0, 0, 0];

        Assert.Equal(uuid, MachineIdSources.ParseSmbiosSystemUuid([.. shortType1, .. full]));
        Assert.Equal(uuid, MachineIdSources.ParseSmbiosSystemUuid(full.AsSpan(0, 0x1B)));
        Assert.Equal(uuid, MachineIdSources.ParseSmbiosSystemUuid([.. full, .. second]));
        Assert.Null(MachineIdSources.ParseSmbiosSystemUuid(type0));
        Assert.Null(MachineIdSources.ParseSmbiosSystemUuid([.. type0, 1, 0x1B, 1]));
        Assert.Null(MachineIdSources.ParseSmbiosSystemUuid([0, 4, 0, 0, (byte)'A', 0, (byte)'B']));
        Assert.Null(MachineIdSources.ParseSmbiosSystemUuid(full.AsSpan(0, 0x1A)));
        Assert.Null(MachineIdSources.ParseSmbiosSystemUuid([.. end, .. full]));
        Assert.Null(MachineIdSources.ParseSmbiosSystemUuid([.. lengthUnderFour, .. full]));
    }

    private static void Deny(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var file = new FileInfo(path);
            FileSecurity rules = file.GetAccessControl();
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;
            rules.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadData, AccessControlType.Deny));
            file.SetAccessControl(rules);
        }
        else
        {
            Assert.SkipWhen(Environment.IsPrivilegedProcess, "Root reads a file of any mode.");
            File.SetUnixFileMode(path, UnixFileMode.None);
        }
    }
}
