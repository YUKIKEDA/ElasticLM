using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Server.Tests;

/// <summary>
/// The settings of <c>elm-server</c> (design §13.8): the file, the variables, the access entries, and the rate limiter.
/// </summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "elm-settings-" + Guid.CreateVersion7().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// A settings file sets every key; a variable overrides its key; lists in variables split on ';'.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A file with every key; variables ELM_LISTEN and ELM_TRUST_ROOTKEYS.
    ///
    /// Steps:
    /// - Load.
    ///
    /// Expected:
    /// - The file's values, with listen and the trust files from the variables.
    /// </remarks>
    [Fact]
    public void Load_FileAndVariables_AreRead()
    {
        string path = Write(
            """
            {
              "product": "acme-cad",
              "listen": "https://0.0.0.0:7443",
              "publicUrls": ["https://licenses.example:7443/"],
              "rateLimit": { "perIp": 5 },
              "license": "server.elmlic",
              "trust": { "rootKeys": ["root.pem"], "revokedKeyIds": ["k1"] },
              "tls": { "certificate": "cert.pem", "key": "key.pem" },
              "allow": ["10.0.0.0/8"],
              "deny": ["user:guest"],
              "dataDirectory": "data"
            }
            """
        );
        var variables = new Dictionary<string, string> { ["ELM_LISTEN"] = "https://127.0.0.1:9000", ["ELM_TRUST_ROOTKEYS"] = "a.pem; b.pem" };

        ServerSettings settings = ServerSettings.Load(path, name => variables.GetValueOrDefault(name));

        Assert.Equal("acme-cad", settings.Product);
        Assert.Equal(new Uri("https://127.0.0.1:9000"), settings.Listen);
        Assert.Equal([new Uri("https://licenses.example:7443/")], settings.PublicUrls);
        Assert.Equal(5, settings.RateLimitPerIp);
        Assert.Equal("server.elmlic", settings.LicensePath);
        Assert.Equal(["a.pem", "b.pem"], settings.TrustRootKeyFiles);
        Assert.Equal(["k1"], settings.TrustRevokedKeyIds);
        Assert.Equal("cert.pem", settings.TlsCertificate);
        Assert.Equal("key.pem", settings.TlsKey);
        Assert.Equal("data", settings.DataDirectory);
        Assert.True(settings.Access.AllowsAddress(IPAddress.Parse("10.1.2.3")));
        Assert.False(settings.Access.AllowsAddress(IPAddress.Parse("192.168.0.1")));
        Assert.False(settings.Access.AllowsClient(null, "guest"));
    }

    /// <summary>
    /// Invalid settings stop the start with a message that names the key.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Files and variables with one fault each.
    ///
    /// Steps:
    /// - Load each.
    ///
    /// Expected:
    /// - ServerStartException naming: the unknown key, product, listen, rateLimit.perIp (0 and 10001), the allow entry, and
    ///   the missing file.
    /// </remarks>
    [Fact]
    public void Load_InvalidSettings_Throw()
    {
        void AssertError(string? json, string expected, Dictionary<string, string>? variables = null)
        {
            string? path = json is null ? null : Write(json);
            var e = Assert.Throws<ServerStartException>(() => ServerSettings.Load(path, name => variables?.GetValueOrDefault(name)));
            Assert.Contains(expected, e.Message, StringComparison.Ordinal);
        }

        AssertError("""{"product":"acme-cad","borrow":{"maxDuration":1}}""", "borrow");
        AssertError("""{"listen":"https://0.0.0.0:1"}""", "product");
        AssertError("""{"product":"Acme"}""", "product");
        AssertError("""{"product":"acme-cad","listen":"http://0.0.0.0:7070"}""", "listen");
        AssertError("""{"product":"acme-cad","rateLimit":{"perIp":0}}""", "rateLimit.perIp");
        AssertError("""{"product":"acme-cad"}""", "rateLimit.perIp", new() { ["ELM_RATELIMIT_PERIP"] = "10001" });
        AssertError("""{"product":"acme-cad","allow":["somewhere"]}""", "somewhere");
        AssertError("""{"product":"acme-cad","product":"acme-cam"}""", "not valid");
        var e = Assert.Throws<ServerStartException>(() => ServerSettings.Load(Path.Combine(_directory, "none.json"), _ => null));
        Assert.Contains("does not exist", e.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// IP entries: a deny entry wins; with allow entries only matching addresses pass; IPv4-mapped IPv6 addresses count as
    /// IPv4. Host and user entries: with allow entries, a client that sends nothing is refused; host names ignore case.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Rules allow 10.0.0.0/8, ::1, host:PC1; deny 10.0.0.5, user:guest.
    ///
    /// Steps:
    /// - Check addresses and clients.
    ///
    /// Expected:
    /// - As listed in the assertions.
    /// </remarks>
    [Fact]
    public void AccessRules_Match()
    {
        AccessRules rules = AccessRules.Parse(["10.0.0.0/8", "::1", "host:PC1"], ["10.0.0.5", "user:guest"]);

        Assert.True(rules.AllowsAddress(IPAddress.Parse("10.0.0.4")));
        Assert.True(rules.AllowsAddress(IPAddress.Parse("::ffff:10.0.0.4")));
        Assert.False(rules.AllowsAddress(IPAddress.Parse("10.0.0.5")));
        Assert.True(rules.AllowsAddress(IPAddress.IPv6Loopback));
        Assert.False(rules.AllowsAddress(IPAddress.Parse("11.0.0.1")));
        Assert.False(rules.AllowsAddress(null));
        Assert.True(AccessRules.Empty.AllowsAddress(null));

        Assert.True(rules.AllowsClient("pc1", "alice"));
        Assert.False(rules.AllowsClient("pc1", "guest"));
        Assert.False(rules.AllowsClient("pc2", "alice"));
        Assert.False(rules.AllowsClient(null, null));
        Assert.True(AccessRules.Parse([], ["user:guest"]).AllowsClient(null, null));
        Assert.Throws<ServerStartException>(() => AccessRules.Parse(["host:"], []));
        Assert.Throws<ServerStartException>(() => AccessRules.Parse([.. Enumerable.Repeat("::1", 1_001)], []));
    }

    /// <summary>
    /// The rate limiter allows bursts up to the rate and refills at the rate per second; an IPv6 /64 is one source; when the
    /// table is full, new sources share one overflow bucket instead of being refused; idle sources are forgotten.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A limiter of 2 per second for at most 2 sources, on a fake clock.
    ///
    /// Steps:
    /// - Three requests from A; half a second later one more. One request from each of two addresses in one IPv6 /64 and a
    ///   third from it. Requests from C, D, and E (the table is full). One second later purge; C again.
    ///
    /// Expected:
    /// - Allowed, Allowed, refused; allowed after half a second. The /64 is one source: allowed, allowed, refused. C and D are
    ///   allowed and E refused, from the shared overflow bucket. After the purge no source is tracked and C is allowed.
    /// </remarks>
    [Fact]
    public void RateLimiter_RefillsGroupsAndOverflows()
    {
        var time = new FakeTimeProvider();
        var limiter = new IpRateLimiter(2, time, capacity: 2);
        IPAddress a = IPAddress.Parse("10.0.0.1");

        Assert.True(limiter.TryAcquire(a));
        Assert.True(limiter.TryAcquire(IPAddress.Parse("::ffff:10.0.0.1")));
        Assert.False(limiter.TryAcquire(a));
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(limiter.TryAcquire(a));

        Assert.True(limiter.TryAcquire(IPAddress.Parse("2001:db8:1:2::1")));
        Assert.True(limiter.TryAcquire(IPAddress.Parse("2001:db8:1:2:ffff::9")));
        Assert.False(limiter.TryAcquire(IPAddress.Parse("2001:db8:1:2::77")));

        Assert.True(limiter.TryAcquire(IPAddress.Parse("10.0.0.3")));
        Assert.True(limiter.TryAcquire(IPAddress.Parse("10.0.0.4")));
        Assert.False(limiter.TryAcquire(IPAddress.Parse("10.0.0.5")));
        Assert.Equal(2, limiter.Count);

        time.Advance(TimeSpan.FromSeconds(1));
        limiter.Purge();
        Assert.Equal(0, limiter.Count);
        Assert.True(limiter.TryAcquire(IPAddress.Parse("10.0.0.3")));
    }

    private string Write(string json)
    {
        string path = Path.Combine(_directory, Guid.CreateVersion7().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }
}
