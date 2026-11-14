using ElasticLM.Licensing;

namespace ElasticLM.Issuing.Tests;

/// <summary>
/// A log checked by <see cref="LogVerifier.Verify"/> with the event lines it handed to its callback (design §18.1, V1d23),
/// for the tests that read the lines. Every read checks that the count in the report is the number of lines handed out.
/// </summary>
/// <param name="Report">The verifier's report.</param>
/// <param name="Entries">The event lines, in the order the verifier handed them out.</param>
internal sealed record VerifiedLog(LogReport Report, IReadOnlyList<LogEntry> Entries)
{
    /// <summary>Gets the findings.</summary>
    public IReadOnlyList<LogIssue> Issues => Report.Issues;

    /// <summary>Gets a value indicating whether there is no error.</summary>
    public bool Intact => Report.Intact;

    /// <summary>Gets the key at the end of each file.</summary>
    public IReadOnlyList<LogFileKey> Files => Report.Files;

    /// <summary>Verifies the files and collects the event lines.</summary>
    /// <param name="files">The file paths, oldest first.</param>
    /// <param name="trust">The vendor's trust store.</param>
    /// <param name="productId">The product of a server's log, or <see langword="null"/>.</param>
    /// <param name="stream">The stream name.</param>
    /// <param name="node">The node of a service's log, or <see langword="null"/>.</param>
    /// <returns>The report and the lines.</returns>
    public static VerifiedLog Of(IReadOnlyList<string> files, TrustStore trust, string? productId, string stream, string? node = null)
    {
        var entries = new List<LogEntry>();
        LogReport report = LogVerifier.Verify(files, trust, productId, stream, node, entries.Add);
        Assert.Equal(entries.Count, report.EntryCount);
        return new VerifiedLog(report, entries);
    }
}
