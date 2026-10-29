using AuraORM;
using static AuraORM.Sql;

namespace ElasticLM.Service;

/// <content>
/// Administrators (M5a).
/// </content>
internal sealed partial class ServiceStore<TDialect>
{
    public override Task<AdminRow?> FindAdminAsync(AdminIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return _db.From(AdminRow.Table)
            .Where(a => And(Eq(a.Issuer, identity.Issuer), Eq(a.Subject, identity.Subject)))
            .Select(a => a)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public override Task<AdminRow> SignInAdminAsync(
        AdminIdentity identity,
        string role,
        string? ip,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(identity);
        long seconds = now.ToUnixTimeSeconds();
        return _db.InTransactionAsync(
            async tx =>
            {
                // Two first requests at once both try to add the administrator; the second insert does nothing.
                int added = await tx.Insert(AdminRow.Table)
                    .Values(
                        new AdminRow.Insert
                        {
                            Id = Guid.CreateVersion7(now),
                            Issuer = identity.Issuer,
                            Subject = identity.Subject,
                            Role = role,
                            Enabled = true,
                            CreatedAt = seconds,
                            LastLoginAt = seconds,
                        }
                    )
                    .OnConflict(a => [a.Issuer, a.Subject])
                    .DoNothing()
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                AdminRow admin = await tx.From(AdminRow.Table)
                    .Where(a => And(Eq(a.Issuer, identity.Issuer), Eq(a.Subject, identity.Subject)))
                    .Select(a => a)
                    .SingleAsync(cancellationToken)
                    .ConfigureAwait(false);
                string id = admin.Id.ToString("D");
                if (added > 0)
                {
                    string detail = AuditLog.Detail(w =>
                    {
                        w.WriteString("issuer", identity.Issuer);
                        w.WriteString("subject", identity.Subject);
                        w.WriteString("role", role);
                    });
                    var created = new AuditEntry(AuditActors.Oidc, id, AuditActions.AdminCreate, id, "ELM_OK", ip, detail);
                    await AppendAsync(tx, created, now, cancellationToken).ConfigureAwait(false);
                    return admin;
                }

                if (!admin.Enabled)
                {
                    return admin;
                }

                if (admin.Role != role)
                {
                    admin = await ChangeRoleAsync(tx, admin, role, ip, now, cancellationToken).ConfigureAwait(false);
                }

                if (admin.LastLoginAt is not long last || seconds - last >= (long)LastLoginInterval.TotalSeconds)
                {
                    await tx.Update(AdminRow.Table)
                        .Set(a => [Set(a.LastLoginAt, (long?)seconds)])
                        .Where(a => Eq(a.Id, admin.Id))
                        .ExecuteAsync(cancellationToken)
                        .ConfigureAwait(false);
                    admin = admin with { LastLoginAt = seconds };
                }

                return admin;
            },
            cancellationToken
        );
    }

    public override Task RemoveAdminRoleAsync(AdminRow admin, string? ip, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admin);
        return admin.Role == Permissions.RoleNone
            ? Task.CompletedTask
            : _db.InTransactionAsync(tx => ChangeRoleAsync(tx, admin, Permissions.RoleNone, ip, now, cancellationToken), cancellationToken);
    }

    public override async Task<IReadOnlyList<AdminRow>> ListAdminsAsync(Guid? after, int limit, CancellationToken cancellationToken) =>
        await _db.From(AdminRow.Table)
            .Where(a => And([after is Guid id ? Gt(a.Id, id) : null]))
            .Select(a => a)
            .OrderBy(a => [Asc(a.Id)])
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public override Task<AdminChange> SetAdminEnabledAsync(
        Guid id,
        bool enabled,
        Guid callerId,
        AdminIdentity? bootstrap,
        Func<bool, long, AuditEntry> audit,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(audit);
        return _db.InTransactionAsync(
            async tx =>
            {
                // The audit chain's lock first: every change of an administrator's state holds it, so the count of owners
                // below cannot change before this transaction ends.
                _ = await LockStateAsync(tx, cancellationToken).ConfigureAwait(false);
                AdminRow? admin = await tx.From(AdminRow.Table)
                    .Where(a => Eq(a.Id, id))
                    .Select(a => a)
                    .SingleOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (admin is null)
                {
                    return AdminChange.NotFound;
                }

                if (!enabled && admin.Enabled)
                {
                    bool isBootstrap = bootstrap is not null && admin.Issuer == bootstrap.Issuer && admin.Subject == bootstrap.Subject;
                    long otherOwners =
                        admin.Role != Permissions.RoleOwner
                            ? 1
                            : await tx.From(AdminRow.Table)
                                .Where(a => And(Eq(a.Role, Permissions.RoleOwner), Eq(a.Enabled, true), Ne(a.Id, id)))
                                .Select(a => Count())
                                .SingleAsync(cancellationToken)
                                .ConfigureAwait(false);
                    if (admin.Id == callerId || isBootstrap || otherOwners == 0)
                    {
                        return AdminChange.Refused;
                    }
                }

                bool unchanged = admin.Enabled == enabled;
                long sessions = 0;
                if (!unchanged)
                {
                    await tx.Update(AdminRow.Table)
                        .Set(a => [Set(a.Enabled, enabled)])
                        .Where(a => Eq(a.Id, id))
                        .ExecuteAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!enabled)
                    {
                        // design §14.5 (M5b): a disabled administrator's sessions end with it.
                        sessions = await tx.Delete(AdminSessionRow.Table)
                            .Where(s => Eq(s.AdminId, id))
                            .ExecuteAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                await AppendAsync(tx, audit(unchanged, sessions), now, cancellationToken).ConfigureAwait(false);
                return AdminChange.Done;
            },
            cancellationToken
        );
    }

    // Changes the role only when the row still holds the role read, so of two requests at once only one writes admin.role.
    private static async Task<AdminRow> ChangeRoleAsync(
        Transaction<TDialect> tx,
        AdminRow admin,
        string role,
        string? ip,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        int changed = await tx.Update(AdminRow.Table)
            .Set(a => [Set(a.Role, role)])
            .Where(a => And(Eq(a.Id, admin.Id), Eq(a.Role, admin.Role)))
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        if (changed > 0)
        {
            string id = admin.Id.ToString("D");
            string detail = AuditLog.Detail(w =>
            {
                w.WriteString("from", admin.Role);
                w.WriteString("to", role);
            });
            await AppendAsync(tx, new AuditEntry(AuditActors.Oidc, id, AuditActions.AdminRole, id, "ELM_OK", ip, detail), now, cancellationToken)
                .ConfigureAwait(false);
        }

        return admin with
        {
            Role = role,
        };
    }
}
