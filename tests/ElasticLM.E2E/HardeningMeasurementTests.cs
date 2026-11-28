using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ElasticLM.Client;
using ElasticLM.Licensing;

namespace ElasticLM.E2E;

/// <summary>
/// A temporary measurement for V1d30a (Issue #540, questions 30a-1 and 30a-2), run only by the temporary workflow
/// <c>measure-540.yml</c> and removed with it before the pull request is ready: the page's systemd flow with one lease, under
/// the sample unit with one variant of the hardening directives added. Every value goes to the job's log
/// (<see cref="RunReport.Note"/>); a step that fails is written down and the next step still runs, so that one run measures
/// every step.
/// </summary>
public sealed partial class HardeningMeasurementTests
{
    private const string VariantVariable = "ELM_E2E_HARDENING_VARIANT";
    private const string OutVariable = "ELM_E2E_HARDENING_OUT";

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromSeconds(300);

    // The candidates of 30a-1, each alone; {data} is the data folder.
    private static readonly Dictionary<string, string[]> Single = new(StringComparer.Ordinal)
    {
        ["no-new-privileges"] = ["NoNewPrivileges=yes"],
        ["protect-system"] = ["ProtectSystem=strict", "ReadWritePaths={data}"],
        ["protect-home"] = ["ProtectHome=yes"],
        ["private-tmp"] = ["PrivateTmp=yes"],
        ["private-devices"] = ["PrivateDevices=yes"],
        ["capability-bounding-set"] = ["CapabilityBoundingSet="],
        ["restrict-address-families"] = ["RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6"],
        ["protect-kernel-tunables"] = ["ProtectKernelTunables=yes"],
        ["protect-kernel-modules"] = ["ProtectKernelModules=yes"],
        ["protect-kernel-logs"] = ["ProtectKernelLogs=yes"],
        ["protect-control-groups"] = ["ProtectControlGroups=yes"],
        ["protect-clock"] = ["ProtectClock=yes"],
        ["protect-hostname"] = ["ProtectHostname=yes"],
        ["restrict-namespaces"] = ["RestrictNamespaces=yes"],
        ["restrict-realtime"] = ["RestrictRealtime=yes"],
        ["restrict-suid-sgid"] = ["RestrictSUIDSGID=yes"],
        ["lock-personality"] = ["LockPersonality=yes"],
        ["memory-deny-write-execute"] = ["MemoryDenyWriteExecute=yes"],
        ["system-call-architectures"] = ["SystemCallArchitectures=native"],
        ["system-call-filter"] = ["SystemCallFilter=@system-service", "SystemCallErrorNumber=EPERM"],
    };

