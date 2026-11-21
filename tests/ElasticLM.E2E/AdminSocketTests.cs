using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ElasticLM.E2E;

/// <summary>
/// The published elm-server and elm around the socket file of the administration API (design §13.1, §13.7, §20.7, V1d2):
/// what needs a process of its own, another process's environment, or another user of the machine.
/// </summary>
public sealed partial class AdminSocketTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A server whose dataDirectory is relative listens on the absolute path of its socket file: elm reaches it from another
    /// working folder with an absolute --data, and from the server's folder with the relative one.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The published elm-server for acme-cad, started in the vendor's folder with ELM_DATADIRECTORY set to the relative
    ///   path "d", and a server license bound to its key.
    ///
    /// Steps:
    /// - Run elm server status in a second folder with --data and the absolute path of the data folder; run it in the
    ///   vendor's folder with --data d.
    ///
    /// Expected:
    /// - Both exit 0 and print the server's license.
    /// </remarks>
    [Fact]
    public async Task Server_RelativeDataDirectory_ElmReachesItByAbsoluteAndRelativePath()
    {
        await using Vendor vendor = await Vendor.CreateAsync("license", "lease");
        string license = vendor.File("server.elmlic");
        await using ServerProcess server = ServerProcess.Prepare(vendor, "d", "acme-cad", license);
        server.SetEnvironment("ELM_DATADIRECTORY", "d");
        await server.IssueLicenseAsync(vendor, license);
        await server.StartAsync();
        string elsewhere = vendor.File("elsewhere");
        Directory.CreateDirectory(elsewhere);

        (int absolute, string absoluteOut, string absoluteError) = await Binaries.RunAsync(
            Binaries.Elm,
            ["server", "status", "--data", server.DataDirectory],
            elsewhere
        );
        (int relative, string relativeOut, string relativeError) = await vendor.ElmAsync("server", "status", "--data", "d");

        Assert.True(absolute == 0, absoluteError);
        Assert.Contains("license: e2e-server", absoluteOut, StringComparison.Ordinal);
        Assert.True(relative == 0, relativeError);
        Assert.Contains("license: e2e-server", relativeOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// A killed server leaves its socket file; the next start takes the lock, finds the file's connection refused, deletes
    /// it, and listens.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The published elm-server for acme-cad, running with a server license.
    ///
    /// Steps:
    /// - Kill the server's process; look for admin.sock; start the server again on the same data folder and a port the system
    ///   chooses; run elm server status.
    ///
    /// Expected:
    /// - admin.sock is still in the data folder after the kill; the second start answers, and status exits 0.
    /// </remarks>
    [Fact]
    public async Task Server_KilledAndStartedAgain_TakesTheFolderWithTheSocketFileThatWasLeft()
    {
        await using Vendor vendor = await Vendor.CreateAsync("license", "lease");
        string license = vendor.File("server.elmlic");
        await using ServerProcess server = ServerProcess.Prepare(vendor, "s", "acme-cad", license);
        await server.IssueLicenseAsync(vendor, license);
        await server.StartAsync();

        await server.KillAsync();
        Assert.True(Path.Exists(server.AdminSocketPath), "The killed server left no socket file.");

        // On a port of its own: the test needs no client to find the server where it was.
        await using ServerProcess again = server.Twin();
        await again.StartAsync();

        (int code, _, string error) = await again.ElmServerAsync(vendor, "status");
        Assert.True(code == 0, error);
    }

    /// <summary>
    /// Two servers started on one data folder one right after the other: exactly one runs, and the other stops at the lock
    /// file. The moment at which the two overlap is not the test's to choose, so the pair is started several times.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The published elm-server for acme-cad with a server license, prepared twice for each round with the same data folder,
    ///   each on a port the system chooses.
    ///
    /// Steps:
    /// - Five times: launch both processes without waiting in between; wait until one has ended; wait until the other
    ///   answers; kill it.
    ///
    /// Expected:
    /// - Each time one process ends with exit code 1 and the line "error: Another elm-server is running on the data folder
    ///   '{folder}': it holds the lock file '{path}'.", and the other answers on its port.
    /// </remarks>
    [Fact]
    public async Task Server_TwoStartsOnOneDataFolder_OneRunsAndOneStopsAtTheLock()
    {
        await using Vendor vendor = await Vendor.CreateAsync("license", "lease");
        string license = vendor.File("server.elmlic");
        await using ServerProcess first = ServerProcess.Prepare(vendor, "s", "acme-cad", license);
        await first.IssueLicenseAsync(vendor, license);
        string line =
            $"error: Another elm-server is running on the data folder '{first.DataDirectory}': it holds the lock file "
            + $"'{Path.Combine(first.DataDirectory, "server.lock")}'.";

        for (int round = 1; round <= 5; round++)
        {
            // Two new servers in each round, each on a port the system chooses: none starts again on a port it let go.
            await using ServerProcess one = first.Twin();
            await using ServerProcess two = first.Twin();
            one.Launch();
            two.Launch();
            await RealClock.WaitUntilAsync("one of two servers on one data folder ending", () => one.HasExited || two.HasExited, Bound);
            (ServerProcess stopped, ServerProcess running) = one.HasExited ? (one, two) : (two, one);

            await running.WaitUntilItAnswersAsync();
            Assert.False(running.HasExited, $"Round {round}: both servers ended: {running.Log}");
            Assert.True(stopped.ExitCode == 1, $"Round {round}: the server that stopped exited with {stopped.ExitCode}: {stopped.Log}");

            // The log's last lines come after the process has ended.
            await RealClock.WaitUntilAsync("the stopped server's last line", () => stopped.Log.Contains(line, StringComparison.Ordinal), Bound);
            await one.KillAsync();
            await two.KillAsync();
        }
    }

    /// <summary>
    /// Where the file lock does not hold, the probe of the socket file still stops a second server: started with .NET's file
    /// locking switched off, it goes past the lock and stops because the running server's socket accepts its connection.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux or macOS: Windows has no way to switch off the sharing mode that is the lock there.
    /// - The published elm-server for acme-cad, running with a server license; the bytes of its admin.token.
    ///
    /// Steps:
    /// - Run a second server on the same data folder with DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1 to its end; run elm server
    ///   status.
    ///
    /// Expected:
    /// - The second server exits 1, and the last line of its standard error is "error: Another elm-server is running on the
    ///   data folder '{folder}': its administration socket '{path}' accepts connections.". A last line that names the lock
    ///   file instead would mean that the published executable does not read the variable.
    /// - admin.sock is still there, status exits 0, and admin.token has the same bytes.
    /// </remarks>
    [Fact]
    public async Task Server_FileLockingSwitchedOff_SecondStartStopsAtTheSocketProbe()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows locks the file by its sharing mode, which no setting switches off.");
        await using Vendor vendor = await Vendor.CreateAsync("license", "lease");
        string license = vendor.File("server.elmlic");
        await using ServerProcess first = ServerProcess.Prepare(vendor, "s", "acme-cad", license);
        await first.IssueLicenseAsync(vendor, license);
        await first.StartAsync();
        string tokenFile = Path.Combine(first.DataDirectory, "admin.token");
        byte[] token = await File.ReadAllBytesAsync(tokenFile, TestContext.Current.CancellationToken);
        await using ServerProcess second = first.Twin();
        second.SetEnvironment("DOTNET_SYSTEM_IO_DISABLEFILELOCKING", "1");

        (int code, _, string error) = await second.RunToExitAsync();

        Assert.Equal(1, code);
        Assert.Equal(
            $"error: Another elm-server is running on the data folder '{first.DataDirectory}': its administration socket "
                + $"'{first.AdminSocketPath}' accepts connections.",
            LastLine(error)
        );
        Assert.True(Path.Exists(first.AdminSocketPath));
        (int status, _, string statusError) = await first.ElmServerAsync(vendor, "status");
        Assert.True(status == 0, statusError);
        Assert.Equal(token, await File.ReadAllBytesAsync(tokenFile, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A listener that the operator adds through Kestrel's configuration does not serve the administration API, with or
    /// without TLS and whether it is a TCP port or another socket file: the server answers /admin only on the listener it
    /// bound to admin.sock itself.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The published elm-server for acme-cad with a server license, started with Kestrel__Endpoints__Tcp__Url set to an
    ///   http URL on 127.0.0.1 and port 0, whose port the test reads from the host's "Now listening on" line once the server
    ///   has started, and Kestrel__Endpoints__Socket__Url set to http://unix: and the path of a second socket file in the
    ///   vendor's folder. The server reads both from its process's environment, which a test in the server's own process
    ///   cannot give it.
    /// - A request without a token tells the two outcomes apart: the administration API answers it 401, and the guard before
    ///   it 404. On Linux and macOS the token is also read from admin.token, which holds its 32 bytes there, and sent.
    ///
    /// Steps:
    /// - GET /admin/v1/status without a token on admin.sock, on the added TCP listener, and on the added socket; on Linux
    ///   and macOS the same with the token on the two added listeners. GET /v1/nonce on the two added listeners.
    ///
    /// Expected:
    /// - 401 on admin.sock, so the administration API is up; 404 for every request on the two added listeners.
    /// </remarks>
    [Fact]
    public async Task Server_ListenersAddedThroughConfiguration_DoNotServeTheAdministrationApi()
    {
        await using Vendor vendor = await Vendor.CreateAsync("license", "lease");
        string license = vendor.File("server.elmlic");
        await using ServerProcess server = ServerProcess.Prepare(vendor, "s", "acme-cad", license);
        await server.IssueLicenseAsync(vendor, license);
        string otherSocket = vendor.File("x.sock");
        server.SetEnvironment("Kestrel__Endpoints__Tcp__Url", "http://127.0.0.1:0");

        // Kestrel takes "http://unix:/" for a socket file. On Linux and macOS that "/" is the first character of the path; on
        // Windows the path, which begins with a drive letter, follows it (BindingAddress.GetUnixPipePath, aspnetcore
        // release/10.0).
        server.SetEnvironment("Kestrel__Endpoints__Socket__Url", "http://unix:" + (OperatingSystem.IsWindows() ? "/" : string.Empty) + otherSocket);
        await server.StartAsync();
        CancellationToken cancel = TestContext.Current.CancellationToken;

        // The host names each address it listens on before the server's start line (design §18.1).
        Match listening = AddedTcpListener().Match(server.Log);
        Assert.True(listening.Success, $"The server named no http listener on 127.0.0.1: {server.Log}");
        var tcp = new Uri(listening.Groups[1].Value);

        using HttpClient own = SocketHttp(server.AdminSocketPath);
        using HttpClient added = new(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = tcp };
        using HttpClient addedSocket = SocketHttp(otherSocket);
        using (HttpResponseMessage answer = await own.GetAsync("admin/v1/status", cancel))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
        }

        string? token = OperatingSystem.IsWindows()
            ? null
            : Convert
                .ToBase64String(await File.ReadAllBytesAsync(Path.Combine(server.DataDirectory, "admin.token"), cancel))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        foreach ((HttpClient http, string name) in new[] { (added, "the added TCP listener"), (addedSocket, "the added socket") })
        {
            using (HttpResponseMessage answer = await http.GetAsync("admin/v1/status", cancel))
            {
                Assert.True(answer.StatusCode == HttpStatusCode.NotFound, $"{name} answered /admin/v1/status with {(int)answer.StatusCode}.");
            }

            using (HttpResponseMessage answer = await http.GetAsync("v1/nonce", cancel))
            {
                Assert.True(answer.StatusCode == HttpStatusCode.NotFound, $"{name} answered /v1/nonce with {(int)answer.StatusCode}.");
            }

            if (token is not null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "admin/v1/status");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage answer = await http.SendAsync(request, cancel);
                Assert.True(answer.StatusCode == HttpStatusCode.NotFound, $"{name} answered the token with {(int)answer.StatusCode}.");
            }
        }
    }

    /// <summary>
    /// Another user of the machine can neither connect to the server's socket file nor listen in the server's data folder:
    /// the folder's mode keeps them out (design §10.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux or macOS, with sudo that lets this user run python3 as the user nobody without a password, as the CI runners
    ///   do; on GitHub Actions the test fails without it, elsewhere it is skipped and says so. Windows has a test of its
    ///   own, Server_AnotherWindowsUser_CanNeitherConnectNorListenInTheDataFolder.
    /// - The vendor's folder under a folder that sudo makes for this user, with mode 0755, in /var/lib (Linux) or /opt (macOS), so every user can enter it
    ///   and the server's check of the folders above its data folder passes (/tmp, which every user can write, is refused;
    ///   design §10.2, V1d24); the published elm-server running there with a server license; its data folder has mode 0700.
    /// - A python3 script that connects to a socket file, or binds one, and exits with 13 for PermissionError.
    ///
    /// Steps:
    /// - As this user: connect to admin.sock. As nobody: list the vendor's folder; connect to admin.sock; bind a socket file
    ///   in the data folder.
    ///
    /// Expected:
    /// - This user connects (exit 0), so the script works; nobody lists the vendor's folder (exit 0), so the way to the data
    ///   folder is open; nobody's connection and nobody's bind both end with exit code 13.
    /// </remarks>
    [Fact]
    public async Task Server_AnotherUser_CanNeitherConnectNorListenInTheDataFolder()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has Server_AnotherWindowsUser_CanNeitherConnectNorListenInTheDataFolder.");
        const string Script =
            "import os, socket, sys\n"
            + "action, path = sys.argv[1], sys.argv[2]\n"
            + "try:\n"
            + "    if action == 'list':\n"
            + "        os.listdir(path)\n"
            + "    else:\n"
            + "        s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)\n"
            + "        s.connect(path) if action == 'connect' else s.bind(path)\n"
            + "except PermissionError:\n"
            + "    sys.exit(13)\n";
        static Task<(int Code, string Out, string Error)> AsNobody(string folder, string action, string path) =>
            Binaries.RunAsync("sudo", ["-n", "-u", "nobody", "python3", "-c", Script, action, path], folder);

        (int probe, _, string probeError) = await AsNobody("/", "list", "/");
        if (probe != 0)
        {
            string why = "sudo -n -u nobody python3 exited with " + probe + ": " + probeError.Trim();
            Assert.False(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "On GitHub Actions this test must run. " + why);
            Assert.Skip("This machine does not let the test run python3 as another user. " + why);
        }

        // The Ubuntu runner's /opt has mode 0777 and a default access list that gives everyone rwx (getfacl on the runner,
        // V1d24, PR #447), so a server's folder under it is refused; /var/lib there is root's, of mode 0755. macOS has no
        // /var/lib, and its /opt passes. The shared folder also drops any access list it took from its parent.
        string above = OperatingSystem.IsMacOS() ? "/opt" : "/var/lib";
        string shared = above + "/elm-e2e-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
        bool aboveExisted = Directory.Exists(above);
        (int made, _, string madeError) = await Binaries.RunAsync(
            "sudo",
            [
                "-n",
                "sh",
                "-c",
                "mkdir -p "
                    + above
                    + " && mkdir -m 755 "
                    + shared
                    + " && { ! command -v setfacl >/dev/null || setfacl -b -k "
                    + shared
                    + "; } && chown "
                    + Environment.UserName
                    + " "
                    + shared,
            ],
            "/"
        );
        Assert.True(made == 0, "sudo could not make the shared folder: " + madeError.Trim());
        try
        {
            await NobodyCannotReachTheServerAsync(shared, Script);
        }
        finally
        {
            await Binaries.RunAsync("sudo", ["-n", "rm", "-rf", shared], "/");
            if (!aboveExisted)
            {
                await Binaries.RunAsync("sudo", ["-n", "rmdir", above], "/");
            }
        }
    }

    // The body of the test before, in the folder that sudo made.
    private static async Task NobodyCannotReachTheServerAsync(string shared, string script)
    {
        Task<(int Code, string Out, string Error)> AsNobody(string folder, string action, string path) =>
            Binaries.RunAsync("sudo", ["-n", "-u", "nobody", "python3", "-c", script, action, path], folder);

        await using Vendor vendor = await Vendor.CreateInAsync(shared, "license", "lease");
        string license = vendor.File("server.elmlic");
        await using ServerProcess server = ServerProcess.Prepare(vendor, "s", "acme-cad", license);
        await server.IssueLicenseAsync(vendor, license);
        await server.StartAsync();

        (int own, _, string ownError) = await Binaries.RunAsync("python3", ["-c", script, "connect", server.AdminSocketPath], vendor.Folder);
        (int listed, _, string listError) = await AsNobody("/", "list", vendor.Folder);
        (int connected, _, string connectError) = await AsNobody("/", "connect", server.AdminSocketPath);
        (int bound, _, string bindError) = await AsNobody("/", "bind", Path.Combine(server.DataDirectory, "o.sock"));

        Assert.True(own == 0, $"This user's connection exited with {own}: {ownError}");
        Assert.True(listed == 0, $"nobody could not list the vendor's folder (exit {listed}): {listError}");
        Assert.True(connected == 13, $"nobody's connection exited with {connected}: {connectError}");
        Assert.True(bound == 13, $"nobody's bind exited with {bound}: {bindError}");
    }

    /// <summary>
    /// On Windows, another local user can neither connect to the server's socket file nor listen in the server's data folder:
    /// the folder's access list, which the socket file inherits, keeps them out (design §10.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows, in a process that may make a local user: an elevated administrator, as the runner's user of GitHub Actions
    ///   is. On GitHub Actions the test fails when it cannot make the user; elsewhere it is skipped and says so.
    /// - The vendor's folder under the machine's ProgramData folder, which every user can enter (the temporary folder is
    ///   inside this user's profile), and the published elm-server running there with a server license.
    /// - A PowerShell 7 script in the vendor's folder that connects to one socket file and binds another, and prints the
    ///   SocketError of each, or "Done". It holds no secret.
    /// - A local user made for the test with a password from RandomNumberGenerator, which goes into no message, and removed
    ///   at the end, also when the test fails.
    ///
    /// Steps:
    /// - As this user: run the script against admin.sock and a path in the vendor's folder. Make the user. As that user:
    ///   run the script against admin.sock and a path in the data folder.
    ///
    /// Expected:
    /// - This user's connection prints "connect=Done", so the script works.
    /// - The other user's run prints a SocketError for the connection and for the bind, not "Done"; the test's output names
    ///   both errors.
    /// </remarks>
    [Fact]
    public async Task Server_AnotherWindowsUser_CanNeitherConnectNorListenInTheDataFolder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Linux and macOS have Server_AnotherUser_CanNeitherConnectNorListenInTheDataFolder.");
        if (!OperatingSystem.IsWindows())
        {
            // CA1416 does not read SkipUnless as a platform check.
            return;
        }

        const string Script =
            "param([string]$Connect, [string]$Bind)\n"
            + "function Get-Outcome([scriptblock]$action) {\n"
            + "    $socket = [Net.Sockets.Socket]::new('Unix', 'Stream', 'Unspecified')\n"
            + "    try { & $action $socket; 'Done' }\n"
            + "    catch {\n"
            + "        $inner = $_.Exception\n"
            + "        while ($inner -and $inner -isnot [Net.Sockets.SocketException]) { $inner = $inner.InnerException }\n"
            + "        if ($inner) { [string]$inner.SocketErrorCode } else { 'Other:' + $_.Exception.GetType().Name }\n"
            + "    }\n"
            + "    finally { $socket.Dispose() }\n"
            + "}\n"
            + "'connect=' + (Get-Outcome { param($s) $s.Connect([Net.Sockets.UnixDomainSocketEndPoint]::new($Connect)) })\n"
            + "'bind=' + (Get-Outcome { param($s) $s.Bind([Net.Sockets.UnixDomainSocketEndPoint]::new($Bind)) })\n";
        bool onActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        string? pwsh = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, "pwsh.exe"))
            .FirstOrDefault(File.Exists);
        if (pwsh is null)
        {
            Assert.False(onActions, "On GitHub Actions this test must run, and pwsh.exe is not on PATH.");
            Assert.Skip("pwsh.exe (PowerShell 7) is not on PATH; Windows PowerShell has no Unix domain socket endpoint.");
        }

        await using Vendor vendor = await Vendor.CreateInAsync(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "license",
            "lease"
        );
        string license = vendor.File("server.elmlic");
        await using ServerProcess server = ServerProcess.Prepare(vendor, "s", "acme-cad", license);
        await server.IssueLicenseAsync(vendor, license);
        await server.StartAsync();
        string script = vendor.File("probe.ps1");
        await File.WriteAllTextAsync(script, Script, TestContext.Current.CancellationToken);
        string ownBind = vendor.File("own.sock");
        string[] Arguments(string bind) => ["-NoProfile", "-NonInteractive", "-File", script, "-Connect", server.AdminSocketPath, "-Bind", bind];

        (int ownCode, string ownOut, string ownError) = await Binaries.RunAsync(pwsh, Arguments(ownBind), vendor.Folder);
        File.Delete(ownBind);
        Assert.True(
            ownCode == 0 && ownOut.Contains("connect=Done", StringComparison.Ordinal),
            $"Step 1, the script as this user (exit {ownCode}): {ownOut}{ownError}"
        );

        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            Assert.False(onActions, "On GitHub Actions this test must run, and the runner's user is not an elevated administrator.");
            Assert.Skip("Making a local user needs an elevated administrator; this process is not one.");
        }

        // 20 characters at most for a local user's name. The password meets the default complexity rule: upper and lower case,
        // digits, and a symbol.
        string user = "elm-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
        string password = "Aa1!" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        string net = Path.Combine(Environment.SystemDirectory, "net.exe");

        // For a password of more than 14 characters net.exe asks whether to go on, and without an answer it ends with
        // "No valid response was provided." (run 43081605974 on windows-latest); /y answers it.
        (int addCode, _, string addError) = await Binaries.RunAsync(net, ["user", user, password, "/add", "/y"], vendor.Folder);
        try
        {
            // The command's arguments hold the password, so only its exit code and its error text go into the message.
            Assert.True(addCode == 0, $"Step 2, net user /add exited with {addCode}: {addError.Trim()}");
            string otherBind = Path.Combine(server.DataDirectory, "o.sock");
            var start = new System.Diagnostics.ProcessStartInfo(pwsh)
            {
                UserName = user,
                PasswordInClearText = password,
                LoadUserProfile = false,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = vendor.Folder,
            };
            foreach (string argument in Arguments(otherBind))
            {
                start.ArgumentList.Add(argument);
            }

            System.Diagnostics.Process? child = null;
            try
            {
                child = System.Diagnostics.Process.Start(start);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Assert.Fail($"Step 3, starting pwsh as the other user: {e.GetType().Name}: {e.Message}");
            }

            Assert.True(child is not null, "Step 3, starting pwsh as the other user: no process.");
            using (child)
            {
                Task<string> output = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                Task<string> error = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                limit.CancelAfter(TimeSpan.FromMinutes(1));
                try
                {
                    await child.WaitForExitAsync(limit.Token);
                }
                catch (OperationCanceledException)
                {
                    child.Kill(entireProcessTree: true);
                    Assert.Fail("Step 4, the script as the other user did not end within a minute.");
                }

                string printed = await output;
                string[] lines = printed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                string? connected = lines.FirstOrDefault(l => l.StartsWith("connect=", StringComparison.Ordinal));
                string? bound = lines.FirstOrDefault(l => l.StartsWith("bind=", StringComparison.Ordinal));
                TestContext.Current.TestOutputHelper?.WriteLine($"Another Windows user: {connected}, {bound}");
                Assert.True(
                    child.ExitCode == 0 && connected is not null && bound is not null,
                    $"Step 4, the script as the other user (exit {child.ExitCode}): {printed}{await error}"
                );
                Assert.True(connected != "connect=Done", "Step 5: the other user connected to admin.sock.");
                Assert.True(bound != "bind=Done", "Step 5: the other user bound a socket file in the data folder.");
                Assert.False(Path.Exists(otherBind));
            }
        }
        finally
        {
            if (addCode == 0)
            {
                (int removed, _, string removeError) = await Binaries.RunAsync(net, ["user", user, "/delete"], vendor.Folder);
                Assert.True(removed == 0, $"Cleanup, net user /delete exited with {removed}: {removeError.Trim()}");
            }
        }
    }

    /// <summary>
    /// On Linux and macOS the lock file of a running server can be deleted, and a second server then locks a new file; the
    /// probe of the socket file still stops it, and the first server goes on answering.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Linux or macOS: Windows refuses to delete the file while the server holds it
    ///   (LockFile_WhileTheServerRuns_CannotBeOpenedDeletedOrCopied in the server's tests).
    /// - The published elm-server for acme-cad, running with a server license; the bytes of its admin.token.
    ///
    /// Steps:
    /// - Delete server.lock in the data folder; run a second server on the same data folder to its end; get the status from
    ///   the first server's socket with the token read from admin.token, and run elm server status.
    ///
    /// Expected:
    /// - The second server exits 1, and the last line of its standard error is "error: Another elm-server is running on the
    ///   data folder '{folder}': its administration socket '{path}' accepts connections.".
    /// - admin.token has the same bytes; the first server answers 200 on its socket with that token, and status exits 0.
    /// </remarks>
    [Fact]
    public async Task Server_LockFileDeletedWhileRunning_SecondStartStopsAtTheSocketProbe()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows refuses to delete the lock file while the server holds it.");
        await using Vendor vendor = await Vendor.CreateAsync("license", "lease");
        string license = vendor.File("server.elmlic");
        await using ServerProcess first = ServerProcess.Prepare(vendor, "s", "acme-cad", license);
        await first.IssueLicenseAsync(vendor, license);
        await first.StartAsync();
        string tokenFile = Path.Combine(first.DataDirectory, "admin.token");
        CancellationToken cancel = TestContext.Current.CancellationToken;
        byte[] token = await File.ReadAllBytesAsync(tokenFile, cancel);
        await using ServerProcess second = first.Twin();

        File.Delete(Path.Combine(first.DataDirectory, "server.lock"));
        (int code, _, string error) = await second.RunToExitAsync();

        Assert.Equal(1, code);
        Assert.Equal(
            $"error: Another elm-server is running on the data folder '{first.DataDirectory}': its administration socket "
                + $"'{first.AdminSocketPath}' accepts connections.",
            LastLine(error)
        );
        Assert.Equal(token, await File.ReadAllBytesAsync(tokenFile, cancel));
        using HttpClient admin = SocketHttp(first.AdminSocketPath);
        using var request = new HttpRequestMessage(HttpMethod.Get, "admin/v1/status");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Base64Url(token));
        using HttpResponseMessage answer = await admin.SendAsync(request, cancel);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        (int status, _, string statusError) = await first.ElmServerAsync(vendor, "status");
        Assert.True(status == 0, statusError);
    }

    // The token as the administration API takes it. On Linux and macOS admin.token holds the token's 32 bytes as they are.
    private static string Base64Url(byte[] token) => Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string LastLine(string text) => text.TrimEnd('\r', '\n').Split('\n')[^1].TrimEnd('\r');

    // An HTTP client whose every connection goes to a Unix domain socket file, whatever the request's host.
    private static HttpClient SocketHttp(string socketPath) =>
        new(
            new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = async (_, cancellationToken) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            }
        )
        {
            BaseAddress = new Uri("http://elm-server.invalid/"),
        };

    // The web host's line for the http listener on 127.0.0.1 (the server's own listener is https).
    [GeneratedRegex(@"Now listening on: (http://127\.0\.0\.1:\d+)")]
    private static partial Regex AddedTcpListener();
}
