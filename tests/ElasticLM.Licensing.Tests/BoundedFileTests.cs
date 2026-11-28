using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace ElasticLM.Licensing.Tests;

/// <summary>
/// Tests of <see cref="BoundedFile"/>, the one reader of whole files (design §8.6, the reading of files, V1d24c).
/// </summary>
public sealed class BoundedFileTests : IDisposable
{
    private const int Limit = 16;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "elm-bounded-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));

    public BoundedFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// A file of the limit or fewer bytes is read whole, and a file of one byte more is too large.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Files of 0, 15, 16, and 17 bytes, each byte different from its neighbours.
    ///
    /// Steps:
    /// - Read each with a limit of 16 bytes.
    ///
    /// Expected:
    /// - The bytes of the files of 0, 15, and 16 bytes; null for the file of 17.
    /// </remarks>
    /// <param name="length">The file's length.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(Limit - 1)]
    [InlineData(Limit)]
    [InlineData(Limit + 1)]
    public void Read_FileAtTheLimitAndOnePast_IsWholeThenNull(int length)
    {
        byte[] content = Content(length);
        string path = Path.Combine(_directory, "file");
        File.WriteAllBytes(path, content);

        byte[]? read = BoundedFile.Read(path, Limit, FileShare.Read);

        Assert.Equal(length <= Limit ? content : null, read);
    }

