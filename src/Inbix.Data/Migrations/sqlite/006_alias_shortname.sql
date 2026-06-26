-- Optional per-alias display name ("shortname"). When enabled, the mailbox shows as the shortname
-- (e.g. spotify@localhost -> "spotify") in the sidebar and inbox title instead of the full address.

ALTER TABLE aliases ADD COLUMN shortname         TEXT    NOT NULL DEFAULT '';
ALTER TABLE aliases ADD COLUMN shortname_enabled INTEGER NOT NULL DEFAULT 0;
