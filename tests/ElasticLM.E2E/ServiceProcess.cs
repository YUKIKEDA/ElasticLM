using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace ElasticLM.E2E;

/// <summary>
/// The published elm-service, set up with <c>elm-service setup</c> and started as a process on a free loopback port, with the
/// vendor's keys, a new nonce secret, a usage log in the vendor's folder, and SQLite in that folder or a new PostgreSQL
/// database on the server <see cref="PostgresVariable"/> names (design §20.7).
/// </summary>
internal sealed class ServiceProcess : IAsyncDisposable
{
    /// <summary>The variable that names a PostgreSQL server for the service (the Linux CI run sets it).</summary>
    public const string PostgresVariable = "ELM_E2E_POSTGRES";

    /// <summary>The node name the service writes its usage log under (design §18.1).</summary>
    public const string NodeName = "e2e-node";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly StringBuilder _log;
    private readonly string? _database;

    private ServiceProcess(Process process, StringBuilder log, Uri url, HttpClient admin, string? database, string usageLog)
    {
        _process = process;
        _log = log;
        Url = url;
        Admin = admin;
        _database = database;
        UsageLogDirectory = usageLog;
    }

    /// <summary>Gets the databases the service's flows run on here: SQLite, and PostgreSQL when <see cref="PostgresVariable"/> is set.</summary>
    public static TheoryData<string> Databases =>
        Environment.GetEnvironmentVariable(PostgresVariable) is { Length: > 0 } ? new("sqlite", "postgres") : new("sqlite");

    /// <summary>Gets the service's address.</summary>
    public Uri Url { get; }

    /// <summary>Gets a client of the admin API with the key <c>setup</c> printed.</summary>
    public HttpClient Admin { get; }

    /// <summary>Gets the folder of the service's usage log.</summary>
    public string UsageLogDirectory { get; }

    /// <summary>Gets what the service has written so far: its own log, which holds no secret (design §18).</summary>
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

    private static string PostgresServer =>
        Environment.GetEnvironmentVariable(PostgresVariable) ?? throw new InvalidOperationException($"{PostgresVariable} is not set.");

    /// <summary>Sets up and starts the service for the vendor.</summary>
    /// <param name="vendor">The vendor whose keys the service signs with; its certificate has the use log.</param>
    /// <param name="database"><c>sqlite</c> or <c>postgres</c>.</param>
    public static async Task<ServiceProcess> StartAsync(Vendor vendor, string database)
    {
        var url = new Uri($"http://127.0.0.1:{FreePort()}");
        string usageLog = vendor.File("usage-log");
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
            ["ELM_USAGELOG_DIRECTORY"] = usageLog,
            ["ELM_NODE_NAME"] = NodeName,

            // A test makes its records faster than an administrator would (testing.mdc).
            ["ELM_RATELIMIT_PERKEY"] = "1000",
            ["ELM_RATELIMIT_PERIP"] = "1000",
            ["ELM_RATELIMIT_CLIENTPERIP"] = "1000",
            ["ELM_RATELIMIT_CLIENTPERKEY"] = "1000",
        };
        string? created = null;
        if (database == "postgres")
        {
            created = "elm_e2e_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            await PostgresAsync($"CREATE DATABASE {created}");
            environment["ELM_DATABASE_PROVIDER"] = "postgres";
            environment["ELM_DATABASE_CONNECTION"] = new NpgsqlConnectionStringBuilder(PostgresServer) { Database = created }.ConnectionString;
        }
        else
        {
            environment["ELM_DATABASE_CONNECTION"] = "Data Source=" + vendor.File("service.db");
        }

        // The key setup prints is a secret: it goes to the admin client only, never into a message (security.mdc).
        (int code, string key, string error) = await Binaries.RunAsync(Binaries.Service, ["setup"], vendor.Folder, environment);
        key = key.Trim();
        Assert.True(code == 0 && key.StartsWith("elm_", StringComparison.Ordinal), $"elm-service setup exited with {code}: {error}");

        var log = new StringBuilder();
        Process process = Binaries.StartLogged(Binaries.Service, [], vendor.Folder, environment, log);
        var admin = new HttpClient { BaseAddress = url };
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var service = new ServiceProcess(process, log, url, admin, created, usageLog);
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

    /// <summary>Returns a loopback port no one listens on now.</summary>
    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Sends a JSON body to the admin API and returns the answer, failing the test unless it is a success.</summary>
    /// <param name="method">The method.</param>
    /// <param name="path">The path under <c>v1/admin/</c>.</param>
    /// <param name="body">The JSON body, or <see langword="null"/> for none.</param>
    public async Task<JsonElement> SendAsync(HttpMethod method, string path, string? body = null)
    {
        using var request = new HttpRequestMessage(method, "v1/admin/" + path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Admin.SendAsync(request, TestContext.Current.CancellationToken);
        string text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // A success may hold a license key and is never put in a message; a refusal holds the error only (security.mdc).
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Makes a product with the feature <c>pro</c> and returns its ID.</summary>
    /// <param name="code">The product's code.</param>
    public async Task<string> ProductAsync(string code)
    {
        string id = (await SendAsync(HttpMethod.Post, "products", $$"""{"code":"{{code}}","name":"{{code}}"}""")).GetProperty("id").GetString()!;
        await SendAsync(HttpMethod.Post, $"products/{id}/features", """{"code":"pro","name":"Pro"}""");
        return id;
    }

    /// <summary>Makes a policy of the product with the members given and returns its ID.</summary>
    /// <param name="product">The product's ID.</param>
    /// <param name="members">More members of the policy, separated by commas, or empty.</param>
    public async Task<string> PolicyAsync(string product, string members = "")
    {
        string more = members.Length > 0 ? "," + members : string.Empty;
        string body = $$"""{"productId":"{{product}}","name":"p-{{Guid.NewGuid():N}}"{{more}}}""";
        return (await SendAsync(HttpMethod.Post, "policies", body)).GetProperty("id").GetString()!;
    }

    /// <summary>Makes a license of the policy with the feature <c>pro</c> and returns its ID and key.</summary>
    /// <param name="policy">The policy's ID.</param>
    /// <param name="seats">The feature's seats, or <see langword="null"/> for none.</param>
    public async Task<(string Id, string Key)> LicenseAsync(string policy, int? seats = null)
    {
        string feature = seats is int count ? $$"""{"code":"pro","seats":{{count}}}""" : """{"code":"pro"}""";
        JsonElement license = await SendAsync(HttpMethod.Post, "licenses", $$"""{"policyId":"{{policy}}","name":"e2e","features":[{{feature}}]}""");
        return (license.GetProperty("id").GetString()!, license.GetProperty("key").GetString()!);
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
        if (_database is not null)
        {
            await PostgresAsync($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)");
        }
    }

    private static async Task PostgresAsync(string command)
    {
        await using var connection = new NpgsqlConnection(PostgresServer);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var run = new NpgsqlCommand(command, connection);
        await run.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
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
}
