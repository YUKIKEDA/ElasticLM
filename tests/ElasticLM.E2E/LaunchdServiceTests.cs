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
    /// <summary>
    /// The page's steps for macOS: elm-server starts under launchd as its own account, comes back after a kill, stops on
    /// SIGTERM with exit code 0 and its records closed, and writes its operational log to the plist's log file.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - macOS and sudo without a password, as the runners of GitHub Actions have. On GitHub Actions the test fails without
    ///   it; elsewhere it is skipped and says so, and it runs only with ELM_E2E_SYSTEM_SERVICES=1, since it creates an account,
    ///   a group, and a launch daemon.
    /// - The vendor's elm-service with the product acme-cad, a server policy, and a license of 3 seats.
    /// - The names of the page with the digits of this run: the role account _elme2e{8 hex digits} with a free UID from 450
    ///   to 499, the label elm-e2e-{digits}, and the folders /usr/local/elm-e2e-{digits}, /Library/Application
    ///   Support/elm-e2e-{digits}, and /Library/Logs/elm-e2e-{digits} in place of those of elm-server.
    ///
    /// Steps:
    /// - dseditgroup -o create -i and sysadminctl -addUser -UID -GID -roleAccount; install the published elm-server and elm (0755), the settings file of
    ///   samples/ElmServerService/macos (listen on 127.0.0.1 port 0), and the root key (0644) as root; install -d -o the
    ///   account -m 0700 the data folder; install -d -m 0755 the folder of the log, and install -o the account -m 0640 the log file.
    /// - Run elm-server once as the account from the terminal (sudo -u); elm server activate as the account, with the key in
    ///   ELM_LICENSE_KEY since the test has no terminal to type it in.
    /// - Install the plist of the sample (0644) in /Library/LaunchDaemons and launchctl bootstrap system; wait until elm
    ///   server status answers as the account.
    /// - Kill the process with SIGKILL and time until elm server status answers again from a new process.
    /// - launchctl kill SIGTERM, timed until the job has no process; read launchctl print, the newest usage log, and the log
    ///   file.
    /// - launchctl kickstart, wait until elm server status answers, and SIGTERM again.
    /// - Clean up, also when the run is canceled: launchctl bootout, end every process of the account, remove the plist and
    ///   the folders, delete the account (keeping its home, the shared /var/empty) and the group, and check that both are
    ///   gone and /var/empty is still there.
    ///
    /// Expected:
    /// - The account exists with the UID and the group given; the first start exits with 1, and its line starts with the
    ///   missing server license and names an installation key; activate exits with 0.
    /// - launchctl print shows the exit timeout of 60 seconds.
    /// - After the kill a new process answers.
    /// - After SIGTERM the job runs no process and its last exit code is 0; the newest usage log ends with server.stopped and
    ///   its signature, and the newest borrow record holds server.stopped; the log file holds the server.started and server.stopped lines of the operational log.
    /// - launchctl kickstart brings the server back.
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
            // The page's step 1: a group of its own, and the role account in it, with an ID from 450 to 499 that no account and
            // no group has.
            string uid = await FreeIdAsync();
            await SystemService.SudoOkAsync("dseditgroup", "-o", "create", "-r", "elm-server e2e", "-i", uid, account);
            await SystemService.SudoOkAsync(
                "sysadminctl",
                "-addUser",
                account,
                "-fullName",
                "elm-server e2e",
                "-UID",
                uid,
                "-GID",
                uid,
                "-roleAccount"
            );
            (int known, string idOut, string idError) = await Binaries.RunAsync("id", [account], "/");
            Assert.True(
                known == 0 && idOut.Contains($"uid={uid}({account}) gid={uid}({account})", StringComparison.Ordinal),
                $"sysadminctl did not make {account} with UID and GID {uid}: {idError}{idOut}"
            );
            RunReport.Note($"elm-server under launchd: id {account}: {idOut.Trim()}");
            await SystemService.SudoOkAsync("install", "-d", "-m", "0755", bin);
            await SystemService.SudoOkAsync("install", "-m", "0755", Binaries.Server, Binaries.Elm, bin);
            await SystemService.SudoOkAsync("install", "-m", "0644", vendor.RootPublicKey, bin + "/root.pub.pem");
            string settings = SystemService.Sample(
                "macos/elm-server.json",
                ("https://0.0.0.0:7070", "https://127.0.0.1:0"),
                ("/Library/Application Support/elm-server", data),
                ("/usr/local/elm-server", bin),
                ("https://licensing.example.com", service.Url.ToString().TrimEnd('/'))
            );
            await SystemService.InstallTextAsync(vendor, settings, bin + "/elm-server.json", "0644");
            await SystemService.SudoOkAsync("install", "-d", "-o", account, "-g", account, "-m", "0700", data);
            await SystemService.SudoOkAsync("install", "-d", "-m", "0755", logs);
            await SystemService.SudoOkAsync("install", "-m", "0640", "-o", account, "-g", account, "/dev/null", logFile);

            // Step 2: one start from the terminal makes the installation key and stops for want of the license.
            (int first, string firstOut, string firstError) = await SystemService.SudoAsync([
                "-u",
                account,
                bin + "/elm-server",
                "--config",
                bin + "/elm-server.json",
            ]);
            Assert.True(first == 1, $"The first start exited with {first}: {firstError}{firstOut}");
            string firstLine = firstError.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1];
            Assert.StartsWith($"error: The server license '{license}' does not exist.", firstLine, StringComparison.Ordinal);
            Assert.Matches(InstallationKey(), firstLine);

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
                ("<string>_elm-server</string>", $"<string>{account}</string>"),
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
                SystemService.StateTimeout
            );
            await WaitForStatusAsync(account, elm, data);
            TimeSpan restart = Stopwatch.GetElapsedTime(killed);
            RunReport.Note($"elm-server under launchd: SIGKILL to an answer from the restarted server took {restart.TotalMilliseconds:F0} ms.");

            // The stop: SIGTERM, as the page says, and the job keeps no process since the exit is a success.
            long stopping = Stopwatch.GetTimestamp();
            await SystemService.SudoOkAsync("launchctl", "kill", "SIGTERM", "system/" + label);
            string after = string.Empty;
            await RealClock.WaitUntilAsync(
                "the stop under launchd",
                async () => Pid(after = await PrintAsync(label)).Length == 0,
                SystemService.StateTimeout
            );
            TimeSpan stop = Stopwatch.GetElapsedTime(stopping);
            string exit = LastExit().Match(after).Groups[1].Value;
            RunReport.Note($"elm-server under launchd: SIGTERM to no process took {stop.TotalMilliseconds:F0} ms; last exit code = {exit}.");
            Assert.Equal("0", exit);
            await SystemService.AssertRecordsClosedAsync(data);

            string ownerAndMode = await SystemService.SudoOkAsync("stat", "-f", "%Su:%Sg %Lp", logFile);
            RunReport.Note($"elm-server under launchd: the log file is {ownerAndMode.Trim()}.");
            string log = await SystemService.SudoOkAsync("cat", logFile);
            Assert.Contains("server.started: listening on port", log, StringComparison.Ordinal);
            Assert.Contains("server.stopped: ", log, StringComparison.Ordinal);

            // The page's "Start again", and the stop once more.
            await SystemService.SudoOkAsync("launchctl", "kickstart", "system/" + label);
            await WaitForStatusAsync(account, elm, data);
            await SystemService.SudoOkAsync("launchctl", "kill", "SIGTERM", "system/" + label);
            await RealClock.WaitUntilAsync(
                "the second stop under launchd",
                async () => Pid(await PrintAsync(label)).Length == 0,
                SystemService.StateTimeout
            );
            passed = true;
        }
        finally
        {
            List<string> problems = [];
            await SystemService.CleanUpAsync(
                problems,
                "launchctl bootout",
                () => SystemService.SudoTryAsync("launchctl", "bootout", "system/" + label)
            );
            await SystemService.CleanUpAsync(problems, "the account's processes", () => SystemService.KillProcessesOfAsync(account));
            await SystemService.CleanUpAsync(problems, "the folders", () => SystemService.SudoCleanUpAsync("rm", "-rf", plist, bin, data, logs));

            // -keepHome: the role account's home is the shared /var/empty.
            await SystemService.CleanUpAsync(
                problems,
                "the account",
                () => SystemService.SudoTryAsync("sysadminctl", "-deleteUser", account, "-keepHome")
            );
            await SystemService.CleanUpAsync(problems, "the group", () => SystemService.SudoTryAsync("dseditgroup", "-o", "delete", account));
            await SystemService.CleanUpAsync(problems, "the account is gone", () => SystemService.AssertNoAccountAsync(account));
            await SystemService.CleanUpAsync(
                problems,
                "the group is gone",
                async () =>
                    Assert.True(
                        (await Binaries.RunAsync("dscl", [".", "-read", "/Groups/" + account], "/", cleanup: true)).Code != 0,
                        $"The group {account} is still there."
                    )
            );
            await SystemService.CleanUpAsync(
                problems,
                "/var/empty",
                () =>
                {
                    Assert.True(Directory.Exists("/var/empty"), "/var/empty is gone after the account was deleted.");
                    return Task.CompletedTask;
                }
            );
            Assert.True(!passed || problems.Count == 0, "Cleanup: " + string.Join("; ", problems));
        }
    }

    private static async Task<string> PrintAsync(string label) => (await SystemService.SudoAsync(["launchctl", "print", "system/" + label])).Out;

    // The process of the job, or "" when it runs none.
    private static string Pid(string printed) => PidLine().Match(printed).Groups[1].Value;

    // The lowest ID from 450 to 499, the range that sysadminctl takes for a role account, that no account and no group has.
    private static async Task<string> FreeIdAsync()
    {
        HashSet<string> taken = [];
        foreach ((string records, string attribute) in (List<(string, string)>)[("/Users", "UniqueID"), ("/Groups", "PrimaryGroupID")])
        {
            (int code, string output, string error) = await Binaries.RunAsync("dscl", [".", "-list", records, attribute], "/");
            Assert.True(code == 0, $"dscl exited with {code}: {error}");
            taken.UnionWith(
                output.Split('\n').Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty)
            );
        }

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
            SystemService.StateTimeout
        );
        Assert.True(answered, error);
    }

    [GeneratedRegex("installation key ([A-Za-z0-9_-]{43})")]
    private static partial Regex InstallationKey();

    [GeneratedRegex(@"^\s*pid = (\d+)", RegexOptions.Multiline)]
    private static partial Regex PidLine();

    [GeneratedRegex(@"^\s*exit timeout = (\d+)", RegexOptions.Multiline)]
    private static partial Regex ExitTimeout();

    [GeneratedRegex(@"^\s*last exit code = (\d+)", RegexOptions.Multiline)]
    private static partial Regex LastExit();
}
