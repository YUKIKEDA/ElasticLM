using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ElasticLM.E2E;

/// <summary>
/// The published elm-server as a Windows service under the service control manager (design §13.1, §13.4, V1d29a): a start
/// error goes to the event log, and a service stop closes the records before the service reports STOPPED.
/// </summary>
public sealed partial class WindowsServiceTests
{
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Registered as a service of LocalSystem, elm-server first stops for want of a server license and leaves that line in
    /// the event log as event 50; with the license issued for the key it made, it starts, and a service stop leaves the usage
    /// log and the borrow record ending with server.stopped and the service STOPPED with exit code 0.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows, in a process that may register a service: an elevated administrator, as the runner's user of GitHub
    ///   Actions is. On GitHub Actions the test fails when it is not one; elsewhere it is skipped and says so.
    /// - The vendor's folder, and a settings file in it with absolute paths: the product e2e-svc-{8 hex digits}, listen on
    ///   127.0.0.1 port 0, the license file in the vendor's folder, the vendor's root key, and the data folder
    ///   %ProgramData%\elm-svc-{the same digits}\data, which the service makes as LocalSystem.
    /// - A service elm-e2e-{the same digits} of the published elm-server with --config and that file, as LocalSystem.
    ///
    /// Steps:
    /// - Start the service and wait for STOPPED; read the newest event 50 of the source elm-server that names the license
    ///   file.
    /// - Issue the server license for the installation key that the event names; start the service, wait for RUNNING and until elm
    ///   server status --data answers on its socket, as an administrator; stop it with sc stop and wait for STOPPED, timing the stop.
    /// - Clean up: stop and delete the service, remove the source elm-server from the registry when this test added it,
    ///   and remove the folders.
    ///
    /// Expected:
    /// - elm server status exits with 0 before the service stops.
    /// - The event is an error whose text starts with "error: The server license '{license}' does not exist." and names an
    ///   installation key.
    /// - After the stop, the service is STOPPED with WIN32_EXIT_CODE 0, the newest usage log file and the newest borrow record
    ///   hold a server.stopped line, and the usage log ends with its signature.
    /// - The stop's time goes to the test's output and, through run.ps1, to the job's log and summary (RunReport.Note).
    /// </remarks>
    [Fact]
    public async Task Service_StartWithoutLicenseThenStop_ReportsTheErrorAndClosesTheRecords()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A Windows service runs on Windows only.");
        if (!OperatingSystem.IsWindows())
        {
            // CA1416 does not read SkipUnless as a platform check.
            return;
        }

