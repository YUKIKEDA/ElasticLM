using Microsoft.Extensions.Logging;

namespace ElasticLM.Server;

/// <summary>
/// The server's log messages. The signed usage log of design §18.1 comes with the next row (M3c); these are operational logs.
/// </summary>
internal static partial class ServerLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "server.started: listening on port {Port} as {PublicUrls}, key {KeyId}, license {LicenseId}, in {Milliseconds} ms; reclaim window until {RecoveryEnds:O}"
    )]
    public static partial void Started(
        ILogger log,
        int port,
        IReadOnlyList<Uri> publicUrls,
        string keyId,
        string licenseId,
        long milliseconds,
        DateTimeOffset recoveryEnds
    );

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "server.stopped: {Count} leases were not returned")]
    public static partial void Stopped(ILogger log, int count);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "The cleanup failed.")]
    public static partial void CleanupFailed(ILogger log, Exception exception);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "lease.acquired: {LeaseId} {Feature} x{Count} for key {KeyId}")]
    public static partial void Acquired(ILogger log, string leaseId, string feature, int count, string keyId);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "lease.released: {LeaseId}")]
    public static partial void Released(ILogger log, string leaseId);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "lease.reclaimed: {LeaseId} for key {KeyId}")]
    public static partial void Reclaimed(ILogger log, string? leaseId, string keyId);

    [LoggerMessage(EventId = 20, Level = LogLevel.Warning, Message = "clone.detected: lease {LeaseId}, key {KeyId}; the lease is stopped")]
    public static partial void CloneDetected(ILogger log, string? leaseId, string keyId);

    [LoggerMessage(EventId = 21, Level = LogLevel.Warning, Message = "clone.flagged: lease {LeaseId}, key {KeyId}")]
    public static partial void CloneFlagged(ILogger log, string? leaseId, string keyId);

    [LoggerMessage(EventId = 30, Level = LogLevel.Error, Message = "The usage log cannot be written.")]
    public static partial void UsageLogFailed(ILogger log, Exception exception);

    [LoggerMessage(
        EventId = 31,
        Level = LogLevel.Warning,
        Message = "The broken borrow record was set aside as {File}; its borrows are not counted until they expire, so seats may be lent beyond the license meanwhile."
    )]
    public static partial void RecordSetAside(ILogger log, string file);

    [LoggerMessage(EventId = 32, Level = LogLevel.Warning, Message = "The {Stream} log could not continue its chain: {Reason}")]
    public static partial void ChainReset(ILogger log, string stream, string reason);

    [LoggerMessage(EventId = 40, Level = LogLevel.Information, Message = "admin.action: {Action} {Target} -> {Result}")]
    public static partial void AdminAction(ILogger log, string action, string? target, string result);

    [LoggerMessage(EventId = 41, Level = LogLevel.Information, Message = "license.reloaded: {LicenseId}, token {TokenId}")]
    public static partial void Reloaded(ILogger log, string licenseId, string tokenId);

    [LoggerMessage(EventId = 42, Level = LogLevel.Warning, Message = "license.refresh_failed: {Code} {Reason}")]
    public static partial void LicenseRefreshFailed(ILogger log, string code, string reason);
}
