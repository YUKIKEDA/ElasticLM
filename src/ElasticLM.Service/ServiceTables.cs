using AuraORM;

namespace ElasticLM.Service;

// The tables of the migrations under Migrations/ (design §14.2), as AuraORM declares them, so a column that does not exist or
// a value of the wrong type does not compile (decision 0.81). AuraORM does not read the schema at run time; the tests run every
// statement against the migrated schema on SQLite and PostgreSQL.

/// <summary>An administrator (design §14.6).</summary>
[Table("admins", Naming = ColumnNaming.SnakeCase)]
internal sealed partial record AdminRow([PrimaryKey] Guid Id, string Issuer, string Subject, string Role, bool Enabled, long CreatedAt);

/// <summary>An API key: its hash, never the key (design §14.6).</summary>
[Table("api_keys", Naming = ColumnNaming.SnakeCase)]
internal sealed partial record ApiKeyRow(
    [PrimaryKey] Guid Id,
    Guid AdminId,
    string Name,
    string Prefix,
    byte[] Hash,
    string Permissions,
    long CreatedAt,
    long ExpiresAt,
    long? RevokedAt
);

/// <summary>A row of the audit log (design §18.2).</summary>
[Table("audit_log", Naming = ColumnNaming.SnakeCase)]
internal sealed partial record AuditLogRow(
    [PrimaryKey] long N,
    long Ts,
    string ActorType,
    string? ActorId,
    string Action,
    string? Target,
    string Result,
    string? Ip,
    string? Detail,
    byte[] Prev,
    byte[] Hash
);

/// <summary>The audit chain's head and anchor: one row, locked by every append (design §18.2).</summary>
[Table("audit_state", Naming = ColumnNaming.SnakeCase)]
internal sealed partial record AuditStateRow([PrimaryKey] int Id, long HeadN, byte[] HeadHash, long AnchorN, byte[] AnchorHash);

/// <summary>An API key joined with its administrator's role and state, as authentication reads it.</summary>
[QueryResult]
internal sealed partial record KeyHolderRow(
    Guid Id,
    Guid AdminId,
    string Name,
    string Prefix,
    string Permissions,
    long CreatedAt,
    long ExpiresAt,
    long? RevokedAt,
    string Role,
    bool Enabled
);

/// <summary>A product (design §9.1): its <c>code</c> is the tokens' <c>prd</c> and never changes.</summary>
[Table("products", Naming = ColumnNaming.SnakeCase)]
internal sealed partial record ProductRow([PrimaryKey] Guid Id, string Code, string Name, Guid? TrialPolicyId, long CreatedAt);

/// <summary>A feature of a product (design §9.1): its <c>code</c> is the tokens' <c>feat[].id</c> and never changes.</summary>
[Table("features", Naming = ColumnNaming.SnakeCase)]
internal sealed partial record FeatureRow([PrimaryKey] Guid Id, Guid ProductId, string Code, string Name, long CreatedAt);

/// <summary>A policy (design §9.3): the choices as their API names, durations in seconds, null for "none".</summary>
[Table("policies", Naming = ColumnNaming.SnakeCase)]
internal sealed partial record PolicyRow(
    [PrimaryKey] Guid Id,
    Guid ProductId,
    string Name,
    string LicenseKind,
    string Binding,
    long MaxActivations,
    long? DeactivationLimit,
    string ExpirationBasis,
    long? Duration,
    long TokenLifetime,
    long RefreshWindow,
    long GracePeriod,
    string FingerprintMatching,
    string CloneStrategy,
    string Floating,
    long LeaseDuration,
    long? Overage,
    long? BorrowLimit,
    long BorrowReturnLimit,
    string ClientInfoStrategy,
    long TrialDuration,
    long CreatedAt,
    long UpdatedAt
);
