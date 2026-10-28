-- design §12.7.6 (M4d1): cloud leases and the early returns of borrows, counted under the license row's lock (§14.4).
CREATE TABLE leases (
    id text PRIMARY KEY,
    license_id uuid NOT NULL REFERENCES licenses (id),
    feature text NOT NULL,
    seats bigint NOT NULL,
    key_id text NOT NULL,
    key_x bytea NOT NULL,
    key_y bytea NOT NULL,
    seq bigint NOT NULL,
    last_token text NOT NULL,
    issued_by text NOT NULL,
    expires_at bigint NOT NULL,
    borrow boolean NOT NULL,
    borrow_fingerprints text,
    borrow_feature_seats bigint,
    borrow_limit bigint,
    stopped boolean NOT NULL,
    client_host text,
    client_user text,
    app text,
    created_at bigint NOT NULL
);

CREATE INDEX leases_by_feature ON leases (license_id, feature, expires_at);

CREATE INDEX leases_by_expiry ON leases (expires_at);

-- design §12.7.4: an early return counts against the installation key and each fingerprint for 30 days.
CREATE TABLE lease_returns (
    license_id uuid NOT NULL REFERENCES licenses (id),
    counter text NOT NULL,
    returned_at bigint NOT NULL,
    PRIMARY KEY (license_id, counter, returned_at)
);


CREATE INDEX lease_returns_by_time ON lease_returns (returned_at);
