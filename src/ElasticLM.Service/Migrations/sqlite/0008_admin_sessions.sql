-- design §15.3 (M5b): the admin UI's sessions. The cookie's value is kept only as its SHA-256.
CREATE TABLE admin_sessions (
    id TEXT PRIMARY KEY NOT NULL,
    admin_id TEXT NOT NULL REFERENCES admins (id),
    hash BLOB NOT NULL UNIQUE,
    created_at INTEGER NOT NULL,
    last_seen_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL,
    ip TEXT
);

CREATE INDEX admin_sessions_admin ON admin_sessions (admin_id);

-- design §15.3 (M5b): the Data Protection keys, each encrypted with the key encryption key (AES-256-GCM, the name as
-- associated data). Rows are never removed.
CREATE TABLE dataprotection_keys (
    name TEXT PRIMARY KEY NOT NULL,
    nonce BLOB NOT NULL,
    ciphertext BLOB NOT NULL,
    tag BLOB NOT NULL,
    created_at INTEGER NOT NULL
);
