using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElasticLM.Issuing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace ElasticLM.Service.Tests;

/// <summary>
/// A vendor (root key, signing key and certificate), a temporary folder, a database, and one fake clock, from which tests
/// start the service on a loopback port. Keys are made in the test and deleted with the folder. On PostgreSQL each instance
/// works in a schema of its own, dropped at the end.
/// </summary>
internal sealed class TestService : IAsyncDisposable
{
    public const string Passphrase = "correct horse battery staple";

    /// <summary>The variable that names a PostgreSQL server for the tests (CI sets it on Linux).</summary>
    public const string PostgresVariable = "ELM_TEST_POSTGRES";

    public static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The nonces' secret of every test service: 32 bytes, made fresh for each run.</summary>
    public static readonly byte[] NonceSecret = RandomNumberGenerator.GetBytes(32);

    private readonly ECDsa _root = KeyFiles.CreateKey();
    private readonly List<IAsyncDisposable> _owned = [];
    private readonly string? _schema;

    public TestService(DatabaseProvider provider = DatabaseProvider.Sqlite)
    {
        Provider = provider;
        Directory = Path.Combine(Path.GetTempPath(), "elm-service-tests-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(FilePath("root.pub.pem"), KeyFiles.ExportPublic(_root));
        SigningKey = KeyFiles.CreateKey();
        File.WriteAllText(FilePath("signing.key.pem"), KeyFiles.ExportEncrypted(SigningKey, Passphrase));
        WriteCertificate("signing.cert", KeyUses.License | KeyUses.Lease, Start.AddDays(-1), TimeSpan.FromDays(365));
        if (provider == DatabaseProvider.Postgres)
        {
            string server =
                Environment.GetEnvironmentVariable(PostgresVariable) ?? throw new InvalidOperationException($"{PostgresVariable} is not set.");
            _schema = "elm_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            using var connection = new NpgsqlConnection(server);
            connection.Open();
            using var create = new NpgsqlCommand($"CREATE SCHEMA {_schema}", connection);
            create.ExecuteNonQuery();
            ConnectionString = new NpgsqlConnectionStringBuilder(server) { SearchPath = _schema }.ConnectionString;
        }
        else
        {
            ConnectionString = "Data Source=" + FilePath("elm.db");
        }
    }

    public DatabaseProvider Provider { get; }

    public FakeTimeProvider Time { get; } = new(Start);

    public string Directory { get; }

    public string ConnectionString { get; }

    public ECDsa SigningKey { get; }

    /// <summary>Gets whether the PostgreSQL tests can run here.</summary>
    public static bool PostgresAvailable => Environment.GetEnvironmentVariable(PostgresVariable) is { Length: > 0 };

    public string FilePath(string name) => Path.Combine(Directory, name);

    /// <summary>Writes a certificate of the signing key and returns its path.</summary>
    public string WriteCertificate(string name, KeyUses uses, DateTimeOffset notBefore, TimeSpan validity, ECDsa? key = null)
    {
        string certificate = CertificateIssuer.Issue(_root, key ?? SigningKey, uses, notBefore, validity, Time);
        File.WriteAllText(FilePath(name), certificate);
        return FilePath(name);
    }

    public ServiceSettings Settings(Func<ServiceSettings, ServiceSettings>? change = null)
    {
        var settings = new ServiceSettings
        {
            Listen = new Uri("http://127.0.0.1:0"),
            Database = Provider,
            ConnectionString = ConnectionString,
            SigningKeyFile = FilePath("signing.key.pem"),
            SigningCertificateFiles = [FilePath("signing.cert")],
            TrustRootKeyFiles = [FilePath("root.pub.pem")],
            BootstrapOwner = new AdminIdentity("https://idp.example", "owner-1"),
            RateLimitPerIp = 1_000,
            RateLimitPerKey = 1_000,
            RateLimitClientPerIp = 1_000,
            RateLimitClientPerKey = 1_000,
            NonceSecret = NonceSecret,
        };
        return change?.Invoke(settings) ?? settings;
    }

    public ServiceHostOptions HostOptions(string? passphrase = Passphrase) =>
        new()
        {
            Time = Time,
            Environment = name => name == ServiceSettings.SigningPassphraseVariable ? passphrase : null,
            MinimumLogLevel = LogLevel.Warning,
        };

    public async Task<ElmService> StartAsync(Func<ServiceSettings, ServiceSettings>? change = null)
    {
        ElmService service = await ElmService.StartAsync(Settings(change), HostOptions(), TestContext.Current.CancellationToken);
        _owned.Add(service);
        return service;
    }

    /// <summary>Runs setup and returns the key it printed.</summary>
    public async Task<string> SetupAsync()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await Setup.RunAsync(Settings(), Time, output, error, TestContext.Current.CancellationToken);
        Assert.True(code == 0, error.ToString());
        return output.ToString().Trim();
    }

