-- design §14.2, §14.6, §18.2 (M4a1). Times are UNIX seconds; IDs are UUID version 7 as text (decision 0.67).

CREATE TABLE admins (
    id TEXT PRIMARY KEY NOT NULL,
    issuer TEXT NOT NULL,
    subject TEXT NOT NULL,
    role TEXT NOT NULL,
    enabled INTEGER NOT NULL,
    created_at INTEGER NOT NULL,
    UNIQUE (issuer, subject)
);

CREATE TABLE api_keys (
    id TEXT PRIMARY KEY NOT NULL,
    admin_id TEXT NOT NULL REFERENCES admins (id),
    name TEXT NOT NULL,
    prefix TEXT NOT NULL,
    hash BLOB NOT NULL UNIQUE,
    permissions TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL,
    revoked_at INTEGER
);

CREATE TABLE audit_log (
    n INTEGER PRIMARY KEY NOT NULL,
    ts INTEGER NOT NULL,
    actor_type TEXT NOT NULL,
    actor_id TEXT,
    action TEXT NOT NULL,
    target TEXT,
    result TEXT NOT NULL,
    ip TEXT,
    detail TEXT,
    prev BLOB NOT NULL,
    hash BLOB NOT NULL
);

-- The chain's head (the last row) and anchor (the last row removed by retention). Appends lock this one row.
CREATE TABLE audit_state (
    id INTEGER PRIMARY KEY NOT NULL CHECK (id = 1),
    head_n INTEGER NOT NULL,
    head_hash BLOB NOT NULL,
    anchor_n INTEGER NOT NULL,
    anchor_hash BLOB NOT NULL
);

INSERT INTO audit_state (id, head_n, head_hash, anchor_n, anchor_hash)
VALUES (1, 0, zeroblob(32), 0, zeroblob(32));
