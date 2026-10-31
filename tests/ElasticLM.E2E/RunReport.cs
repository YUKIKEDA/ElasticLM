using System.Globalization;
using ElasticLM.E2E;

[assembly: AssemblyFixture(typeof(RunReport))]

namespace ElasticLM.E2E;

/// <summary>
/// Writes the run's real-clock waits, in seconds, to the file <see cref="FileVariable"/> names once every test has run, so
/// that <c>run.ps1</c> can put it in the job summary (design §20.7).
/// </summary>
public sealed class RunReport : IDisposable
{
    /// <summary>The variable that names the file for the run's real-clock waits.</summary>
    public const string FileVariable = "ELM_E2E_WAITED";

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable(FileVariable) is { Length: > 0 } file)
        {
            File.WriteAllText(file, RealClock.Waited.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture));
        }
    }
}
