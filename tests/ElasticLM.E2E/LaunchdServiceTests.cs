using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ElasticLM.E2E;

/// <summary>
/// The published elm-server as a launchd daemon, set up as the How-to guide "Run elm-server as a system service" says for
/// macOS (design §13.1, "systemd and launchd", V1d30): a role account of its own, the files root writes, one start from the
/// terminal, the activation as that account, and the plist of <c>samples/ElmServerService</c>.
/// </summary>
public sealed partial class LaunchdServiceTests
{
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The page's steps for macOS: elm-server starts under launchd as its own account, comes back after a kill, stops on
    /// SIGTERM with exit code 0 and its records closed, and writes its operational log to the plist's log file.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - macOS and sudo without a password, as the runners of GitHub Actions have. On GitHub Actions the test fails without
    ///   it; elsewhere it is skipped and says so.
    /// - The vendor's elm-service with the product acme-cad, a server policy, and a license of 3 seats.
    /// - The names of the page with the digits of this run: the role account _elme2e{8 hex digits} with a free UID from 450
    ///   to 499, the label elm-e2e-{digits}, and the folders /usr/local/elm-e2e-{digits}, /Library/Application
    ///   Support/elm-e2e-{digits}, and /Library/Logs/elm-e2e-{digits} in place of those of elm-server.
    ///
    /// Steps:
    /// - sysadminctl -addUser -UID -roleAccount; install the published elm-server and elm (0755), the settings file of
    ///   samples/ElmServerService/macos (listen on 127.0.0.1 port 0), and the root key (0644) as root; install -d -o the
    ///   account -m 0700 the data folder; install -d -m 0755 the folder of the log.
    /// - Run elm-server once as the account from the terminal (sudo -u); elm server activate as the account, with the key in
    ///   ELM_LICENSE_KEY since the test has no terminal to type it in.
    /// - Install the plist of the sample (0644) in /Library/LaunchDaemons and launchctl bootstrap system; wait until elm
    ///   server status answers as the account.
    /// - Kill the process with SIGKILL and time until elm server status answers again from a new process.
    /// - launchctl kill SIGTERM, timed until the job has no process; read launchctl print, the newest usage log, and the log
    ///   file.
    /// - Clean up: launchctl bootout, remove the plist, the folders, and the account.
    ///
    /// Expected:
    /// - The account exists with the UID given; the first start exits with 1 and names the missing server license and an
    ///   installation key; activate exits with 0.
    /// - launchctl print shows the exit timeout of 60 seconds.
    /// - After the kill a new process answers.
    /// - After SIGTERM the job runs no process and its last exit code is 0; the newest usage log ends with server.stopped and
    ///   its signature; the log file holds the server.started and server.stopped lines of the operational log.
    /// - The values read and the times go to the job's log (RunReport.Note).
    /// </remarks>
    [Fact]
    public async Task Launchd_ThePageSteps_RunRestartAndStopTheServer()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "launchd runs on macOS only.");
        await SystemService.RequireSudoAsync();

        string id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        string account = "_elme2e" + id;
        string label = "elm-e2e-" + id;
        string bin = "/usr/local/elm-e2e-" + id;
        string data = "/Library/Application Support/elm-e2e-" + id;
        string logs = "/Library/Logs/elm-e2e-" + id;
        string logFile = logs + "/elm-server.log";
        string license = data + "/server.elmlic";
        string plist = $"/Library/LaunchDaemons/{label}.plist";
        string elm = bin + "/elm";

        await using Vendor vendor = await Vendor.CreateAsync("license", "lease", "log");
        (ServiceProcess started, string key) = await SystemService.StartVendorServiceAsync(vendor);
        await using ServiceProcess service = started;

        bool passed = false;
        try
        {
            // The page's step 1: the role account, with a UID from 450 to 499 that no account has.
            string uid = await FreeUidAsync();
            await SystemService.SudoOkAsync("sysadminctl", "-addUser", account, "-fullName", "elm-server e2e", "-UID", uid, "-roleAccount");
            (int known, string idOut, string idError) = await Binaries.RunAsync("id", ["-u", account], "/");
            Assert.True(known == 0 && idOut.Trim() == uid, $"sysadminctl did not make {account} with UID {uid}: {idError}{idOut}");
            string group = (await Binaries.RunAsync("id", ["-gn", account], "/")).Out.Trim();
            RunReport.Note($"elm-server under launchd: sysadminctl -roleAccount made UID {uid}, primary group {group}.");

            await SystemService.SudoOkAsync("install", "-d", "-m", "0755", bin);
            await SystemService.SudoOkAsync("install", "-m", "0755", Binaries.Server, Binaries.Elm, bin);
            await SystemService.SudoOkAsync("install", "-m", "0644", vendor.RootPublicKey, bin + "/root.pub.pem");
            string settings = SystemService.Sample(
                "macos/elm-server.json",
                ("https://0.0.0.0:7070", "https://127.0.0.1:0"),
                ("/Library/Application Support/elm-server", data),
                ("/usr/local/elm-server", bin)
            );
            await SystemService.InstallTextAsync(vendor, settings, bin + "/elm-server.json", "0644");
            await SystemService.SudoOkAsync("install", "-d", "-o", account, "-g", group, "-m", "0700", data);
            await SystemService.SudoOkAsync("install", "-d", "-m", "0755", logs);

            // Step 2: one start from the terminal makes the installation key and stops for want of the license.
            (int first, string firstOut, string firstError) = await SystemService.SudoAsync([
                "-u",
                account,
                bin + "/elm-server",
                "--config",
                bin + "/elm-server.json",
            ]);
            Assert.True(first == 1, $"The first start exited with {first}: {firstError}{firstOut}");
            Assert.Contains($"error: The server license '{license}' does not exist.", firstError, StringComparison.Ordinal);
            Assert.Contains("installation key", firstError, StringComparison.Ordinal);

            // Step 3: the activation as the account; sudo passes ELM_LICENSE_KEY on only because --preserve-env names it.
            (int activated, _, string activateError) = await SystemService.SudoAsync(
                [
                    "--preserve-env=ELM_LICENSE_KEY",
                    "-u",
                    account,
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
                    bin + "/root.pub.pem",
                ],
                new Dictionary<string, string?> { ["ELM_LICENSE_KEY"] = key }
            );
            Assert.True(activated == 0, $"elm server activate exited with {activated}: {activateError}");

            // Step 4: the plist of the sample.
            string plistText = SystemService.Sample(
                "elm-server.plist",
                ("<string>elm-server</string>\n  <key>ProgramArguments</key>", $"<string>{label}</string>\n  <key>ProgramArguments</key>"),
                ("/usr/local/elm-server/", bin + "/"),
                (
                    "<string>_elm-server</string>\n  <key>GroupName</key>\n  <string>_elm-server</string>",
                    $"<string>{account}</string>\n  <key>GroupName</key>\n  <string>{group}</string>"
                ),
                ("/Library/Logs/elm-server/", logs + "/")
            );
            await SystemService.InstallTextAsync(vendor, plistText, plist, "0644");
            await SystemService.SudoOkAsync("launchctl", "bootstrap", "system", plist);

            string printed = await PrintAsync(label);
            string timeout = ExitTimeout().Match(printed).Groups[1].Value;
            RunReport.Note($"elm-server under launchd: launchctl print shows exit timeout = {timeout}.");
            Assert.Equal("60", timeout);

            await WaitForStatusAsync(account, elm, data);
            string firstPid = Pid(await PrintAsync(label));

            // The restart: KeepAlive with SuccessfulExit false starts the job again after an end that is not a success.
            long killed = Stopwatch.GetTimestamp();
            await SystemService.SudoOkAsync("kill", "-9", firstPid);
            await RealClock.WaitUntilAsync(
                "the restart under launchd",
                async () => Pid(await PrintAsync(label)) is { Length: > 0 } pid && pid != firstPid,
                StateTimeout
            );
            await WaitForStatusAsync(account, elm, data);
            TimeSpan restart = Stopwatch.GetElapsedTime(killed);
            RunReport.Note($"elm-server under launchd: SIGKILL to an answer from the restarted server took {restart.TotalMilliseconds:F0} ms.");

            // The stop: SIGTERM, as the page says, and the job keeps no process since the exit is a success.
            long stopping = Stopwatch.GetTimestamp();
            await SystemService.SudoOkAsync("launchctl", "kill", "SIGTERM", "system/" + label);
            string after = string.Empty;
            await RealClock.WaitUntilAsync("the stop under launchd", async () => Pid(after = await PrintAsync(label)).Length == 0, StateTimeout);
            TimeSpan stop = Stopwatch.GetElapsedTime(stopping);
            string exit = LastExit().Match(after).Groups[1].Value;
            RunReport.Note($"elm-server under launchd: SIGTERM to no process took {stop.TotalMilliseconds:F0} ms; last exit code = {exit}.");
            Assert.Equal("0", exit);
            await SystemService.AssertUsageLogClosedAsync(data);

            string ownerAndMode = await SystemService.SudoOkAsync("stat", "-f", "%Su:%Sg %Lp", logFile);
            RunReport.Note($"elm-server under launchd: the log file is {ownerAndMode.Trim()}.");
            string log = await SystemService.SudoOkAsync("cat", logFile);
            Assert.Contains("server.started: listening on port", log, StringComparison.Ordinal);
            Assert.Contains("server.stopped: ", log, StringComparison.Ordinal);
            passed = true;
        }
        finally
        {
            List<string> problems = [];
            await SystemService.CleanUpAsync(problems, "launchctl bootout", () => IgnoreAsync("launchctl", "bootout", "system/" + label));
            await SystemService.CleanUpAsync(problems, "the folders", () => SystemService.SudoOkAsync("rm", "-rf", plist, bin, data, logs));
            await SystemService.CleanUpAsync(problems, "the account", () => IgnoreAsync("sysadminctl", "-deleteUser", account));
            Assert.True(!passed || problems.Count == 0, "Cleanup: " + string.Join("; ", problems));
        }
    }

    // A cleanup step whose target may not exist when the test failed before making it.
    private static async Task IgnoreAsync(params string[] arguments) => await SystemService.SudoAsync(arguments);

    private static async Task<string> PrintAsync(string label) => (await SystemService.SudoAsync(["launchctl", "print", "system/" + label])).Out;

    // The process of the job, or "" when it runs none.
    private static string Pid(string printed) => PidLine().Match(printed).Groups[1].Value;

    // The lowest UID from 450 to 499, the range that sysadminctl takes for a role account, that no account has.
    private static async Task<string> FreeUidAsync()
    {
        (int code, string output, string error) = await Binaries.RunAsync("dscl", [".", "-list", "/Users", "UniqueID"], "/");
        Assert.True(code == 0, $"dscl exited with {code}: {error}");
        HashSet<string> taken =
        [
            .. output.Split('\n').Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty),
        ];
        return Enumerable.Range(450, 50).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)).First(n => !taken.Contains(n));
    }

    // Waits until elm server status answers as the account on its socket.
    private static async Task WaitForStatusAsync(string account, string elm, string data)
    {
        string error = string.Empty;
        bool answered = false;
        await RealClock.WaitUntilAsync(
            "elm server status under launchd",
            async () =>
            {
                (int code, _, error) = await SystemService.SudoAsync(["-u", account, elm, "server", "status", "--data", data]);
                answered = code == 0;
                return answered;
            },
            StateTimeout
        );
        Assert.True(answered, error);
    }

    [GeneratedRegex(@"^\s*pid = (\d+)", RegexOptions.Multiline)]
    private static partial Regex PidLine();

    [GeneratedRegex(@"^\s*exit timeout = (\d+)", RegexOptions.Multiline)]
    private static partial Regex ExitTimeout();

    [GeneratedRegex(@"^\s*last exit code = (\d+)", RegexOptions.Multiline)]
    private static partial Regex LastExit();
}