    /// <summary>
    /// Measures one variant of the sample unit: whether elm-server starts, refreshes its license with service.url, gives and
    /// takes back a lease, and stops with exit code 0 and its records closed, and what the journal and systemd-analyze
    /// security say.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The temporary workflow sets ELM_E2E_HARDENING_VARIANT to baseline, localhost, a key of the candidates, all, or
    ///   all-localhost; without it the test is skipped. Linux with systemd and sudo without a password.
    /// - The vendor's elm-service with a server policy whose license is due for a refresh one second after it is issued
    ///   (tokenLifetime 3600, refreshWindow 3599), so that every start of the server refreshes it through service.url.
    ///
    /// Steps:
    /// - The page's steps of SystemdServiceTests up to the unit, with service.url on localhost for the localhost variants.
    /// - For restrict-address-families and all, resolve localhost, a name of /etc/hosts, and the runner's host name with
    ///   getent ahosts and with elm server refresh, under systemd-run with no directive and with the variant's directives.
    /// - Install the unit with the variant's directives, enable --now, wait for elm server status, wait for the refresh in
    ///   the usage log, acquire a lease through the server's reclaim time and release it, stop, and read the unit's exit
    ///   status, the records, the journal, and systemd-analyze security.
    /// - Clean up as SystemdServiceTests does, and remove the line of /etc/hosts.
    ///
    /// Expected:
    /// - Nothing is asserted but the cleanup: the values go to the job's log.
    /// </remarks>
    [Fact]
    public async Task Systemd_HardeningVariant_IsMeasured()
    {
        string? variant = Environment.GetEnvironmentVariable(VariantVariable);
        Assert.SkipWhen(variant is null, $"A measurement of the temporary workflow measure-540.yml; it sets {VariantVariable}.");
        Assert.SkipUnless(OperatingSystem.IsLinux(), "systemd runs on Linux only.");
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
        string hostsName = $"elm-e2e-{id}.test";
        string[] directives = DirectivesOf(variant!).Select(d => d.Replace("{data}", data, StringComparison.Ordinal)).ToArray();
        void Note(string line) => RunReport.Note($"hardening[{variant}]: {line}");
        Note($"directives: {(directives.Length == 0 ? "(none)" : string.Join(" | ", directives))}");

        await using Vendor vendor = await Vendor.CreateAsync("license", "lease", "log");
        await using ServiceProcess service = await ServiceProcess.StartAsync(vendor, "sqlite");
        string product = await service.ProductAsync(SystemService.Product);
        string policy = await service.PolicyAsync(
            product,
            "\"licenseKind\":\"server\",\"leaseDuration\":120,\"tokenLifetime\":3600,\"refreshWindow\":3599"
        );
        (_, string key) = await service.LicenseAsync(policy, seats: 3);
        string serviceUrl = service.Url.ToString().TrimEnd('/');
        string serverServiceUrl = variant!.EndsWith("localhost", StringComparison.Ordinal)
            ? new UriBuilder(service.Url) { Host = "localhost" }
                .Uri.ToString()
                .TrimEnd('/')
            : serviceUrl;
        Note($"service.url of the server: {serverServiceUrl}");

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
                ("/etc/elm-server", etc),
                ("https://licensing.example.com", serverServiceUrl)
            );
            await SystemService.InstallTextAsync(vendor, settings, etc + "/elm-server.json", "0644");
            await SystemService.SudoOkAsync("install", "-d", "-o", user, "-g", user, "-m", "0700", data);

