-- design §12.7.7 (V1d5b): the release of an acquisition whose answer was lost finds the lease by the key of its proof.
CREATE INDEX leases_by_key ON leases (key_id);
