-- Catch-all support: a single permanent alias that, when enabled, accepts and stores mail for any
-- address on an accepted domain that does not match a specific alias. Seeded disabled.

ALTER TABLE aliases ADD COLUMN is_catch_all INTEGER NOT NULL DEFAULT 0;

INSERT INTO aliases (local_part, domain, enabled, created_at, notes, is_catch_all)
VALUES ('*', '*', 0, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'),
        'Catch-all: stores mail for any address on accepted domains.', 1);
