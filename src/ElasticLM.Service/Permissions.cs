namespace ElasticLM.Service;

/// <summary>
/// The permissions of the administration API, in the form <c>resource.action</c>, and the roles that hold them (design
/// §14.6, decision 0.78). Rows after M4a2 add their own permissions.
/// </summary>
internal static class Permissions
{
    public const string ApiKeyRead = "apikey.read";
    public const string ApiKeyCreate = "apikey.create";
    public const string ApiKeyRevoke = "apikey.revoke";
    public const string AuditRead = "audit.read";
    public const string ProductRead = "product.read";
    public const string ProductWrite = "product.write";
    public const string PolicyRead = "policy.read";
    public const string PolicyWrite = "policy.write";
    public const string LicenseRead = "license.read";
    public const string LicenseCreate = "license.create";
    public const string LicenseUpdate = "license.update";
    public const string LicenseRevoke = "license.revoke";
    public const string LicenseFile = "license.file";
    public const string SigningKeyRead = "signingkey.read";
    public const string ActivationRead = "activation.read";
    public const string ActivationDeactivate = "activation.deactivate";
    public const string ActivationOffline = "activation.offline";
    public const string TrialRead = "trial.read";
    public const string TrialDelete = "trial.delete";
    public const string LeaseRead = "lease.read";
    public const string LeaseRelease = "lease.release";
    public const string UsageLogRead = "usagelog.read";
    public const string UsageLogSeal = "usagelog.seal";
    public const string AdminRead = "admin.read";
    public const string AdminUpdate = "admin.update";

    public const string RoleOwner = "owner";
    public const string RoleAdmin = "admin";
    public const string RoleSupport = "support";
    public const string RoleViewer = "viewer";

    /// <summary>The role of an administrator whose token maps to no role (M5a); it holds no permission.</summary>
    public const string RoleNone = "none";

    /// <summary>Gets every permission, in a fixed order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ApiKeyRead,
        ApiKeyCreate,
        ApiKeyRevoke,
        AuditRead,
        ProductRead,
        ProductWrite,
        PolicyRead,
        PolicyWrite,
        LicenseRead,
        LicenseCreate,
        LicenseUpdate,
        LicenseRevoke,
        LicenseFile,
        SigningKeyRead,
        ActivationRead,
        ActivationDeactivate,
        ActivationOffline,
        TrialRead,
        TrialDelete,
        LeaseRead,
        LeaseRelease,
        UsageLogRead,
        UsageLogSeal,
        AdminRead,
        AdminUpdate,
    ];

    /// <summary>
    /// Returns the permissions of a role: <c>owner</c> all, <c>admin</c> all but <c>apikey.*</c> and <c>admin.*</c>, <c>viewer</c>
    /// the <c>*.read</c> ones but <c>admin.read</c>, <c>support</c> those, <c>activation.deactivate</c>, <c>activation.offline</c>,
    /// and <c>lease.release</c> (design §14.6, M5a); an unknown role none.
    /// </summary>
    public static IReadOnlySet<string> OfRole(string role) =>
        role switch
        {
            RoleOwner => All.ToHashSet(StringComparer.Ordinal),
            RoleAdmin => All.Where(p => !p.StartsWith("apikey.", StringComparison.Ordinal) && !OwnerOnly(p)).ToHashSet(StringComparer.Ordinal),
            RoleSupport => All.Where(p => (IsRead(p) && !OwnerOnly(p)) || p is ActivationDeactivate or ActivationOffline or LeaseRelease)
                .ToHashSet(StringComparer.Ordinal),
            RoleViewer => All.Where(p => IsRead(p) && !OwnerOnly(p)).ToHashSet(StringComparer.Ordinal),
            _ => new HashSet<string>(StringComparer.Ordinal),
        };

    /// <summary>Returns whether a name is a permission of this build.</summary>
    public static bool IsKnown(string permission) => All.Contains(permission, StringComparer.Ordinal);

    /// <summary>Joins permissions for storage: known ones only, without repeats, in the order of <see cref="All"/>.</summary>
    public static string Join(IEnumerable<string> permissions)
    {
        var set = permissions.ToHashSet(StringComparer.Ordinal);
        return string.Join(' ', All.Where(set.Contains));
    }

    /// <summary>Splits stored permissions.</summary>
    public static string[] Split(string stored) => stored.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static bool IsRead(string permission) => permission.EndsWith(".read", StringComparison.Ordinal);

    // design §14.6 (M5a): only owners see and disable administrators.
    private static bool OwnerOnly(string permission) => permission.StartsWith("admin.", StringComparison.Ordinal);
}
