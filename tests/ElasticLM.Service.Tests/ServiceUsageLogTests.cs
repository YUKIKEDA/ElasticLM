using System.Net;
using System.Text;
using System.Text.Json;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using static ElasticLM.Service.Tests.ElmServiceActivationsTests;

namespace ElasticLM.Service.Tests;

/// <summary>
/// The usage log of <c>elm-service</c> (design §18.1, decision 0.95): the lease and borrow events of one node, signed with
/// the signing key and verified with its certificates, and its administration API (§14.5).
/// </summary>
public sealed class ServiceUsageLogTests
{
    private const string Cloud = "\"floating\":\"cloud\",\"clientInfoStrategy\":\"hostAndUser\",\"borrowLimit\":86400";
    private const string ThreeSeats = """[{"code":"core","seats":3}]""";

    /// <summary>
    /// Each lease and borrow event is one line with the license, product, feature, seats, key, and lease, and refusals are
    /// counted per feature and code; the log verifies with the certificates on its first line, without a product.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A node "n1" writing its usage log with a signing key certified for license, lease, and log; a cloud-floating policy
    ///   that keeps client info and lends borrows up to 86400 seconds; a license with 3 seats of core; each engine.
    ///
    /// Steps:
    /// - Device A acquires with client info and app 1.2, renews after 200 seconds, sends that renewal again as a retry, and
    ///   releases; acquires again, and an
    ///   administrator releases it. Device B borrows for 3600 seconds and returns it early; borrows again, and an
    ///   administrator releases it. Device A asks twice for feature nope. Stop the service; verify the node's files; read them.
    ///
    /// Expected:
    /// - Intact with no issue. The signed lines (the retry writes none) are service.started, lease.acquired, lease.renewed,
    ///   lease.released (by
    ///   device), lease.acquired, lease.released (by admin), borrow.started, borrow.returned (by device), borrow.started,
    ///   borrow.returned (by admin), lease.denied (nope, ELM_FEATURE_NOT_LICENSED, 2), service.stopped. The first acquisition
    ///   has the license, app, core, 1, A's key, the lease ID, its expiry, pc, u, and 1.2. No file holds the license key.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Usage_LeaseEvents_AreSignedLines(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin) = await StartLoggedAsync(test, "n1");
        (string licenseId, string key) = await LicenseAsync(admin, await CloudPolicyAsync(admin), ThreeSeats);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using var a = TestDevice.Create(test, service);
        using var b = TestDevice.Create(test, service);

