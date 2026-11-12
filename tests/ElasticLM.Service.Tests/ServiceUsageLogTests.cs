using System.Net;
using System.Net.Sockets;
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

    // The length of the file of Issue #327: more than the service and the system keep for a caller that stopped reading.
    private const int LargeFileLength = 32 * 1024 * 1024;

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
    ///   releases; acquires again, and an administrator releases it.
    /// - Device B borrows for 3600 seconds and returns it early; borrows again, tries to renew the borrow, and an
    ///   administrator releases it.
    /// - Device A asks twice for feature nope. Stop the service; verify the node's files; read them.
    ///
    /// Expected:
    /// - Intact with no issue. The signed lines (the retry writes none) are service.started, lease.acquired, lease.renewed,
    ///   lease.released (by device), lease.acquired, lease.released (by admin), borrow.started, borrow.returned (by device),
    ///   borrow.started, borrow.returned (by admin), two lease.denied lines (nope, ELM_FEATURE_NOT_LICENSED, 2; core,
    ///   ELM_INVALID_ARGUMENT, 1), service.stopped.
    /// - The first acquisition has the license, app, core, 1, A's key, the lease ID, its expiry, pc, u, and 1.2. No file
    ///   holds the license key.
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
        Assert.Equal(HttpStatusCode.BadRequest, (await b.RenewAsync(borrowAgain, 1)).Status);
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
        Assert.Equal(
            [("core", "ELM_INVALID_ARGUMENT", 1L), ("nope", "ELM_FEATURE_NOT_LICENSED", 2L)],
            report
                .Entries.Where(e => e.Type == "lease.denied")
                .Select(e =>
                    (e.Value.GetProperty("feat").GetString(), e.Value.GetProperty("code").GetString(), e.Value.GetProperty("count").GetInt64())
                )
                .Order()
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
    /// - A node writing its usage log; a cloud-floating license with 3 seats of core; each engine.
    ///
    /// Steps:
    /// - Acquire a lease (600 seconds) and borrow for 700 seconds; move the clock 701 seconds; wait for both expiry lines;
    ///   move the clock another minute; stop; verify.
    ///
    /// Expected:
    /// - Intact; one lease.expired with the lease's ID and one borrow.expired with the borrow's.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Usage_Expiry_IsWrittenOnce(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin) = await StartLoggedAsync(test, "n1");
        (_, string key) = await LicenseAsync(admin, await CloudPolicyAsync(admin), ThreeSeats);
        using var device = TestDevice.Create(test, service);
        string lease = LeaseId(await device.AcquireAsync(key));
        string borrow = LeaseId(await device.BorrowAsync(key, 700, TestDevice.Fingerprints()));

        test.Time.Advance(TimeSpan.FromSeconds(701));
        await test.WaitWithClockAsync(() =>
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
    /// A start that fails after service.started, here because the node cannot listen, writes service.stopped once before it
    /// closes the file, so every start has its pair (design §14.8, §18.1, V1c9 R7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A node "n1" writing its usage log; a TcpListener on 127.0.0.1 holding the port given as listen.
    ///
    /// Steps:
    /// - Start on the held port; verify the node's files.
    ///
    /// Expected:
    /// - ServiceStartException "elm-service cannot listen on …"; one file, intact with no issue, whose signed lines are
    ///   service.started and service.stopped, the second naming n1.
    /// </remarks>
    [Fact]
    public async Task Usage_ListenFails_WritesServiceStopped()
    {
        await using TestService test = TestService.Open("sqlite");
        await test.SetupAsync();
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        int port = ((IPEndPoint)holder.LocalEndpoint).Port;

        ServiceStartException refused = await Assert.ThrowsAsync<ServiceStartException>(() =>
            ElmService.StartAsync(
                Logged(test, "n1") with
                {
                    Listen = new Uri($"http://127.0.0.1:{port}"),
                },
                test.HostOptions(),
                TestContext.Current.CancellationToken
            )
        );
        Assert.StartsWith($"elm-service cannot listen on http://127.0.0.1:{port}/: ", refused.Message, StringComparison.Ordinal);
        Assert.Single(Files(test, "n1"));
        LogReport report = Verify(test, "n1");
        Assert.Empty(report.Issues);
        Assert.All(report.Entries, e => Assert.True(e.IsSigned));
        Assert.Equal(["service.started", "service.stopped"], report.Entries.Select(e => e.Type));
        Assert.Equal("n1", report.Entries[^1].Value.GetProperty("node").GetString());
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
    /// - Seal; list; copy the file as node n2's; get the listed file; get an unknown name, n2's file, and "..%5Celm.db"; move
    ///   the clock a day and seal (a second file); list with limit 1, after its next, and with limit 0; seal with the
    ///   reader; list on the second service; read the audit log.
    ///
    /// Expected:
    /// - 204; node n1 with one file whose size is the file's; its content is the file's, ending with a signature line; 404
    ///   ELM_NOT_FOUND "No usage log file of this node has this name." three times; the first file with next its name, the
    ///   second with no next, 400 ELM_INVALID_ARGUMENT; 403 ELM_FORBIDDEN; 409 ELM_CONFLICT "This node
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
        File.Copy(path, Path.Combine(test.FilePath("usage"), "usage-n2-20261001T000000000Z.jsonl"));
        string content = await admin.GetStringAsync($"v1/admin/usage-log/files/{name}", cancel);
        Assert.Equal(Text(test, "n1"), content);
        Assert.StartsWith("{\"type\":\"auth\"", content.TrimEnd('\n').Split('\n')[^1], StringComparison.Ordinal);
        foreach (string other in new[] { "usage-n1-20261002T000000000Z.jsonl", "usage-n2-20261001T000000000Z.jsonl", "..%5Celm.db" })
        {
            await TestService.AssertProblemAsync(
                await admin.GetAsync($"v1/admin/usage-log/files/{other}", cancel),
                HttpStatusCode.NotFound,
                "ELM_NOT_FOUND",
                "No usage log file of this node has this name."
            );
        }

        test.Time.Advance(TimeSpan.FromDays(1));
        using (HttpResponseMessage rotated = await admin.PostAsync("v1/admin/usage-log/seal", null, cancel))
        {
            Assert.Equal(HttpStatusCode.NoContent, rotated.StatusCode);
        }

        JsonElement page = await TestService.GetAsync(admin, "v1/admin/usage-log/files?limit=1");
        Assert.Equal((name, name), (page.GetProperty("items")[0].GetProperty("name").GetString(), page.GetProperty("next").GetString()));
        JsonElement rest = await TestService.GetAsync(admin, $"v1/admin/usage-log/files?after={name}");
        Assert.Equal((1, false), (rest.GetProperty("items").GetArrayLength(), rest.TryGetProperty("next", out _)));
        await TestService.AssertProblemAsync(
            await admin.GetAsync("v1/admin/usage-log/files?limit=0", cancel),
            HttpStatusCode.BadRequest,
            "ELM_INVALID_ARGUMENT",
            "limit must be between 1 and 200."
        );

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
        string certificate = File.ReadAllText(test.FilePath("log.cert")).Trim();
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

    /// <summary>
    /// The service starts with one to four certificate files, read without their line breaks (elm key certify ends each
    /// with one), and refuses five or an empty one (design §8.6, §14.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Certificate files ending with a line break, as every test writes them; an empty file.
    ///
    /// Steps:
    /// - Start with five certificate files; with the certificate and the empty file; with four.
    ///
    /// Expected:
    /// - ServiceStartException "signing.certificates must name one to four certificate files, none empty." twice; the
    ///   last starts.
    /// </remarks>
    [Fact]
    public async Task Start_CertificateFiles_AreOneToFour()
    {
        await using TestService test = TestService.Open("sqlite");
        await test.SetupAsync();
        string certificate = test.FilePath("signing.cert");
        File.WriteAllText(test.FilePath("empty.cert"), string.Empty);
        const string Rule = "signing.certificates must name one to four certificate files, none empty.";
        foreach (string[] files in new[] { Enumerable.Repeat(certificate, 5).ToArray(), [certificate, test.FilePath("empty.cert")] })
        {
            ServiceStartException refused = await Assert.ThrowsAsync<ServiceStartException>(() =>
                ElmService.StartAsync(
                    test.Settings(s => s with { SigningCertificateFiles = files }),
                    test.HostOptions(),
                    TestContext.Current.CancellationToken
                )
            );
            Assert.Equal(Rule, refused.Message);
        }

        await test.StartAsync(s => s with { SigningCertificateFiles = [.. Enumerable.Repeat(certificate, 4)] });
    }

    /// <summary>
    /// Two nodes on one PostgreSQL database each run the sweep, and a lease that runs out is written once, by the node whose
    /// sweep removed it (design §18.1, 0.95).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Nodes n1 and n2 on one PostgreSQL database, sharing the usage log folder; a cloud-floating license.
    ///
    /// Steps:
    /// - Acquire a lease on n1; move the clock 601 seconds and then a minute; wait until a lease.expired line is written;
    ///   stop both.
    ///
    /// Expected:
    /// - Exactly one lease.expired line across both nodes' files; each node's files verify.
    /// </remarks>
    [Theory]
    [InlineData("postgres")]
    public async Task Usage_TwoNodes_WriteAnExpiryOnce(string provider)
    {
        await using TestService test = TestService.Open(provider);
        string owner = await test.SetupAsync();
        ElmService n1 = await test.StartAsync(_ => Logged(test, "n1"));
        ElmService n2 = await test.StartAsync(_ => Logged(test, "n2"));
        HttpClient admin = test.Http(n1, owner);
        (_, string key) = await LicenseAsync(admin, await CloudPolicyAsync(admin), ThreeSeats);
        using var device = TestDevice.Create(test, n1);
        Assert.Equal(HttpStatusCode.Created, (await device.AcquireAsync(key)).Status);

        test.Time.Advance(TimeSpan.FromSeconds(601));
        await test.WaitWithClockAsync(() =>
            Task.FromResult((Text(test, "n1") + Text(test, "n2")).Contains("\"lease.expired\"", StringComparison.Ordinal))
        );
        test.Time.Advance(TimeSpan.FromMinutes(1));
        await n1.WaitForTimersAsync();
        await n2.WaitForTimersAsync();
        await n1.StopAsync();
        await n2.StopAsync();

        LogReport one = Verify(test, "n1");
        LogReport two = Verify(test, "n2");
        Assert.True(one.Intact && two.Intact);
        Assert.Single(one.Entries.Concat(two.Entries), e => e.Type == "lease.expired");
    }

    /// <summary>
    /// The pending lines are signed every 60 seconds; a policy that keeps no client info writes no host or user, but the
    /// app's version (design §18.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A node writing its usage log; a cloud-floating policy with clientInfoStrategy none; a license with 3 seats of core.
    ///
    /// Steps:
    /// - Acquire with client info and app 2.0; move the clock 60 seconds; wait for a signature after the acquisition; read
    ///   the acquisition's line.
    ///
    /// Expected:
    /// - The file's last line is a signature; the line has app 2.0 and no host or user.
    /// </remarks>
    [Fact]
    public async Task Usage_EveryMinute_IsSigned()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin) = await StartLoggedAsync(test, "n1");
        string policy = await TestService.PolicyAsync(admin, await ProductAsync(admin, "app", "core"), "\"floating\":\"cloud\"");
        (_, string key) = await LicenseAsync(admin, policy, ThreeSeats);
        using var device = TestDevice.Create(test, service);
        Assert.Equal(HttpStatusCode.Created, (await device.AcquireAsync(key, more: ""","client":{"host":"pc","user":"u"},"app":"2.0" """)).Status);

        test.Time.Advance(TimeSpan.FromSeconds(60));
        await test.WaitWithClockAsync(() =>
            Task.FromResult(Text(test, "n1").TrimEnd('\n').Split('\n')[^1].StartsWith("{\"type\":\"auth\"", StringComparison.Ordinal))
        );
        JsonElement line = Verify(test, "n1").Entries.Single(e => e.Type == "lease.acquired").Value;
        Assert.Equal(
            ("2.0", false, false),
            (line.GetProperty("app").GetString(), line.TryGetProperty("host", out _), line.TryGetProperty("user", out _))
        );
    }

    /// <summary>
    /// A lease written or removed reports whether it was there, so a request that the sweep went ahead of writes no line
    /// (design §18.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A lease row inserted under its license's lock; each engine.
    ///
    /// Steps:
    /// - Update it; remove it; remove it again; update it again.
    ///
    /// Expected:
    /// - true, true, false, false.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task Store_LeaseWrites_ReportTheRow(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin) = await StartLoggedAsync(test, "n1");
        (string licenseId, string key) = await LicenseAsync(admin, await CloudPolicyAsync(admin), ThreeSeats);
        using var device = TestDevice.Create(test, service);
        string id = LeaseId(await device.AcquireAsync(key));
        DateTimeOffset now = test.Time.GetUtcNow();
        bool[] results = await service.Database.WithLicenseAsync(
            Guid.Parse(licenseId),
            async scope =>
            {
                LeaseRow lease = (await scope!.FindLeaseAsync(id, now.ToUnixTimeSeconds(), CancellationToken.None))!;
                return new[]
                {
                    await scope.UpdateLeaseAsync(lease with { Seq = 2 }, CancellationToken.None),
                    await scope.DeleteLeaseAsync(id, CancellationToken.None),
                    await scope.DeleteLeaseAsync(id, CancellationToken.None),
                    await scope.UpdateLeaseAsync(lease, CancellationToken.None),
                };
            },
            now,
            TestContext.Current.CancellationToken
        );
        Assert.Equal([true, true, false, false], results);
    }

    /// <summary>
    /// A second node of the same name does not start while the first writes in the folder; it starts once the first stops
    /// (decision 0.95). The files name the node on their first line.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Node n1 writing its usage log.
    ///
    /// Steps:
    /// - Start a second n1 on the same folder; stop the first; start the second again; stop it; read the first lines; verify
    ///   as node n1 and as node n2.
    ///
    /// Expected:
    /// - ServiceStartException "Another node named 'n1' writes its usage log in '…'; give each node its own node.name."; the
    ///   second starts; each first line has node n1; as n1 no issue, as n2 an error.
    /// </remarks>
    [Fact]
    public async Task Usage_SameNodeName_DoesNotStartTwice()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService first, _) = await StartLoggedAsync(test, "n1");
        ServiceStartException refused = await Assert.ThrowsAsync<ServiceStartException>(() =>
            ElmService.StartAsync(Logged(test, "n1"), test.HostOptions(), TestContext.Current.CancellationToken)
        );
        Assert.Equal(
            $"Another node named 'n1' writes its usage log in '{Path.GetFullPath(test.FilePath("usage"))}'; give each node its own node.name.",
            refused.Message
        );

        await first.StopAsync();
        await using (ElmService second = await ElmService.StartAsync(Logged(test, "n1"), test.HostOptions(), TestContext.Current.CancellationToken))
        {
            await second.StopAsync();
        }

        Assert.All(Files(test, "n1"), f => Assert.Contains("\"node\":\"n1\"", File.ReadLines(f).First(), StringComparison.Ordinal));
        TrustStore trust = TrustStore.Create([File.ReadAllText(test.FilePath("root.pub.pem"))], []);
        Assert.Empty(LogVerifier.Verify(Files(test, "n1"), trust, null, "usage", "n1").Issues);
        Assert.False(LogVerifier.Verify(Files(test, "n1"), trust, null, "usage", "n2").Intact);
    }

    /// <summary>
    /// A download that the caller leaves midway is audited (design §18.2, V1d9): one usagelog.get row with the result
    /// ELM_NETWORK_ERROR, whose detail says how many bytes were handed to the response and how long the file is.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A node "n1" writing its usage log, and an older file of that node of 33,554,432 bytes; each engine.
    ///
    /// Steps:
    /// - Get the file; read 16,777,216 bytes of the answer; close the connection; wait for a usagelog.get row; check the
    ///   chain with POST /v1/admin/audit/verify; stop the service; read the rows.
    ///
    /// Expected:
    /// - 200. Exactly one usagelog.get row: result ELM_NETWORK_ERROR, target the file's name, detail with the two members
    ///   sent and size, size 33,554,432, and sent at least what the test read and less than size. The chain is intact.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task UsageGet_CallerLeavesMidway_IsAuditedAsNetworkError(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin) = await StartLoggedAsync(test, "n1");
        string name = WriteOlderFile(test, "n1", LargeFileLength);
        CancellationToken cancel = TestContext.Current.CancellationToken;

        long read = 0;
        using (HttpResponseMessage response = await admin.GetAsync(FilePath(name), HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using Stream body = await response.Content.ReadAsStreamAsync(cancel);
            byte[] buffer = new byte[81_920];
            while (read < LargeFileLength / 2)
            {
                int count = await body.ReadAsync(buffer, cancel);
                Assert.True(count > 0, "The answer ended before half of the file.");
                read += count;
            }
        }

        await TestService.WaitForAsync(async () => (await RowsAsync(service, "usagelog.get")).Length > 0);
        await TestService.AssertAuditIntactAsync(admin);
        await service.StopAsync();
        AuditEntry row = Assert.Single(await RowsAsync(service, "usagelog.get"));
        Assert.Equal(("ELM_NETWORK_ERROR", name), (row.Result, row.Target));
        (long sent, long size) = SentAndSize(row.Detail);
        Assert.Equal(LargeFileLength, size);
        Assert.InRange(sent, read, size - 1);
    }

    /// <summary>
    /// A whole download is audited with the same detail (design §18.2, V1d9): sent equals size.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A node "n1" writing its usage log, and an older file of that node of 200,000 bytes, more than two reads of the
    ///   download.
    ///
    /// Steps:
    /// - Get the file to its end; wait for a usagelog.get row; check the chain with POST /v1/admin/audit/verify; stop the
    ///   service; read the rows.
    ///
    /// Expected:
    /// - The answer is the file's bytes. Exactly one usagelog.get row: result ELM_OK, target the file's name, detail
    ///   {"sent":200000,"size":200000}. The chain is intact.
    /// </remarks>
    [Fact]
    public async Task UsageGet_WholeFile_IsAuditedWithSentEqualToSize()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin) = await StartLoggedAsync(test, "n1");
        string name = WriteOlderFile(test, "n1", 200_000);

        byte[] content = await admin.GetByteArrayAsync(FilePath(name), TestContext.Current.CancellationToken);

        Assert.Equal(File.ReadAllBytes(Path.Combine(test.FilePath("usage"), name)), content);
        await TestService.WaitForAsync(async () => (await RowsAsync(service, "usagelog.get")).Length > 0);
        await TestService.AssertAuditIntactAsync(admin);
        await service.StopAsync();
        AuditEntry row = Assert.Single(await RowsAsync(service, "usagelog.get"));
        Assert.Equal(("ELM_OK", name, "{\"sent\":200000,\"size\":200000}"), (row.Result, row.Target, row.Detail));
    }

    /// <summary>
    /// A download sends the file up to the length it had when the download began, so a file that grows meanwhile, as the
    /// node's newest file does, is still audited with sent equal to size (design §18.2, V1d9).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A node "n1" writing its usage log, and an older file of that node of 33,553,432 bytes: not a whole number of the
    ///   download's reads, so a last read that is not cut to the length takes bytes added after it.
    ///
    /// Steps:
    /// - Get the file; read 1,048,576 bytes of the answer; add 1,048,576 bytes to the file; read the answer to its end;
    ///   wait for a usagelog.get row; stop the service; read the rows.
    ///
    /// Expected:
    /// - The answer is the file's first 33,553,432 bytes. Exactly one usagelog.get row: result ELM_OK, detail
    ///   {"sent":33553432,"size":33553432}.
    /// </remarks>
    [Fact]
    public async Task UsageGet_FileGrowsWhileItIsSent_SendsTheLengthItHad()
    {
        const int Length = LargeFileLength - 1_000;
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin) = await StartLoggedAsync(test, "n1");
        string name = WriteOlderFile(test, "n1", Length);
        string path = Path.Combine(test.FilePath("usage"), name);
        CancellationToken cancel = TestContext.Current.CancellationToken;

        long read = 0;
        using var received = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        using (HttpResponseMessage response = await admin.GetAsync(FilePath(name), HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            await using Stream body = await response.Content.ReadAsStreamAsync(cancel);
            byte[] buffer = new byte[81_920];
            int count;
            while (read < 1_048_576 && (count = await body.ReadAsync(buffer, cancel)) > 0)
            {
                received.AppendData(buffer, 0, count);
                read += count;
            }

            using (var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                file.Write(new byte[1_048_576]);
            }

            while ((count = await body.ReadAsync(buffer, cancel)) > 0)
            {
                received.AppendData(buffer, 0, count);
                read += count;
            }
        }

        Assert.Equal(Length, read);
        using (FileStream file = File.OpenRead(path))
        {
            byte[] first = new byte[Length];
            await file.ReadExactlyAsync(first, cancel);
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(first), received.GetHashAndReset());
        }

        await TestService.WaitForAsync(async () => (await RowsAsync(service, "usagelog.get")).Length > 0);
        await service.StopAsync();
        AuditEntry row = Assert.Single(await RowsAsync(service, "usagelog.get"));
        Assert.Equal(("ELM_OK", name, "{\"sent\":33553432,\"size\":33553432}"), (row.Result, row.Target, row.Detail));
    }

    /// <summary>
    /// A download whose file cannot be read to the length it had ends as a failed transfer, not as a whole answer, and is
    /// audited with the result ELM_INTERNAL_ERROR (design §18.2, V1d9).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A node "n1" writing its usage log, and an older file of that node of 33,554,432 bytes.
    ///
    /// Steps:
    /// - Get the file; read 1,048,576 bytes of the answer; cut the file to 16,777,216 bytes, as another program on the host
    ///   can; read the answer to its end; wait for a usagelog.get row; check the chain with POST /v1/admin/audit/verify; stop
    ///   the service; read the rows.
    ///
    /// Expected:
    /// - 200, then the read of the answer throws an IOException instead of ending. Exactly one usagelog.get row: result
    ///   ELM_INTERNAL_ERROR, target the file's name, size 33,554,432, and sent at least what the test read and less than
    ///   size. The chain is intact.
    /// </remarks>
    [Fact]
    public async Task UsageGet_FileCutWhileItIsSent_FailsTheTransferAndIsAuditedAsInternalError()
    {
        await using TestService test = TestService.Open("sqlite");
        (ElmService service, HttpClient admin) = await StartLoggedAsync(test, "n1");
        string name = WriteOlderFile(test, "n1", LargeFileLength);
        CancellationToken cancel = TestContext.Current.CancellationToken;

        long read = 0;
        using (HttpResponseMessage response = await admin.GetAsync(FilePath(name), HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using Stream body = await response.Content.ReadAsStreamAsync(cancel);
            byte[] buffer = new byte[81_920];
            while (read < 1_048_576)
            {
                int count = await body.ReadAsync(buffer, cancel);
                Assert.True(count > 0, "The answer ended before the first mebibyte.");
                read += count;
            }

            string path = Path.Combine(test.FilePath("usage"), name);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                file.SetLength(LargeFileLength / 2);
            }

            await Assert.ThrowsAnyAsync<IOException>(async () =>
            {
                int count;
                while ((count = await body.ReadAsync(buffer, cancel)) > 0)
                {
                    read += count;
                }
            });
        }

        await TestService.WaitForAsync(async () => (await RowsAsync(service, "usagelog.get")).Length > 0);
        await TestService.AssertAuditIntactAsync(admin);
        await service.StopAsync();
        AuditEntry row = Assert.Single(await RowsAsync(service, "usagelog.get"));
        Assert.Equal(("ELM_INTERNAL_ERROR", name), (row.Result, row.Target));
        (long sent, long size) = SentAndSize(row.Detail);
        Assert.Equal(LargeFileLength, size);
        Assert.InRange(sent, read, size - 1);
    }

    /// <summary>
    /// An operation that ends with an exception that is not the database's is audited with the result ELM_INTERNAL_ERROR,
    /// and the caller gets no answer of the operation (design §18.2, V1d9).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A node "n1" writing its usage log, whose access to the signing key the test can make throw a
    ///   CryptographicException (the host option of the usage log's key); the line service.started waits for its signature.
    ///
    /// Steps:
    /// - Make the key throw; POST /v1/admin/usage-log/seal; let the key work again; check the chain with
    ///   POST /v1/admin/audit/verify; read the rows.
    ///
    /// Expected:
    /// - 500 without a body. Exactly one usagelog.seal row: result ELM_INTERNAL_ERROR, no target, no detail. The chain is
    ///   intact.
    /// </remarks>
    [Fact]
    public async Task Operation_ThrowsAnotherException_IsAuditedAsInternalError()
    {
        await using TestService test = TestService.Open("sqlite");
        string owner = await test.SetupAsync();
        bool fail = false;
        SigningKeyAccess Wrap(SigningKeyAccess key) =>
            use => fail ? throw new System.Security.Cryptography.CryptographicException("The key cannot sign.") : key(use);
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

        fail = true;
        using (HttpResponseMessage failed = await admin.PostAsync("v1/admin/usage-log/seal", null, TestContext.Current.CancellationToken))
        {
            fail = false;
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Empty(await failed.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        }

        await TestService.AssertAuditIntactAsync(admin);
        AuditEntry row = Assert.Single(await RowsAsync(service, "usagelog.seal"));
        Assert.Equal(("ELM_INTERNAL_ERROR", null, null), (row.Result, row.Target, row.Detail));
    }

    /// <summary>
    /// A state change whose transaction committed its audit row gets no second row when a later step of the operation
    /// fails (design §18.2, V1d9): the row of the transaction stands.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A node "n1" writing its usage log; a cloud-floating license; a lease of a device; each engine.
    /// - The table of the products renamed, so that the read for the usage line, which follows the commit of the release,
    ///   fails as a database that goes away does.
    ///
    /// Steps:
    /// - DELETE /v1/admin/leases/{id}; give the table its name back; check the chain with POST /v1/admin/audit/verify;
    ///   count the leases; read the rows.
    ///
    /// Expected:
    /// - 500 ELM_INTERNAL_ERROR "The database failed."; no lease is left; exactly one lease.release row: result ELM_OK,
    ///   target the lease. The chain is intact.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestService.Providers), MemberType = typeof(TestService))]
    public async Task StateChange_FailsAfterItsCommit_LeavesOneRow(string provider)
    {
        await using TestService test = TestService.Open(provider);
        (ElmService service, HttpClient admin) = await StartLoggedAsync(test, "n1");
        (_, string key) = await LicenseAsync(admin, await CloudPolicyAsync(admin), ThreeSeats);
        using var device = TestDevice.Create(test, service);
        string lease = LeaseId(await device.AcquireAsync(key));

        await test.ExecuteAsync("ALTER TABLE products RENAME TO products_gone");
        await TestService.AssertProblemAsync(
            await admin.DeleteAsync($"v1/admin/leases/{lease}", TestContext.Current.CancellationToken),
            HttpStatusCode.InternalServerError,
            "ELM_INTERNAL_ERROR",
            "The database failed."
        );
        await test.ExecuteAsync("ALTER TABLE products_gone RENAME TO products");

        await TestService.AssertAuditIntactAsync(admin);
        Assert.Equal(0, await test.ScalarAsync("SELECT COUNT(*) FROM leases"));
        AuditEntry row = Assert.Single(await RowsAsync(service, "lease.release"));
        Assert.Equal(("ELM_OK", lease), (row.Result, row.Target));
    }

    private static string FilePath(string name) => $"v1/admin/usage-log/files/{name}";

    // Writes a file of this node from a day before the test's start, which the node lists and serves but does not write to.
    // Its lines are numbered so that a wrong part of it does not compare as equal.
    private static string WriteOlderFile(TestService test, string node, int length)
    {
        string name = $"usage-{node}-20260901T000000000Z.jsonl";
        using var stream = new FileStream(Path.Combine(test.FilePath("usage"), name), FileMode.CreateNew, FileAccess.Write);
        for (int line = 0; stream.Length < length; line++)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(
                line.ToString("D10", System.Globalization.CultureInfo.InvariantCulture).PadRight(1_023, 'x') + "\n"
            );
            stream.Write(bytes, 0, (int)Math.Min(bytes.Length, length - stream.Length));
        }

        return name;
    }

    private static async Task<AuditEntry[]> RowsAsync(ElmService service, string action)
    {
        IReadOnlyList<AuditRow> rows = await service.Database.ListAuditAsync(null, false, AuditLog.MaxLimit, TestContext.Current.CancellationToken);
        return [.. rows.Select(r => r.Entry).Where(e => e.Action == action)];
    }

    private static (long Sent, long Size) SentAndSize(string? detail)
    {
        using JsonDocument parsed = JsonDocument.Parse(detail!);
        Assert.Equal(["sent", "size"], parsed.RootElement.EnumerateObject().Select(p => p.Name));
        return (parsed.RootElement.GetProperty("sent").GetInt64(), parsed.RootElement.GetProperty("size").GetInt64());
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
