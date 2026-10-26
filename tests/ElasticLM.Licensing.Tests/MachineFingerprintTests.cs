namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Tests of <see cref="MachineFingerprint"/> and <see cref="MachineIdSources"/> (design §10.3).
/// </summary>
public sealed class MachineFingerprintTests
{
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
    /// - A Windows, Linux, or macOS machine with an OS machine ID.
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

        if (OperatingSystem.IsLinux() && !File.Exists("/etc/machine-id"))
        {
            Assert.Skip("This Linux machine has no /etc/machine-id (for example, a container).");
        }

        IReadOnlyList<string> first = MachineFingerprint.Compute("acme-cad");
        IReadOnlyList<string> second = MachineFingerprint.Compute("acme-cad");

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
        Assert.All(first, f => Assert.Matches("^[0-9a-f]{32}$", f));
    }

    /// <summary>
    /// The machine-id parser accepts 32 lowercase hex characters with an optional newline and rejects everything else.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Contents as machine-id(5) describes, and broken ones.
    ///
    /// Steps:
    /// - Parse each.
    ///
    /// Expected:
    /// - A 16-byte value for the valid forms; null for uppercase, "uninitialized", a short ID, and an empty file.
    /// </remarks>
    [Theory]
    [InlineData("00112233445566778899aabbccddeeff\n", true)]
    [InlineData("00112233445566778899aabbccddeeff", true)]
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
}
