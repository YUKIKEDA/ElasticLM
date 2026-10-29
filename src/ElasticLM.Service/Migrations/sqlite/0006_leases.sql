-- design §12.7.6 (M4d1): cloud leases and the early returns of borrows, counted under the license row's lock (§14.4).
CREATE TABLE leases (
    id TEXT PRIMARY KEY NOT NULL,
    license_id TEXT NOT NULL REFERENCES licenses (id),
    feature TEXT NOT NULL,
    seats INTEGER NOT NULL,
    key_id TEXT NOT NULL,
    key_x BLOB NOT NULL,
    key_y BLOB NOT NULL,
    seq INTEGER NOT NULL,
    last_token TEXT NOT NULL,
    issued_by TEXT NOT NULL,
    expires_at INTEGER NOT NULL,
    borrow INTEGER NOT NULL,
    borrow_fingerprints TEXT,
    borrow_feature_seats INTEGER,
    borrow_limit INTEGER,
    stopped INTEGER NOT NULL,
    client_host TEXT,
    client_user TEXT,
    app TEXT,
    created_at INTEGER NOT NULL
);

CREATE INDEX leases_by_feature ON leases (license_id, feature, expires_at);

CREATE INDEX leases_by_expiry ON leases (expires_at);

-- design §12.7.4: an early return counts against the installation key and each fingerprint for 30 days.
CREATE TABLE lease_returns (
    license_id TEXT NOT NULL REFERENCES licenses (id),
    counter TEXT NOT NULL,
    returned_at INTEGER NOT NULL,
    PRIMARY KEY (license_id, counter, returned_at)
);


CREATE INDEX lease_returns_by_time ON lease_returns (returned_at);
