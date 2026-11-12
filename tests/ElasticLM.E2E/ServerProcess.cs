using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ElasticLM.E2E;

/// <summary>
/// The published elm-server as a process: https with its own self-signed certificate on a free loopback port, the local admin
/// API on the socket file admin.sock in its data folder, and that data folder in the vendor's folder (design §13). The data
/// folder is not made here: the server makes it with an owner-only mode, and refuses a folder that group or other can write.
/// Its name has at most two characters, so that the socket file's path stays within the system's limit (design §20.7, the
/// V1d2 Grill).
/// </summary>
internal sealed partial class ServerProcess : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, string?> _environment;
    private readonly string _folder;
    private Process? _process;
    private StringBuilder _log = new();

    private ServerProcess(string folder, Dictionary<string, string?> environment, Uri url, string dataDirectory)
    {
        _folder = folder;
        _environment = environment;
        Url = url;
        DataDirectory = dataDirectory;
    }

    /// <summary>Gets the server's address for the clients.</summary>
    public Uri Url { get; }

    /// <summary>Gets the server's data folder: its key, its records, and its usage log.</summary>
    public string DataDirectory { get; }

    /// <summary>Gets the socket file of the local admin API, which is there while the server runs.</summary>
    public string AdminSocketPath => Path.Combine(DataDirectory, "admin.sock");

    /// <summary>Gets a value indicating whether the process that <see cref="Launch"/> started has ended.</summary>
    public bool HasExited => _process is null || _process.HasExited;

    /// <summary>Gets the exit code of the process that <see cref="Launch"/> started and that has ended.</summary>
    public int ExitCode => _process!.ExitCode;

    /// <summary>Gets what the running process has written so far.</summary>
    public string Log
    {
        get
        {
            lock (_log)
            {
                return _log.ToString();
            }
        }
    }

    /// <summary>
    /// Prepares a server for the product with its license at <paramref name="license"/>, whose data folder will be
    /// <paramref name="name"/> under the vendor's folder. Nothing runs yet.
    /// </summary>
    /// <param name="vendor">The vendor whose root key the server trusts.</param>
    /// <param name="name">The name of the server's data folder, of at most two characters.</param>
    /// <param name="product">The product the server serves.</param>
    /// <param name="license">Where the server's license file is or will be.</param>
    /// <param name="serviceUrl">The service the server refreshes its license with, if any.</param>
    public static ServerProcess Prepare(Vendor vendor, string name, string product, string license, Uri? serviceUrl = null)
    {
        Assert.True(name.Length <= 2, $"The data folder's name '{name}' is longer than two characters.");
        var url = new Uri($"https://127.0.0.1:{ServiceProcess.FreePort()}");
        string data = vendor.File(name);
        Dictionary<string, string?> environment = new()
        {
            ["ELM_LISTEN"] = url.ToString(),
            ["ELM_DATADIRECTORY"] = data,
            ["ELM_PRODUCT"] = product,
            ["ELM_LICENSE"] = license,
            ["ELM_TRUST_ROOTKEYS"] = vendor.RootPublicKey,
            ["ELM_RATELIMIT_PERIP"] = "1000",
        };
        if (serviceUrl is not null)
        {
            environment["ELM_SERVICE_URL"] = serviceUrl.ToString();
        }

        return new ServerProcess(vendor.Folder, environment, url, data);
    }

    /// <summary>
    /// Prepares a second server with the same settings and the same data folder, on another port. Nothing runs yet.
    /// </summary>
    public ServerProcess Twin()
    {
        var url = new Uri($"https://127.0.0.1:{ServiceProcess.FreePort()}");
        return new ServerProcess(_folder, new Dictionary<string, string?>(_environment) { ["ELM_LISTEN"] = url.ToString() }, url, DataDirectory);
    }

    /// <summary>
    /// Runs the server once without its license: it makes its installation key, names it, and stops. Returns the key's
    /// thumbprint.
    /// </summary>
    public async Task<string> InstallationKeyAsync()
    {
        (int code, string output, string error) = await RunToExitAsync();
        Match key = InstallationKey().Match(output + error);
        Assert.True(code != 0 && key.Success, $"elm-server without its license exited with {code}: {output}{error}");
        return key.Groups[1].Value;
    }

    /// <summary>Sets a variable of the server's environment, such as a setting (design §13.8), for the runs from now on.</summary>
    public void SetEnvironment(string name, string value) => _environment[name] = value;

    /// <summary>
    /// Runs the server once without its license and issues, with elm, a server license of 30 days for acme-cad (pro, 3 seats)
    /// bound to the key that run made.
    /// </summary>
    /// <param name="vendor">The vendor that signs the license.</param>
    /// <param name="license">Where the license file goes.</param>
    public async Task IssueLicenseAsync(Vendor vendor, string license)
    {
        string serverKey = await InstallationKeyAsync();
        string expires = DateTimeOffset.UtcNow.AddDays(30).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        await vendor.ElmOkAsync(
            "license",
            "issue",
            "--signing-key",
            vendor.SigningKey,
            "--certificate",
            vendor.Certificate,
            "--product",
            "acme-cad",
            "--license-id",
            "e2e-server",
            "--kind",
            "server",
            "--feature",
            "pro;seats=3",
            "--expires",
            expires,
            "--bind-key",
            serverKey,
            "--trust",
            vendor.RootPublicKey,
            "--out",
            license
        );
    }

    /// <summary>Runs the server to its end, for a start that stops by itself, and returns its exit code and output.</summary>
    public Task<(int Code, string Out, string Error)> RunToExitAsync() => Binaries.RunAsync(Binaries.Server, [], _folder, _environment);

    /// <summary>Starts the server's process and does not wait for it to answer or to end.</summary>
    public void Launch()
    {
        Assert.Null(_process);
        _log = new StringBuilder();
        _process = Binaries.StartLogged(Binaries.Server, [], _folder, _environment, _log);
    }

    /// <summary>Waits until the server that <see cref="Launch"/> started answers.</summary>
    public async Task WaitUntilItAnswersAsync()
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var probe = new HttpClient(handler) { BaseAddress = Url };
        await RealClock.WaitUntilAsync("elm-server /v1/server/license", () => AnswersAsync(probe), StartTimeout);
    }

    /// <summary>Starts the server and waits until it answers.</summary>
    public async Task StartAsync()
    {
        Launch();

        // The probe only waits for the port; the clients check the server's key against its license (design §12.7.5).
        await WaitUntilItAnswersAsync();
    }

    /// <summary>Ends the server's process, as a crash or a power cut would.</summary>
    public async Task KillAsync()
    {
        if (_process is null)
        {
            return;
        }

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        await _process.WaitForExitAsync(CancellationToken.None);
        _process.Dispose();
        _process = null;
    }

    /// <summary>Runs <c>elm server</c> against this server's admin API, which it finds in the data folder.</summary>
    /// <param name="vendor">The vendor whose folder elm runs in.</param>
    /// <param name="arguments">The arguments after <c>elm server</c>.</param>
    public Task<(int Code, string Out, string Error)> ElmServerAsync(Vendor vendor, params string[] arguments) =>
        vendor.ElmAsync(["server", .. arguments, "--data", DataDirectory]);

    public ValueTask DisposeAsync() => new(KillAsync());

    private static async Task<bool> AnswersAsync(HttpClient probe)
    {
        try
        {
            using HttpResponseMessage response = await probe.GetAsync("v1/server/license", TestContext.Current.CancellationToken);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    [GeneratedRegex("installation key ([A-Za-z0-9_-]{43})")]
    private static partial Regex InstallationKey();
}
