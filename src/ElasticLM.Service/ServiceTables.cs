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
