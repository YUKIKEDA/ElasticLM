using System.Net.Security;
using System.Security.Cryptography;
using ElasticLM.Client;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Server.Tests;

/// <summary>
/// A vendor (root key, signing key, certificate), temporary folders, and one fake clock, from which the tests start servers
/// on loopback ports and create clients. Keys and licenses are made in the test and deleted with the folders.
/// </summary>
internal sealed class Deployment : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ECDsa _root = KeyFiles.CreateKey();
    private readonly ECDsa _signing = KeyFiles.CreateKey();
    private readonly string _certificate;
    private readonly List<IAsyncDisposable> _owned = [];

    public Deployment()
    {
        Directory = Path.Combine(Path.GetTempPath(), "elm-server-tests-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
        System.IO.Directory.CreateDirectory(Directory);
        RootKeyFile = Path.Combine(Directory, "root.pub.pem");
        File.WriteAllText(RootKeyFile, KeyFiles.ExportPublic(_root));
        Trust = TrustStore.Create([KeyFiles.ExportPublic(_root)], []);
        _certificate = CertificateIssuer.Issue(_root, _signing, KeyUses.License, Start.AddDays(-1), TimeSpan.FromDays(365), Time);
    }

    public FakeTimeProvider Time { get; } = new(Start);

    public string Directory { get; }

    public string RootKeyFile { get; }

    public TrustStore Trust { get; }

    public string FilePath(string name) => System.IO.Path.Combine(Directory, name);

    /// <summary>Returns the installation key thumbprint of a server data folder, creating the key as the server would.</summary>
    public static string ServerKeyId(string dataDirectory)
    {
        var store = new InstallationKeyStore("acme-cad", KeyScope.Machine, dataDirectory);
        Assert.True(store.TryLoadOrCreate(out InstallationKey? key, out _));
        using (key)
        {
            return key!.Thumbprint;
        }
    }

    /// <summary>Issues a server license for the server of a data folder and writes it there as server.elmlic.</summary>
    public string WriteServerLicense(string dataDirectory, Func<LicenseSpec, LicenseSpec>? change = null)
    {
        var spec = new LicenseSpec
        {
            LicenseId = "lic-srv",
            ProductId = "acme-cad",
            Kind = LicenseKind.Server,
            Features = [new FeatureSpec("pro", ">=2.0 <3.0", Seats: 2)],
            ExpiresAt = Start.AddDays(30),
            GracePeriod = TimeSpan.FromDays(7),
            InstallationKeyThumbprint = ServerKeyId(dataDirectory),
            LeaseDuration = TimeSpan.FromMinutes(10),
            BorrowLimit = TimeSpan.FromDays(7),
            OveragePercent = 0,
            CloneStrategy = CloneStrategy.Deny,
            BorrowReturnLimit = 2,
        };
        IssuedLicense issued = LicenseIssuer.Issue(_signing, [_certificate], change?.Invoke(spec) ?? spec, Time);
        string path = System.IO.Path.Combine(dataDirectory, "server.elmlic");
        File.WriteAllBytes(path, issued.File);
        return path;
    }

    public ServerSettings Settings(string dataDirectory, int port = 0) =>
        new()
        {
            Product = "acme-cad",
            AdminListen = new Uri("http://127.0.0.1:0"),
            Listen = new Uri($"https://127.0.0.1:{port}"),
            LicensePath = System.IO.Path.Combine(dataDirectory, "server.elmlic"),
            TrustRootKeyFiles = [RootKeyFile],
            DataDirectory = dataDirectory,
            RateLimitPerIp = 1_000,
        };

    /// <summary>Starts a server with a license in a new data folder, or in the given one.</summary>
    public async Task<ElmServer> StartServerAsync(
        string? dataDirectory = null,
        Func<ServerSettings, ServerSettings>? change = null,
        bool setAside = false
    )
    {
        dataDirectory ??= NewFolder("server");
        if (!File.Exists(System.IO.Path.Combine(dataDirectory, "server.elmlic")))
        {
            WriteServerLicense(dataDirectory);
        }

        ServerSettings settings = Settings(dataDirectory);
        ElmServer server = await ElmServer.StartAsync(change?.Invoke(settings) ?? settings, HostOptions(setAside: setAside));
        _owned.Add(server);
        return server;
    }

    public ServerHostOptions HostOptions(Func<string, string?>? environment = null, bool setAside = false) =>
        new()
        {
            Time = Time,
            Environment = environment ?? (_ => null),
            MinimumLogLevel = LogLevel.Warning,
            SetAsideBrokenRecords = setAside,
        };

    /// <summary>An HTTP client of a server's administration API: with the given token, with none, or with the real one.</summary>
    public static HttpClient AdminHttp(ElmServer server, bool authenticate = true, string? token = null)
    {
        var http = new HttpClient { BaseAddress = server.AdminUrl };
        string? bearer = !authenticate ? null : token ?? AdminToken.Read(server.DataDirectory);
        if (bearer is not null)
        {
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        return http;
    }

    /// <summary>Creates a new folder that only this user can use, as a machine-scope data folder must be (design §10.2).</summary>
    public string NewFolder(string name)
    {
        string path = System.IO.Path.Combine(Directory, name + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
        ProtectedFiles.CreateDirectory(path, KeyScope.Machine);
        return path;
    }

    /// <summary>Creates a client of a server URL, with its own installation key unless a data folder is shared.</summary>
    public LicenseClient Client(Uri serverUrl, string? dataDirectory = null, Func<LicenseClientOptions, LicenseClientOptions>? change = null)
    {
        var options = new LicenseClientOptions
        {
            ProductId = "acme-cad",
            ServerUrl = serverUrl,
            Trust = Trust,
            DataDirectory = dataDirectory ?? NewFolder("client"),
            ClockGuard = StatelessClockGuard.Instance,
            TimeProvider = Time,
        };
        Assert.True(LicenseClient.TryCreate(change?.Invoke(options) ?? options, out LicenseClient? client, out _));
        _owned.Add(client!);
        return client!;
    }

    /// <summary>An HTTP client that accepts the server's self-signed certificate, for raw protocol tests.</summary>
    public static HttpClient RawHttp(Uri serverUrl) =>
        new(
            new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null,
                },
            }
        )
        {
            BaseAddress = serverUrl,
        };

    /// <summary>Waits, in real time, until a condition holds; the client renews on timer threads.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 1_000 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "The condition did not hold within 10 seconds.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IAsyncDisposable owned in Enumerable.Reverse(_owned))
        {
            await owned.DisposeAsync();
        }

        _root.Dispose();
        _signing.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by the OS (Windows) is left in the temporary folder.
        }
    }
}
