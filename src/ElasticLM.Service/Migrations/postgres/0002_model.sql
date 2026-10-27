-- design §9.1, §9.3, §14.5 (M4a2): products, their features, and policies. Durations are seconds; null means the design's
-- "none" (no expiry, no limit, no borrowing, no overage).

CREATE TABLE products (
    id uuid PRIMARY KEY,
    code text NOT NULL UNIQUE,
    name text NOT NULL,
    trial_policy_id uuid,
    created_at bigint NOT NULL
);

CREATE TABLE features (
    id uuid PRIMARY KEY,
    product_id uuid NOT NULL REFERENCES products (id),
    code text NOT NULL,
    name text NOT NULL,
    created_at bigint NOT NULL,
    UNIQUE (product_id, code)
);

CREATE TABLE policies (
    id uuid PRIMARY KEY,
    product_id uuid NOT NULL REFERENCES products (id),
    name text NOT NULL,
    license_kind text NOT NULL,
    binding text NOT NULL,
    max_activations bigint NOT NULL,
    deactivation_limit bigint,
    expiration_basis text NOT NULL,
    duration bigint,
    token_lifetime bigint NOT NULL,
    refresh_window bigint NOT NULL,
    grace_period bigint NOT NULL,
    fingerprint_matching text NOT NULL,
    clone_strategy text NOT NULL,
    floating text NOT NULL,
    lease_duration bigint NOT NULL,
    overage bigint,
    borrow_limit bigint,
    borrow_return_limit bigint NOT NULL,
    client_info_strategy text NOT NULL,
    trial_duration bigint NOT NULL,
    created_at bigint NOT NULL,
    updated_at bigint NOT NULL
);

CREATE INDEX policies_by_product ON policies (product_id, id);

ALTER TABLE products ADD CONSTRAINT products_trial_policy FOREIGN KEY (trial_policy_id) REFERENCES policies (id);
