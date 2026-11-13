using System.Text;
using ElasticLM.Cli;
using ElasticLM.Client;
using ElasticLM.Client.Tests;

namespace ElasticLM.Server.Tests;

/// <summary>
/// The tests of this collection count the bytes the whole process allocates, so no other test may run beside them.
/// </summary>
[CollectionDefinition(nameof(ProcessAllocation), DisableParallelization = true)]
public sealed class ProcessAllocation;

/// <summary>
/// The memory that <c>elm server</c> takes for one answer of the administration API stays a small multiple of the answer
/// (design §13.7, the review of V1d6): the body is read once and checked and read without a document model, which takes
/// many times the length of a body dense in tokens.
/// </summary>
[Collection(nameof(ProcessAllocation))]
public sealed class AdminAnswerMemoryTests : IAsyncDisposable
{
    // The answer: 16 MiB of "[0,0,…]", one token in two bytes, with a Content-Length, so that elm reads it into one buffer.
    private const int BodyLength = 16 * 1024 * 1024;

    private readonly Deployment _deployment = new();

    public ValueTask DisposeAsync() => _deployment.DisposeAsync();

    /// <summary>
    /// For an answer of 16 MiB that holds a token in every two bytes, the process allocates less than twice the answer while
    /// the command runs.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The data folder of a stopped server, with its administrator token, and a listener of the test on admin.sock in it
    ///   that answers with status 200 and the body "[0,0,…,0]" of 16,777,215 bytes, with a Content-Length.
    /// - No other test runs in the process (a collection without parallel runs).
    ///
    /// Steps:
    /// - Count the bytes the process allocated (GC.GetTotalAllocatedBytes) around each command: elm server leases --json,
    ///   which writes the whole answer to a writer that drops it; elm server status, which reads it as a status; elm server
    ///   deactivate-proof, which asks for the status's autoRefresh.
    ///
    /// Expected:
    /// - The exit codes 0, 3, and 1.
    /// - Each command allocated less than 33,554,432 bytes, twice the answer. Measured on Windows 11: 17,171,368 bytes for
    ///   leases and 17,240,120 for leases --json, the answer's buffer and a little more. The code before the review, which
    ///   read the answer as a JsonDocument and cloned its root, allocated 369,544,424 bytes for leases, 22 times the answer.
    /// </remarks>
    [Fact]
    public async Task Commands_TokenDenseAnswer_AllocateLessThanTwiceTheAnswer()
    {
        byte[] body = Zeros(BodyLength);
        ElmServer server = await _deployment.StartServerAsync();
        await server.DisposeAsync();
        string folder = server.DataDirectory;
        await using RawAnswerServer listener = RawAnswerServer.StartOnSocket(AdminSocket.PathIn(folder), _ => RawAnswer.Bytes(body));
        string[] common = ["--product", "acme-cad", "--data", folder];

        (int Code, long Allocated) json = await AllocatedAsync(["server", "leases", "--json", .. common]);
        (int Code, long Allocated) status = await AllocatedAsync(["server", "status", .. common]);
        (int Code, long Allocated) proof = await AllocatedAsync(["server", "deactivate-proof", .. common, "--out", Path.Combine(folder, "p.file")]);

        Assert.Equal((0, 3, 1), (json.Code, status.Code, proof.Code));
        Assert.InRange(json.Allocated, 0, 2L * body.Length);
        Assert.InRange(status.Allocated, 0, 2L * body.Length);
        Assert.InRange(proof.Allocated, 0, 2L * body.Length);
    }

    /// <summary>
    /// For the same answer in chunks, without a length, the process allocates less than 2.5 times the answer: the buffer
    /// grows by doubling, and the body is handed on in that buffer, not copied out of it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The folder and listener of <see cref="Commands_TokenDenseAnswer_AllocateLessThanTwiceTheAnswer"/>; the answer is
    ///   the same 16,777,215 bytes in chunks of 64 KiB, without a Content-Length.
    ///
    /// Steps:
    /// - Count the bytes the process allocated around elm server deactivate-proof.
    ///
    /// Expected:
    /// - Exit code 1, and less than 41,943,040 bytes allocated, 2.5 times the answer. The buffers of 1 KiB to 16 MiB add
    ///   up to twice the answer (measured with 64 MiB: 134,921,808 bytes, 2.01 times); a copy of the body out of the
    ///   buffer adds one time more.
    /// </remarks>
    [Fact]
    public async Task Command_TokenDenseAnswerInChunks_AllocatesLessThanTwoAndAHalfTimesTheAnswer()
    {
        byte[] body = Zeros(BodyLength);
        ElmServer server = await _deployment.StartServerAsync();
        await server.DisposeAsync();
        string folder = server.DataDirectory;
        await using RawAnswerServer listener = RawAnswerServer.StartOnSocket(
            AdminSocket.PathIn(folder),
            _ => RawAnswer.Bytes(body) with { Chunked = true, Declared = null }
        );

        (int Code, long Allocated) proof = await AllocatedAsync([
            "server",
            "deactivate-proof",
            "--product",
            "acme-cad",
            "--data",
            folder,
            "--out",
            Path.Combine(folder, "p.file"),
        ]);

        Assert.Equal(1, proof.Code);
        Assert.InRange(proof.Allocated, 0, 5L * body.Length / 2);
    }

    private static byte[] Zeros(int length)
    {
        // "[" at 0, a "0" at every odd index, a "," at every even one, and "]" at the last, which is even.
        byte[] body = new byte[length - (length % 2 == 0 ? 1 : 0)];
        for (int i = 1; i < body.Length; i++)
        {
            body[i] = i % 2 == 1 ? (byte)'0' : (byte)',';
        }

        body[0] = (byte)'[';
        body[^1] = (byte)']';
        return body;
    }

    private async Task<(int Code, long Allocated)> AllocatedAsync(string[] args)
    {
        string pem = File.ReadAllText(_deployment.RootKeyFile);
        var host = new CliHost
        {
            Out = TextWriter.Null,
            Error = new StringWriter(new StringBuilder()),
            Time = _deployment.Time,
            GetEnvironmentVariable = name => name == "ELM_TRUST_ROOT_KEYS" ? pem : null,
        };
        long before = GC.GetTotalAllocatedBytes(true);
        int code = await Task.Run(() => ElmApp.Run(args, host), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        return (code, GC.GetTotalAllocatedBytes(true) - before);
    }
}
