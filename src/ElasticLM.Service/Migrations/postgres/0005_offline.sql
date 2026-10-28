-- design §12.8.4 (M4c1): the answer to the last offline request of each activation, kept until the request expires, so the
-- same request file gets the same answer and another request with its key and nonce is refused. One row an activation, so a
-- device cannot grow the table by sending new requests.
CREATE TABLE offline_requests (
    activation_id uuid PRIMARY KEY,
    key_id text NOT NULL,
    nonce text NOT NULL,
    request_hash bytea NOT NULL,
    license_id uuid NOT NULL REFERENCES licenses (id),
    response bytea NOT NULL,
    expires_at bigint NOT NULL
);

CREATE INDEX offline_requests_by_request ON offline_requests (key_id, nonce);

CREATE INDEX offline_requests_by_expiry ON offline_requests (expires_at);
