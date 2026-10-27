-- design §9.1, §9.3, §14.5 (M4a2): products, their features, and policies. Durations are seconds; null means the design's
-- "none" (no expiry, no limit, no borrowing, no overage).

CREATE TABLE products (
    id TEXT PRIMARY KEY NOT NULL,
    code TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    trial_policy_id TEXT REFERENCES policies (id),
    created_at INTEGER NOT NULL
);

CREATE TABLE features (
    id TEXT PRIMARY KEY NOT NULL,
    product_id TEXT NOT NULL REFERENCES products (id),
    code TEXT NOT NULL,
    name TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    UNIQUE (product_id, code)
);

CREATE TABLE policies (
    id TEXT PRIMARY KEY NOT NULL,
    product_id TEXT NOT NULL REFERENCES products (id),
    name TEXT NOT NULL,
    license_kind TEXT NOT NULL,
    binding TEXT NOT NULL,
    max_activations INTEGER NOT NULL,
    deactivation_limit INTEGER,
    expiration_basis TEXT NOT NULL,
    duration INTEGER,
    token_lifetime INTEGER NOT NULL,
    refresh_window INTEGER NOT NULL,
    grace_period INTEGER NOT NULL,
    fingerprint_matching TEXT NOT NULL,
    clone_strategy TEXT NOT NULL,
    floating TEXT NOT NULL,
    lease_duration INTEGER NOT NULL,
    overage INTEGER,
    borrow_limit INTEGER,
    borrow_return_limit INTEGER NOT NULL,
    client_info_strategy TEXT NOT NULL,
    trial_duration INTEGER NOT NULL,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);

CREATE INDEX policies_by_product ON policies (product_id, id);
