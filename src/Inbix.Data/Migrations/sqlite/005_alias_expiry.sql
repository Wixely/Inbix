-- Per-mailbox message expiry (retention). Every alias and the catch-all can auto-delete old mail.
-- Disabled by default; 60 days by default. Expiry is measured from a message's last state change
-- (junk/unjunk/sweep/unsweep) or, if it was never moved, its received date.

ALTER TABLE aliases ADD COLUMN expiry_enabled INTEGER NOT NULL DEFAULT 0;
ALTER TABLE aliases ADD COLUMN expiry_days    INTEGER NOT NULL DEFAULT 60;

-- Last time the message's junk state changed (moved to/from junk, swept/unswept). NULL = never moved,
-- in which case retention counts from received_at.
ALTER TABLE messages ADD COLUMN state_changed_at TEXT NULL;
