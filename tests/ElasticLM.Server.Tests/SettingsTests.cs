using System.Net;
using ElasticLM.Issuing;
using ElasticLM.Licensing.Tests;
using ElasticLM.Service.Tests;
using Microsoft.Extensions.Time.Testing;

namespace ElasticLM.Server.Tests;

/// <summary>
/// The settings of <c>elm-server</c> (design §13.8): the file, the variables, and the access entries.
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
              "dataDirectory": "data",
              "borrow": { "maxDuration": "3.00:00:00", "reserve": 2 },
              "log": { "usage": "logs/usage.jsonl" }
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
        Assert.Equal(TimeSpan.FromDays(3), settings.BorrowMaxDuration);
        Assert.Equal(2, settings.BorrowReserve);
        Assert.Equal("logs/usage.jsonl", settings.UsageLog);
        Assert.True(settings.Access.AllowsAddress(IPAddress.Parse("10.1.2.3")));
        Assert.False(settings.Access.AllowsAddress(IPAddress.Parse("192.168.0.1")));
        Assert.False(settings.Access.AllowsClient(null, "guest"));
    }

    /// <summary>
    /// The borrow settings are read up to their limits, exactly: borrow.maxDuration from one second to 100 years (36,600
    /// days) and borrow.reserve up to 1,000,000; one step beyond each stops the start.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Settings files of the product with one borrow value each.
    ///
    /// Steps:
    /// - Load maxDuration 00:00:01, 36600.00:00:00, 00:00:00.9990000, 36600.00:00:01; reserve 1000000 and 1000001.
    ///
    /// Expected:
    /// - 1 s, 36,600 days, an error, an error; 1,000,000 and an error naming borrow.reserve.
    /// </remarks>
    [Fact]
    public void Load_BorrowLimits_AreExact()
    {
        ServerSettings Load(string borrow) => ServerSettings.Load(Write("{\"product\":\"acme-cad\",\"borrow\":" + borrow + "}"), _ => null);

        Assert.Equal(TimeSpan.FromSeconds(1), Load("""{"maxDuration":"00:00:01"}""").BorrowMaxDuration);
        Assert.Equal(TimeSpan.FromDays(36_600), Load("""{"maxDuration":"36600.00:00:00"}""").BorrowMaxDuration);
        Assert.Throws<ServerStartException>(() => Load("""{"maxDuration":"00:00:00.9990000"}"""));
        Assert.Throws<ServerStartException>(() => Load("""{"maxDuration":"36600.00:00:01"}"""));
        Assert.Equal(1_000_000, Load("""{"reserve":1000000}""").BorrowReserve);
        Assert.Contains(
            "borrow.reserve",
            Assert.Throws<ServerStartException>(() => Load("""{"reserve":1000001}""")).Message,
            StringComparison.Ordinal
        );
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
    /// - ServerStartException naming: the unknown key, product, listen (http, and a host name other than localhost),
    ///   rateLimit.perIp (0 and 10001), the allow entry, borrow.maxDuration (not a
    ///   duration, zero), borrow.reserve (-1), log.usage (no file name; three spaces in the file and in ELM_LOG_USAGE), and the
    ///   missing file.
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
        AssertError("""{"product":"acme-cad","listen":"https://licenses.example:7070"}""", "listen must name an IP address");
        AssertError("""{"product":"acme-cad","rateLimit":{"perIp":0}}""", "rateLimit.perIp");
        AssertError("""{"product":"acme-cad"}""", "rateLimit.perIp", new() { ["ELM_RATELIMIT_PERIP"] = "10001" });
        AssertError("""{"product":"acme-cad","allow":["somewhere"]}""", "somewhere");
        AssertError("""{"product":"acme-cad","borrow":{"maxDuration":"7 days"}}""", "borrow.maxDuration");
        AssertError("""{"product":"acme-cad","borrow":{"maxDuration":"00:00:00"}}""", "borrow.maxDuration");
        AssertError("""{"product":"acme-cad","borrow":{"reserve":-1}}""", "borrow.reserve");
        AssertError("""{"product":"acme-cad","log":{"usage":"logs/"}}""", "log.usage");
        AssertError("""{"product":"acme-cad","log":{"usage":"   "}}""", "log.usage must name a file: '   '.");
        AssertError("""{"product":"acme-cad"}""", "log.usage must name a file: '   '.", new() { ["ELM_LOG_USAGE"] = "   " });
        AssertError("""{"product":"acme-cad","product":"acme-cam"}""", "not valid");
        var e = Assert.Throws<ServerStartException>(() => ServerSettings.Load(Path.Combine(_directory, "none.json"), _ => null));
        Assert.Contains("does not exist", e.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// admin.listen is no longer a setting: a value that is left, in the file or in ELM_ADMIN_LISTEN, stops the start with the
    /// line that says where the administration API listens now, whatever the value is (design §13.8, V1d2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Settings files of the product with: admin.listen set to the earlier default http://127.0.0.1:7071; set to another
    ///   URL; set to the number 7071; set to an object; set to an empty string; set to an array that holds an object; set to
    ///   true; set to false; set to null; an admin section with a member "port" and no listen; an admin section with listen
    ///   set to an object and then a member "port"; no admin section.
    ///
    /// Steps:
    /// - Load the first eight; load the last with ELM_ADMIN_LISTEN set to the earlier default; load the one with null; load
    ///   the last with an empty ELM_ADMIN_LISTEN; load the two with the other member.
    ///
    /// Expected:
    /// - The first nine throw ServerStartException with exactly "admin.listen is no longer a setting: the administration API
    ///   listens on the socket file admin.sock in the data folder. Remove admin.listen (ELM_ADMIN_LISTEN).": the value's type
    ///   does not matter.
    /// - The null value and the empty variable load. The other member is an unknown key in both files: "… is not valid: …",
    ///   and the message names "port".
    /// </remarks>
    [Fact]
    public void Load_AdminListenLeft_StopsWithTheLine()
    {
        const string Line =
            "admin.listen is no longer a setting: the administration API listens on the socket file admin.sock in the data folder. "
            + "Remove admin.listen (ELM_ADMIN_LISTEN).";
        string plain = Write("""{"product":"acme-cad"}""");
        static string? Variable(string name, string value) => name == "ELM_ADMIN_LISTEN" ? value : null;

        string[] left =
        [
            Write("""{"product":"acme-cad","admin":{"listen":"http://127.0.0.1:7071"}}"""),
            Write("""{"product":"acme-cad","admin":{"listen":"http://[::1]:7099"}}"""),
            Write("""{"product":"acme-cad","admin":{"listen":7071}}"""),
            Write("""{"product":"acme-cad","admin":{"listen":{"port":7071}}}"""),
            Write("""{"product":"acme-cad","admin":{"listen":""}}"""),
            Write("""{"product":"acme-cad","admin":{"listen":[7071,{"a":[null]}]}}"""),
            Write("""{"product":"acme-cad","admin":{"listen":true}}"""),
            Write("""{"product":"acme-cad","admin":{"listen":false}}"""),
        ];
        foreach (string file in left)
        {
            Assert.Equal(Line, Assert.Throws<ServerStartException>(() => ServerSettings.Load(file, _ => null)).Message);
        }

        Assert.Equal(
            Line,
            Assert.Throws<ServerStartException>(() => ServerSettings.Load(plain, name => Variable(name, "http://127.0.0.1:7071"))).Message
        );

        Assert.Equal("acme-cad", ServerSettings.Load(Write("""{"product":"acme-cad","admin":{"listen":null}}"""), _ => null).Product);
        Assert.Equal("acme-cad", ServerSettings.Load(plain, name => Variable(name, string.Empty)).Product);
        string unknown = Write("""{"product":"acme-cad","admin":{"port":7071}}""");
        string refusal = Assert.Throws<ServerStartException>(() => ServerSettings.Load(unknown, _ => null)).Message;
        Assert.Contains("is not valid", refusal, StringComparison.Ordinal);
        Assert.Contains("port", refusal, StringComparison.Ordinal);

        // The value is skipped whole: the member after it is still read, and refused as unknown.
        string after = Write("""{"product":"acme-cad","admin":{"listen":{"a":[1,2]},"port":7071}}""");
        refusal = Assert.Throws<ServerStartException>(() => ServerSettings.Load(after, _ => null)).Message;
        Assert.Contains("is not valid", refusal, StringComparison.Ordinal);
        Assert.Contains("port", refusal, StringComparison.Ordinal);
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
    /// The settings reference of <c>elm-server</c> lists every key of the settings file and its variable (design §20.7, V1c2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - docs/guide/reference/server-configuration.md and docs/guide.ja/reference/server-configuration.md under the repository
    ///   root.
    ///
    /// Steps:
    /// - Walk the source-generated type of the settings file into its sections: every key as its dotted path (rateLimit.perIp),
    ///   and its variable by the rule ELM_ + the path in upper case with '.' as '_' (ELM_RATELIMIT_PERIP). Every key of
    ///   <see cref="ServerSettings.Load"/> reads that variable.
    /// - Add the variables with no key: ELM_TRUST_ROOT_KEYS and ELM_TLS_PASSWORD.
    /// - Look for each in code on both pages.
    ///
    /// Expected:
    /// - Every path and variable appears as a whole word in a code span or a code block on both pages; the message lists every
    ///   one that does not.
    /// </remarks>
    [Fact]
    public void SettingsFile_AllKeysAndVariables_AppearOnReferencePage()
    {
        IReadOnlyList<string> keys = SettingsReference.KeyPaths(ServerJsonContext.Default.SettingsFile);
        string[] names =
        [
            .. keys,
            .. keys.Select(SettingsReference.Variable),
            ServerSettings.TrustRootKeysVariable,
            ServerSettings.TlsPasswordVariable,
        ];

        IReadOnlyList<string> missing = ReferencePages.FindMissing("server-configuration.md", names);

        Assert.True(missing.Count == 0, ReferencePages.Message(missing));
    }

    private string Write(string json)
    {
        string path = Path.Combine(_directory, Guid.CreateVersion7().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }
}
