-- design §10.4, §12.2, §12.4 to §12.6, §12.9 (M4b1): activations, trials, and the proofs' jti. A trial license has no key,
-- so key_hash becomes nullable (a UNIQUE constraint lets several rows be NULL). deactivations counts the deactivations
-- sent by devices (design §9.3 DeactivationLimit).

ALTER TABLE licenses ALTER COLUMN key_hash DROP NOT NULL;
ALTER TABLE licenses ADD COLUMN deactivations bigint NOT NULL DEFAULT 0;

-- One installation key holds at most one activation of a license. key_x and key_y are the P-256 public key, so a refresh
-- needs no jwk. last_file is the license file last issued and last_proof the jti of the request that got it (design §10.4).
CREATE TABLE activations (
    id uuid PRIMARY KEY,
    license_id uuid NOT NULL REFERENCES licenses (id),
    key_id text NOT NULL,
    key_x bytea NOT NULL,
    key_y bytea NOT NULL,
    fingerprints text NOT NULL,
    client_host text,
    client_user text,
    app text,
    seq bigint NOT NULL,
    last_file bytea NOT NULL,
    last_proof text NOT NULL,
    blocked_at bigint,
    created_at bigint NOT NULL,
    updated_at bigint NOT NULL,
    UNIQUE (license_id, key_id)
);

-- A trial is keyed by its license; its fingerprints are unique per product (design §12.9).
CREATE TABLE trials (
    license_id uuid PRIMARY KEY REFERENCES licenses (id),
    product_id uuid NOT NULL REFERENCES products (id),
    created_at bigint NOT NULL
);

CREATE INDEX trials_by_product ON trials (product_id, license_id);

CREATE TABLE trial_fingerprints (
    product_id uuid NOT NULL REFERENCES products (id),
    fingerprint text NOT NULL,
    license_id uuid NOT NULL REFERENCES trials (license_id),
    PRIMARY KEY (product_id, fingerprint)
);

CREATE INDEX trial_fingerprints_by_license ON trial_fingerprints (license_id);

-- design §12.2 rule 5: a jti is kept until its nonce has expired, and refused while kept.
CREATE TABLE proof_jtis (
    jti text PRIMARY KEY,
    expires_at bigint NOT NULL
);

CREATE INDEX proof_jtis_by_expiry ON proof_jtis (expires_at);
