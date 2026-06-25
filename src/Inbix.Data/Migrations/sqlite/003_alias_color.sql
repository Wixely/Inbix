-- Per-alias accent colour (hex, e.g. #8b7cf6). Surfaced in the sidebar inbox list and the dashboard
-- mail chips. Defaults to the violet theme accent so existing aliases keep the current look.

ALTER TABLE aliases ADD COLUMN color TEXT NOT NULL DEFAULT '#8b7cf6';