        DeviceAnswer acquired = await a.AcquireAsync(key, more: ""","client":{"host":"pc","user":"u"},"app":"1.2" """);
        string first = LeaseId(acquired);
        test.Time.Advance(TimeSpan.FromSeconds(200));
        DeviceAnswer renewed = await a.RenewAsync(first, 1);
        Assert.Equal(HttpStatusCode.OK, renewed.Status);
        Assert.Equal(HttpStatusCode.OK, (await a.RenewAsync(first, 1, [renewed.Jti])).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await a.ReleaseAsync(first)).Status);
        string second = LeaseId(await a.AcquireAsync(key));
        await DeleteAsync(admin, $"v1/admin/leases/{second}");
        string[] prints = TestDevice.Fingerprints();
        string borrow = LeaseId(await b.BorrowAsync(key, 3600, prints));
        Assert.Equal(HttpStatusCode.NoContent, (await b.ReleaseAsync(borrow)).Status);
        string borrowAgain = LeaseId(await b.BorrowAsync(key, 3600, prints));
        await DeleteAsync(admin, $"v1/admin/leases/{borrowAgain}");
        Assert.Equal(HttpStatusCode.Forbidden, (await a.AcquireAsync(key, feature: "nope")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.AcquireAsync(key, feature: "nope")).Status);
        await service.StopAsync();

        LogReport report = Verify(test, "n1");
        Assert.Empty(report.Issues);
        Assert.All(report.Entries, e => Assert.True(e.IsSigned));
        Assert.Equal(
            [
                "service.started",
                "lease.acquired",
                "lease.renewed",
                "lease.released",
                "lease.acquired",
                "lease.released",
                "borrow.started",
                "borrow.returned",
                "borrow.started",
                "borrow.returned",
                "lease.denied",
                "service.stopped",
            ],
            report.Entries.Select(e => e.Type)
        );
        JsonElement line = report.Entries[1].Value;
        Assert.Equal(
            (licenseId, "app", "core", 1, a.KeyId, first, TestService.Start.ToUnixTimeSeconds() + 600, "pc", "u", "1.2"),
            (
                line.GetProperty("lic").GetString(),
                line.GetProperty("prd").GetString(),
                line.GetProperty("feat").GetString(),
                line.GetProperty("n").GetInt32(),
                line.GetProperty("key").GetString(),
                line.GetProperty("lease").GetString(),
                line.GetProperty("exp").GetInt64(),
                line.GetProperty("host").GetString(),
                line.GetProperty("user").GetString(),
                line.GetProperty("app").GetString()
            )
        );
        Assert.Equal(
            ["device", "admin", "device", "admin"],
            report.Entries.Where(e => e.Type is "lease.released" or "borrow.returned").Select(e => e.Value.GetProperty("by").GetString())
        );
        JsonElement denied = report.Entries[10].Value;
        Assert.Equal(
            ("nope", "ELM_FEATURE_NOT_LICENSED", 2L),
            (denied.GetProperty("feat").GetString(), denied.GetProperty("code").GetString(), denied.GetProperty("count").GetInt64())
        );
        Assert.Equal("n1", report.Entries[^1].Value.GetProperty("node").GetString());
        foreach (string file in Files(test, "n1"))
        {
            Assert.DoesNotContain(key, File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A lease and a borrow that run out are written by the sweep that removes them, once each (design §18.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A node writing its usage log; a cloud-floating license with 3 seats of core.
    ///
    /// Steps:
    /// - Acquire a lease (600 seconds) and borrow for 700 seconds; move the clock 701 seconds; wait for both expiry lines;
    ///   move the clock another minute; stop; verify.
    ///
    /// Expected:
    /// - Intact; one lease.expired with the lease's ID and one borrow.expired with the borrow's.
    /// </remarks>
    [Fact]
    public async Task Usage_Expiry_IsWrittenOnce()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin) = await StartLoggedAsync(test, "n1");
        (_, string key) = await LicenseAsync(admin, await CloudPolicyAsync(admin), ThreeSeats);
        using var device = TestDevice.Create(test, service);
        string lease = LeaseId(await device.AcquireAsync(key));
        string borrow = LeaseId(await device.BorrowAsync(key, 700, TestDevice.Fingerprints()));

        test.Time.Advance(TimeSpan.FromSeconds(701));
        await TestService.WaitForAsync(() =>
            Task.FromResult(
                Text(test, "n1").Contains("\"lease.expired\"", StringComparison.Ordinal)
                    && Text(test, "n1").Contains("\"borrow.expired\"", StringComparison.Ordinal)
            )
        );
        test.Time.Advance(TimeSpan.FromMinutes(1));
        await service.WaitForTimersAsync();
        await service.StopAsync();

        LogReport report = Verify(test, "n1");
        Assert.True(report.Intact);
        Assert.Equal(lease, Assert.Single(report.Entries, e => e.Type == "lease.expired").Value.GetProperty("lease").GetString());
        Assert.Equal(borrow, Assert.Single(report.Entries, e => e.Type == "borrow.expired").Value.GetProperty("lease").GetString());
    }

    /// <summary>
    /// A node set to write a usage log does not start unless a certificate lets the signing key sign for log (decision
    /// 0.95); without the setting it starts as before.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The signing key's certificate for license and lease only.
    ///
    /// Steps:
    /// - Start with usageLog.directory set; start with a log certificate and a usageLog.directory that is a file; start
    ///   without the setting.
    ///
    /// Expected:
    /// - ServiceStartException "No certificate that the trust store vouches for lets the signing key … sign for Log at …";
    ///   ServiceStartException "The usage log cannot be written in '…': …"; the third starts, and its folder has no file.
    /// </remarks>
    [Fact]
    public async Task Usage_WithoutLogCertificate_DoesNotStart()
    {
        await using TestService test = TestService.Open("sqlite");
        await test.SetupAsync();
        ServiceStartException refused = await Assert.ThrowsAsync<ServiceStartException>(() =>
            ElmService.StartAsync(
                test.Settings(s => s with { UsageLogDirectory = test.FilePath("usage") }),
                test.HostOptions(),
                TestContext.Current.CancellationToken
            )
        );
        Assert.StartsWith(
            $"No certificate that the trust store vouches for lets the signing key {KeyFiles.KeyId(test.SigningKey)} sign for Log at ",
            refused.Message,
            StringComparison.Ordinal
        );
        File.WriteAllText(test.FilePath("taken"), "a file, not a folder");
        ServiceStartException unwritable = await Assert.ThrowsAsync<ServiceStartException>(() =>
            ElmService.StartAsync(
                Logged(test, "n1") with
                {
                    UsageLogDirectory = test.FilePath("taken"),
                },
                test.HostOptions(),
                TestContext.Current.CancellationToken
            )
        );
        Assert.StartsWith($"The usage log cannot be written in '{test.FilePath("taken")}': ", unwritable.Message, StringComparison.Ordinal);
        await test.StartAsync();
        Assert.Empty(Directory.Exists(test.FilePath("usage")) ? Directory.EnumerateFiles(test.FilePath("usage")) : []);
    }

    /// <summary>
    /// When the usage log cannot be written, requests go on, and the next line starts a new file with chain.reset; the log
    /// still verifies, with warnings (design §18.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A node whose usage log key can be made to throw IOException; a cloud-floating license with 3 seats of core.
    ///
    /// Steps:
    /// - Acquire; make the key fail; seal through the administration API; acquire; let the key work; acquire; stop; verify.
    ///
    /// Expected:
    /// - 201, 204, 201, 201; three files (the first; one whose chain.reset could not be signed while the key still failed;
    ///   the one after); intact with a warning that names the reset after the failed write.
    /// </remarks>
    [Fact]
    public async Task Usage_WriteFails_RequestsGoOn()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        bool fail = false;
        SigningKeyAccess Wrap(SigningKeyAccess key) => use => fail ? throw new IOException("The disk is full.") : key(use);
        await using ElmService service = await ElmService.StartAsync(
            Logged(test, "n1"),
            new ServiceHostOptions
            {
                Time = test.Time,
                Environment = test.HostOptions().Environment,
                MinimumLogLevel = Microsoft.Extensions.Logging.LogLevel.Critical,
                WrapUsageLogKey = Wrap,
            },
            TestContext.Current.CancellationToken
        );
        HttpClient admin = test.Http(service, owner);
        (_, string key) = await LicenseAsync(admin, await CloudPolicyAsync(admin), ThreeSeats);
        using var device = TestDevice.Create(test, service);

        Assert.Equal(HttpStatusCode.Created, (await device.AcquireAsync(key)).Status);
        fail = true;
        using (HttpResponseMessage sealedNow = await admin.PostAsync("v1/admin/usage-log/seal", null, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, sealedNow.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Created, (await device.AcquireAsync(key)).Status);
        fail = false;
        test.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Created, (await device.AcquireAsync(key)).Status);
        await service.StopAsync();

        Assert.Equal(3, Files(test, "n1").Count);
        LogReport report = Verify(test, "n1");
        Assert.True(report.Intact);
        Assert.Contains(
            report.Issues,
            i => i.Severity == LogIssueSeverity.Warning && i.Message.StartsWith("The chain was reset: a write to", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// The administration API lists this node's files, returns one by its name only, and signs now; usagelog.read does not
    /// seal; a node without a usage log answers 409; each call writes its audit row.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A node "n1" writing its usage log; a key with usagelog.read alone; a second service without a usage log.
    ///
    /// Steps:
    /// - Seal; list; get the listed file; get an unknown name and "..%5Celm.db"; seal with the reader; list on the second
    ///   service; read the audit log.
    ///
    /// Expected:
    /// - 204; node n1 with one file whose size is the file's; its content is the file's, ending with a signature line; 404
    ///   ELM_NOT_FOUND "No usage log file of this node has this name." twice; 403 ELM_FORBIDDEN; 409 ELM_CONFLICT "This node
    ///   writes no usage log; set usageLog.directory."; audit rows usagelog.seal, usagelog.list, and usagelog.get with the
    ///   file's name. viewer and support have usagelog.read, only admin and owner usagelog.seal.
    /// </remarks>
    [Fact]
    public async Task Usage_AdminApi_ServesThisNode()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin) = await StartLoggedAsync(test, "n1");
        CancellationToken cancel = TestContext.Current.CancellationToken;

        using (HttpResponseMessage sealedNow = await admin.PostAsync("v1/admin/usage-log/seal", null, cancel))
        {
            Assert.Equal(HttpStatusCode.NoContent, sealedNow.StatusCode);
        }

        JsonElement list = await TestService.GetAsync(admin, "v1/admin/usage-log/files");
        JsonElement item = list.GetProperty("items").EnumerateArray().Single();
        string name = item.GetProperty("name").GetString()!;
        string path = Files(test, "n1").Single();
        Assert.Equal(
            ("n1", Path.GetFileName(path), new FileInfo(path).Length),
            (list.GetProperty("node").GetString(), name, item.GetProperty("size").GetInt64())
        );
        string content = await admin.GetStringAsync($"v1/admin/usage-log/files/{name}", cancel);
        Assert.Equal(Text(test, "n1"), content);
        Assert.StartsWith("{\"type\":\"auth\"", content.TrimEnd('\n').Split('\n')[^1], StringComparison.Ordinal);
        foreach (string other in new[] { "usage-n1-20261002T000000000Z.jsonl", "..%5Celm.db" })
        {
            await TestService.AssertProblemAsync(
                await admin.GetAsync($"v1/admin/usage-log/files/{other}", cancel),
                HttpStatusCode.NotFound,
                "ELM_NOT_FOUND",
                "No usage log file of this node has this name."
            );
        }

        HttpClient reader = test.Http(service, await TestService.CreateKeyAsync(admin, "\"usagelog.read\""));
        await TestService.AssertProblemAsync(
            await reader.PostAsync("v1/admin/usage-log/seal", null, cancel),
            HttpStatusCode.Forbidden,
            "ELM_FORBIDDEN",
            "The API key does not have the permission usagelog.seal."
        );

        await using (TestService plain = TestService.Open("sqlite"))
        {
            string owner = await plain.SetupAsync();
            ElmService without = await plain.StartAsync();
            await TestService.AssertProblemAsync(
                await plain.Http(without, owner).GetAsync("v1/admin/usage-log/files", cancel),
                HttpStatusCode.Conflict,
                "ELM_CONFLICT",
                "This node writes no usage log; set usageLog.directory."
            );
        }

        JsonElement[] rows = await TestService.AuditRowsAsync(test, service, await TestService.CreateKeyAsync(admin, "\"audit.read\""));
        Assert.Contains(rows, r => r.GetProperty("action").GetString() == "usagelog.seal" && r.GetProperty("result").GetString() == "ELM_OK");
        Assert.Contains(rows, r => r.GetProperty("action").GetString() == "usagelog.list");
        Assert.Contains(rows, r => r.GetProperty("action").GetString() == "usagelog.get" && r.GetProperty("target").GetString() == name);
        Assert.Contains("usagelog.read", Permissions.OfRole(Permissions.RoleViewer));
        Assert.Contains("usagelog.read", Permissions.OfRole(Permissions.RoleSupport));
        Assert.DoesNotContain("usagelog.seal", Permissions.OfRole(Permissions.RoleSupport));
        Assert.Contains("usagelog.seal", Permissions.OfRole(Permissions.RoleAdmin));
    }

    /// <summary>
    /// Nodes that share a folder keep separate chains: each node continues its own newest file (decision 0.95).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - One database and one usage log folder.
    ///
    /// Steps:
    /// - Run node a, then node b, then node a again, each acquiring once; verify each node's files.
    ///
    /// Expected:
    /// - Node a has two files and node b one; each verifies with no issue (a's second file links to its first).
    /// </remarks>
    [Fact]
    public async Task Usage_TwoNodes_KeepSeparateChains()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        string key = string.Empty;
        foreach (string node in new[] { "a", "b", "a" })
        {
            await using ElmService service = await ElmService.StartAsync(
                Logged(test, node),
                test.HostOptions(),
                TestContext.Current.CancellationToken
            );
            HttpClient admin = test.Http(service, owner);
            if (key.Length == 0)
            {
                (_, key) = await LicenseAsync(admin, await CloudPolicyAsync(admin), ThreeSeats);
            }

            using var device = TestDevice.Create(test, service);
            Assert.Equal(HttpStatusCode.Created, (await device.AcquireAsync(key)).Status);
            await service.StopAsync();
            test.Time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal((2, 1), (Files(test, "a").Count, Files(test, "b").Count));
        Assert.Empty(Verify(test, "a").Issues);
        Assert.Empty(Verify(test, "b").Issues);
    }

    /// <summary>
    /// Refusals are counted per feature and code; beyond 100 kinds in a minute they are counted without the feature, as
    /// elm-server does (design §18.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A usage log opened in a folder with a certificate for log.
    ///
    /// Steps:
    /// - Count refusals of 101 features, the first one twice, with ELM_NO_SEATS; write the counts; close; verify.
    ///
    /// Expected:
    /// - 101 lease.denied lines: f0 with 2, f1 to f99 with 1, and one without a feature with 1.
    /// </remarks>
    [Fact]
    public async Task Usage_Denials_AreCountedUpTo100Kinds()
    {
        await using TestService test = TestService.Open("sqlite");
        Logged(test, "n1");
        string certificate = File.ReadAllText(test.FilePath("log.cert"));
        using (
            ServiceUsageLog log = ServiceUsageLog.Open(
                test.FilePath("usage"),
                "n1",
                use => use(test.SigningKey),
                [certificate],
                test.Time,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
            )
        )
        {
            log.Denied("f0", ErrorCode.NoSeats);
            for (int i = 0; i < 101; i++)
            {
                log.Denied("f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), ErrorCode.NoSeats);
            }

            log.WriteDenials();
        }

        JsonElement[] denied = [.. Verify(test, "n1").Entries.Where(e => e.Type == "lease.denied").Select(e => e.Value)];
        Assert.Equal(101, denied.Length);
        Assert.Equal(2, denied.Single(d => d.TryGetProperty("feat", out JsonElement f) && f.GetString() == "f0").GetProperty("count").GetInt64());
        Assert.Equal(
            99,
            denied.Count(d => d.TryGetProperty("feat", out JsonElement f) && f.GetString() != "f0" && d.GetProperty("count").GetInt64() == 1)
        );
        JsonElement rest = denied.Single(d => !d.TryGetProperty("feat", out _));
        Assert.Equal(("ELM_NO_SEATS", 1L), (rest.GetProperty("code").GetString(), rest.GetProperty("count").GetInt64()));
    }

    // A node writing its usage log in the test's "usage" folder, with a certificate that allows log.
    private static ServiceSettings Logged(TestService test, string node)
    {
        string certificate = test.WriteCertificate(
            "log.cert",
            KeyUses.License | KeyUses.Lease | KeyUses.Log,
            TestService.Start.AddDays(-1),
            TimeSpan.FromDays(365)
        );
        return test.Settings(s => s with { UsageLogDirectory = test.FilePath("usage"), NodeName = node, SigningCertificateFiles = [certificate] });
    }

    private static async Task<(ElmService Service, HttpClient Admin)> StartLoggedAsync(TestService test, string node)
    {
        string owner = await test.SetupAsync();
        ElmService service = await test.StartAsync(_ => Logged(test, node));
        return (service, test.Http(service, owner));
    }

    private static IReadOnlyList<string> Files(TestService test, string node) => SignedLog.FindFiles(test.FilePath("usage"), "usage-" + node);

    private static string Text(TestService test, string node)
    {
        var text = new StringBuilder();
        foreach (string file in Files(test, node))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            text.Append(reader.ReadToEnd());
        }

        return text.ToString();
    }

    private static LogReport Verify(TestService test, string node) =>
        LogVerifier.Verify(Files(test, node), TrustStore.Create([File.ReadAllText(test.FilePath("root.pub.pem"))], []), null, "usage");

    private static string LeaseId(DeviceAnswer answer)
    {
        Assert.True(answer.Status is HttpStatusCode.Created, answer.Text);
        using JsonDocument payload = JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(answer.Lease.Split('.')[1]));
        return payload.RootElement.GetProperty("lse").GetString()!;
    }

    private static async Task DeleteAsync(HttpClient admin, string path)
    {
        using HttpResponseMessage response = await admin.DeleteAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<string> CloudPolicyAsync(HttpClient admin) =>
        await TestService.PolicyAsync(admin, await ProductAsync(admin, "app", "core"), Cloud);
}
