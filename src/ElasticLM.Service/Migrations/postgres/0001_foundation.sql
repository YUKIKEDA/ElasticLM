-- design §14.2, §14.6, §18.2 (M4a1). Times are UNIX seconds; IDs are UUID version 7 (decision 0.67).

CREATE TABLE admins (
    id uuid PRIMARY KEY,
    issuer text NOT NULL,
    subject text NOT NULL,
    role text NOT NULL,
    enabled boolean NOT NULL,
    created_at bigint NOT NULL,
    UNIQUE (issuer, subject)
);

CREATE TABLE api_keys (
    id uuid PRIMARY KEY,
    admin_id uuid NOT NULL REFERENCES admins (id),
    name text NOT NULL,
    prefix text NOT NULL,
    hash bytea NOT NULL UNIQUE,
    permissions text NOT NULL,
    created_at bigint NOT NULL,
    expires_at bigint NOT NULL,
    revoked_at bigint
);

CREATE TABLE audit_log (
    n bigint PRIMARY KEY,
    ts bigint NOT NULL,
    actor_type text NOT NULL,
    actor_id text,
    action text NOT NULL,
    target text,
    result text NOT NULL,
    ip text,
    detail text,
    prev bytea NOT NULL,
    hash bytea NOT NULL
);

-- The chain's head (the last row) and anchor (the last row removed by retention). Appends lock this one row.
CREATE TABLE audit_state (
    id integer PRIMARY KEY CHECK (id = 1),
    head_n bigint NOT NULL,
    head_hash bytea NOT NULL,
    anchor_n bigint NOT NULL,
    anchor_hash bytea NOT NULL
);

INSERT INTO audit_state (id, head_n, head_hash, anchor_n, anchor_hash)
VALUES (1, 0, decode(repeat('00', 32), 'hex'), 0, decode(repeat('00', 32), 'hex'));
