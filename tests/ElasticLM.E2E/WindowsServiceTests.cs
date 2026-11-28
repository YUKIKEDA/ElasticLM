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

    private static async Task CleanUpAsync(List<string> problems, string step, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e)
        {
            problems.Add($"{step}: {e.GetType().Name}: {e.Message}");
        }
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