    /// <summary>
    /// A file far larger than the limit is too large, and only about the limit is read and allocated, not the file's length.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A file of 64 MiB (a sparse file where the file system makes one).
    ///
    /// Steps:
    /// - Read it with a limit of 16 bytes, counting the bytes this thread allocates.
    ///
    /// Expected:
    /// - Null, and less than 1 MiB allocated. A reader that read the whole file, or sized its buffer from the file's length,
    ///   would allocate 64 MiB.
    /// </remarks>
    [Fact]
    public void Read_FileFarLargerThanTheLimit_ReadsAboutTheLimit()
    {
        string path = Path.Combine(_directory, "large");
        using (var file = new FileStream(path, FileMode.CreateNew))
        {
            file.SetLength(64L * 1024 * 1024);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[]? read = BoundedFile.Read(path, Limit, FileShare.Read);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(read);
        Assert.InRange(allocated, 0, 1024 * 1024);
    }

    /// <summary>
    /// A file that cannot seek (a pipe) is read without its length: the limit's bytes whole, and a pipe that gives one byte
    /// more, which the writer sends after the limit's bytes as a file that grows while it is read, is too large without a
    /// wait for the pipe's end.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pipe that a path names: on Windows a named pipe \\.\pipe\elm-…; on Linux and macOS a FIFO made with mkfifo. A
    ///   writer on another thread writes 16 bytes, then, for the second case, 1 byte more and then 4,096 more, and closes the
    ///   pipe only after the read has returned.
    ///
    /// Steps:
    /// - Read it with a limit of 16 bytes: once with 16 bytes written (the writer closes at once), once with the bytes after
    ///   them.
    ///
    /// Expected:
    /// - The 16 bytes; then null, returned while the writer still holds the pipe open. A reader that asked the pipe for its
    ///   length would throw NotSupportedException.
    /// </remarks>
    /// <param name="grows">Whether the writer sends bytes after the limit's.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_FileThatCannotSeek_IsReadWithinTheLimit(bool grows)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        byte[] content = Content(Limit);
        using var returned = new SemaphoreSlim(0);
        string path;
        Task writer;
        NamedPipeServerStream? server = null;
        async Task WriteAsync(Stream pipe)
        {
            await pipe.WriteAsync(content, cancel);
            if (grows)
            {
                await pipe.WriteAsync(Content(1 + 4096), cancel);
                await pipe.FlushAsync(cancel);

                // The reader returns before the pipe ends: it does not wait for the close.
                await returned.WaitAsync(cancel);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            string name = "elm-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
            server = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            path = @"\\.\pipe\" + name;
            writer = Task.Run(
                async () =>
                {
                    await server.WaitForConnectionAsync(cancel);
                    try
                    {
                        await WriteAsync(server);
                        if (!grows && OperatingSystem.IsWindows())
                        {
                            server.WaitForPipeDrain();
                        }
                    }
                    catch (IOException) when (grows)
                    {
                        // The reader closed its end after the limit's byte; what it did not read is lost.
                    }

                    server.Dispose();
                },
                cancel
            );
        }
        else
        {
            path = Path.Combine(_directory, "pipe");
            MakeFifo(path);
            writer = Task.Run(
                async () =>
                {
                    await using var pipe = new FileStream(path, FileMode.Open, FileAccess.Write);
                    try
                    {
                        await WriteAsync(pipe);
                    }
                    catch (IOException) when (grows)
                    {
                        // The reader closed its end after the limit's byte (EPIPE).
                    }
                },
                cancel
            );
        }

        try
        {
            byte[]? read = await Task.Run(() => BoundedFile.Read(path, Limit, FileShare.Read), cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            returned.Release();
            await writer.WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.Equal(grows ? null : content, read);
        }
        finally
        {
            returned.Release();
            server?.Dispose();
        }
    }

    /// <summary>
    /// A file that cannot seek gives up no byte past the one after the limit: the read takes 17 bytes from a pipe that holds
    /// more (design §8.6, the reading of files).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux or macOS; a FIFO made with mkfifo, which the test holds open for reading and writing (so that neither its own
    ///   open nor the reader's waits for the other end), and into which it writes 8,192 bytes of letters.
    ///
    /// Steps:
    /// - Read the FIFO with a limit of 16 bytes; write the byte FF to it; read from the test's own handle up to the FF,
    ///   counting the letters left.
    ///
    /// Expected:
    /// - Null, and 8,175 letters left (8,192 - 17). A reader that asked the pipe for more than 17 bytes would leave fewer.
    ///   FileStream's default buffer is not one such: it reads a file that cannot seek without the buffer
    ///   (BufferedFileStreamStrategy.ReadSpan, dotnet/runtime release/10.0), so this test passes with it too.
    /// </remarks>
    [Fact]
    public void Read_PipeThatHoldsMore_TakesOneBytePastTheLimitOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A FIFO that a path names is Linux and macOS only.");
        string path = Path.Combine(_directory, "pipe");
        MakeFifo(path);
        using var pipe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 0);
        pipe.Write(Content(8192));

        byte[]? read = BoundedFile.Read(path, Limit, FileShare.ReadWrite);
        pipe.Write([0xFF]);
        int left = 0;
        byte[] chunk = new byte[1024];
        bool end = false;
        while (!end)
        {
            int count = pipe.Read(chunk);
            Assert.NotEqual(0, count);
            int at = Array.IndexOf(chunk, (byte)0xFF, 0, count);
            left += at < 0 ? count : at;
            end = at >= 0;
        }

        Assert.Null(read);
        Assert.Equal(8192 - (Limit + 1), left);
    }

