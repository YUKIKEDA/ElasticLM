namespace ElasticLM.E2E;

/// <summary>
/// What the tests of elm-server as a system service share (design §13.1, "systemd and launchd", V1d30): the samples of the
/// How-to guide "Run elm-server as a system service", the vendor's service that activates the server, sudo, and the cleanup.
/// The tests follow the page's steps with names of their own, so that nothing of a developer's or another run's is touched.
/// </summary>
internal static class SystemService
{
    /// <summary>The product of the samples' settings files, which the tests' service also makes.</summary>
    public const string Product = "acme-cad";

    private static readonly Lazy<string> Root = new(FindRoot);

    /// <summary>
    /// Reads a file of <c>samples/ElmServerService</c> and replaces each text the page's reader would keep with the test's
    /// own; every text to replace must be in the sample, so that the sample and the test cannot drift apart unseen.
    /// </summary>
    /// <param name="name">The file's path under <c>samples/ElmServerService</c>.</param>
    /// <param name="replacements">The texts of the sample and what each becomes.</param>
    public static string Sample(string name, params (string From, string To)[] replacements)
    {
        string text = File.ReadAllText(Path.Combine(Root.Value, "samples", "ElmServerService", name)).ReplaceLineEndings("\n");
        foreach ((string from, string to) in replacements)
        {
            Assert.True(text.Contains(from, StringComparison.Ordinal), $"The sample {name} no longer holds '{from}'.");
            text = text.Replace(from, to, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>
    /// Starts the vendor's elm-service with the product of the samples, a server policy, and a license of 3 seats, for
    /// <c>elm server activate</c>.
    /// </summary>
    /// <param name="vendor">The vendor.</param>
    public static async Task<(ServiceProcess Service, string Key)> StartVendorServiceAsync(Vendor vendor)
    {
        ServiceProcess service = await ServiceProcess.StartAsync(vendor, "sqlite");
        try
        {
            string product = await service.ProductAsync(Product);
            (_, string key) = await service.LicenseAsync(
                await service.PolicyAsync(product, "\"licenseKind\":\"server\",\"leaseDuration\":120"),
                seats: 3
            );
            return (service, key);
        }
        catch
        {
            await service.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Runs a command with <c>sudo -n</c> in the folder <c>/</c>, as an administrator's terminal does, and returns its exit
    /// code and output.
    /// </summary>
    /// <param name="arguments">The arguments of sudo.</param>
    /// <param name="environment">Variables for sudo, which passes only those that <c>--preserve-env</c> names.</param>
    public static Task<(int Code, string Out, string Error)> SudoAsync(
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null
    ) => Binaries.RunAsync("sudo", ["-n", .. arguments], "/", environment);

    /// <summary>Runs a command with sudo and returns its output, failing the test unless it exits with 0.</summary>
    /// <param name="arguments">The arguments of sudo.</param>
    public static async Task<string> SudoOkAsync(params string[] arguments)
    {
        (int code, string output, string error) = await SudoAsync(arguments);

        // The command's first words only: a later argument may be a path the test made, never a secret.
        Assert.True(code == 0, $"sudo {string.Join(' ', arguments.Take(3))} exited with {code}: {error}{output}");
        return output;
    }

    /// <summary>
    /// Whether this process may run commands with sudo without a password, as the runners of GitHub Actions can. On GitHub
    /// Actions the test fails when it cannot; elsewhere it is skipped and says so.
    /// </summary>
    public static async Task RequireSudoAsync()
    {
        bool onActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        (int code, _, _) = await SudoAsync(["true"]);
        if (code != 0)
        {
            Assert.False(onActions, "On GitHub Actions this test must run, and sudo -n true failed.");
            Assert.Skip("Installing a system service needs sudo without a password; sudo -n true failed here.");
        }
    }

    /// <summary>Writes a file the test made to a path that only root may write, with the owner and the mode given.</summary>
    /// <param name="vendor">The vendor whose folder holds the file first.</param>
    /// <param name="text">The file's text.</param>
    /// <param name="target">The absolute path to install it at.</param>
    /// <param name="mode">The mode, such as <c>0644</c>.</param>
    public static async Task InstallTextAsync(Vendor vendor, string text, string target, string mode)
    {
        string source = vendor.File(Path.GetFileName(target));
        await File.WriteAllTextAsync(source, text, TestContext.Current.CancellationToken);
        await SudoOkAsync("install", "-m", mode, "-o", "root", "-g", OperatingSystem.IsMacOS() ? "wheel" : "root", source, target);
    }

    /// <summary>
    /// Checks that the newest usage log file of the folder, read as root, ends with server.stopped and its signature: the
    /// records were closed before the process ended (design §13.1, V1d30, Q30-g).
    /// </summary>
    /// <param name="data">The data folder, which holds the usage log by default.</param>
    public static async Task AssertUsageLogClosedAsync(string data)
    {
        string list = await SudoOkAsync("find", data, "-maxdepth", "1", "-name", "usage-*.jsonl");
        string[] files = list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Array.Sort(files, StringComparer.Ordinal);
        Assert.NotEmpty(files);
        string[] lines = (await SudoOkAsync("cat", files[^1])).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("\"type\":\"server.stopped\"", lines[^2], StringComparison.Ordinal);
        Assert.Contains("\"type\":\"auth\"", lines[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs one step of a cleanup and records its failure, so that every step runs whatever the one before it did
    /// (testing.mdc).
    /// </summary>
    /// <param name="problems">The failures so far.</param>
    /// <param name="step">The step's name.</param>
    /// <param name="action">The step.</param>
    public static async Task CleanUpAsync(List<string> problems, string step, Func<Task> action)
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

    private static string FindRoot()
    {
        for (string? folder = AppContext.BaseDirectory; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (File.Exists(Path.Combine(folder, "ElasticLM.slnx")))
            {
                return folder;
            }
        }

        throw new InvalidOperationException($"No folder above {AppContext.BaseDirectory} holds ElasticLM.slnx.");
    }
}
