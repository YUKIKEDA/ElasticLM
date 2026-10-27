using System.Text.Json;
using ElasticLM.Issuing;
using ElasticLM.Leasing;
using ElasticLM.Licensing;
using Microsoft.Extensions.Logging;

namespace ElasticLM.Server;

/// <summary>
/// The usage log (design §18.1): a signed log, stream <c>usage</c>, with one line per event, signed every 100 lines and
/// every 60 seconds when lines are pending. Refusals are counted and written as one line per feature and code each minute,
/// so a flood of refused or unauthenticated requests cannot grow the log faster than the kinds of refusal. A failure to
/// write is reported in the operational log and does not fail the request that caused it.
/// </summary>
internal sealed class UsageLog : IDisposable
{
    public const string Stream = "usage";

    // The kinds of refusal counted in one minute; beyond it, refusals are counted without their feature.
    private const int MaxDenialKinds = 100;

    private readonly SignedLog _log;
    private readonly ILogger _operational;
    private readonly Lock _denialsLock = new();
    private readonly Dictionary<(string Type, string? Feature, ErrorCode Code), long> _denials = [];

    private UsageLog(SignedLog log, ILogger operational)
    {
        _log = log;
        _operational = operational;
    }

    /// <summary>Gets why the chain could not be continued at the start, or <see langword="null"/>.</summary>
    public string? ResetReason => _log.ResetReason;

    /// <summary>
    /// Opens the log in the folder and with the base name of <c>log.usage</c>.
    /// </summary>
    public static UsageLog Open(string path, SigningKeyAccess key, byte[] licenseFile, byte[] keyJwk, TimeProvider time, ILogger operational)
    {
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full)!;
        string baseName = Path.GetFileNameWithoutExtension(full);
        return new UsageLog(SignedLog.Open(directory, baseName, Stream, key, licenseFile, keyJwk, time), operational);
    }

    /// <summary>Writes an event about a lease.</summary>
    public void Lease(
        string type,
        string? leaseId,
        string? feature,
        int? seats,
        string? keyId,
        string? licenseId,
        Action<Utf8JsonWriter>? more = null
    ) =>
        Write(
            type,
            w =>
            {
                WriteIf(w, "lic", licenseId);
                WriteIf(w, "feat", feature);
                if (seats is int n)
                {
                    w.WriteNumber("n", n);
                }

                WriteIf(w, "key", keyId);
                WriteIf(w, "lease", leaseId);
                more?.Invoke(w);
            }
        );

    /// <summary>Writes an event about a lease known to the engine.</summary>
    public void Lease(string type, LeaseInfo lease, string licenseId) =>
        Lease(type, lease.LeaseId, lease.FeatureId, lease.Seats, lease.KeyId, licenseId);

    /// <summary>Counts a refused lease request (<c>lease.denied</c>) for the line of this minute.</summary>
    /// <param name="feature">The feature, when the request named one the server knows.</param>
    /// <param name="code">The error code of the refusal.</param>
    public void Denied(string? feature, ErrorCode code) => Count("lease.denied", feature, code);

    /// <summary>Counts an administration request without the right token (<c>admin.denied</c>) for the line of this minute.</summary>
    public void AdminDenied() => Count("admin.denied", null, ErrorCode.Unauthorized);

    /// <summary>Writes the refusals counted since the last call: one line per type, feature, and code, with the count.</summary>
    public void WriteDenials()
    {
        KeyValuePair<(string Type, string? Feature, ErrorCode Code), long>[] counted;
        lock (_denialsLock)
        {
            counted = [.. _denials];
            _denials.Clear();
        }

        foreach (((string type, string? feature, ErrorCode code), long count) in counted)
        {
            Write(
                type,
                w =>
                {
                    WriteIf(w, "feat", feature);
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
            ServerLog.UsageLogFailed(_operational, e);
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
            ServerLog.UsageLogFailed(_operational, e);
        }
    }

    /// <summary>Records a new server license; the old key signs the change. When the change cannot be written, the next file
    /// starts with the new license.</summary>
    public void ChangeKey(byte[] licenseFile, byte[] keyJwk, SigningKeyAccess key)
    {
        try
        {
            _log.ChangeKey(licenseFile, keyJwk, key, takeOnFailure: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ServerLog.UsageLogFailed(_operational, e);
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
            ServerLog.UsageLogFailed(_operational, e);
        }
    }

    private void Count(string type, string? feature, ErrorCode code)
    {
        lock (_denialsLock)
        {
            (string, string?, ErrorCode) kind = (type, feature, code);
            if (!_denials.ContainsKey(kind) && _denials.Count >= MaxDenialKinds)
            {
                kind = (type, null, code);
            }

            _denials[kind] = _denials.GetValueOrDefault(kind) + 1;
        }
    }

    private static void WriteIf(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}