    /// <summary>
    /// A device that never ends (/dev/zero, and a link to it) is too large after one byte past the limit, where a reader to
    /// the end would not stop (design §8.6, the reading of files).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux or macOS; /dev/zero, and a symbolic link to it in the test's folder.
    ///
    /// Steps:
    /// - Read each with a limit of 16 bytes.
    ///
    /// Expected:
    /// - Null for both.
    /// </remarks>
    [Fact]
    public void Read_DeviceWithoutEnd_IsTooLarge()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "/dev/zero is Linux and macOS only.");
        string link = Path.Combine(_directory, "zero");
        File.CreateSymbolicLink(link, "/dev/zero");

        Assert.Null(BoundedFile.Read("/dev/zero", Limit, FileShare.Read));
        Assert.Null(BoundedFile.Read(link, Limit, FileShare.Read));
    }

    /// <summary>
    /// A missing file and a folder are errors of the file system, which the callers catch, not a result.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A path where nothing is, and a folder.
    ///
    /// Steps:
    /// - Read each with a limit of 16 bytes.
    ///
    /// Expected:
    /// - FileNotFoundException; UnauthorizedAccessException.
    /// </remarks>
    [Fact]
    public void Read_MissingFileOrFolder_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => BoundedFile.Read(Path.Combine(_directory, "missing"), Limit, FileShare.Read));
        Assert.Throws<UnauthorizedAccessException>(() => BoundedFile.Read(_directory, Limit, FileShare.Read));
    }

    /// <summary>
    /// The file is opened with the sharing the caller gives: on Windows, a reader that does not share Write is refused while
    /// a writer holds the file, and one that shares it reads.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows (Linux and macOS take no sharing but FileShare.None into account); a file of 16 bytes that a handle of the
    ///   test holds open for writing with FileShare.ReadWrite | FileShare.Delete.
    ///
    /// Steps:
    /// - Read it with FileShare.Read; read it with FileShare.ReadWrite | FileShare.Delete.
    ///
    /// Expected:
    /// - IOException; the 16 bytes.
    /// </remarks>
    [Fact]
    public void Read_SharingGiven_IsTheSharingOfTheOpen()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses an open for the sharing of other handles.");
        string path = Path.Combine(_directory, "shared");
        File.WriteAllBytes(path, Content(Limit));

        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Throws<IOException>(() => BoundedFile.Read(path, Limit, FileShare.Read));
            Assert.Equal(Content(Limit), BoundedFile.Read(path, Limit, FileShare.ReadWrite | FileShare.Delete));
        }
    }

    /// <summary>
    /// The text of a file is decoded as File.ReadAllText decodes it: UTF-8, a byte order mark of UTF-8, UTF-16, or UTF-32
    /// left out and naming the encoding, bytes that do not decode replaced; past the limit, null.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Files of the bytes: none; "a"; EF BB BF "a"; FF FE "a" 00 (UTF-16 LE); FE FF 00 "a" (UTF-16 BE); FF FE 00 00 "a" 00
    ///   00 00 (UTF-32 LE); 00 00 FE FF 00 00 00 "a" (UTF-32 BE); FF FE "a" (UTF-16 cut in a character); "a" FF "b"; and 17
    ///   bytes of "a".
    ///
    /// Steps:
    /// - Read each with ReadAllText and a limit of 16 bytes, and, for all but the last, with File.ReadAllText.
    ///
    /// Expected:
    /// - "", "a", "a", "a", "a", "a", "a", "\uFFFD", "a\uFFFDb", each the same as File.ReadAllText; null for the 17 bytes.
    /// </remarks>
    [Fact]
    public void ReadAllText_File_IsDecodedAsFileReadAllTextDoes()
    {
        string path = Path.Combine(_directory, "text");
        foreach (
            (byte[] bytes, string expected) in new (byte[], string)[]
            {
                ([], string.Empty),
                ("a"u8.ToArray(), "a"),
                ([0xEF, 0xBB, 0xBF, (byte)'a'], "a"),
                ([0xFF, 0xFE, (byte)'a', 0x00], "a"),
                ([0xFE, 0xFF, 0x00, (byte)'a'], "a"),
                ([0xFF, 0xFE, 0x00, 0x00, (byte)'a', 0x00, 0x00, 0x00], "a"),
                ([0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, (byte)'a'], "a"),
                ([0xFF, 0xFE, (byte)'a'], "\uFFFD"),
                ([(byte)'a', 0xFF, (byte)'b'], "a\uFFFDb"),
            }
        )
        {
            File.WriteAllBytes(path, bytes);
            Assert.Equal((expected, expected), (BoundedFile.ReadAllText(path, Limit), File.ReadAllText(path)));
        }

        File.WriteAllText(path, new string('a', Limit + 1), new UTF8Encoding(false));
        Assert.Null(BoundedFile.ReadAllText(path, Limit));
    }

    // Bytes that differ from their neighbours, so a shifted or cut copy does not compare equal.
    private static byte[] Content(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)('a' + (i % 26)))];

    // Makes a FIFO at a path with mkfifo, which Linux and macOS have.
    private static void MakeFifo(string path)
    {
        using var mkfifo = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("mkfifo", [path]) { RedirectStandardError = true, RedirectStandardOutput = true }
        )!;
        string error = mkfifo.StandardError.ReadToEnd();
        mkfifo.WaitForExit();
        Assert.True(mkfifo.ExitCode == 0, "mkfifo failed: " + error);
    }
}
