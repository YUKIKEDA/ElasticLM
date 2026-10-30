using Microsoft.Extensions.Logging;

namespace ElasticLM.Service;

/// <summary>
/// The service's operational log messages. API keys and other secrets are never logged; only their IDs.
/// </summary>
internal static partial class ServiceLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "service.started: listening on {Url}, database {Provider}, signing key {KeyId} (certificates until {CertificateEnd}), in {Milliseconds} ms"
    )]
    public static partial void Started(
        ILogger log,
        Uri url,
        DatabaseProvider provider,
        string keyId,
        DateTimeOffset certificateEnd,
        long milliseconds
    );

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "service.stopped")]
    public static partial void Stopped(ILogger log);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Schema versions applied: {Versions}")]
    public static partial void Migrated(ILogger log, string versions);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "The signing key's certificates end at {End}, within 30 days; issue new certificates and restart (design §7.5)."
    )]
    public static partial void CertificateEndsSoon(ILogger log, DateTimeOffset end);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "audit.removed: {Count} rows older than {Before}")]
    public static partial void AuditRemoved(ILogger log, long count, DateTimeOffset before);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "A periodic task failed.")]
    public static partial void PeriodicFailed(ILogger log, Exception exception);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "A request failed on the database.")]
    public static partial void RequestFailed(ILogger log, Exception exception);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "A license file or a lease could not be signed: {Reason}")]
    public static partial void IssueFailed(ILogger log, string reason);

    [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "The usage log cannot be written; the request goes on.")]
    public static partial void UsageLogFailed(ILogger log, Exception exception);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "usage log: {Where}")]
    public static partial void UsageLogAt(ILogger log, string where);

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Information,
        Message = "API key {KeyId} was refused: its administrator {AdminId} has not signed in for the days of apiKeys.inactiveAdminDays."
    )]
    public static partial void KeyInactive(ILogger log, Guid keyId, Guid adminId);

    [LoggerMessage(EventId = 12, Level = LogLevel.Debug, Message = "An access token was refused: {Reason}")]
    public static partial void TokenRefused(ILogger log, string reason);

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "The identity provider's keys cannot be read: {Reason}")]
    public static partial void IdentityProviderUnavailable(ILogger log, string reason);

    [LoggerMessage(
        EventId = 14,
        Level = LogLevel.Warning,
        Message = "The Data Protection key {Name} does not decrypt with the key encryption key; it is skipped."
    )]
    public static partial void DataProtectionKeySkipped(ILogger log, string name);

    [LoggerMessage(EventId = 15, Level = LogLevel.Information, Message = "A Data Protection key was made: {Count}.")]
    public static partial void DataProtectionKeyMade(ILogger log, int count);

    [LoggerMessage(EventId = 16, Level = LogLevel.Information, Message = "An admin UI sign-in was refused: {Reason}")]
    public static partial void SignInRefused(ILogger log, string reason);

    [LoggerMessage(EventId = 17, Level = LogLevel.Warning, Message = "The identity provider cannot be reached for the admin UI's sign-in: {Reason}")]
    public static partial void SignInUnavailable(ILogger log, string reason);

    [LoggerMessage(EventId = 18, Level = LogLevel.Information, Message = "session.removed: {Count} sessions past their end or idle time")]
    public static partial void SessionsRemoved(ILogger log, long count);
}
