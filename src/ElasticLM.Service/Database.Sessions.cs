namespace ElasticLM.Service;

/// <summary>
/// A session of the admin UI with its administrator's role and state, as the session check reads it (design §15.3).
/// </summary>
/// <param name="Id">The session's ID.</param>
/// <param name="AdminId">The administrator.</param>
/// <param name="CreatedAt">When it was made, in UNIX seconds.</param>
/// <param name="LastSeenAt">The last request written, in UNIX seconds.</param>
/// <param name="ExpiresAt">When it ends whatever the use, in UNIX seconds.</param>
/// <param name="Role">The administrator's role now.</param>
/// <param name="Enabled">Whether the administrator is enabled.</param>
internal sealed record SessionHolder(Guid Id, Guid AdminId, long CreatedAt, long LastSeenAt, long ExpiresAt, string Role, bool Enabled);

/// <summary>
/// What making a session did (design §15.3): the new session, the session the browser sent that was removed, and the oldest
/// session removed by the limit per administrator.
/// </summary>
/// <param name="Id">The new session's ID.</param>
/// <param name="Replaced">The removed session the browser sent, or <see langword="null"/>.</param>
/// <param name="Evicted">The session removed by the limit, or <see langword="null"/>.</param>
internal sealed record SessionCreated(Guid Id, Guid? Replaced, Guid? Evicted);

/// <summary>
/// A new session of the admin UI (design §15.3).
/// </summary>
/// <param name="AdminId">The administrator.</param>
/// <param name="Hash">The SHA-256 of the cookie's value.</param>
/// <param name="Presented">The SHA-256 of a well-formed session cookie the browser sent, or <see langword="null"/>.</param>
/// <param name="Ip">The address the sign-in came from, or <see langword="null"/>.</param>
/// <param name="Lifetime">The longest the session lasts (<c>session.maxHours</c>).</param>
/// <param name="Idle">How long the session lasts without a request (<c>session.idleMinutes</c>).</param>
/// <param name="Limit">The most live sessions one administrator keeps.</param>
internal sealed record NewSession(Guid AdminId, byte[] Hash, byte[]? Presented, string? Ip, TimeSpan Lifetime, TimeSpan Idle, int Limit);

/// <content>
/// The admin UI's sessions and the Data Protection keys (design §15.3, M5b). Each change of a session that the design
/// audits is written with its audit row in one transaction.
/// </content>
internal abstract partial class Database
{
    /// <summary>
    /// Makes a session in one transaction (design §15.3, callback step 5): removes the session whose hash the browser sent,
    /// removes the oldest live session of the administrator when it already has the limit, adds the new one, and writes the
    /// audit row.
    /// </summary>
    /// <param name="session">The new session.</param>
    /// <param name="audit">Makes the audit row from what was done.</param>
    /// <param name="now">The time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public abstract Task<SessionCreated> CreateSessionAsync(
        NewSession session,
        Func<SessionCreated, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );

    /// <summary>Returns the session of a cookie's hash with its administrator, or <see langword="null"/>.</summary>
    public abstract Task<SessionHolder?> FindSessionAsync(byte[] hash, CancellationToken cancellationToken);

    /// <summary>Removes a session that has ended, without an audit row (design §15.3, check step 6).</summary>
    public abstract Task RemoveSessionAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Writes a session's last request when the written one is older than <paramref name="before"/>.</summary>
    public abstract Task TouchSessionAsync(Guid id, long now, long before, CancellationToken cancellationToken);

    /// <summary>
    /// Returns live sessions after an ID, in ID order (design §14.5): a session past its end or its idle time is left out.
    /// </summary>
    /// <param name="after">The ID to list after, or <see langword="null"/>.</param>
    /// <param name="adminId">The administrator to list, or <see langword="null"/> for all.</param>
    /// <param name="now">The time, in UNIX seconds.</param>
    /// <param name="idleSeconds">The idle time.</param>
    /// <param name="limit">The most rows.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public abstract Task<IReadOnlyList<AdminSessionRow>> ListSessionsAsync(
        Guid? after,
        Guid? adminId,
        long now,
        long idleSeconds,
        int limit,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Removes a live session with its audit row, in one transaction (design §14.5 <c>session.revoke</c>).
    /// </summary>
    /// <returns>The session's administrator, or <see langword="null"/> when no live session has the ID; nothing is written then.</returns>
    public abstract Task<Guid?> RevokeSessionAsync(
        Guid id,
        long idleSeconds,
        Func<Guid, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Removes the session of a cookie's hash with its audit row, in one transaction (design §15.3 <c>session.logout</c>).
    /// </summary>
    /// <param name="hash">The SHA-256 of the cookie's value.</param>
    /// <param name="audit">Makes the audit row from the session's ID and administrator.</param>
    /// <param name="now">The time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether a session was removed.</returns>
    public abstract Task<bool> LogoutSessionAsync(
        byte[] hash,
        Func<Guid, Guid, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );

    /// <summary>Removes the sessions past their end or their idle time (design §15.3, the hourly sweep).</summary>
    /// <returns>The sessions removed.</returns>
    public abstract Task<long> RemoveOldSessionsAsync(long now, long idleSeconds, CancellationToken cancellationToken);

    /// <summary>Returns every stored Data Protection key, encrypted.</summary>
    public abstract Task<IReadOnlyList<DataProtectionKeyRow>> ListDataProtectionKeysAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Adds Data Protection keys under a database lock, so that one node at a time decides (design §15.3): PostgreSQL takes
    /// an advisory lock for the transaction, SQLite holds the write lock of <c>BEGIN IMMEDIATE</c>. The rows are read again
    /// under the lock and given to <paramref name="decide"/>, which returns the rows to add; they are written in the same
    /// transaction, on the connection that holds the lock.
    /// </summary>
    /// <returns>The rows added.</returns>
    public abstract Task<int> AddDataProtectionKeysAsync(
        Func<IReadOnlyList<DataProtectionKeyRow>, IReadOnlyList<DataProtectionKeyRow>> decide,
        CancellationToken cancellationToken
    );
}
