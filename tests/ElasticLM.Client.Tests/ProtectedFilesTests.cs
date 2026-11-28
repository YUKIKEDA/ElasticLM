using System.Security.Cryptography;
using ElasticLM.Licensing;

namespace ElasticLM.Client.Tests;

/// <summary>
/// Tests of <see cref="ProtectedFiles"/>: the limit of a secret file's read (design §8.6, V1d24c).
/// </summary>
public sealed class ProtectedFilesTests : IDisposable
{
    private static readonly byte[] Purpose = "elm-test-secret"u8.ToArray();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "elm-protected-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));

    public ProtectedFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// A secret file is read when the limit is its length, and not when the limit is one byte less.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A secret of 1,000 random bytes written with TryCreateSecret for this user (on Windows protected with DPAPI, so the
    ///   file is longer than the secret; on Linux and macOS the file is the secret).
    ///
    /// Steps:
    /// - TryReadSecret with a limit of the file's length; with a limit of one byte less.
    ///
    /// Expected:
    /// - The secret; null.
    /// </remarks>
    [Fact]
    public void TryReadSecret_LimitAtTheFileAndOneLess_ReadsThenNull()
    {
        string path = Path.Combine(_dir, "secret");
        byte[] secret = RandomNumberGenerator.GetBytes(1000);
        ProtectedFiles.TryCreateSecret(path, secret, KeyScope.User, Purpose)!.Dispose();

        int length = (int)new FileInfo(path).Length;

        Assert.Equal(secret, ProtectedFiles.TryReadSecret(path, length, Purpose));
        Assert.Null(ProtectedFiles.TryReadSecret(path, length - 1, Purpose));
    }
}
