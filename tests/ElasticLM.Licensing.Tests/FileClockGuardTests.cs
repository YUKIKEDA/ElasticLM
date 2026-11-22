using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Tests of <see cref="FileClockGuard"/> (design §11.4).
/// </summary>
public sealed class FileClockGuardTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "elm-clock-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));

    public FileClockGuardTests() => Directory.CreateDirectory(_directory);

    private string StatePath => Path.Combine(_directory, "clock.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// A clock set back past the last time seen minus the skew is rejected, also by a new guard reading the same file.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A guard that observed T0 + 1 day.
    ///
    /// Steps:
    /// - Observe T0 + 1 day - 300 s and T0 + 1 day - 301 s with a token issued at T0, in the same guard and in a new guard on
    ///   the same file.
    ///
    /// Expected:
    /// - -300 s: accepted. -301 s: rejected, in both guards.
    /// </remarks>
    [Fact]
    public void Observe_ClockSetBack_RejectsBeyondSkew()
    {
        var guard = new FileClockGuard(StatePath);
        DateTimeOffset seen = T0.AddDays(1);
        Assert.True(guard.Observe(seen, T0, Skew));

        Assert.True(guard.Observe(seen.AddSeconds(-300), T0, Skew));
        Assert.False(guard.Observe(seen.AddSeconds(-301), T0, Skew));
        Assert.False(new FileClockGuard(StatePath).Observe(seen.AddSeconds(-301), T0, Skew));
    }

    /// <summary>
    /// The service's time after an online refresh replaces the last time seen, also by an earlier time, in this guard and in
    /// the file (design §11.4 step 4); a guard without state ignores it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A guard that observed T0 + 10 days (the clock was set forward).
    ///
    /// Steps:
    /// - Observe T0 with a token issued at T0; accept T0 as the service's time; observe T0 again in this guard and in a new
    ///   guard on the same file; accept a time on the stateless guard.
    ///
    /// Expected:
    /// - Rejected; accepted twice; no exception.
    /// </remarks>
    [Fact]
    public void AcceptServerTime_ReplacesTheLastTimeSeen()
    {
        var guard = new FileClockGuard(StatePath);
        Assert.True(guard.Observe(T0.AddDays(10), T0, Skew));
        Assert.False(guard.Observe(T0, T0, Skew));

        guard.AcceptServerTime(T0);
        Assert.True(guard.Observe(T0, T0, Skew));
        Assert.True(new FileClockGuard(StatePath).Observe(T0, T0, Skew));
        ((IClockGuard)StatelessClockGuard.Instance).AcceptServerTime(T0);
    }

    /// <summary>
    /// Without a state file, the token's iat is the floor: a clock before iat minus the skew is rejected.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - No state file.
    ///
    /// Steps:
    /// - Observe iat - 301 s, then iat - 300 s in a new guard.
    ///
    /// Expected:
    /// - -301 s: rejected. -300 s: accepted.
    /// </remarks>
    [Fact]
    public void Observe_NoState_UsesIssuedAtAsFloor()
    {
        Assert.False(new FileClockGuard(StatePath).Observe(T0.AddSeconds(-301), T0, Skew));
        Assert.True(new FileClockGuard(Path.Combine(_directory, "other.json")).Observe(T0.AddSeconds(-300), T0, Skew));
    }

    /// <summary>
    /// With an installation key, a state file whose time was edited fails its signature and falls back to the token's iat.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A signed state at T0 + 30 days.
    ///
    /// Steps:
    /// - Rewrite lastSeen to T0 + 60 days keeping the signature; observe T0 + 10 days with a token issued at T0.
    ///
    /// Expected:
    /// - Accepted: the edited value is ignored, and the floor is the token's iat, not T0 + 60 days.
    /// </remarks>
    [Fact]
    public void Observe_EditedSignedState_FallsBackToIssuedAt()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.True(new FileClockGuard(StatePath, key).Observe(T0.AddDays(30), T0, Skew));
        string text = File.ReadAllText(StatePath);
        long original = T0.AddDays(30).ToUnixTimeSeconds();
        long edited = T0.AddDays(60).ToUnixTimeSeconds();
        File.WriteAllText(
            StatePath,
            text.Replace(
                original.ToString(System.Globalization.CultureInfo.InvariantCulture),
                edited.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal
            )
        );

        Assert.True(new FileClockGuard(StatePath, key).Observe(T0.AddDays(10), T0, Skew));
    }

    /// <summary>
    /// With an installation key, a state file that carries no valid signature counts as missing: without the sig member,
    /// with an empty signature, and with a signature that is not 64 bytes or not the key's. The guard falls back to the
    /// token's iat and writes a new signed state, without an exception (design §11.4, §20.7 V1d14).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A signed state at T0 + 30 days, which a new guard of the same key honors: it rejects T0 + 10 days.
    /// - The same file with its time kept and its sig changed in one way: the member removed; an empty string; the first 63
    ///   bytes of the signature; 64 zero bytes; the signature and one more zero byte.
    ///
    /// Steps:
    /// - Observe T0 + 10 days with a token issued at T0 in a new guard; observe 301 s earlier in another new guard.
    ///
    /// Expected:
    /// - Accepted: the state is ignored, and the floor is the token's iat. Then rejected: the first guard replaced the state
    ///   with a signed one at T0 + 10 days.
    /// </remarks>
    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("63-bytes")]
    [InlineData("64-zero-bytes")]
    [InlineData("65-bytes")]
    public void Observe_StateWithoutAValidSignature_FallsBackToIssuedAt(string form)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.True(new FileClockGuard(StatePath, key).Observe(T0.AddDays(30), T0, Skew));
        Assert.False(new FileClockGuard(StatePath, key).Observe(T0.AddDays(10), T0, Skew));
        JsonObject state = JsonNode.Parse(File.ReadAllText(StatePath))!.AsObject();
        byte[] signature = Base64Url.DecodeFromChars(state["sig"]!.GetValue<string>());
        Assert.Equal(64, signature.Length);
        byte[]? changed = form switch
        {
            "missing" => null,
            "empty" => [],
            "63-bytes" => signature[..63],
            "64-zero-bytes" => new byte[64],
            _ => [.. signature, 0],
        };
        if (changed is null)
        {
            Assert.True(state.Remove("sig"));
        }
        else
        {
            state["sig"] = Base64Url.EncodeToString(changed);
        }

        File.WriteAllText(StatePath, state.ToJsonString());

        Assert.True(new FileClockGuard(StatePath, key).Observe(T0.AddDays(10), T0, Skew));
        Assert.False(new FileClockGuard(StatePath, key).Observe(T0.AddDays(10).AddSeconds(-301), T0, Skew));
    }

    /// <summary>
    /// With an installation key, an untouched signed state is honored across guards.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A signed state at T0 + 30 days.
    ///
    /// Steps:
    /// - Observe T0 + 10 days in a new guard with the same key.
    ///
    /// Expected:
    /// - Rejected.
    /// </remarks>
    [Fact]
    public void Observe_SignedState_IsHonored()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.True(new FileClockGuard(StatePath, key).Observe(T0.AddDays(30), T0, Skew));

        Assert.False(new FileClockGuard(StatePath, key).Observe(T0.AddDays(10), T0, Skew));
    }

    /// <summary>
    /// When the state cannot be written, the guard keeps going on its in-memory value and the token's iat, without an
    /// exception.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A state path inside a directory that does not exist.
    ///
    /// Steps:
    /// - Observe T0 + 1 day; observe T0 + 1 day - 301 s in the same guard.
    ///
    /// Expected:
    /// - The first is accepted and no file appears; the second is rejected from the in-memory value.
    /// </remarks>
    [Fact]
    public void Observe_UnwritableState_KeepsGoingInMemory()
    {
        string path = Path.Combine(_directory, "missing", "clock.json");
        var guard = new FileClockGuard(path);

        Assert.True(guard.Observe(T0.AddDays(1), T0, Skew));
        Assert.False(File.Exists(path));
        Assert.False(guard.Observe(T0.AddDays(1).AddSeconds(-301), T0, Skew));
    }

    /// <summary>
    /// After a failed write, the guard does not try again until the time advanced by 60 seconds.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A state path in a missing directory; the first write fails.
    ///
    /// Steps:
    /// - Create the directory; observe T0 + 59 s; check the file; observe T0 + 60 s; check the file.
    ///
    /// Expected:
    /// - No file after +59 s; the file exists after +60 s.
    /// </remarks>
    [Fact]
    public void Observe_AfterFailedWrite_WaitsBeforeRetrying()
    {
        string directory = Path.Combine(_directory, "later");
        string path = Path.Combine(directory, "clock.json");
        var guard = new FileClockGuard(path);
        Assert.True(guard.Observe(T0, T0, Skew));
        Directory.CreateDirectory(directory);

        Assert.True(guard.Observe(T0.AddSeconds(59), T0, Skew));
        Assert.False(File.Exists(path));
        Assert.True(guard.Observe(T0.AddSeconds(60), T0, Skew));
        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// The state is written only when the time advanced by 60 seconds or more since the last write.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A guard that observed T0.
    ///
    /// Steps:
    /// - Observe T0 + 59 s, read the file; observe T0 + 60 s, read the file.
    ///
    /// Expected:
    /// - After +59 s the file still holds T0; after +60 s it holds T0 + 60 s.
    /// </remarks>
    [Fact]
    public void Observe_SmallAdvance_DoesNotWrite()
    {
        var guard = new FileClockGuard(StatePath);
        Assert.True(guard.Observe(T0, T0, Skew));
        string seconds0 = T0.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        string seconds60 = T0.AddSeconds(60).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(guard.Observe(T0.AddSeconds(59), T0, Skew));
        Assert.Contains(seconds0, File.ReadAllText(StatePath), StringComparison.Ordinal);
        Assert.True(guard.Observe(T0.AddSeconds(60), T0, Skew));
        Assert.Contains(seconds60, File.ReadAllText(StatePath), StringComparison.Ordinal);
    }

    /// <summary>
    /// On Linux and macOS the state is written for this user only (mode 0600), also over a state file that other users could
    /// write, and no temporary file is left (design §11.4 step 7, V1d24d).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux or macOS; Windows has no modes and is skipped.
    /// - A state file with the content {} and mode 0666.
    ///
    /// Steps:
    /// - Observe T0 with a token issued at T0 (the file holds no state, so the guard writes); read the mode; observe T0 -
    ///   301 s in a new guard.
    ///
    /// Expected:
    /// - Accepted; the file has mode 0600; the folder holds no .tmp file; the new guard rejects T0 - 301 s, so the file holds
    ///   the new state.
    /// </remarks>
    [Fact]
    public void Observe_WritesTheStateForThisUserOnlyOnUnix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Modes are Linux and macOS only.");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(StatePath, "{}");
        File.SetUnixFileMode(StatePath, (UnixFileMode)0b110_110_110);

        Assert.True(new FileClockGuard(StatePath).Observe(T0, T0, Skew));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(StatePath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.False(new FileClockGuard(StatePath).Observe(T0.AddSeconds(-301), T0, Skew));
    }

    /// <summary>
    /// When the new state cannot replace the old one, the guard deletes its temporary file, throws nothing, keeps going on
    /// its in-memory value, and does not write again for 60 seconds (design §11.4 steps 6 and 7, V1d24d).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A folder with a file in it at the state path, so no file can replace it.
    ///
    /// Steps:
    /// - Observe T0 + 1 day with a token issued at T0; list the .tmp files beside the state path; observe T0 + 1 day - 301 s;
    ///   remove the folder; observe T0 + 1 day + 59 s; observe T0 + 1 day + 60 s.
    ///
    /// Expected:
    /// - The first is accepted without an exception; no .tmp file; the folder and its file are still there. -301 s is
    ///   rejected from the in-memory value. After +59 s no state file; after +60 s the state file exists.
    /// </remarks>
    [Fact]
    public void Observe_ReplacementFails_DeletesTheTemporaryFileAndKeepsGoing()
    {
        Directory.CreateDirectory(StatePath);
        File.WriteAllText(Path.Combine(StatePath, "inside.txt"), "inside");
        var guard = new FileClockGuard(StatePath);
        DateTimeOffset seen = T0.AddDays(1);

        Assert.True(guard.Observe(seen, T0, Skew));

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.Equal("inside", File.ReadAllText(Path.Combine(StatePath, "inside.txt")));
        Assert.False(guard.Observe(seen.AddSeconds(-301), T0, Skew));
        Directory.Delete(StatePath, recursive: true);
        Assert.True(guard.Observe(seen.AddSeconds(59), T0, Skew));
        Assert.False(File.Exists(StatePath));
        Assert.True(guard.Observe(seen.AddSeconds(60), T0, Skew));
        Assert.True(File.Exists(StatePath));
    }

    /// <summary>
    /// When the temporary file was created but cannot be written, the guard deletes it, throws nothing, keeps going on its
    /// in-memory value, and does not write again for 60 seconds (design §11.4 steps 6 and 7, V1d24d).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux, not as root, with sudo that lets this user mount without a password, as the CI runners do; on GitHub Actions
    ///   the test fails without it, elsewhere it is skipped and says so. Other OSes have no tmpfs.
    /// - A tmpfs of 4 KiB mounted on a new folder and owned by this user, holding a file of 4096 bytes, so a new file can be
    ///   created and no page is left for its content.
    ///
    /// Steps:
    /// - Observe T0 + 1 day with a token issued at T0 and the state path in that folder; list the folder; observe T0 + 1 day
    ///   - 301 s; delete the 4096-byte file; observe T0 + 1 day + 59 s; observe T0 + 1 day + 60 s. Unmount at the end.
    ///
    /// Expected:
    /// - The first is accepted without an exception, and the folder holds only the 4096-byte file. -301 s is rejected from
    ///   the in-memory value. After +59 s no state file; after +60 s the state file exists.
    /// </remarks>
    [Fact]
    public void Observe_DiskFullAfterCreating_DeletesTheTemporaryFileAndKeepsGoing()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "tmpfs is Linux only.");
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "The test runs as a user that is not root.");
        string disk = Path.Combine(_directory, "disk");
        Directory.CreateDirectory(disk);
        Sudo("mount", "-t", "tmpfs", "-o", $"size=4k,uid={UserId()},mode=0700", "tmpfs", disk);
        try
        {
            string filler = Path.Combine(disk, "filler");
            File.WriteAllBytes(filler, new byte[4096]);
            string path = Path.Combine(disk, "clock.json");
            var guard = new FileClockGuard(path);
            DateTimeOffset seen = T0.AddDays(1);

            Assert.True(guard.Observe(seen, T0, Skew));

            Assert.Equal([filler], Directory.GetFileSystemEntries(disk));
            Assert.False(guard.Observe(seen.AddSeconds(-301), T0, Skew));
            File.Delete(filler);
            Assert.True(guard.Observe(seen.AddSeconds(59), T0, Skew));
            Assert.False(File.Exists(path));
            Assert.True(guard.Observe(seen.AddSeconds(60), T0, Skew));
            Assert.True(File.Exists(path));
        }
        finally
        {
            Sudo("umount", disk);
        }
    }

    /// <summary>
    /// Before writing, the guard deletes the temporary files of its state that no writer holds, and no other file (design
    /// §11.4 step 7, V1d24d).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Beside the state path clock.json, files that a write left: clock.json.0123456789ABCDEF.tmp and
    ///   clock.json.FEDCBA9876543210.tmp; and files of other shapes: clock.json.aaaaaaaaaaaaaaaa.tmp (lowercase; Windows names ignore case, so it differs from the others in more than case),
    ///   clock.json.0123456789ABCDE.tmp (15 digits), clock.json.0123456789ABCDEG.tmp (not hexadecimal),
    ///   other.json.0123456789ABCDEF.tmp (another file's).
    ///
    /// Steps:
    /// - Observe T0 with a token issued at T0 (no state, so the guard writes).
    ///
    /// Expected:
    /// - Accepted; the two files of clock.json are gone; the four others are still there; the state file exists.
    /// </remarks>
    [Fact]
    public void Observe_LeftoverTemporaryFiles_AreDeletedBeforeWriting()
    {
        string[] leftovers = ["clock.json.0123456789ABCDEF.tmp", "clock.json.FEDCBA9876543210.tmp"];
        string[] others =
        [
            "clock.json.aaaaaaaaaaaaaaaa.tmp",
            "clock.json.0123456789ABCDE.tmp",
            "clock.json.0123456789ABCDEG.tmp",
            "other.json.0123456789ABCDEF.tmp",
        ];
        foreach (string name in leftovers.Concat(others))
        {
            File.WriteAllText(Path.Combine(_directory, name), "left");
        }

        Assert.True(new FileClockGuard(StatePath).Observe(T0, T0, Skew));

        foreach (string name in leftovers)
        {
            Assert.False(File.Exists(Path.Combine(_directory, name)), name);
        }

        foreach (string name in others)
        {
            Assert.True(File.Exists(Path.Combine(_directory, name)), name);
        }

        Assert.True(File.Exists(StatePath));
    }

    /// <summary>
    /// One write looks at no more than 1,000 leftover temporary files (design §11.4 step 7, V1d24d).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,000 or 1,001 files clock.json.&lt;16 uppercase hexadecimal digits&gt;.tmp beside the state path.
    ///
    /// Steps:
    /// - Observe T0 with a token issued at T0 (no state, so the guard writes); count the .tmp files.
    ///
    /// Expected:
    /// - Accepted; 1,000: none is left; 1,001: one is left.
    /// </remarks>
    /// <param name="count">The number of leftover files.</param>
    /// <param name="left">The number expected to stay.</param>
    [Theory]
    [InlineData(1000, 0)]
    [InlineData(1001, 1)]
    public void Observe_ManyLeftovers_DeletesAtMostAThousand(int count, int left)
    {
        for (int i = 0; i < count; i++)
        {
            File.WriteAllText(Path.Combine(_directory, $"clock.json.{i:X16}.tmp"), "left");
        }

        Assert.True(new FileClockGuard(StatePath).Observe(T0, T0, Skew));

        Assert.Equal(left, Directory.GetFiles(_directory, "*.tmp").Length);
    }

    /// <summary>
    /// A temporary file that another writer holds is not deleted: the writer holds it until it is in place (design §11.4
    /// step 7, V1d24d).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - clock.json.0123456789ABCDEF.tmp beside the state path, opened for writing with FileShare.Read | FileShare.Delete,
    ///   as a writer holds its file, and kept open.
    ///
    /// Steps:
    /// - Observe T0 with a token issued at T0 (no state, so the guard writes); close the held file.
    ///
    /// Expected:
    /// - Accepted; the held file is still there with its content; the state file exists.
    /// </remarks>
    [Fact]
    public void Observe_TemporaryFileHeldByAnotherWriter_IsLeft()
    {
        string held = Path.Combine(_directory, "clock.json.0123456789ABCDEF.tmp");
        using (var writer = new FileStream(held, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete))
        {
            writer.Write("held"u8);
            writer.Flush();

            Assert.True(new FileClockGuard(StatePath).Observe(T0, T0, Skew));
        }

        Assert.Equal("held", File.ReadAllText(held));
        Assert.True(File.Exists(StatePath));
    }

    /// <summary>
    /// A link at a leftover's name is deleted itself, and what it points to is not opened: a FIFO behind it would hold the
    /// guard (design §11.4 step 7, V1d24d).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux or macOS (mkfifo); Windows is skipped.
    /// - A FIFO made with mkfifo in a folder beside the state's folder, and a symbolic link to it at
    ///   clock.json.0123456789ABCDEF.tmp beside the state path.
    ///
    /// Steps:
    /// - Observe T0 with a token issued at T0 (no state, so the guard writes).
    ///
    /// Expected:
    /// - Accepted without waiting; the link is gone; the FIFO is still there; the state file exists.
    /// </remarks>
    [Fact]
    public void Observe_LinkAtALeftoverName_DeletesTheLinkOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "mkfifo is Linux and macOS only.");
        string fifo = Path.Combine(_directory, "elsewhere", "fifo");
        Directory.CreateDirectory(Path.GetDirectoryName(fifo)!);
        using (var mkfifo = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mkfifo", [fifo]))!)
        {
            mkfifo.WaitForExit();
            Assert.Equal(0, mkfifo.ExitCode);
        }

        string link = Path.Combine(_directory, "clock.json.0123456789ABCDEF.tmp");
        File.CreateSymbolicLink(link, fifo);

        Assert.True(new FileClockGuard(StatePath).Observe(T0, T0, Skew));

        Assert.False(File.Exists(link));
        Assert.True(File.Exists(fifo));
        Assert.True(File.Exists(StatePath));
    }

    /// <summary>
    /// A FIFO at a leftover's name is neither opened nor deleted: its open would wait for a writer and hold the guard (design
    /// §11.4 step 8, V1d24d, the owner's 24d-6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux or macOS (mkfifo); Windows is skipped.
    /// - A FIFO made with mkfifo at clock.json.0123456789ABCDEF.tmp beside the state path.
    ///
    /// Steps:
    /// - Observe T0 with a token issued at T0 (no state, so the guard writes).
    ///
    /// Expected:
    /// - Accepted without waiting; the FIFO is still there; the state file exists.
    /// </remarks>
    [Fact]
    public void Observe_FifoAtALeftoverName_IsLeftAlone()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "mkfifo is Linux and macOS only.");
        string fifo = Path.Combine(_directory, "clock.json.0123456789ABCDEF.tmp");
        using (var mkfifo = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mkfifo", [fifo]))!)
        {
            mkfifo.WaitForExit();
            Assert.Equal(0, mkfifo.ExitCode);
        }

        Assert.True(new FileClockGuard(StatePath).Observe(T0, T0, Skew));

        Assert.True(File.Exists(fifo));
        Assert.True(File.Exists(StatePath));
    }

    // The user ID of this process, read with id -u: .NET has no API for it.
    private static string UserId()
    {
        using var id = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("id", "-u") { RedirectStandardOutput = true })!;
        string output = id.StandardOutput.ReadToEnd().Trim();
        id.WaitForExit();
        Assert.Equal(0, id.ExitCode);
        return output;
    }

    // Runs a command with sudo; skips the test where this user may not, except on GitHub Actions.
    private static void Sudo(params string[] arguments)
    {
        using var sudo = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("sudo", ["-n", .. arguments]) { RedirectStandardError = true, RedirectStandardOutput = true }
        )!;
        string error = sudo.StandardError.ReadToEnd();
        sudo.WaitForExit();
        if (sudo.ExitCode != 0)
        {
            string why = $"sudo -n {arguments[0]} exited with {sudo.ExitCode}: {error.Trim()}";
            Assert.False(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "On GitHub Actions this test must run. " + why);
            Assert.Skip("This machine does not let the test run " + arguments[0] + " as root. " + why);
        }
    }
}
