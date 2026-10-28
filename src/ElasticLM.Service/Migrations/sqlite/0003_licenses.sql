-- design §9.3 to §9.5, §14.5 (M4a3): licenses. The key is kept as the SHA-256 of its normal form only. Times are UNIX
-- seconds; expires_at is null for a license without expiry or one that waits for its first activation; suspended_at and
-- revoked_at are the stored states, and Expired is computed from expires_at (decision 0.85). features and meta are JSON.

CREATE TABLE licenses (
    id TEXT PRIMARY KEY NOT NULL,
    product_id TEXT NOT NULL REFERENCES products (id),
    policy_id TEXT NOT NULL REFERENCES policies (id),
    name TEXT NOT NULL,
    key_hash BLOB NOT NULL UNIQUE,
    max_activations INTEGER,
    expires_at INTEGER,
    suspended_at INTEGER,
    revoked_at INTEGER,
    features TEXT NOT NULL,
    meta TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);

CREATE INDEX licenses_by_product ON licenses (product_id, id);
CREATE INDEX licenses_by_policy ON licenses (policy_id, id);
