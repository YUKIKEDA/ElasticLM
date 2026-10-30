namespace ElasticLM.Service;

/// <summary>
/// What disabling or enabling an administrator did (design §14.5, M5a).
/// </summary>
internal enum AdminChange
{
    /// <summary>The state was changed, or already was the asked one; the audit row is written.</summary>
    Done,

    /// <summary>No administrator has the ID; nothing is written.</summary>
    NotFound,

    /// <summary>
    /// Refused: the caller, the bootstrap owner, or the last enabled owner would be disabled; nothing is written.
    /// </summary>
    Refused,
}

/// <content>
/// Administrators (design §14.5, §14.6, M5a): the sign-in of an OIDC access token, and the list, disabling, and enabling of
/// administrators. Each change is written with its audit row in one transaction.
/// </content>
internal abstract partial class Database
{
    /// <summary>How long a written last login stands before a sign-in writes it again (design §14.6).</summary>
    public static readonly TimeSpan LastLoginInterval = TimeSpan.FromHours(1);

    /// <summary>Returns the administrator of an issuer and subject, or <see langword="null"/>.</summary>
    public abstract Task<AdminRow?> FindAdminAsync(AdminIdentity identity, CancellationToken cancellationToken);

    /// <summary>
    /// Signs in the administrator of an OIDC access token in one transaction (design §14.6, steps 8 and 9): adds the
    /// administrator when the issuer and subject are new (audit <c>admin.create</c>, written only by the request that added
    /// the row), changes a different role only when the row still holds the role read (audit <c>admin.role</c>), and writes
    /// the last login when none is written or the written one is at least <see cref="LastLoginInterval"/> old. A disabled
    /// administrator is read and not changed.
    /// </summary>
    /// <param name="identity">The token's issuer and subject.</param>
    /// <param name="role">The role the token's claims give now.</param>
    /// <param name="ip">The caller's address for the audit rows, or <see langword="null"/>.</param>
    /// <param name="now">The time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The administrator after the sign-in.</returns>
    public abstract Task<AdminRow> SignInAdminAsync(
        AdminIdentity identity,
        string role,
        string? ip,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Removes the role of an administrator whose token's claims map to no role (design §14.6, step 7): sets it to
    /// <c>none</c> when the row still holds the role read, with an <c>admin.role</c> audit row.
    /// </summary>
    public abstract Task RemoveAdminRoleAsync(AdminRow admin, string? ip, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Returns administrators after an ID, in ID order.</summary>
    public abstract Task<IReadOnlyList<AdminRow>> ListAdminsAsync(Guid? after, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Enables or disables an administrator with its audit row, in one transaction that first holds the audit chain's lock,
    /// so two requests that each disable one of the last two owners cannot both pass (design §14.5). Disabling the caller,
    /// the bootstrap owner, or the last enabled owner is refused. An administrator already in the asked state is not changed.
    /// Disabling also removes the administrator's sessions of the admin UI in the same transaction (design §14.5, M5b).
    /// </summary>
    /// <param name="id">The administrator.</param>
    /// <param name="enabled">The state to set.</param>
    /// <param name="callerId">The administrator the caller acts for.</param>
    /// <param name="bootstrap">The bootstrap owner, or <see langword="null"/>.</param>
    /// <param name="audit">Makes the audit row from whether the state was already the asked one and the sessions removed.</param>
    /// <param name="now">The time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public abstract Task<AdminChange> SetAdminEnabledAsync(
        Guid id,
        bool enabled,
        Guid callerId,
        AdminIdentity? bootstrap,
        Func<bool, long, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );
}
