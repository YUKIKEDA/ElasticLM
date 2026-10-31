using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ElasticLM.E2E;

/// <summary>
/// The published elm-server as a process: https with its own self-signed certificate on a free loopback port, the local admin
/// API on another, and its data folder in the vendor's folder (design §13). The data folder is not made here: the server
/// makes it with an owner-only mode, and refuses a folder that group or other can write.
/// </summary>
internal sealed partial class ServerProcess : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, string?> _environment;
    private readonly string _folder;
    private Process? _process;
    private StringBuilder _log = new();

    private ServerProcess(string folder, Dictionary<string, string?> environment, Uri url, Uri adminUrl, string dataDirectory)
    {
        _folder = folder;
        _environment = environment;
        Url = url;
        AdminUrl = adminUrl;
        DataDirectory = dataDirectory;
    }

    /// <summary>Gets the server's address for the clients.</summary>
    public Uri Url { get; }

    /// <summary>Gets the address of the local admin API.</summary>
    public Uri AdminUrl { get; }

    /// <summary>Gets the server's data folder: its key, its records, and its usage log.</summary>
    public string DataDirectory { get; }

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
    /// Prepares a server for the product with its license at <paramref name="license"/>, in a new folder named
    /// <paramref name="name"/> under the vendor's folder. Nothing runs yet.
    /// </summary>
    /// <param name="vendor">The vendor whose root key the server trusts.</param>
    /// <param name="name">The name of the server's folder.</param>
    /// <param name="product">The product the server serves.</param>
    /// <param name="license">Where the server's license file is or will be.</param>
    /// <param name="serviceUrl">The service the server refreshes its license with, if any.</param>
    public static ServerProcess Prepare(Vendor vendor, string name, string product, string license, Uri? serviceUrl = null)
    {
        string folder = vendor.File(name);
        Directory.CreateDirectory(folder);
        var url = new Uri($"https://127.0.0.1:{ServiceProcess.FreePort()}");
        var adminUrl = new Uri($"http://127.0.0.1:{ServiceProcess.FreePort()}");
        string data = Path.Combine(folder, "data");
        Dictionary<string, string?> environment = new()
        {
            ["ELM_LISTEN"] = url.ToString(),
            ["ELM_ADMIN_LISTEN"] = adminUrl.ToString(),
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

        return new ServerProcess(folder, environment, url, adminUrl, data);
    }

    /// <summary>
    /// Runs the server once without its license: it makes its installation key, names it, and stops. Returns the key's
    /// thumbprint.
    /// </summary>
    public async Task<string> InstallationKeyAsync()
    {
        (int code, string output, string error) = await Binaries.RunAsync(Binaries.Server, [], _folder, _environment);
        Match key = InstallationKey().Match(output + error);
        Assert.True(code != 0 && key.Success, $"elm-server without its license exited with {code}: {output}{error}");
        return key.Groups[1].Value;
    }

    /// <summary>Starts the server and waits until it answers.</summary>
    public async Task StartAsync()
    {
        Assert.Null(_process);
        _log = new StringBuilder();
        _process = Binaries.StartLogged(Binaries.Server, [], _folder, _environment, _log);
        using var handler = new HttpClientHandler
        {
            // The probe only waits for the port; the clients check the server's key against its license (design §12.7.5).
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var probe = new HttpClient(handler) { BaseAddress = Url };
        await RealClock.WaitUntilAsync("elm-server /v1/server/license", () => AnswersAsync(probe), StartTimeout);
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

    /// <summary>Runs <c>elm server</c> against this server's admin API.</summary>
    /// <param name="vendor">The vendor whose folder elm runs in.</param>
    /// <param name="arguments">The arguments after <c>elm server</c>.</param>
    public Task<(int Code, string Out, string Error)> ElmServerAsync(Vendor vendor, params string[] arguments) =>
        vendor.ElmAsync(["server", .. arguments, "--url", AdminUrl.ToString(), "--data", DataDirectory]);

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
