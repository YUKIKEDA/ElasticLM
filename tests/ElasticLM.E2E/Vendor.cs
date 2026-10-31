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

    /// <summary>Makes a new folder with a root key, a signing key, and a certificate for the uses given.</summary>
    /// <param name="uses">The uses of the certificate, as <c>elm key certify --use</c> takes them.</param>
    public static async Task<Vendor> CreateAsync(params string[] uses)
    {
        string folder = Path.Combine(Path.GetTempPath(), "elm-e2e-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
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

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // A process the test started may still hold a file on Windows for a moment; the folder is in the temp folder.
        }

        return ValueTask.CompletedTask;
    }
}
