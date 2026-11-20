-- design §12.6.1 (V1d26): a retried trial finds the activations of its installation key; the unique index starts with
-- license_id and does not serve it.
CREATE INDEX activations_by_key ON activations (key_id);

-- design §12.7.6 (V1d26): each early return is counted once, also two of one counter in the same second, so the primary key
-- holds the returned lease's ID. The rows from before keep an empty lease ID, and they were unique without it.
ALTER TABLE lease_returns ADD COLUMN lease_id text NOT NULL DEFAULT '';

ALTER TABLE lease_returns ALTER COLUMN lease_id DROP DEFAULT;

ALTER TABLE lease_returns DROP CONSTRAINT lease_returns_pkey;

ALTER TABLE lease_returns ADD PRIMARY KEY (license_id, counter, returned_at, lease_id);
