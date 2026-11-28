using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ElasticLM.Client;
using ElasticLM.Licensing;

namespace ElasticLM.E2E;

/// <summary>
/// The published elm-server as a systemd service, set up as the How-to guide "Run elm-server as a system service" says for
/// Linux (design §13.1, "systemd and launchd", V1d30): a system user of its own, the files root writes, one start from the
/// terminal, the activation as that user, and the unit of <c>samples/ElmServerService</c>.
/// </summary>
public sealed partial class SystemdServiceTests
{
    /// <summary>
    /// The page's steps for Linux: elm-server starts under systemd as its own user, comes back after a kill, stops on
    /// systemctl stop with exit code 0 and its records closed, and writes its operational log to the journal.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux with systemd as the service manager, and sudo without a password, as the runners of GitHub Actions have. On
    ///   GitHub Actions the test fails without them; elsewhere it is skipped and says so, and it runs only with
    ///   ELM_E2E_SYSTEM_SERVICES=1, since it creates a system user and a unit.
    /// - The vendor's elm-service with the product acme-cad, a server policy, and a license of 3 seats.
    /// - The names of the page with the digits of this run: the user elme2e{8 hex digits}, the unit elm-e2e-{digits}, and the
    ///   folders /opt/elm-e2e-{digits}, /etc/elm-e2e-{digits}, and /var/lib/elm-e2e-{digits} in place of /opt/elm-server,
    ///   /etc/elm-server, and /var/lib/elm-server.
    ///
    /// Steps:
    /// - useradd --system --user-group --shell /usr/sbin/nologin; install the published elm-server and elm (0755) and the settings file of
    ///   samples/ElmServerService/linux (listen on 127.0.0.1 port 0, service.url the vendor's service) and the root key (0644) as root; install -d -o the user
    ///   -m 0700 the data folder.
    /// - Run elm-server once as the user from the terminal (sudo -u); elm server activate as the user, with the key in
    ///   ELM_LICENSE_KEY since the test has no terminal to type it in.
    /// - Install the unit of the sample (0644), daemon-reload, enable --now; read with systemctl show the values systemd uses
    ///   for the unit's stop timeout and its hardening directives; wait until elm server status answers as the user.
    /// - Kill the main process with SIGKILL and time until elm server status answers again from a new main process.
    /// - systemctl stop --no-block, timed until the unit is inactive; read the unit's exit status and result, the newest usage
    ///   log and borrow record, and the unit's journal.
    /// - systemctl start, wait until elm server status answers, and systemctl stop again.
    /// - Clean up, also when the run is canceled: disable and remove the unit, end every process of the user, remove the
    ///   folders and the user, and check that the user is gone.
    ///
    /// Expected:
    /// - The first start exits with 1, and its line starts with the missing server license and names an installation key;
    ///   activate exits with 0.
    /// - The unit's TimeoutStopUSec is 1min (the sample's TimeoutStopSec=60), longer than the stop's upper bound of 30 seconds,
    ///   up to 1 second, and the time to close the records (design §13.1).
    /// - systemctl show gives each hardening directive of the sample the value the sample writes, the data folder of this run
    ///   as the only path that ReadWritePaths opens (design §13.1, V1d30a).
    /// - After the kill a new main process answers, at least RestartSec (5 seconds) after the kill.
    /// - After the stop the unit is inactive with ExecMainCode 1 (exited), ExecMainStatus 0, and Result success; the newest
    ///   usage log ends with server.stopped and its signature, and the newest borrow record holds server.stopped; the journal holds the server.started and server.stopped lines of the operational log.
    /// - systemctl start brings the server back.
    /// - The values read and the times go to the job's log (RunReport.Note).
    /// </remarks>
    [Fact]
    public async Task Systemd_ThePageSteps_RunRestartAndStopTheServer()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "systemd runs on Linux only.");
        bool onActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        if (!Directory.Exists("/run/systemd/system"))
        {
            Assert.False(onActions, "On GitHub Actions this test must run, and systemd is not the service manager here.");
            Assert.Skip("systemd is not the service manager of this machine (/run/systemd/system does not exist).");
        }

        await SystemService.RequireSudoAsync();

        string id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        string user = "elme2e" + id;
        string unit = "elm-e2e-" + id;
        string bin = "/opt/elm-e2e-" + id;
        string etc = "/etc/elm-e2e-" + id;
        string data = "/var/lib/elm-e2e-" + id;
        string license = data + "/server.elmlic";
        string unitFile = $"/etc/systemd/system/{unit}.service";
        string elm = bin + "/elm";

        await using Vendor vendor = await Vendor.CreateAsync("license", "lease", "log");
        (ServiceProcess started, string key) = await SystemService.StartVendorServiceAsync(vendor);
        await using ServiceProcess service = started;

        bool passed = false;
        try
        {
            // The page's step 1: the user and the folders.
            await SystemService.SudoOkAsync("useradd", "--system", "--user-group", "--shell", "/usr/sbin/nologin", user);
            RunReport.Note(
                $"elm-server under systemd: the runner's /opt is {(await SystemService.SudoOkAsync("stat", "-c", "%U:%G %a", "/opt")).Trim()}."
            );
            await SystemService.SudoOkAsync("install", "-d", "-m", "0755", bin);
            await SystemService.SudoOkAsync("install", "-m", "0755", Binaries.Server, Binaries.Elm, bin);
            await SystemService.SudoOkAsync("install", "-d", "-m", "0755", etc);
            await SystemService.SudoOkAsync("install", "-m", "0644", vendor.RootPublicKey, etc + "/root.pub.pem");
            string settings = SystemService.Sample(
                "linux/elm-server.json",
                ("https://0.0.0.0:7070", "https://127.0.0.1:0"),
                ("/var/lib/elm-server", data),
                ("/etc/elm-server", etc),
                ("https://licensing.example.com", service.Url.ToString().TrimEnd('/'))
            );
            await SystemService.InstallTextAsync(vendor, settings, etc + "/elm-server.json", "0644");
            await SystemService.SudoOkAsync("install", "-d", "-o", user, "-g", user, "-m", "0700", data);

            // Step 2: one start from the terminal makes the installation key and stops for want of the license.
            (int first, string firstOut, string firstError) = await SystemService.SudoAsync([
                "-u",
                user,
                bin + "/elm-server",
                "--config",
                etc + "/elm-server.json",
            ]);
            Assert.True(first == 1, $"The first start exited with {first}: {firstError}{firstOut}");
            string firstLine = firstError.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1];
            Assert.StartsWith($"error: The server license '{license}' does not exist.", firstLine, StringComparison.Ordinal);
            Assert.Matches(InstallationKey(), firstLine);

            // Step 3: the activation as the service's user; the key comes from ELM_LICENSE_KEY, which sudo passes on only
            // because --preserve-env names it (design §13.7, V1d19).
            (int activated, _, string activateError) = await SystemService.SudoAsync(
                [
                    "--preserve-env=ELM_LICENSE_KEY",
                    "-u",
                    user,
                    elm,
                    "server",
                    "activate",
                    "--product",
                    SystemService.Product,
                    "--data",
                    data,
                    "--service",
                    service.Url.ToString(),
                    "--license",
                    license,
                    "--trust",
                    etc + "/root.pub.pem",
                ],
                new Dictionary<string, string?> { ["ELM_LICENSE_KEY"] = key }
            );
            Assert.True(activated == 0, $"elm server activate exited with {activated}: {activateError}");

            // Step 4: the unit of the sample.
            await InstallUnitAsync(vendor, user, bin, etc, data, unit, unitFile);
            await SystemService.SudoOkAsync("systemctl", "enable", "--now", unit);

            string timeout = await ShowAsync(unit, "TimeoutStopUSec");
            string limits = $"{await ShowAsync(unit, "StartLimitBurst")} starts in {await ShowAsync(unit, "StartLimitIntervalUSec")}";
            RunReport.Note($"elm-server under systemd: TimeoutStopUSec={timeout}, start limit {limits}.");

            // The unit writes TimeoutStopSec=60 rather than leaning on DefaultTimeoutStopSec, which the machine's settings can
            // change (design §13.1); systemctl show prints it as 1min.
            Assert.Equal("1min", timeout);
            await AssertHardenedAsync(unit, data);

            await WaitForStatusAsync(user, elm, data, unit);
            string firstPid = await ShowAsync(unit, "MainPID");

            // The restart: a kill is a failure that Restart=on-failure starts again after RestartSec.
            long killed = Stopwatch.GetTimestamp();
            await SystemService.SudoOkAsync("systemctl", "kill", "--signal=SIGKILL", unit);
            await RealClock.WaitUntilAsync(
                "the restart under systemd",
                async () => await ShowAsync(unit, "MainPID") is string pid && pid != "0" && pid != firstPid,
                SystemService.StateTimeout
            );
            await WaitForStatusAsync(user, elm, data, unit);
            TimeSpan restart = Stopwatch.GetElapsedTime(killed);
            RunReport.Note($"elm-server under systemd: SIGKILL to an answer from the restarted server took {restart.TotalMilliseconds:F0} ms.");
            Assert.True(restart >= TimeSpan.FromSeconds(4.9), $"The server answered {restart.TotalSeconds:F1} s after the kill, before RestartSec.");

            // The stop, timed until the unit is inactive, so that the wait counts in the run's real-clock budget.
            long stopping = Stopwatch.GetTimestamp();
            await SystemService.SudoOkAsync("systemctl", "stop", "--no-block", unit);
            await RealClock.WaitUntilAsync(
                "the stop under systemd",
                async () => await ShowAsync(unit, "ActiveState") == "inactive",
                SystemService.StateTimeout
            );
            TimeSpan stop = Stopwatch.GetElapsedTime(stopping);
            string code = await ShowAsync(unit, "ExecMainCode");
            string status = await ShowAsync(unit, "ExecMainStatus");
            string result = await ShowAsync(unit, "Result");
            RunReport.Note(
                $"elm-server under systemd: systemctl stop to inactive took {stop.TotalMilliseconds:F0} ms; ExecMainCode={code}, ExecMainStatus={status}, Result={result}."
            );
            Assert.Equal("1", code);
            Assert.Equal("0", status);
            Assert.Equal("success", result);
            await SystemService.AssertRecordsClosedAsync(data);

            // The operational log reaches the journal.
            string journal = string.Empty;
            await RealClock.WaitUntilAsync(
                "the journal of the unit",
                async () =>
                {
                    journal = await SystemService.SudoOkAsync("journalctl", "-u", unit, "--no-pager", "-o", "cat");
                    return journal.Contains("server.stopped: ", StringComparison.Ordinal);
                },
                SystemService.StateTimeout
            );
            Assert.Contains("server.started: listening on port", journal, StringComparison.Ordinal);

            // The page's "Start again".
            await SystemService.SudoOkAsync("systemctl", "start", unit);
            await WaitForStatusAsync(user, elm, data, unit);
            await SystemService.SudoOkAsync("systemctl", "stop", unit);
            passed = true;
        }
        finally
        {
            await CleanUpAsync(passed, user, unit, unitFile, bin, etc, data);
        }
    }

    /// <summary>
    /// Under the page's unit, with its hardening, elm-server lends a lease to an app and takes it back, and then stops with
    /// its records closed (V1d30a, question 30a-3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - As for <see cref="Systemd_ThePageSteps_RunRestartAndStopTheServer"/>, but without the vendor's elm-service: the
    ///   server license is issued by elm with 15-second leases, bound to the key of the first start, and installed owned by
    ///   the user with mode 0600, as the page says for a license file the vendor issues by hand. A license that elm-service
    ///   issues has leases of at least 2 minutes (the policy's leaseDuration, design §14.5), and a start without records
    ///   refuses new leases for a lease's length (design §13.4), longer than the run's real-clock budget of 2 minutes
    ///   (design §20.7).
    /// - The settings keep the sample's service.url: the server checks at its start that the license path can be written,
    ///   and, with no activation kept, never contacts the service.
    ///
    /// Steps:
    /// - useradd, install the programs, the settings, the root key, and the data folder as the other test does; run
    ///   elm-server once as the user; elm license issue with --bind-key of the key its line names and --lease-minutes 0.25;
    ///   install the file as the license.
    /// - Install the unit of the sample, enable --now, and wait until elm server status answers as the user.
    /// - Read the port from the server.started line of the journal; acquire pro with ElasticLM.Client, waiting each
    ///   Retry-After while the server answers ELM_SERVER_RECOVERING; elm server leases as the user; release the lease;
    ///   elm server leases again.
    /// - systemctl stop --no-block, timed until the unit is inactive; read the unit's exit status and result, the newest
    ///   usage log, and the newest borrow record.
    /// - Clean up as the other test does.
    ///
    /// Expected:
    /// - The lease is held with 1 seat, and elm server leases names it; after the release, elm server leases no longer
    ///   names it.
    /// - After the stop the unit is inactive with ExecMainStatus 0 and Result success; the newest usage log holds the lease's
    ///   lease.acquired and lease.released lines and ends with server.stopped and its signature, and the newest borrow
    ///   record holds server.stopped.
    /// - The time from the start to the lease goes to the job's log (RunReport.Note).
    /// </remarks>
    [Fact]
    public async Task Systemd_HardenedUnit_LendsAndTakesBackALease()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "systemd runs on Linux only.");
        bool onActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        if (!Directory.Exists("/run/systemd/system"))
        {
            Assert.False(onActions, "On GitHub Actions this test must run, and systemd is not the service manager here.");
            Assert.Skip("systemd is not the service manager of this machine (/run/systemd/system does not exist).");
        }

        await SystemService.RequireSudoAsync();

        string id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        string user = "elme2e" + id;
        string unit = "elm-e2e-" + id;
        string bin = "/opt/elm-e2e-" + id;
        string etc = "/etc/elm-e2e-" + id;
        string data = "/var/lib/elm-e2e-" + id;
        string license = data + "/server.elmlic";
        string unitFile = $"/etc/systemd/system/{unit}.service";
        string elm = bin + "/elm";

        await using Vendor vendor = await Vendor.CreateAsync("license", "lease");
        bool passed = false;
        try
        {
            await SystemService.SudoOkAsync("useradd", "--system", "--user-group", "--shell", "/usr/sbin/nologin", user);
            await SystemService.SudoOkAsync("install", "-d", "-m", "0755", bin);
            await SystemService.SudoOkAsync("install", "-m", "0755", Binaries.Server, Binaries.Elm, bin);
            await SystemService.SudoOkAsync("install", "-d", "-m", "0755", etc);
            await SystemService.SudoOkAsync("install", "-m", "0644", vendor.RootPublicKey, etc + "/root.pub.pem");
            string settings = SystemService.Sample(
                "linux/elm-server.json",
                ("https://0.0.0.0:7070", "https://127.0.0.1:0"),
                ("/var/lib/elm-server", data),
                ("/etc/elm-server", etc)
            );
            await SystemService.InstallTextAsync(vendor, settings, etc + "/elm-server.json", "0644");
            await SystemService.SudoOkAsync("install", "-d", "-o", user, "-g", user, "-m", "0700", data);

            (int first, string firstOut, string firstError) = await SystemService.SudoAsync([
                "-u",
                user,
                bin + "/elm-server",
                "--config",
                etc + "/elm-server.json",
            ]);
            Assert.True(first == 1, $"The first start exited with {first}: {firstError}{firstOut}");
            Match key = InstallationKey().Match(firstError);
            Assert.True(key.Success, $"The first start named no installation key: {firstError}");

            string issued = vendor.File("server.elmlic");
            string expires = DateTimeOffset.UtcNow.AddDays(30).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            await vendor.ElmOkAsync(
                "license",
                "issue",
                "--signing-key",
                vendor.SigningKey,
                "--certificate",
                vendor.Certificate,
                "--product",
                SystemService.Product,
                "--license-id",
                "e2e-systemd-lease",
                "--kind",
                "server",
                "--feature",
                "pro;seats=3",
                "--expires",
                expires,
                "--bind-key",
                key.Groups[1].Value,
                "--lease-minutes",
                "0.25",
                "--trust",
                vendor.RootPublicKey,
                "--out",
                issued
            );
            await SystemService.SudoOkAsync("install", "-m", "0600", "-o", user, "-g", user, issued, license);

            long starting = Stopwatch.GetTimestamp();
            await InstallUnitAsync(vendor, user, bin, etc, data, unit, unitFile);
            await SystemService.SudoOkAsync("systemctl", "enable", "--now", unit);
            await WaitForStatusAsync(user, elm, data, unit);

            Match port = Match.Empty;
            await RealClock.WaitUntilAsync(
                "the server.started line in the journal",
                async () =>
                    (port = ListeningPort().Match(await SystemService.SudoOkAsync("journalctl", "-u", unit, "--no-pager", "-o", "cat"))).Success,
                SystemService.StateTimeout
            );
            var options = new LicenseClientOptions
            {
                ProductId = SystemService.Product,
                ServerUrl = new Uri($"https://127.0.0.1:{port.Groups[1].Value}"),
                Trust = TrustStore.Create([await File.ReadAllTextAsync(vendor.RootPublicKey, TestContext.Current.CancellationToken)], []),
                DataDirectory = vendor.File("client"),
            };
            Assert.True(LicenseClient.TryCreate(options, out LicenseClient? created, out LicenseError? error), error?.Message);
            string leaseId;
            await using (LicenseClient client = created!)
            {
                Lease lease = await AcquireAfterRecoveryAsync(client);
                RunReport.Note(
                    $"elm-server under systemd: the start to a lease from the hardened unit took {Stopwatch.GetElapsedTime(starting).TotalMilliseconds:F0} ms."
                );
                Assert.Equal((LeaseState.Held, 1), (lease.State, lease.Seats));
                leaseId = lease.Id;
                Assert.Contains(leaseId, await LeasesAsync(user, elm, data), StringComparison.Ordinal);
                Assert.Null(await client.ReleaseLeaseAsync(lease, TestContext.Current.CancellationToken));
                Assert.DoesNotContain(leaseId, await LeasesAsync(user, elm, data), StringComparison.Ordinal);
            }

            await SystemService.SudoOkAsync("systemctl", "stop", "--no-block", unit);
            await RealClock.WaitUntilAsync(
                "the stop under systemd",
                async () => await ShowAsync(unit, "ActiveState") == "inactive",
                SystemService.StateTimeout
            );
            Assert.Equal("0", await ShowAsync(unit, "ExecMainStatus"));
            Assert.Equal("success", await ShowAsync(unit, "Result"));
            string usage = await SystemService.NewestAsync(data, "usage");
            Assert.Contains($"\"type\":\"lease.acquired\"", usage, StringComparison.Ordinal);
            Assert.Contains($"\"lease\":\"{leaseId}\"", usage, StringComparison.Ordinal);
            Assert.Contains($"\"type\":\"lease.released\"", usage, StringComparison.Ordinal);
            await SystemService.AssertRecordsClosedAsync(data);
            passed = true;
        }
        finally
        {
            await CleanUpAsync(passed, user, unit, unitFile, bin, etc, data);
        }
    }

    // The unit of the sample with the names of this run, installed and loaded.
    private static async Task InstallUnitAsync(Vendor vendor, string user, string bin, string etc, string data, string unit, string unitFile)
    {
        string unitText = SystemService.Sample(
            "elm-server.service",
            ("User=elm-server", "User=" + user),
            ("Group=elm-server", "Group=" + user),
            ("/opt/elm-server/", bin + "/"),
            ("/etc/elm-server/", etc + "/"),
            ("ReadWritePaths=/var/lib/elm-server", "ReadWritePaths=" + data)
        );
        await SystemService.InstallTextAsync(vendor, unitText, unitFile, "0644");
        await SystemService.SudoOkAsync("systemctl", "daemon-reload");
    }

    // The values systemd uses for the hardening directives of the sample (design §13.1, V1d30a), read with systemctl show.
    private static async Task AssertHardenedAsync(string unit, string data)
    {
        (string Property, string Value)[] expected =
        [
            ("NoNewPrivileges", "yes"),
            ("ProtectSystem", "strict"),
            ("ReadWritePaths", data),
            ("ProtectHome", "yes"),
            ("PrivateTmp", "yes"),
            ("PrivateDevices", "yes"),
            ("CapabilityBoundingSet", string.Empty),
            ("RestrictAddressFamilies", "AF_UNIX AF_INET AF_INET6"),
            ("ProtectKernelTunables", "yes"),
            ("ProtectKernelModules", "yes"),
            ("ProtectKernelLogs", "yes"),
            ("ProtectControlGroups", "yes"),
            ("ProtectClock", "yes"),
            ("ProtectHostname", "yes"),
            ("RestrictNamespaces", "yes"),
            ("RestrictRealtime", "yes"),
            ("RestrictSUIDSGID", "yes"),
            ("LockPersonality", "yes"),
            ("MemoryDenyWriteExecute", "yes"),
            ("SystemCallArchitectures", "native"),
            ("SystemCallErrorNumber", "EPERM"),
        ];
        List<string> read = [];
        foreach ((string property, string value) in expected)
        {
            read.Add($"{property}={await ShowAsync(unit, property)}");
        }

        RunReport.Note("elm-server under systemd: " + string.Join(", ", read) + ".");
        Assert.Equal(expected.Select(e => $"{e.Property}={e.Value}"), read);

        // An allow list prints as the system calls it names: the sample's @system-service holds read and write, and leaves
        // out reboot, which @reboot holds.
        string[] calls = (await ShowAsync(unit, "SystemCallFilter")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("read", calls);
        Assert.Contains("write", calls);
        Assert.DoesNotContain("reboot", calls);
    }

    // Every step runs whatever the one before it did (testing.mdc).
    private static async Task CleanUpAsync(bool passed, string user, string unit, string unitFile, string bin, string etc, string data)
    {
        List<string> problems = [];
        await SystemService.CleanUpAsync(problems, "systemctl disable", () => SystemService.SudoTryAsync("systemctl", "disable", "--now", unit));
        await SystemService.CleanUpAsync(problems, "the unit file", () => SystemService.SudoCleanUpAsync("rm", "-f", unitFile));
        await SystemService.CleanUpAsync(problems, "daemon-reload", () => SystemService.SudoCleanUpAsync("systemctl", "daemon-reload"));
        await SystemService.CleanUpAsync(problems, "reset-failed", () => SystemService.SudoTryAsync("systemctl", "reset-failed", unit));
        await SystemService.CleanUpAsync(problems, "the user's processes", () => SystemService.KillProcessesOfAsync(user, bin));
        await SystemService.CleanUpAsync(problems, "the folders", () => SystemService.SudoCleanUpAsync("rm", "-rf", bin, etc, data));

        // --force: a container's processes may run as the same UID (999 on the runner), and userdel refuses a user with
        // processes otherwise.
        await SystemService.CleanUpAsync(problems, "userdel", () => SystemService.SudoTryAsync("userdel", "--force", user));
        await SystemService.CleanUpAsync(problems, "the user", () => SystemService.AssertNoAccountAsync(user));
        Assert.True(!passed || problems.Count == 0, "Cleanup: " + string.Join("; ", problems));
    }

    // A server just started refuses acquisitions for a lease's length (design §13.4), and says how long with Retry-After.
    private static async Task<Lease> AcquireAfterRecoveryAsync(LicenseClient client)
    {
        for (int i = 0; i < 10; i++)
        {
            LeaseResult result = await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken);
            if (result is LeaseResult.Acquired acquired)
            {
                return acquired.Lease;
            }

            var failed = (LeaseResult.Failed)result;
            Assert.True(failed.Error.Code == ErrorCode.ServerRecovering, failed.Error.Message);
            await RealClock.DelayAsync("the server's reclaim time", failed.RetryAfter ?? TimeSpan.FromSeconds(1));
        }

        Assert.Fail("The server did not give a lease after its reclaim time.");
        return null!;
    }

    private static async Task<string> LeasesAsync(string user, string elm, string data)
    {
        (int code, string output, string error) = await SystemService.SudoAsync(["-u", user, elm, "server", "leases", "--data", data]);
        Assert.True(code == 0, $"elm server leases exited with {code}: {error}");
        return output;
    }

    private static async Task<string> ShowAsync(string unit, string property) =>
        (await SystemService.SudoOkAsync("systemctl", "show", unit, "-p", property, "--value")).Trim();

    // Waits until elm server status answers as the service's user on its socket, or the unit has failed.
    private static async Task WaitForStatusAsync(string user, string elm, string data, string unit)
    {
        bool answered = false;
        string error = string.Empty;
        await RealClock.WaitUntilAsync(
            "elm server status under systemd",
            async () =>
            {
                (int code, _, error) = await SystemService.SudoAsync(["-u", user, elm, "server", "status", "--data", data]);
                answered = code == 0;
                return answered || await ShowAsync(unit, "ActiveState") == "failed";
            },
            SystemService.StateTimeout
        );
        Assert.True(answered, $"The unit failed before elm server status answered: {error}");
    }

    [GeneratedRegex("installation key ([A-Za-z0-9_-]{43})")]
    private static partial Regex InstallationKey();

    [GeneratedRegex(@"server\.started: listening on port (\d+)")]
    private static partial Regex ListeningPort();
}
