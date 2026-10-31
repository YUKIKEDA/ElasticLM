using System.Diagnostics;
using System.Text;

namespace ElasticLM.E2E;

/// <summary>
/// The published executables the tests run (design §20.7): under the folder named by <see cref="FolderVariable"/>, the
/// <c>-OutDir</c> of <c>eng/release/Publish-Rid.ps1</c>, which <c>tests/ElasticLM.E2E/run.ps1</c> and the CI's <c>aot</c>
/// job set.
/// </summary>
internal static class Binaries
{
    /// <summary>The variable that names the folder of the published elm, elm-server, and elm-service.</summary>
    public const string FolderVariable = "ELM_E2E_BIN";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(1);

    /// <summary>Gets the published elm.</summary>
    public static string Elm => Find("elm");

    /// <summary>Gets the published elm-server.</summary>
    public static string Server => Find("elm-server");

    /// <summary>Gets the published elm-service.</summary>
    public static string Service => Find("elm-service");

    /// <summary>
    /// Runs one of the executables to its end and returns its exit code and output. The environment holds only what
    /// <paramref name="environment"/> adds to this process's own.
    /// </summary>
    /// <param name="executable">The executable.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="workingDirectory">The folder it runs in.</param>
    /// <param name="environment">Variables to set for it; a <see langword="null"/> value removes one.</param>
    public static async Task<(int Code, string Out, string Error)> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null
    )
    {
        using Process process = Start(executable, arguments, workingDirectory, environment);
        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await output, await error);
    }

    /// <summary>Starts one of the executables with its output read into the returned log.</summary>
    /// <param name="executable">The executable.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="workingDirectory">The folder it runs in.</param>
    /// <param name="environment">Variables to set for it; a <see langword="null"/> value removes one.</param>
    /// <param name="log">The process's standard output and error, as they come.</param>
    public static Process StartLogged(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        StringBuilder log
    )
    {
        Process process = Start(executable, arguments, workingDirectory, environment);
        process.OutputDataReceived += (_, e) => Append(log, e.Data);
        process.ErrorDataReceived += (_, e) => Append(log, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static void Append(StringBuilder log, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (log)
        {
            log.AppendLine(line);
        }
    }

    private static Process Start(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment
    )
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach ((string name, string? value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        return Process.Start(start) ?? throw new InvalidOperationException($"{executable} did not start.");
    }

    private static string Find(string name)
    {
        string folder =
            Environment.GetEnvironmentVariable(FolderVariable)
            ?? throw new InvalidOperationException($"Set {FolderVariable} to the folder of the published executables (run.ps1 does).");

        // eng/release/Publish-Rid.ps1 publishes each executable into a folder of its own name under its -OutDir.
        string path = Path.Combine(folder, name, OperatingSystem.IsWindows() ? name + ".exe" : name);
        return File.Exists(path) ? path : throw new InvalidOperationException($"{path} does not exist.");
    }
}