            (int first, _, _) = await SystemService.SudoAsync(["-u", user, bin + "/elm-server", "--config", etc + "/elm-server.json"]);
            Note($"first start from the terminal exited with {first}");
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
                    serviceUrl,
                    "--license",
                    license,
                    "--trust",
                    etc + "/root.pub.pem",
                ],
                new Dictionary<string, string?> { ["ELM_LICENSE_KEY"] = key }
            );
            Note($"elm server activate exited with {activated}{(activated == 0 ? string.Empty : ": " + Last(activateError))}");

            if (variant is "restrict-address-families" or "all")
            {
                await SystemService.SudoOkAsync("sh", "-c", $"echo '127.0.0.1 {hostsName}' >> /etc/hosts");
                string machine = (await Binaries.RunAsync("hostname", [], "/")).Out.Trim();
                foreach ((string label, string[] set) in (List<(string, string[])>)[("no directive", []), ("the variant", directives)])
                {
                    foreach (string name in (string[])["localhost", hostsName, machine, "github.com", "elm-e2e.invalid"])
                    {
                        (int code, string output, string error) = await RunSandboxedAsync(user, set, ["getent", "ahosts", name]);
                        Note($"names under {label}: getent ahosts {name}: exit {code}: {Last(output + error)}");
                        long resolving = Stopwatch.GetTimestamp();
                        (code, output, error) = await RunSandboxedAsync(
                            user,
                            set,
                            [
                                "timeout",
                                "15",
                                elm,
                                "server",
                                "refresh",
                                "--product",
                                SystemService.Product,
                                "--data",
                                data,
                                "--service",
                                $"https://{name}:1",
                                "--license",
                                license,
                                "--trust",
                                etc + "/root.pub.pem",
                            ]
                        );
                        Note(
                            $"names under {label}: elm server refresh https://{name}:1: exit {code} after {Stopwatch.GetElapsedTime(resolving).TotalMilliseconds:F0} ms: {Last(output + error)}"
                        );
                    }
                }
            }

            string unitText = SystemService.Sample(
                "elm-server.service",
                ("User=elm-server", "User=" + user),
                ("Group=elm-server", "Group=" + user),
                ("/opt/elm-server/", bin + "/"),
                ("/etc/elm-server/", etc + "/"),
                ("/var/lib/elm-server", data),
                ("TimeoutStopSec=60\n", "TimeoutStopSec=60\n" + string.Concat(directives.Select(d => d + "\n")))
            );
            if (Environment.GetEnvironmentVariable(OutVariable) is { Length: > 0 } outFolder)
            {
                await File.WriteAllTextAsync(Path.Combine(outFolder, "unit.service"), unitText, TestContext.Current.CancellationToken);
            }

            await SystemService.InstallTextAsync(vendor, unitText, unitFile, "0644");
            await SystemService.SudoOkAsync("systemctl", "daemon-reload");
            long startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1;
            (int enabled, _, string enableError) = await SystemService.SudoAsync(["systemctl", "enable", "--now", unit]);
            Note($"systemctl enable --now exited with {enabled}{(enabled == 0 ? string.Empty : ": " + Last(enableError))}");
            foreach (string directive in directives)
            {
                string property = directive[..directive.IndexOf('=', StringComparison.Ordinal)];
                Note($"systemctl show {property}={Cut(await ShowAsync(unit, property))}");
            }

            bool answered = false;
            string statusError = string.Empty;
            await PollAsync(
                async () =>
                {
                    (int code, _, statusError) = await SystemService.SudoAsync(["-u", user, elm, "server", "status", "--data", data]);
                    answered = code == 0;
                    return answered || await ShowAsync(unit, "ActiveState") == "failed";
                },
                StateTimeout
            );
            Note(
                $"started: {(answered ? "yes" : "no: " + Last(statusError))}; ActiveState={await ShowAsync(unit, "ActiveState")}, NRestarts={await ShowAsync(unit, "NRestarts")}"
            );

            if (answered)
            {
                await MeasureRunningAsync(unit, data, vendor, Note);
            }

            long stopping = Stopwatch.GetTimestamp();
            (int stopped, _, string stopError) = await SystemService.SudoAsync(["systemctl", "stop", unit]);
            Note(
                $"systemctl stop exited with {stopped}{(stopped == 0 ? string.Empty : ": " + Last(stopError))} after {Stopwatch.GetElapsedTime(stopping).TotalMilliseconds:F0} ms; ExecMainCode={await ShowAsync(unit, "ExecMainCode")}, ExecMainStatus={await ShowAsync(unit, "ExecMainStatus")}, Result={await ShowAsync(unit, "Result")}"
            );
            await NoteRecordsAsync(data, Note);

            string journal = (await SystemService.SudoAsync(["journalctl", "-u", unit, "--no-pager", "-o", "cat"])).Out;
            string[] lines = journal.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Note(
                $"journal: {lines.Length} lines; server.started {(journal.Contains("server.started: listening on port", StringComparison.Ordinal) ? "yes" : "no")}, server.stopped {(journal.Contains("server.stopped: ", StringComparison.Ordinal) ? "yes" : "no")}"
            );
            string[] flagged = lines.Where(l => Flagged().IsMatch(l)).ToArray();
            Note($"journal: {flagged.Length} lines with error, fail, denied, warn, exception, or EPERM");
            foreach (string line in flagged.Take(8))
            {
                Note("journal line: " + Cut(line));
            }

            string kernel = (
                await SystemService.SudoAsync([
                    "journalctl",
                    "-k",
                    "--no-pager",
                    "-o",
                    "cat",
                    "--since",
                    "@" + startedAt.ToString(CultureInfo.InvariantCulture),
                ])
            ).Out;
            string[] audit = kernel.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => Audit().IsMatch(l)).ToArray();
            Note($"kernel log since the start: {audit.Length} lines with audit, apparmor, or seccomp");
            foreach (string line in audit.Take(5))
            {
                Note("kernel line: " + Cut(line));
            }

            (int analyzed, string security, string securityError) = await SystemService.SudoAsync([
                "systemd-analyze",
                "security",
                "--no-pager",
                unit,
            ]);
            string overall =
                security.Split('\n').FirstOrDefault(l => l.Contains("Overall exposure level", StringComparison.Ordinal))?.Trim() ?? "(none)";
            Note($"systemd-analyze security exited with {analyzed}: {Cut(overall)}{(analyzed == 0 ? string.Empty : " " + Last(securityError))}");
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
            await SystemService.CleanUpAsync(
                problems,
                "/etc/hosts",
                () => SystemService.SudoCleanUpAsync("sed", "-i", $"/{hostsName}/d", "/etc/hosts")
            );
            await SystemService.CleanUpAsync(problems, "userdel", () => SystemService.SudoTryAsync("userdel", "--force", user));
            await SystemService.CleanUpAsync(problems, "the user", () => SystemService.AssertNoAccountAsync(user));
            Note($"cleanup: {(problems.Count == 0 ? "ok" : string.Join("; ", problems))}");
            Assert.True(!passed || problems.Count == 0, "Cleanup: " + string.Join("; ", problems));
        }
    }

    private static string[] DirectivesOf(string variant) =>
        variant switch
        {
            "baseline" or "localhost" => [],
            "all" or "all-localhost" => Single.Values.SelectMany(d => d).ToArray(),
            _ => Single.TryGetValue(variant, out string[]? directives)
                ? directives
                : throw new InvalidOperationException($"Unknown variant '{variant}'."),
        };

    // The refresh through service.url, one lease through the reclaim time, and its return, while the unit runs.
    private static async Task MeasureRunningAsync(string unit, string data, Vendor vendor, Action<string> note)
    {
        string usage = string.Empty;
        long refreshing = Stopwatch.GetTimestamp();
        await PollAsync(
            async () =>
            {
                usage = await UsageAsync(data);
                return usage.Contains("\"type\":\"license.refreshed\"", StringComparison.Ordinal)
                    || usage.Contains("\"type\":\"license.refresh_failed\"", StringComparison.Ordinal);
            },
            StateTimeout
        );
        string refresh =
            usage.Contains("\"type\":\"license.refreshed\"", StringComparison.Ordinal) ? "license.refreshed"
            : usage.Contains("\"type\":\"license.refresh_failed\"", StringComparison.Ordinal)
                ? "license.refresh_failed " + Cut(RefreshFailed().Match(usage).Value)
            : "none within 60 s";
        note($"refresh through service.url: {refresh} after {Stopwatch.GetElapsedTime(refreshing).TotalMilliseconds:F0} ms");

        string journal = (await SystemService.SudoAsync(["journalctl", "-u", unit, "--no-pager", "-o", "cat"])).Out;
        Match port = Port().Match(journal);
        if (!port.Success)
        {
            note("lease: no port in the journal");
            return;
        }

        var options = new LicenseClientOptions
        {
            ProductId = SystemService.Product,
            ServerUrl = new Uri($"https://127.0.0.1:{port.Groups[1].Value}"),
            Trust = TrustStore.Create([await File.ReadAllTextAsync(vendor.RootPublicKey, TestContext.Current.CancellationToken)], []),
            DataDirectory = vendor.File("client"),
        };
        if (!LicenseClient.TryCreate(options, out LicenseClient? created, out LicenseError? error))
        {
            note($"lease: no client: {error!.Code}: {error.Message}");
            return;
        }

        await using LicenseClient client = created!;
        long acquiring = Stopwatch.GetTimestamp();
        string outcome = "no answer";
        while (Stopwatch.GetElapsedTime(acquiring) < LeaseTimeout)
        {
            LeaseResult result = await client.AcquireLeaseAsync("pro", cancellationToken: TestContext.Current.CancellationToken);
            if (result is LeaseResult.Acquired acquired)
            {
                LicenseError? released = await client.ReleaseLeaseAsync(acquired.Lease, TestContext.Current.CancellationToken);
                outcome =
                    $"acquired after {Stopwatch.GetElapsedTime(acquiring).TotalMilliseconds:F0} ms, released {(released is null ? "ok" : released.Code + ": " + released.Message)}";
                break;
            }

            var failed = (LeaseResult.Failed)result;
            outcome = $"{failed.Error.Code}: {failed.Error.Message}";
            if (failed.Error.Code != ErrorCode.ServerRecovering)
            {
                break;
            }

            await Task.Delay(failed.RetryAfter ?? TimeSpan.FromSeconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        note("lease: " + Cut(outcome));
    }

    private static async Task NoteRecordsAsync(string data, Action<string> note)
    {
        try
        {
            string[] usage = (await UsageAsync(data)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            bool closed =
                usage.Length >= 2
                && usage[^2].Contains("\"type\":\"server.stopped\"", StringComparison.Ordinal)
                && usage[^1].Contains("\"type\":\"auth\"", StringComparison.Ordinal);
            note($"usage log ends with server.stopped and auth: {(closed ? "yes" : "no")}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            note($"usage log: {e.GetType().Name}: {Cut(e.Message)}");
        }
    }

    // The newest usage log file of the data folder, read as root.
    private static async Task<string> UsageAsync(string data)
    {
        string list = (await SystemService.SudoAsync(["find", data, "-maxdepth", "1", "-name", "usage-*.jsonl"])).Out;
        string[] files = list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Array.Sort(files, StringComparer.Ordinal);
        return files.Length == 0 ? string.Empty : (await SystemService.SudoAsync(["cat", files[^1]])).Out;
    }

    // Runs a command as the user under systemd-run with the directives given, as the unit would run it.
    private static Task<(int Code, string Out, string Error)> RunSandboxedAsync(string user, string[] directives, string[] command) =>
        SystemService.SudoAsync([
            "systemd-run",
            "--wait",
            "--pipe",
            "--quiet",
            "--collect",
            "-p",
            "User=" + user,
            "-p",
            "Group=" + user,
            .. directives.SelectMany(d => (string[])["-p", d]),
            "--",
            .. command,
        ]);

    private static async Task<string> ShowAsync(string unit, string property) =>
        (await SystemService.SudoAsync(["systemctl", "show", unit, "-p", property, "--value"])).Out.Trim();

    private static async Task PollAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        long start = Stopwatch.GetTimestamp();
        while (!await condition() && Stopwatch.GetElapsedTime(start) < timeout)
        {
            await Task.Delay(Poll, TimeProvider.System, TestContext.Current.CancellationToken);
        }
    }

    private static string Last(string text) =>
        Cut(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "(no output)");

    private static string Cut(string text) => text.Length <= 400 ? text : text[..400] + "…";

    [GeneratedRegex(@"server\.started: listening on port (\d+)")]
    private static partial Regex Port();

    [GeneratedRegex("\\{[^\\n]*\"type\":\"license\\.refresh_failed\"[^\\n]*")]
    private static partial Regex RefreshFailed();

    [GeneratedRegex("error|fail|denied|warn|exception|EPERM", RegexOptions.IgnoreCase)]
    private static partial Regex Flagged();

    [GeneratedRegex("audit|apparmor|seccomp", RegexOptions.IgnoreCase)]
    private static partial Regex Audit();
}
