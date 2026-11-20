-- design §12.6.1 (V1d26): a retried trial finds the activations of its installation key; the unique index starts with
-- license_id and does not serve it.
CREATE INDEX activations_by_key ON activations (key_id);

-- design §12.7.6 (V1d26): each early return is counted once, also two of one counter in the same second, so the primary key
-- holds the returned lease's ID. SQLite cannot change a primary key, so the table is made again; the rows from before keep
-- an empty lease ID, and they were unique without it.
CREATE TABLE lease_returns_v11 (
    license_id TEXT NOT NULL REFERENCES licenses (id),
    counter TEXT NOT NULL,
    returned_at INTEGER NOT NULL,
    lease_id TEXT NOT NULL,
    PRIMARY KEY (license_id, counter, returned_at, lease_id)
);

INSERT INTO lease_returns_v11 (license_id, counter, returned_at, lease_id)
SELECT license_id, counter, returned_at, '' FROM lease_returns;

DROP TABLE lease_returns;

ALTER TABLE lease_returns_v11 RENAME TO lease_returns;

CREATE INDEX lease_returns_by_time ON lease_returns (returned_at);