    /// <summary>An HTTP client of the service, with an API key or without one.</summary>
    public HttpClient Http(ElmService service, string? key = null)
    {
        var http = new HttpClient { BaseAddress = service.Url };
        if (key is not null)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        _owned.Add(new DisposableHttp(http));
        return http;
    }

    /// <summary>Runs SQL on the test database, to tamper with it.</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using Database database = Database.Create(Provider, ConnectionString);
        await using DbConnection connection = await database.OpenAsync(TestContext.Current.CancellationToken);
        await using DbCommand command = Database.Command(connection, null, sql);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Reads one number from the test database.</summary>
    public async Task<long> ScalarAsync(string sql)
    {
        await using Database database = Database.Create(Provider, ConnectionString);
        await using DbConnection connection = await database.OpenAsync(TestContext.Current.CancellationToken);
        await using DbCommand command = Database.Command(connection, null, sql);
        object? value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IAsyncDisposable owned in Enumerable.Reverse(_owned))
        {
            await owned.DisposeAsync();
        }

        _root.Dispose();
        SigningKey.Dispose();
        if (_schema is not null)
        {
            await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable(PostgresVariable));
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {_schema} CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }

        // SQLite keeps no handle once its connections are disposed; a file the OS still holds is left in the temporary folder.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException) { }
    }

    /// <summary>Gets the engines a theory runs on; PostgreSQL is skipped where no server is set.</summary>
    public static TheoryData<string> Providers => ["sqlite", "postgres"];

    /// <summary>Opens a test on an engine, or skips it when the engine is PostgreSQL and no server is set.</summary>
    public static TestService Open(string provider)
    {
        if (provider == "postgres")
        {
            Assert.SkipUnless(PostgresAvailable, $"Set {PostgresVariable} to a PostgreSQL server to run this case.");
            return new TestService(DatabaseProvider.Postgres);
        }

        return new TestService();
    }

    public static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>Creates a key through the API and returns it.</summary>
    public static async Task<string> CreateKeyAsync(HttpClient admin, string permissions, int days = 90)
    {
        string body = "{\"name\":\"k\",\"permissions\":[" + permissions + "],\"expiresInDays\":" + days + "}";
        using HttpResponseMessage created = await admin.PostAsync("v1/admin/api-keys", Json(body), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument item = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return item.RootElement.GetProperty("key").GetString()!;
    }

    /// <summary>Reads the first 200 audit rows through the API (the read adds its own row after them).</summary>
    public static async Task<JsonElement[]> AuditRowsAsync(TestService test, ElmService service, string key)
    {
        string text = await test.Http(service, key).GetStringAsync("v1/admin/audit?limit=200", TestContext.Current.CancellationToken);
        using JsonDocument page = JsonDocument.Parse(text);
        return [.. page.RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone())];
    }

    /// <summary>Asserts a problem answer: its status, code, and detail.</summary>
    public static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code, string detail)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                (code, detail),
                (problem.RootElement.GetProperty("code").GetString(), problem.RootElement.GetProperty("detail").GetString())
            );
        }
    }

    /// <summary>Creates a policy of a product with the members given (JSON members, or empty) and returns its ID.</summary>
    public static async Task<string> PolicyAsync(HttpClient admin, string productId, string members)
    {
        string body = $"{{\"productId\":\"{productId}\",\"name\":\"p\"{(members.Length > 0 ? "," + members : string.Empty)}}}";
        return (await CreatedAsync(admin, "v1/admin/policies", body)).GetProperty("id").GetString()!;
    }

    /// <summary>Posts a body that must give 201 and returns the answer.</summary>
    public static async Task<JsonElement> CreatedAsync(HttpClient admin, string path, string body)
    {
        using HttpResponseMessage response = await admin.PostAsync(path, Json(body), TestContext.Current.CancellationToken);
        string text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Gets a path that must give 200 and returns the answer.</summary>
    public static async Task<JsonElement> GetAsync(HttpClient admin, string path) =>
        JsonDocument.Parse(await admin.GetStringAsync(path, TestContext.Current.CancellationToken)).RootElement.Clone();

    /// <summary>Patches a path with a body that must give 200 and returns the answer.</summary>
    public static async Task<JsonElement> PatchAsync(HttpClient admin, string path, string body)
    {
        using HttpResponseMessage response = await admin.PatchAsync(path, Json(body), TestContext.Current.CancellationToken);
        string text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Posts a body that must give 200 and returns the answer.</summary>
    public static async Task<JsonElement> PostOkAsync(HttpClient admin, string path, string body)
    {
        using HttpResponseMessage response = await admin.PostAsync(path, Json(body), TestContext.Current.CancellationToken);
        string text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    // The service runs its timers on the fake clock but on thread-pool threads; wait, in real time, for their work. The same
    // shape as the elm-server tests' wait for a background write.
    public static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (int i = 0; i < 500; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Fail("The condition did not hold within 10 seconds.");
    }

    private sealed class DisposableHttp(HttpClient http) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            http.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
