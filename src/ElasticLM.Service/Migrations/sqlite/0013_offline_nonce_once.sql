-- design §12.8.4 step 11 (V1d29b): one row a key and nonce, also for two requests for different licenses at once, whose
-- license locks do not meet. Rows from before that share a pair (only PostgreSQL could make them) keep the lowest
-- activation_id; the request file of a removed row is then refused as another request with the nonce.
DELETE FROM offline_requests
WHERE EXISTS (
    SELECT 1 FROM offline_requests AS other
    WHERE other.key_id = offline_requests.key_id AND other.nonce = offline_requests.nonce
        AND other.activation_id < offline_requests.activation_id
);

DROP INDEX offline_requests_by_request;

CREATE UNIQUE INDEX offline_requests_by_request ON offline_requests (key_id, nonce);
