-- design §14.5, §14.6 (M5a): the time of an administrator's last OIDC login, which the 90-day rule of API keys reads.
ALTER TABLE admins ADD COLUMN last_login_at INTEGER;
