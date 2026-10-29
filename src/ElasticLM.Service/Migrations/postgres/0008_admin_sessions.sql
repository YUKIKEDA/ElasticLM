-- design §15.3 (M5b): the admin UI's sessions. The cookie's value is kept only as its SHA-256.
CREATE TABLE admin_sessions (
    id uuid PRIMARY KEY,
    admin_id uuid NOT NULL REFERENCES admins (id),
    hash bytea NOT NULL UNIQUE,
    created_at bigint NOT NULL,
    last_seen_at bigint NOT NULL,
    expires_at bigint NOT NULL,
    ip text
);

CREATE INDEX admin_sessions_admin ON admin_sessions (admin_id);

-- design §15.3 (M5b): the Data Protection keys, each encrypted with the key encryption key (AES-256-GCM, the name as
-- associated data). Rows are never removed.
CREATE TABLE dataprotection_keys (
    name text PRIMARY KEY,
    nonce bytea NOT NULL,
    ciphertext bytea NOT NULL,
    tag bytea NOT NULL,
    created_at bigint NOT NULL
);
