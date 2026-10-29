using AuraORM;

namespace ElasticLM.Service;

// The tables of the migrations under Migrations/ (design §14.2), as AuraORM declares them, so a column that does not exist or
// a value of the wrong type does not compile (decision 0.81). AuraORM does not read the schema at run time; the tests run every
// statement against the migrated schema on SQLite and PostgreSQL.

/// <summary>An administrator (design §14.6).</summary>
[Table("admins", Naming = ColumnNaming.SnakeCase)]
internal sealed partial record AdminRow(
    [PrimaryKey] Guid Id,
    string Issuer,
    string Subject,
    string Role,
    bool Enabled,
    long CreatedAt,
    long? LastLoginAt
);

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

/// <summary>A session of the admin UI: the SHA-256 of its cookie's value, never the value (design §15.3, M5b).</summary>
[Table("admin_sessions", Naming = ColumnNaming.SnakeCase)]
internal sealed partial record AdminSessionRow(
    [PrimaryKey] Guid Id,
    Guid AdminId,
    byte[] Hash,
    long CreatedAt,
    long LastSeenAt,
    long ExpiresAt,
    string? Ip
);

/// <summary>
/// A Data Protection key, encrypted with the key encryption key: AES-256-GCM with the name as associated data (design §15.3).
/// </summary>
[Table("dataprotection_keys", Naming = ColumnNaming.SnakeCase)]
internal sealed partial record DataProtectionKeyRow(
    [PrimaryKey, MaxLength(64)] string Name,
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] Tag,
    long CreatedAt
);

/// <summary>A session joined with its administrator, as the session check reads it (design §15.3).</summary>
[QueryResult]
internal sealed partial record SessionHolderRow(Guid Id, Guid AdminId, long CreatedAt, long LastSeenAt, long ExpiresAt, string Role, bool Enabled);

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
    bool Enabled,
    long AdminCreatedAt,
    long? LastLoginAt
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

/// <summary>
/// A license (design §9.3 to §9.5): its key only as a hash, none for a trial; features and meta as JSON; the deactivations
/// devices have sent (design §12.6).
/// </summary>
[Table("licenses", Naming = ColumnNaming.SnakeCase)]
internal sealed partial record LicenseRow(
    [PrimaryKey] Guid Id,
    Guid ProductId,
    Guid PolicyId,
    string Name,
    byte[]? KeyHash,
    long? MaxActivations,
    long? ExpiresAt,
    long? SuspendedAt,
    long? RevokedAt,
    string Features,
    string Meta,
    long Deactivations,
    long CreatedAt,
    long UpdatedAt
);

/// <summary>
/// An activation (design §10.4, §12.4): an installation key bound to a license, with its P-256 public key, the fingerprints
/// as a JSON array, the last sequence number, the last license file, and the proof's jti that got it.
/// </summary>
[Table("activations", Naming = ColumnNaming.SnakeCase)]
internal sealed partial record ActivationRow(
    [PrimaryKey] Guid Id,
    Guid LicenseId,
    string KeyId,
    byte[] KeyX,
    byte[] KeyY,
    string Fingerprints,
    string? ClientHost,
    string? ClientUser,
    string? App,
    long Seq,
    byte[] LastFile,
    string LastProof,
    long? BlockedAt,
    long CreatedAt,
    long UpdatedAt
);

/// <summary>A trial (design §12.9), keyed by the license it made.</summary>
[Table("trials", Naming = ColumnNaming.SnakeCase)]
internal sealed partial record TrialRow([PrimaryKey] Guid LicenseId, Guid ProductId, long CreatedAt);

/// <summary>A fingerprint that has had a trial of a product (design §12.9).</summary>
[Table("trial_fingerprints", Naming = ColumnNaming.SnakeCase)]
internal sealed partial record TrialFingerprintRow([PrimaryKey] Guid ProductId, [PrimaryKey, MaxLength(32)] string Fingerprint, Guid LicenseId);

/// <summary>A proof's jti, kept until its nonce has expired (design §12.2 rule 5).</summary>
[Table("proof_jtis", Naming = ColumnNaming.SnakeCase)]
internal sealed partial record ProofJtiRow([PrimaryKey, MaxLength(64)] string Jti, long ExpiresAt);

/// <summary>
/// A cloud lease (design §12.7.6): its license, feature, seats, the installation key that holds it, the last token and the
/// proof that got it, and for a borrow the values an early return compares with.
/// </summary>
[Table("leases", Naming = ColumnNaming.SnakeCase)]
internal sealed partial record LeaseRow(
    [PrimaryKey, MaxLength(22)] string Id,
    Guid LicenseId,
    string Feature,
    long Seats,
    string KeyId,
    byte[] KeyX,
    byte[] KeyY,
    long Seq,
    string LastToken,
    string IssuedBy,
    long ExpiresAt,
    bool Borrow,
    string? BorrowFingerprints,
    long? BorrowFeatureSeats,
    long? BorrowLimit,
    bool Stopped,
    string? ClientHost,
    string? ClientUser,
    string? App,
    long CreatedAt
);

/// <summary>An early return of a borrow, counted against a key or a fingerprint for 30 days (design §12.7.4).</summary>
[Table("lease_returns", Naming = ColumnNaming.SnakeCase)]
internal sealed partial record LeaseReturnRow([PrimaryKey] Guid LicenseId, [PrimaryKey, MaxLength(64)] string Counter, long ReturnedAt);

/// <summary>
/// The answer to an activation's last offline request, with the request's key ID, nonce, and the SHA-256 of its signing
/// input, kept until the request expires (design §12.8.4).
/// </summary>
[Table("offline_requests", Naming = ColumnNaming.SnakeCase)]
internal sealed partial record OfflineRequestRow(
    [PrimaryKey] Guid ActivationId,
    string KeyId,
    [MaxLength(22)] string Nonce,
    byte[] RequestHash,
    Guid LicenseId,
    byte[] Response,
    long ExpiresAt
);
