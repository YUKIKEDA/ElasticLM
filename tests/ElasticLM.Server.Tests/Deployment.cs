using System.Net.Security;
using System.Net.Sockets;
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
/// <remarks>
/// A server's data folder holds the socket file of its administration API, whose path has a limit of 103 bytes on macOS and
/// 107 on Windows and Linux (design §13.7). So the folders are short: <c>{TestFolders.Root}/elm-{8 hex digits}/{2 characters}</c>
/// (design §20.7, the V1d2 Grill), and the constructor fails when the temporary folder is too long for that.
/// </remarks>
internal sealed class Deployment : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ECDsa _root = KeyFiles.CreateKey();
    private readonly ECDsa _signing = KeyFiles.CreateKey();
    private readonly string _certificate;
    private readonly List<IAsyncDisposable> _owned = [];
    private int _folders;

    public Deployment()
    {
        Directory = Path.Combine(ElasticLM.Service.Tests.TestFolders.Root, "elm-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
        string longest = AdminSocket.PathIn(Path.Combine(Directory, "zz"));
        int bytes = System.Text.Encoding.UTF8.GetByteCount(longest);
        string tooLong =
            $"The socket file of a test server would be '{longest}': {bytes} bytes in UTF-8, and this system allows {AdminSocket.MaxPathBytes}. "
            + "Run the tests with a shorter test folder (TestFolders.Root).";
        Assert.True(bytes <= AdminSocket.MaxPathBytes, tooLong);
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

    /// <summary>
    /// Replaces the license token in the server license file of a data folder. The change gets the token and a function that
    /// signs as the vendor's signing key does (ES256), for a token that must carry that key's signature over other bytes.
    /// </summary>
    public void ChangeServerLicenseToken(string dataDirectory, Func<string, Func<byte[], byte[]>, string> change)
    {
        string path = System.IO.Path.Combine(dataDirectory, "server.elmlic");
        var file = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        file["license"] = change(
            file["license"]!.GetValue<string>(),
            input => _signing.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
        );
        File.WriteAllText(path, file.ToJsonString());
    }

    public ServerSettings Settings(string dataDirectory, int port = 0) =>
        new()
        {
            Product = "acme-cad",
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
        dataDirectory ??= NewFolder();
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

    /// <summary>An HTTP client of a server's administration API, on its socket file: with the given token, with none, or with
    /// the real one.</summary>
    public static HttpClient AdminHttp(ElmServer server, bool authenticate = true, string? token = null)
    {
        HttpClient http = SocketHttp(server.AdminSocketPath);
        string? bearer = !authenticate ? null : token ?? AdminToken.Read(server.DataDirectory);
        if (bearer is not null)
        {
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        return http;
    }

    /// <summary>An HTTP client whose every connection goes to a Unix domain socket file, whatever the request's host.</summary>
    public static HttpClient SocketHttp(string socketPath) =>
        new(
            new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = async (_, cancellationToken) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            }
        )
        {
            BaseAddress = new Uri("http://elm-server.invalid/"),
        };

    /// <summary>Returns the path of the next folder, which does not exist yet: two characters, so that a server's socket file
    /// stays within the system's limit.</summary>
    public string NextFolderPath()
    {
        const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        int number = Interlocked.Increment(ref _folders) - 1;
        Assert.True(number < Digits.Length * Digits.Length, "A test makes more folders than two characters can name.");
        return System.IO.Path.Combine(Directory, $"{Digits[number / Digits.Length]}{Digits[number % Digits.Length]}");
    }

    /// <summary>Creates a new folder that only this user can use, as a machine-scope data folder must be (design §10.2).</summary>
    public string NewFolder()
    {
        string path = NextFolderPath();
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
            DataDirectory = dataDirectory ?? NewFolder(),
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
        // Clients first, while the servers still listen: a client returns its leases when disposed, and its 5-second limit
        // runs on the fake clock, which does not move here. Against a stopped server each return waits for the connection to
        // be refused, about 2 seconds on Windows, so a test that restarted its server with 70 leases took 2 minutes (#28).
        foreach (IAsyncDisposable owned in Enumerable.Reverse(_owned).OrderBy(o => o is ElmServer ? 1 : 0))
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
