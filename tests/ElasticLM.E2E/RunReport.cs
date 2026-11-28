using System.Globalization;
using ElasticLM.E2E;

[assembly: AssemblyFixture(typeof(RunReport))]

namespace ElasticLM.E2E;

/// <summary>
/// Writes the run's real-clock waits, in seconds, to the file <see cref="FileVariable"/> names once every test has run, so
/// that <c>run.ps1</c> can put it in the job summary (design §20.7). A test's measured line goes to the file
/// <see cref="NotesVariable"/> names, which <c>run.ps1</c> prints to the job's log and the job summary.
/// </summary>
public sealed class RunReport : IDisposable
{
    /// <summary>The variable that names the file for the run's real-clock waits.</summary>
    public const string FileVariable = "ELM_E2E_WAITED";

    /// <summary>The variable that names the file for the lines that tests measure (design §13.4, V1d29a).</summary>
    public const string NotesVariable = "ELM_E2E_NOTES";

    private static readonly Lock NotesGate = new();

    /// <summary>
    /// Adds a measured line for <c>run.ps1</c> to print. Without the variable, as in a run outside <c>run.ps1</c>, the line
    /// goes to the test's output only.
    /// </summary>
    /// <param name="line">One line, without a line break.</param>
    public static void Note(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        if (Environment.GetEnvironmentVariable(NotesVariable) is { Length: > 0 } file)
        {
            lock (NotesGate)
            {
                File.AppendAllText(file, line + "\n");
            }
        }
    }

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable(FileVariable) is { Length: > 0 } file)
        {
            File.WriteAllText(file, RealClock.Waited.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture));
        }
    }
}
