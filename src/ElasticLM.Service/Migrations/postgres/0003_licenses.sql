-- design §9.3 to §9.5, §14.5 (M4a3): licenses. The key is kept as the SHA-256 of its normal form only. Times are UNIX
-- seconds; expires_at is null for a license without expiry or one that waits for its first activation; suspended_at and
-- revoked_at are the stored states, and Expired is computed from expires_at (decision 0.85). features and meta are JSON.

CREATE TABLE licenses (
    id uuid PRIMARY KEY,
    product_id uuid NOT NULL REFERENCES products (id),
    policy_id uuid NOT NULL REFERENCES policies (id),
    name text NOT NULL,
    key_hash bytea NOT NULL UNIQUE,
    max_activations bigint,
    expires_at bigint,
    suspended_at bigint,
    revoked_at bigint,
    features text NOT NULL,
    meta text NOT NULL,
    created_at bigint NOT NULL,
    updated_at bigint NOT NULL
);

CREATE INDEX licenses_by_product ON licenses (product_id, id);
CREATE INDEX licenses_by_policy ON licenses (policy_id, id);
