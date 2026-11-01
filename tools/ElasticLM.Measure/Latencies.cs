using System.Text.Json;

namespace ElasticLM.Measure;

/// <summary>
/// The times of one kind of request, and how many failed (design §20.7). The percentile is the value at place
/// <c>ceil(p × n)</c> of the sorted times, counted from 1.
/// </summary>
internal sealed class Latencies
{
    private readonly List<double> _milliseconds = [];
    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Records the time of a request that succeeded.</summary>
    /// <param name="milliseconds">The time, from building the proof to the answer.</param>
    public void Succeeded(double milliseconds)
    {
        lock (_gate)
        {
            _milliseconds.Add(milliseconds);
        }
    }

    /// <summary>Records a request that failed, by its error code or status.</summary>
    /// <param name="code">The error code or the status.</param>
    public void Failed(string code)
    {
        lock (_gate)
        {
            _failures[code] = _failures.GetValueOrDefault(code) + 1;
        }
    }

    /// <summary>Writes the count, the failures, and p50, p99, and the maximum in milliseconds.</summary>
    /// <param name="writer">The writer, inside an object.</param>
    /// <param name="name">The property's name.</param>
    public void Write(Utf8JsonWriter writer, string name)
    {
        lock (_gate)
        {
            double[] sorted = [.. _milliseconds.Order()];
            writer.WriteStartObject(name);
            writer.WriteNumber("count", sorted.Length);
            writer.WriteNumber("failed", _failures.Values.Sum());
            writer.WriteStartObject("failures");
            foreach ((string code, int count) in _failures)
            {
                writer.WriteNumber(code, count);
            }

            writer.WriteEndObject();
            if (sorted.Length > 0)
            {
                writer.WriteNumber("p50Ms", Round(Percentile(sorted, 0.50)));
                writer.WriteNumber("p99Ms", Round(Percentile(sorted, 0.99)));
                writer.WriteNumber("maxMs", Round(sorted[^1]));
            }

            writer.WriteEndObject();
        }
    }

    /// <summary>Returns the value at place <c>ceil(p × n)</c> of sorted values, counted from 1.</summary>
    /// <param name="sorted">The values, sorted.</param>
    /// <param name="p">The fraction, from 0 to 1.</param>
    public static double Percentile(double[] sorted, double p) => sorted[Math.Max(1, (int)Math.Ceiling(p * sorted.Length)) - 1];

    private static double Round(double value) => Math.Round(value, 2);
}