        bool onActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            if (!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Assert.False(onActions, "On GitHub Actions this test must run, and the runner's user is not an elevated administrator.");
                Assert.Skip("Registering a service needs an elevated administrator; this process is not one.");
            }
        }

        string id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        string product = "e2e-svc-" + id;
        string name = "elm-e2e-" + id;
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "elm-svc-" + id);
        string data = Path.Combine(root, "data");
        string sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        string reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
        const string Source = @"HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\elm-server";

        await using Vendor vendor = await Vendor.CreateAsync("license", "lease");
        string license = vendor.File("server.elmlic");
        string settings = vendor.File("elm-server.json");
        await File.WriteAllTextAsync(
            settings,
            JsonSerializer.Serialize(
                new Dictionary<string, object>
                {
                    ["product"] = product,
                    ["listen"] = "https://127.0.0.1:0",
                    ["license"] = license,
                    ["dataDirectory"] = data,
                    ["trust"] = new Dictionary<string, string[]> { ["rootKeys"] = [vendor.RootPublicKey] },
                }
            ),
            TestContext.Current.CancellationToken
        );

        bool hadSource = (await Binaries.RunAsync(reg, ["query", Source], vendor.Folder)).Code == 0;
        (int created, string createOut, string createError) = await Binaries.RunAsync(
            sc,
            ["create", name, "binPath=", $"\"{Binaries.Server}\" --config \"{settings}\""],
            vendor.Folder
        );
        Assert.True(created == 0, $"sc create exited with {created}: {createOut}{createError}");
        bool passed = false;
        try
        {
            // Step 1: no server license yet. The service makes its data folder and key, and ends before it connects to the
            // service control manager, which sc start may report as an error.
            await Binaries.RunAsync(sc, ["start", name], vendor.Folder);
            await WaitForStateAsync(sc, name, "STOPPED", vendor.Folder);
            string startError = string.Empty;
            await RealClock.WaitUntilAsync(
                "event 50 of elm-server",
                async () => (startError = await StartErrorAsync(license, vendor.Folder)).Length > 0,
                StateTimeout
            );
            Assert.StartsWith($"error: The server license '{license}' does not exist.", startError, StringComparison.Ordinal);
            Match key = InstallationKey().Match(startError);
            Assert.True(key.Success, $"Step 1: the event names no installation key: {startError}");

            // Step 2: the license for that key; the service starts and stops.
            await vendor.ElmOkAsync(
                "license",
                "issue",
                "--signing-key",
                vendor.SigningKey,
                "--certificate",
                vendor.Certificate,
                "--product",
                product,
                "--license-id",
                "e2e-service",
                "--kind",
                "server",
                "--feature",
                "pro;seats=3",
                "--expires",
                DateTimeOffset.UtcNow.AddDays(30).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                "--bind-key",
                key.Groups[1].Value,
                "--trust",
                vendor.RootPublicKey,
                "--out",
                license
            );
            (int started, string startOut, string startErr) = await Binaries.RunAsync(sc, ["start", name], vendor.Folder);
            Assert.True(started == 0, $"Step 2, sc start exited with {started}: {startOut}{startErr}");
            await WaitForStateAsync(sc, name, "RUNNING", vendor.Folder);

            // The usage log being written has a name of its own until it is closed, and the operational log goes to the event
            // log from Warning up only, so the server's answer on admin.sock tells that it has started; elm reads the token from
            // the data folder that LocalSystem made, as an administrator does (design §10.2).
            bool answered = false;
            string statusError = string.Empty;
            await RealClock.WaitUntilAsync(
                "elm server status",
                async () =>
                {
                    (int code, _, statusError) = await vendor.ElmAsync("server", "status", "--data", data);
                    answered = code == 0;
                    if (answered)
                    {
                        return true;
                    }

                    (_, string state, _) = await Binaries.RunAsync(sc, ["query", name], vendor.Folder);
                    return Regex.IsMatch(state, @"STATE\s*:\s*\d+\s+STOPPED\b");
                },
                StateTimeout
            );
            Assert.True(answered, $"Step 2, the service stopped before elm server status answered: {statusError}");

            long stopping = Stopwatch.GetTimestamp();
            (int stopped, string stopOut, string stopError) = await Binaries.RunAsync(sc, ["stop", name], vendor.Folder);
            Assert.True(stopped == 0, $"Step 2, sc stop exited with {stopped}: {stopOut}{stopError}");
            string query = await WaitForStateAsync(sc, name, "STOPPED", vendor.Folder);
            TimeSpan stopTime = Stopwatch.GetElapsedTime(stopping);
            string line = $"elm-server as a Windows service: sc stop to STOPPED took {stopTime.TotalMilliseconds:F0} ms.";
            RunReport.Note(line);

            Assert.Matches(@"WIN32_EXIT_CODE\s*:\s*0\s", query);
            string usage = Newest(data, "usage");
            string[] usageLines = usage.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("\"type\":\"server.stopped\"", usageLines[^2], StringComparison.Ordinal);
            Assert.Contains("\"type\":\"auth\"", usageLines[^1], StringComparison.Ordinal);
            Assert.Contains("\"type\":\"server.stopped\"", Newest(data, "borrows"), StringComparison.Ordinal);
            passed = true;
        }
        finally
        {
            // Each step runs whatever the one before it did, and a failed step fails the test only when the checks passed
            // (testing.mdc): a failed check stays the test's result.
            List<string> problems = [];
            await CleanUpAsync(problems, "sc stop", () => StopIfRunningAsync(sc, name, vendor.Folder));
            await CleanUpAsync(
                problems,
                "sc delete",
                async () =>
                {
                    (int deleted, string deleteOut, string deleteError) = await Binaries.RunAsync(sc, ["delete", name], vendor.Folder);
                    Assert.True(deleted == 0, $"sc delete exited with {deleted}: {deleteOut}{deleteError}");
                }
            );
            if (!hadSource)
            {
                await CleanUpAsync(problems, "reg delete", () => Binaries.RunAsync(reg, ["delete", Source, "/f"], vendor.Folder));
            }

            await CleanUpAsync(
                problems,
                "the data folder",
                () =>
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }

                    return Task.CompletedTask;
                }
            );
            Assert.True(!passed || problems.Count == 0, "Cleanup: " + string.Join("; ", problems));
        }
    }

    /// <summary>
    /// The steps of the How-to guide "Run elm-server as a system service" for Windows (design §13.1, V1d30): New-Service,
    /// one start that stops for want of the server license and leaves it in the event log, elm server activate in the
    /// elevated terminal, sc.exe failure, Start-Service, and Stop-Service with the records closed.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows, in an elevated administrator's process, as the runner's user of GitHub Actions is. On GitHub Actions the test
    ///   fails when it is not one; elsewhere it is skipped and says so.
    /// - The vendor's elm-service with the product acme-cad, a server policy, and a license of 3 seats.
    /// - The names of the page with the digits of this run: the service elm-e2e-{8 hex digits}, the folder
    ///   %ProgramFiles%\elm-e2e-{digits} for the executables, the settings file of samples/ElmServerService/windows (listen on
    ///   127.0.0.1 port 0), and the root key, and the data folder %ProgramData%\elm-e2e-{digits}.
    ///
    /// Steps:
    /// - Copy elm-server.exe, elm.exe, the settings file, and the root key into the folder; run the page's PowerShell lines
    ///   in pwsh: New-Service with the absolute paths, Start-Service, which fails, and Get-WinEvent for the newest event 50 of
    ///   elm-server.
    /// - elm server activate with the key in ELM_LICENSE_KEY (the test has no terminal to type it in); sc.exe failure as the
    ///   page writes it; Start-Service; wait until elm server status answers; Stop-Service, timed in pwsh; sc.exe qfailure and
    ///   sc.exe query.
    /// - Clean up: stop and delete the service, remove the source elm-server when this test added it, remove the folders.
    ///
    /// Expected:
    /// - The event names the missing server license and an installation key; activate exits with 0.
    /// - sc.exe qfailure lists three restarts after 60000 ms and a reset of 86400 seconds.
    /// - After Stop-Service the service is STOPPED with WIN32_EXIT_CODE 0, and the newest usage log ends with server.stopped
    ///   and its signature. The stop's time goes to the job's log (RunReport.Note).
    /// </remarks>
    [Fact]
    public async Task NewService_ThePageSteps_StartActivateAndStopTheServer()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A Windows service runs on Windows only.");
        if (!OperatingSystem.IsWindows())
        {
            // CA1416 does not read SkipUnless as a platform check.
            return;
        }

        bool onActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            if (!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Assert.False(onActions, "On GitHub Actions this test must run, and the runner's user is not an elevated administrator.");
                Assert.Skip("Registering a service needs an elevated administrator; this process is not one.");
            }
        }

        string id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        string name = "elm-e2e-" + id;
        string bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), name);
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), name);
        string license = Path.Combine(data, "server.elmlic");
        string settings = Path.Combine(bin, "elm-server.json");
        string trust = Path.Combine(bin, "root.pub.pem");
        string elm = Path.Combine(bin, "elm.exe");
        string sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        string reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
        const string Source = @"HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\elm-server";

        await using Vendor vendor = await Vendor.CreateAsync("license", "lease", "log");
        (ServiceProcess started, string key) = await SystemService.StartVendorServiceAsync(vendor);
        await using ServiceProcess service = started;
        bool hadSource = (await Binaries.RunAsync(reg, ["query", Source], vendor.Folder)).Code == 0;
        bool passed = false;
        try
        {
            // The page's step 1: the files in a folder that only administrators may write.
            Directory.CreateDirectory(bin);
            File.Copy(Binaries.Server, Path.Combine(bin, "elm-server.exe"));
            File.Copy(Binaries.Elm, elm);
            File.Copy(vendor.RootPublicKey, trust);
            static string Json(string path) => path.Replace(@"\", @"\\", StringComparison.Ordinal);
            await File.WriteAllTextAsync(
                settings,
                SystemService.Sample(
                    "windows/elm-server.json",
                    ("https://0.0.0.0:7070", "https://127.0.0.1:0"),
                    (@"C:\\ProgramData\\elm-server", Json(data)),
                    (@"C:\\Program Files\\elm-server", Json(bin))
                ),
                TestContext.Current.CancellationToken
            );

            // Step 2: New-Service and one start, which stops for want of the license and leaves the reason in the event log.
            string exe = Path.Combine(bin, "elm-server.exe");
            await PowerShellOkAsync(
                vendor.Folder,
                $"New-Service -Name '{name}' -BinaryPathName '\"{exe}\" --config \"{settings}\"' -StartupType Automatic | Out-Null"
            );
            await PowerShellAsync(vendor.Folder, $"Start-Service -Name '{name}'");
            await WaitForStateAsync(sc, name, "STOPPED", vendor.Folder);
            string startError = string.Empty;
            await RealClock.WaitUntilAsync(
                "event 50 of elm-server from Get-WinEvent",
                async () =>
                {
                    (int code, string output, _) = await PowerShellAsync(
                        vendor.Folder,
                        "Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'elm-server'; Id = 50 } -MaxEvents 1 | Select-Object -ExpandProperty Message"
                    );
                    startError = output.Trim();
                    return code == 0 && startError.Contains(license, StringComparison.Ordinal);
                },
                StateTimeout
            );
            Assert.StartsWith($"error: The server license '{license}' does not exist.", startError, StringComparison.Ordinal);
            Assert.Matches(InstallationKey(), startError);

            // Step 3: elm server activate in the elevated terminal (design §10.2).
            (int activated, _, string activateError) = await Binaries.RunAsync(
                elm,
                [
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
                    trust,
                ],
                vendor.Folder,
                new Dictionary<string, string?> { ["ELM_LICENSE_KEY"] = key }
            );
            Assert.True(activated == 0, $"elm server activate exited with {activated}: {activateError}");

            // Step 4: the restarts, after the license, and the start.
            (int failure, string failureOut, string failureError) = await Binaries.RunAsync(
                sc,
                ["failure", name, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000"],
                vendor.Folder
            );
            Assert.True(failure == 0, $"sc.exe failure exited with {failure}: {failureOut}{failureError}");
            (_, string actions, _) = await Binaries.RunAsync(sc, ["qfailure", name], vendor.Folder);
            Assert.Matches(@"RESET_PERIOD \(in seconds\)\s*:\s*86400", actions);
            Assert.Equal(3, Regex.Count(actions, @"RESTART -- Delay = 60000 milliseconds"));
            await PowerShellOkAsync(vendor.Folder, $"Start-Service -Name '{name}'");
            bool answered = false;
            string statusError = string.Empty;
            await RealClock.WaitUntilAsync(
                "elm server status of the service",
                async () =>
                {
                    (int code, _, statusError) = await Binaries.RunAsync(elm, ["server", "status", "--data", data], vendor.Folder);
                    answered = code == 0;
                    return answered;
                },
                StateTimeout
            );
            Assert.True(answered, statusError);

            // Step 5: the stop, timed in pwsh around Stop-Service, which returns once the service has stopped.
            string took = await PowerShellOkAsync(
                vendor.Folder,
                $"$watch = [Diagnostics.Stopwatch]::StartNew(); Stop-Service -Name '{name}'; $watch.ElapsedMilliseconds"
            );
            RunReport.Note($"elm-server as a Windows service: Stop-Service took {took.Trim()} ms.");
            (_, string query, _) = await Binaries.RunAsync(sc, ["query", name], vendor.Folder);
            Assert.Matches(@"STATE\s*:\s*\d+\s+STOPPED\b", query);
            Assert.Matches(@"WIN32_EXIT_CODE\s*:\s*0\s", query);
            string[] usageLines = Newest(data, "usage").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("\"type\":\"server.stopped\"", usageLines[^2], StringComparison.Ordinal);
            Assert.Contains("\"type\":\"auth\"", usageLines[^1], StringComparison.Ordinal);
            passed = true;
        }
        finally
        {
            List<string> problems = [];
            await SystemService.CleanUpAsync(problems, "sc stop", () => StopIfRunningAsync(sc, name, vendor.Folder));
            await SystemService.CleanUpAsync(problems, "sc delete", () => Binaries.RunAsync(sc, ["delete", name], vendor.Folder));
            if (!hadSource)
            {
                await SystemService.CleanUpAsync(problems, "reg delete", () => Binaries.RunAsync(reg, ["delete", Source, "/f"], vendor.Folder));
            }

            foreach (string folder in (string[])[bin, data])
            {
                await SystemService.CleanUpAsync(
                    problems,
                    folder,
                    () =>
                    {
                        if (Directory.Exists(folder))
                        {
                            Directory.Delete(folder, recursive: true);
                        }

                        return Task.CompletedTask;
                    }
                );
            }

            Assert.True(!passed || problems.Count == 0, "Cleanup: " + string.Join("; ", problems));
        }
    }

    private static Task CleanUpAsync(List<string> problems, string step, Func<Task> action) => SystemService.CleanUpAsync(problems, step, action);

    // Runs a line of the page in PowerShell 7, as the administrator's terminal does.
    private static Task<(int Code, string Out, string Error)> PowerShellAsync(string folder, string command) =>
        Binaries.RunAsync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", command], folder);

    private static async Task<string> PowerShellOkAsync(string folder, string command)
    {
        (int code, string output, string error) = await PowerShellAsync(folder, command);
        Assert.True(code == 0, $"pwsh exited with {code} for '{command.Split(' ')[0]}': {error}");
        return output;
    }

    // Stops the service when it runs and waits until it has stopped, so that its process no longer holds the data folder.
    private static async Task StopIfRunningAsync(string sc, string name, string folder)
    {
        (_, string output, _) = await Binaries.RunAsync(sc, ["query", name], folder);
        if (!Regex.IsMatch(output, @"STATE\s*:\s*\d+\s+STOPPED\b"))
        {
            await Binaries.RunAsync(sc, ["stop", name], folder);
            await WaitForStateAsync(sc, name, "STOPPED", folder);
        }
    }

    // Waits until sc query names the state, and returns its output.
    private static async Task<string> WaitForStateAsync(string sc, string name, string state, string folder)
    {
        string output = string.Empty;
        await RealClock.WaitUntilAsync(
            $"the service {state}",
            async () =>
            {
                (_, output, _) = await Binaries.RunAsync(sc, ["query", name], folder);
                return Regex.IsMatch(output, $@"STATE\s*:\s*\d+\s+{state}\b");
            },
            StateTimeout
        );
        return output;
    }

    // The text of the newest event 50 of the source elm-server that names the license file, or "" when there is none.
    private static async Task<string> StartErrorAsync(string license, string folder)
    {
        string wevtutil = Path.Combine(Environment.SystemDirectory, "wevtutil.exe");
        (int code, string output, string error) = await Binaries.RunAsync(
            wevtutil,
            ["qe", "Application", "/q:*[System[Provider[@Name='elm-server'] and (EventID=50) and (Level=2)]]", "/f:text", "/rd:true", "/c:20"],
            folder
        );
        Assert.True(code == 0, $"wevtutil exited with {code}: {error}");
        foreach (string entry in output.Split("Event[", StringSplitOptions.RemoveEmptyEntries))
        {
            int at = entry.IndexOf("error: ", StringComparison.Ordinal);
            if (at >= 0 && entry.Contains(license, StringComparison.Ordinal))
            {
                return entry[at..].Trim();
            }
        }

        return string.Empty;
    }

    // The text of the newest file of a signed log in the data folder (usage-….jsonl, borrows-….jsonl), or "" before there is one.
    private static string Newest(string data, string name)
    {
        string[] files = Directory.Exists(data) ? Directory.GetFiles(data, name + "-*.jsonl") : [];
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length == 0)
        {
            return string.Empty;
        }

        using var stream = new FileStream(files[^1], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex("installation key ([A-Za-z0-9_-]{43})")]
    private static partial Regex InstallationKey();
}
