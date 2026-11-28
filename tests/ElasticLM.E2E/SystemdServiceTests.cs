using System.Diagnostics;
using System.Globalization;
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
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The page's steps for Linux: elm-server starts under systemd as its own user, comes back after a kill, stops on
    /// systemctl stop with exit code 0 and its records closed, and writes its operational log to the journal.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux with systemd as the service manager, and sudo without a password, as the runners of GitHub Actions have. On
    ///   GitHub Actions the test fails without them; elsewhere it is skipped and says so.
    /// - The vendor's elm-service with the product acme-cad, a server policy, and a license of 3 seats.
    /// - The names of the page with the digits of this run: the user elme2e{8 hex digits}, the unit elm-e2e-{digits}, and the
    ///   folders /opt/elm-e2e-{digits}, /etc/elm-e2e-{digits}, and /var/lib/elm-e2e-{digits} in place of /opt/elm-server,
    ///   /etc/elm-server, and /var/lib/elm-server.
    ///
    /// Steps:
    /// - useradd --system --shell /usr/sbin/nologin; install the published elm-server and elm (0755) and the settings file of
    ///   samples/ElmServerService/linux (listen on 127.0.0.1 port 0) and the root key (0644) as root; install -d -o the user
    ///   -m 0700 the data folder.
    /// - Run elm-server once as the user from the terminal (sudo -u); elm server activate as the user, with the key in
    ///   ELM_LICENSE_KEY since the test has no terminal to type it in.
    /// - Install the unit of the sample (0644), daemon-reload, enable --now; wait until elm server status answers as the user.
    /// - Kill the main process with SIGKILL and time until elm server status answers again from a new main process.
    /// - systemctl stop, timed; read the unit's exit status and result, the newest usage log, and the unit's journal.
    /// - Clean up: disable and remove the unit, remove the user and the folders.
    ///
    /// Expected:
    /// - The first start exits with 1 and names the missing server license and an installation key; activate exits with 0.
    /// - The unit's TimeoutStopUSec is longer than 30 seconds (the host's ShutdownTimeout, design §13.1).
    /// - After the kill a new main process answers, at least RestartSec (5 seconds) after the kill.
    /// - After the stop the unit is inactive with ExecMainStatus 0 and Result success; the newest usage log ends with
    ///   server.stopped and its signature; the journal holds the server.started and server.stopped lines of the operational log.
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
            await SystemService.SudoOkAsync("useradd", "--system", "--shell", "/usr/sbin/nologin", user);
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

            // Step 2: one start from the terminal makes the installation key and stops for want of the license.
            (int first, string firstOut, string firstError) = await SystemService.SudoAsync([
                "-u",
                user,
                bin + "/elm-server",
                "--config",
                etc + "/elm-server.json",
            ]);
            Assert.True(first == 1, $"The first start exited with {first}: {firstError}{firstOut}");
            Assert.Contains($"error: The server license '{license}' does not exist.", firstError, StringComparison.Ordinal);
            Assert.Contains("installation key", firstError, StringComparison.Ordinal);

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
            Assert.True(ParseSpan(timeout) > TimeSpan.FromSeconds(30), $"TimeoutStopUSec is {timeout}, not longer than 30 seconds.");

            await WaitForStatusAsync(user, elm, data, unit);
            string firstPid = await ShowAsync(unit, "MainPID");

            // The restart: a kill is a failure that Restart=on-failure starts again after RestartSec.
            long killed = Stopwatch.GetTimestamp();
            await SystemService.SudoOkAsync("systemctl", "kill", "--signal=SIGKILL", unit);
            await RealClock.WaitUntilAsync(
                "the restart under systemd",
                async () => await ShowAsync(unit, "MainPID") is string pid && pid != "0" && pid != firstPid,
                StateTimeout
            );
            await WaitForStatusAsync(user, elm, data, unit);
            TimeSpan restart = Stopwatch.GetElapsedTime(killed);
            RunReport.Note($"elm-server under systemd: SIGKILL to an answer from the restarted server took {restart.TotalMilliseconds:F0} ms.");
            Assert.True(restart >= TimeSpan.FromSeconds(4.9), $"The server answered {restart.TotalSeconds:F1} s after the kill, before RestartSec.");

            // The stop.
            long stopping = Stopwatch.GetTimestamp();
            await SystemService.SudoOkAsync("systemctl", "stop", unit);
            TimeSpan stop = Stopwatch.GetElapsedTime(stopping);
            string status = await ShowAsync(unit, "ExecMainStatus");
            string result = await ShowAsync(unit, "Result");
            string state = await ShowAsync(unit, "ActiveState");
            RunReport.Note(
                $"elm-server under systemd: systemctl stop took {stop.TotalMilliseconds:F0} ms; ExecMainStatus={status}, Result={result}, ActiveState={state}."
            );
            Assert.Equal("0", status);
            Assert.Equal("success", result);
            Assert.Equal("inactive", state);
            await SystemService.AssertUsageLogClosedAsync(data);

            // The operational log reaches the journal.
            string journal = string.Empty;
            await RealClock.WaitUntilAsync(
                "the journal of the unit",
                async () =>
                {
                    journal = await SystemService.SudoOkAsync("journalctl", "-u", unit, "--no-pager", "-o", "cat");
                    return journal.Contains("server.stopped: ", StringComparison.Ordinal);
                },
                StateTimeout
            );
            Assert.Contains("server.started: listening on port", journal, StringComparison.Ordinal);
            passed = true;
        }
        finally
        {
            List<string> problems = [];
            await SystemService.CleanUpAsync(problems, "systemctl disable", () => IgnoreAsync("systemctl", "disable", "--now", unit));
            await SystemService.CleanUpAsync(problems, "the unit file", () => SystemService.SudoOkAsync("rm", "-f", unitFile));
            await SystemService.CleanUpAsync(problems, "daemon-reload", () => SystemService.SudoOkAsync("systemctl", "daemon-reload"));
            await SystemService.CleanUpAsync(problems, "reset-failed", () => IgnoreAsync("systemctl", "reset-failed", unit));
            await SystemService.CleanUpAsync(problems, "the folders", () => SystemService.SudoOkAsync("rm", "-rf", bin, etc, data));
            await SystemService.CleanUpAsync(problems, "userdel", () => IgnoreAsync("userdel", user));
            Assert.True(!passed || problems.Count == 0, "Cleanup: " + string.Join("; ", problems));
        }
    }

    // A cleanup step whose target may not exist when the test failed before making it.
    private static async Task IgnoreAsync(params string[] arguments) => await SystemService.SudoAsync(arguments);

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
            StateTimeout
        );
        Assert.True(answered, $"The unit failed before elm server status answered: {error}");
    }

    // A time span as systemctl show prints it, such as "1min 30s", "90s", or "500ms".
    private static TimeSpan ParseSpan(string text)
    {
        if (text == "infinity")
        {
            return TimeSpan.MaxValue;
        }

        TimeSpan total = TimeSpan.Zero;
        MatchCollection parts = SpanPart().Matches(text);
        Assert.True(parts.Count > 0, $"'{text}' is not a time span of systemd.");
        foreach (Match part in parts)
        {
            double value = double.Parse(part.Groups["value"].Value, CultureInfo.InvariantCulture);
            total += part.Groups["unit"].Value switch
            {
                "h" => TimeSpan.FromHours(value),
                "min" => TimeSpan.FromMinutes(value),
                "s" => TimeSpan.FromSeconds(value),
                "ms" => TimeSpan.FromMilliseconds(value),
                "us" => TimeSpan.FromMicroseconds(value),
                _ => throw new InvalidOperationException($"'{text}' has a unit this test does not read."),
            };
        }

        return total;
    }

    [GeneratedRegex(@"(?<value>[0-9]+(?:\.[0-9]+)?)(?<unit>h|min|ms|us|s)\b")]
    private static partial Regex SpanPart();
}
