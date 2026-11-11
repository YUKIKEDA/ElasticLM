using System.Security.Cryptography;

namespace ElasticLM.E2E;

/// <summary>
/// A vendor's folder for one test: a root key, a signing key, and its certificate, made with the published elm
/// (design §16), and removed with the folder.
/// </summary>
internal sealed class Vendor : IAsyncDisposable
{
    /// <summary>The passphrase of the keys this class makes; each run's keys are new.</summary>
    public const string Passphrase = "end-to-end passphrase 0123";

    private Vendor(string folder) => Folder = folder;

    /// <summary>Gets the folder that holds the keys and everything the test writes.</summary>
    public string Folder { get; }

    /// <summary>Gets the root public key, the trust store of the apps.</summary>
    public string RootPublicKey => File("root.pub.pem");

    /// <summary>Gets the encrypted signing key.</summary>
    public string SigningKey => File("signing.key.pem");

    /// <summary>Gets the signing key's certificate.</summary>
    public string Certificate => File("signing.cert");

    /// <summary>Gets the environment elm needs for this vendor's keys.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; } = new Dictionary<string, string?> { ["ELM_KEY_PASSPHRASE"] = Passphrase };

    /// <summary>
    /// Gets the longest path of a socket file that this system takes, in UTF-8 bytes (design §13.7): 103 on macOS, 107 on
    /// Windows and Linux.
    /// </summary>
    public static int SocketPathLimit => OperatingSystem.IsMacOS() ? 103 : 107;

    /// <summary>Makes a new folder with a root key, a signing key, and a certificate for the uses given.</summary>
    /// <param name="uses">The uses of the certificate, as <c>elm key certify --use</c> takes them.</param>
    public static Task<Vendor> CreateAsync(params string[] uses) => CreateInAsync(Path.GetTempPath(), uses);

    /// <summary>
    /// Makes a new folder under <paramref name="parent"/> with a root key, a signing key, and a certificate for the uses
    /// given. The folder is <c>elm-{8 hex digits}</c>, and a server's data folder in it has a name of two characters, so that
    /// the path of the server's socket file stays within the system's limit (design §20.7, the V1d2 Grill); the method fails
    /// when <paramref name="parent"/> is too long for that.
    /// </summary>
    /// <param name="parent">The folder that gets the new folder.</param>
    /// <param name="uses">The uses of the certificate, as <c>elm key certify --use</c> takes them.</param>
    public static async Task<Vendor> CreateInAsync(string parent, params string[] uses)
    {
        string folder = Path.Combine(Path.GetFullPath(parent), "elm-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
        string longest = Path.Combine(folder, "zz", "admin.sock");
        int bytes = System.Text.Encoding.UTF8.GetByteCount(longest);
        Assert.True(
            bytes <= SocketPathLimit,
            $"The socket file of a test server would be '{longest}': {bytes} bytes in UTF-8, and this system allows {SocketPathLimit}."
        );
        Directory.CreateDirectory(folder);
        var vendor = new Vendor(folder);
        try
        {
            await vendor.ElmOkAsync("key", "root", "create", "--out", "root.key.pem", "--public-out", "root.pub.pem");
            await vendor.ElmOkAsync("key", "signing", "create", "--out", "signing.key.pem", "--public-out", "signing.pub.pem");
            await vendor.ElmOkAsync([
                "key",
                "certify",
                "--root",
                "root.key.pem",
                "--public",
                "signing.pub.pem",
                "--use",
                .. uses,
                "--out",
                "signing.cert",
            ]);
            return vendor;
        }
        catch
        {
            await vendor.DisposeAsync();
            throw;
        }
    }

    /// <summary>Returns the path of a file in the folder.</summary>
    /// <param name="name">The file's name.</param>
    public string File(string name) => Path.Combine(Folder, name);

    /// <summary>Runs the published elm in the folder.</summary>
    /// <param name="arguments">Its arguments.</param>
    public Task<(int Code, string Out, string Error)> ElmAsync(params string[] arguments) =>
        Binaries.RunAsync(Binaries.Elm, arguments, Folder, Environment);

    /// <summary>Runs the published elm in the folder and returns its output, failing the test unless it exits with 0.</summary>
    /// <param name="arguments">Its arguments.</param>
    public async Task<string> ElmOkAsync(params string[] arguments)
    {
        (int code, string output, string error) = await ElmAsync(arguments);

        // The command's name only: its arguments may hold a license key (security.mdc).
        Assert.True(code == 0, $"elm {string.Join(' ', arguments.Take(2))} exited with {code}: {error}");
        return output;
    }

    public async ValueTask DisposeAsync()
    {
        // On Windows a process the test has just ended may hold a file for a moment; the keys must not stay behind
        // (scratch.mdc), so the removal is tried again for up to 5 seconds and then fails the test.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(Folder, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 50)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, CancellationToken.None);
            }
        }
    }
}
