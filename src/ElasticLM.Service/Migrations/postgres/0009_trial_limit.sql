-- design §9.3, §12.9 (V1d17): the trials a product may make in 24 hours, held by its trial policy. Existing policies get the
-- default of 1,000. The index finds a product's trials of the last 24 hours without reading its older ones.
ALTER TABLE policies ADD COLUMN trial_daily_limit bigint NOT NULL DEFAULT 1000;

CREATE INDEX trials_by_product_time ON trials (product_id, created_at);
