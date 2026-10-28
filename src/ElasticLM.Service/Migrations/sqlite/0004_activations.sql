-- design §10.4, §12.2, §12.4 to §12.6, §12.9 (M4b1): activations, trials, and the proofs' jti. A trial license has no key,
-- so key_hash becomes nullable (SQLite's UNIQUE lets several rows be NULL); SQLite changes a column only by building the
-- table again. deactivations counts the deactivations sent by devices (design §9.3 DeactivationLimit).

CREATE TABLE licenses_new (
    id TEXT PRIMARY KEY NOT NULL,
    product_id TEXT NOT NULL REFERENCES products (id),
    policy_id TEXT NOT NULL REFERENCES policies (id),
    name TEXT NOT NULL,
    key_hash BLOB UNIQUE,
    max_activations INTEGER,
    expires_at INTEGER,
    suspended_at INTEGER,
    revoked_at INTEGER,
    features TEXT NOT NULL,
    meta TEXT NOT NULL,
    deactivations INTEGER NOT NULL DEFAULT 0,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);

INSERT INTO licenses_new (
    id, product_id, policy_id, name, key_hash, max_activations, expires_at, suspended_at, revoked_at, features, meta,
    created_at, updated_at
)
SELECT
    id, product_id, policy_id, name, key_hash, max_activations, expires_at, suspended_at, revoked_at, features, meta,
    created_at, updated_at
FROM licenses;

DROP TABLE licenses;
ALTER TABLE licenses_new RENAME TO licenses;
CREATE INDEX licenses_by_product ON licenses (product_id, id);
CREATE INDEX licenses_by_policy ON licenses (policy_id, id);

-- One installation key holds at most one activation of a license. key_x and key_y are the P-256 public key, so a refresh
-- needs no jwk. last_file is the license file last issued and last_proof the jti of the request that got it (design §10.4).
CREATE TABLE activations (
    id TEXT PRIMARY KEY NOT NULL,
    license_id TEXT NOT NULL REFERENCES licenses (id),
    key_id TEXT NOT NULL,
    key_x BLOB NOT NULL,
    key_y BLOB NOT NULL,
    fingerprints TEXT NOT NULL,
    client_host TEXT,
    client_user TEXT,
    app TEXT,
    seq INTEGER NOT NULL,
    last_file BLOB NOT NULL,
    last_proof TEXT NOT NULL,
    blocked_at INTEGER,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    UNIQUE (license_id, key_id)
);

-- A trial is keyed by its license; its fingerprints are unique per product (design §12.9).
CREATE TABLE trials (
    license_id TEXT PRIMARY KEY NOT NULL REFERENCES licenses (id),
    product_id TEXT NOT NULL REFERENCES products (id),
    created_at INTEGER NOT NULL
);

CREATE INDEX trials_by_product ON trials (product_id, license_id);

CREATE TABLE trial_fingerprints (
    product_id TEXT NOT NULL REFERENCES products (id),
    fingerprint TEXT NOT NULL,
    license_id TEXT NOT NULL REFERENCES trials (license_id),
    PRIMARY KEY (product_id, fingerprint)
);

CREATE INDEX trial_fingerprints_by_license ON trial_fingerprints (license_id);

-- design §12.2 rule 5: a jti is kept until its nonce has expired, and refused while kept.
CREATE TABLE proof_jtis (
    jti TEXT PRIMARY KEY NOT NULL,
    expires_at INTEGER NOT NULL
);

CREATE INDEX proof_jtis_by_expiry ON proof_jtis (expires_at);
