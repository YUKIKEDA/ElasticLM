using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

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
    /// - Install the unit of the sample (0644), daemon-reload, enable --now; wait until elm server status answers as the user.
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
            string unitText = SystemService.Sample(
                "elm-server.service",
                ("User=elm-server", "User=" + user),
                ("Group=elm-server", "Group=" + user),
                ("/opt/elm-server/", bin + "/"),
                ("/etc/elm-server/", etc + "/")
            );
            await SystemService.InstallTextAsync(vendor, unitText, unitFile, "0644");
            await SystemService.SudoOkAsync("systemctl", "daemon-reload");
            await SystemService.SudoOkAsync("systemctl", "enable", "--now", unit);

            string timeout = await ShowAsync(unit, "TimeoutStopUSec");
            string limits = $"{await ShowAsync(unit, "StartLimitBurst")} starts in {await ShowAsync(unit, "StartLimitIntervalUSec")}";
            RunReport.Note($"elm-server under systemd: TimeoutStopUSec={timeout}, start limit {limits}.");

            // The unit writes TimeoutStopSec=60 rather than leaning on DefaultTimeoutStopSec, which the machine's settings can
            // change (design §13.1); systemctl show prints it as 1min.
            Assert.Equal("1min", timeout);

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
}
