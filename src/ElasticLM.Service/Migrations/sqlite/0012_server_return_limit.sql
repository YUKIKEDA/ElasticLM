-- design §9.3, §12.7.4 (V1d16): the early returns an on-premises server takes in 30 days, carried into its server license as
-- sbrl. Existing policies get the default of 10,000.
ALTER TABLE policies ADD COLUMN server_borrow_return_limit INTEGER NOT NULL DEFAULT 10000;
