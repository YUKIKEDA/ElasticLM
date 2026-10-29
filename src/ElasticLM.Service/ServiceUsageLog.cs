using System.Text.Json;
using ElasticLM.Issuing;
using ElasticLM.Licensing;
using Microsoft.Extensions.Logging;

namespace ElasticLM.Service;

/// <summary>
/// The usage log of <c>elm-service</c> (design §18.1, decision 0.95): one signed log per node, stream <c>usage</c>, files
/// <c>usage-&lt;node&gt;-&lt;time&gt;.jsonl</c>, signed with the service's signing key every 100 lines and every 60 seconds
/// when lines are pending; the first line of each file carries the key's certificates. It records leases and borrows;
/// activations and administration are in the audit log (§18.2). Refusals are counted and written as one line per feature
/// and code each minute. A failure to write is reported in the operational log and does not fail the request.
/// </summary>
internal sealed class ServiceUsageLog : IDisposable
{
    public const string Stream = "usage";

    // The kinds of refusal counted in one minute; beyond it, refusals are counted without their feature (as elm-server does).
    private const int MaxDenialKinds = 100;

    private readonly SignedLog _log;
    private readonly ILogger _operational;
    private readonly Lock _denialsLock = new();
    private readonly Dictionary<(string? Feature, ErrorCode Code), long> _denials = [];

    private ServiceUsageLog(SignedLog log, string directory, string baseName, ILogger operational)
    {
        _log = log;
        Directory = directory;
        BaseName = baseName;
        _operational = operational;
    }

    /// <summary>Gets the folder of the files.</summary>
    public string Directory { get; }

    /// <summary>Gets the files' name prefix, <c>usage-&lt;node&gt;</c>.</summary>
    public string BaseName { get; }

    /// <summary>
    /// Opens this node's log in the folder, continuing its chain from the node's newest file.
    /// </summary>
    /// <exception cref="IOException">The folder or the new file cannot be written.</exception>
    public static ServiceUsageLog Open(
        string directory,
        string node,
        SigningKeyAccess key,
        IReadOnlyList<string> certificates,
        TimeProvider time,
        ILogger operational
    )
    {
        string full = Path.GetFullPath(directory);
        string baseName = $"{Stream}-{node}";
        SignedLog log = SignedLog.Open(full, baseName, Stream, key, LogKeyMaterial.Certificates(certificates), time);
        return new ServiceUsageLog(log, full, baseName, operational);
    }

    /// <summary>Returns the names of this node's files, oldest first.</summary>
    public IReadOnlyList<string> Files() => [.. SignedLog.FindFiles(Directory, BaseName).Select(p => Path.GetFileName(p))];

    /// <summary>Writes an event about a lease: its license, product, feature, seats, installation key, and ID.</summary>
    public void Lease(string type, LeaseRow lease, string productCode, Action<Utf8JsonWriter>? more = null) =>
        Write(
            type,
            w =>
            {
                w.WriteString("lic", lease.LicenseId.ToString("D"));
                w.WriteString("prd", productCode);
                w.WriteString("feat", lease.Feature);
                w.WriteNumber("n", lease.Seats);
                w.WriteString("key", lease.KeyId);
                w.WriteString("lease", lease.Id);
                more?.Invoke(w);
            }
        );

    /// <summary>Counts a refused lease request (<c>lease.denied</c>) for the line of this minute.</summary>
    /// <param name="feature">The feature, when the request named one.</param>
    /// <param name="code">The error code of the refusal.</param>
    public void Denied(string? feature, ErrorCode code)
    {
        lock (_denialsLock)
        {
            (string?, ErrorCode) kind = (feature, code);
            if (!_denials.ContainsKey(kind) && _denials.Count >= MaxDenialKinds)
            {
                kind = (null, code);
            }

            _denials[kind] = _denials.GetValueOrDefault(kind) + 1;
        }
    }

    /// <summary>Writes the refusals counted since the last call: one line per feature and code, with the count.</summary>
    public void WriteDenials()
    {
        KeyValuePair<(string? Feature, ErrorCode Code), long>[] counted;
        lock (_denialsLock)
        {
            counted = [.. _denials];
            _denials.Clear();
        }

        foreach (((string? feature, ErrorCode code), long count) in counted)
        {
            Write(
                "lease.denied",
                w =>
                {
                    if (feature is not null)
                    {
                        w.WriteString("feat", feature);
                    }

                    w.WriteString("code", ErrorCodes.ToWireName(code));
                    w.WriteNumber("count", count);
                }
            );
        }
    }

    /// <summary>Writes any event.</summary>
    public void Write(string type, Action<Utf8JsonWriter> fields)
    {
        try
        {
            _log.Append(type, fields);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            ServiceLog.UsageLogFailed(_operational, e);
        }
    }

    /// <summary>Signs the pending lines (the 60-second timer and the administrator's order).</summary>
    public void Seal()
    {
        try
        {
            _log.Seal();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ServiceLog.UsageLogFailed(_operational, e);
        }
    }

    public void Dispose()
    {
        try
        {
            _log.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ServiceLog.UsageLogFailed(_operational, e);
        }
    }
}
