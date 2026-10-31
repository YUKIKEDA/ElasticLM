using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ElasticLM.E2E;

/// <summary>
/// The published elm-service, set up with <c>elm-service setup</c> and started as a process on a free loopback port, with the
/// vendor's keys, a new nonce secret, and SQLite in the vendor's folder, or PostgreSQL in a schema of its own when
/// <see cref="PostgresVariable"/> is set (design §20.7).
/// </summary>
internal sealed class ServiceProcess : IAsyncDisposable
{
    /// <summary>The variable that names a PostgreSQL server for the service (the Linux CI run sets it).</summary>
    public const string PostgresVariable = "ELM_E2E_POSTGRES";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly StringBuilder _log;

    private ServiceProcess(Process process, StringBuilder log, Uri url, HttpClient admin)
    {
        _process = process;
        _log = log;
        Url = url;
        Admin = admin;
    }

    /// <summary>Gets the service's address.</summary>
    public Uri Url { get; }

    /// <summary>Gets a client of the admin API with the key <c>setup</c> printed.</summary>
    public HttpClient Admin { get; }

    /// <summary>Gets what the service has written so far.</summary>
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

    /// <summary>Gets whether the tests run the service on PostgreSQL.</summary>
    public static bool OnPostgres => Environment.GetEnvironmentVariable(PostgresVariable) is { Length: > 0 };

    /// <summary>Sets up and starts the service for the vendor.</summary>
    /// <param name="vendor">The vendor whose keys the service signs with.</param>
    public static async Task<ServiceProcess> StartAsync(Vendor vendor)
    {
        var url = new Uri($"http://127.0.0.1:{FreePort()}");
        Dictionary<string, string?> environment = new()
        {
            ["ELM_LISTEN"] = url.ToString(),
            ["ELM_SIGNING_KEY"] = vendor.SigningKey,
            ["ELM_SIGNING_CERTIFICATES"] = vendor.Certificate,
            ["ELM_SIGNING_PASSPHRASE"] = Vendor.Passphrase,
            ["ELM_TRUST_ROOTKEYS"] = vendor.RootPublicKey,
            ["ELM_NONCE_SECRET"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["ELM_BOOTSTRAPOWNER_ISS"] = "https://idp.example",
            ["ELM_BOOTSTRAPOWNER_SUB"] = "e2e-owner",

            // A test makes its records faster than an administrator would (testing.mdc).
            ["ELM_RATELIMIT_PERKEY"] = "1000",
            ["ELM_RATELIMIT_PERIP"] = "1000",
            ["ELM_RATELIMIT_CLIENTPERIP"] = "1000",
            ["ELM_RATELIMIT_CLIENTPERKEY"] = "1000",
        };
        if (OnPostgres)
        {
            environment["ELM_DATABASE_PROVIDER"] = "postgres";
            environment["ELM_DATABASE_CONNECTION"] = await NewSchemaAsync();
        }
        else
        {
            environment["ELM_DATABASE_CONNECTION"] = "Data Source=" + vendor.File("service.db");
        }

        (int code, string key, string error) = await Binaries.RunAsync(Binaries.Service, ["setup"], vendor.Folder, environment);
        key = key.Trim();
        Assert.True(code == 0 && key.StartsWith("elm_", StringComparison.Ordinal), $"elm-service setup exited with {code}: {error}");

        var log = new StringBuilder();
        Process process = Binaries.StartLogged(Binaries.Service, [], vendor.Folder, environment, log);
        var admin = new HttpClient { BaseAddress = url };
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var service = new ServiceProcess(process, log, url, admin);
        try
        {
            using var probe = new HttpClient { BaseAddress = url };
            await RealClock.WaitUntilAsync("elm-service /health", () => HealthyAsync(probe), StartTimeout);
            return service;
        }
        catch
        {
            await service.DisposeAsync();
            throw;
        }
    }

    /// <summary>Posts a JSON body to the admin API and returns the answer, failing the test unless it is 200 or 201.</summary>
    /// <param name="path">The path under <c>v1/admin/</c>.</param>
    /// <param name="body">The JSON body, or <see langword="null"/> for none.</param>
    public async Task<JsonElement> PostAsync(string path, string? body = null)
    {
        using var content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Admin.PostAsync("v1/admin/" + path, content, TestContext.Current.CancellationToken);
        string text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created, $"POST {path}: {(int)response.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Gets a path of the admin API and returns the answer, failing the test unless it is 200.</summary>
    /// <param name="path">The path under <c>v1/admin/</c>.</param>
    public async Task<JsonElement> GetAsync(string path)
    {
        using HttpResponseMessage response = await Admin.GetAsync("v1/admin/" + path, TestContext.Current.CancellationToken);
        string text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        Admin.Dispose();
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        // Windows keeps the SQLite file open until the process has ended.
        await _process.WaitForExitAsync(CancellationToken.None);
        _process.Dispose();
    }

    /// <summary>Returns a loopback port no one listens on now.</summary>
    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<bool> HealthyAsync(HttpClient probe)
    {
        try
        {
            using HttpResponseMessage response = await probe.GetAsync("health", TestContext.Current.CancellationToken);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static Task<string> NewSchemaAsync() => throw new NotImplementedException("PostgreSQL: added with the CI step.");
}
