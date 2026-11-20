using System.Security.Cryptography;

namespace ElasticLM.Issuing.Tests;

/// <summary>
/// The rename that gives a log file its name (design §18.1, V1d16): it fails when the name exists, and on Linux and macOS it
/// flushes the folder.
/// </summary>
public sealed class DurableRenameTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "elm-rename-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));

    public DurableRenameTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// A file takes a free name, with its content, and the call reports the folder flushed; a name that exists is refused and
    /// both files stay as they were.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Files a.partial ("one") and b.partial ("two") in a folder, the first held open for writing with delete sharing, as
    ///   the log holds a file it is starting.
    ///
    /// Steps:
    /// - Move a.partial to a; move b.partial to a.
    ///
    /// Expected:
    /// - True with no problem, a holds "one", and a.partial is gone; IOException, a still holds "one", and b.partial stays.
    /// </remarks>
    [Fact]
    public void Move_FreeNameAndTakenName_RenamesAndRefuses()
    {
        string first = Path.Combine(_dir, "a.partial");
        string second = Path.Combine(_dir, "b.partial");
        string target = Path.Combine(_dir, "a");
        File.WriteAllText(second, "two");
        using (var held = new FileStream(first, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete))
        {
            held.Write("one"u8);
            held.Flush(flushToDisk: true);

            Assert.True(DurableRename.Move(first, target, out string? problem));
            Assert.Null(problem);
        }

        Assert.Equal("one", File.ReadAllText(target));
        Assert.False(File.Exists(first));
        Assert.Throws<IOException>(() => DurableRename.Move(second, target, out _));
        Assert.Equal("one", File.ReadAllText(target));
        Assert.True(File.Exists(second));
    }

    /// <summary>
    /// On Linux and macOS a folder that exists is flushed, and a folder that does not exist is reported with errno 2.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux or macOS; a folder, and a path in it that does not exist.
    ///
    /// Steps:
    /// - Flush each.
    ///
    /// Expected:
    /// - No problem; "The folder of the log cannot be opened to flush it (errno 2).".
    /// </remarks>
    [Fact]
    public void FlushFolder_ExistingAndMissing_FlushesAndReportsTheErrno()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows renames with write-through and flushes no folder.");

        Assert.Null(DurableRename.FlushFolder(_dir));
        Assert.Equal("The folder of the log cannot be opened to flush it (errno 2).", DurableRename.FlushFolder(Path.Combine(_dir, "missing")));
    }
}
