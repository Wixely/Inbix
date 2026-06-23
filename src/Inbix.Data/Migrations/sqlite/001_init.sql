-- Inbix initial schema (SQLite dialect).
-- Timestamps are stored as ISO-8601 TEXT (see DateTimeOffsetHandler).

CREATE TABLE aliases (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    local_part  TEXT    NOT NULL,
    domain      TEXT    NOT NULL,
    enabled     INTEGER NOT NULL DEFAULT 1,
    created_at  TEXT    NOT NULL,
    disabled_at TEXT    NULL,
    notes       TEXT    NULL,
    UNIQUE (local_part, domain)
);

CREATE TABLE smtp_sessions (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    remote_ip  TEXT NULL,
    helo       TEXT NULL,
    mail_from  TEXT NULL,
    started_at TEXT NOT NULL,
    ended_at   TEXT NULL,
    result     TEXT NULL
);

CREATE TABLE messages (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    alias_id          INTEGER NOT NULL REFERENCES aliases (id),
    smtp_session_id   INTEGER NULL REFERENCES smtp_sessions (id),
    recipient         TEXT    NOT NULL,
    sender            TEXT    NULL,
    subject           TEXT    NULL,
    message_id_header TEXT    NULL,
    received_at       TEXT    NOT NULL,
    size_bytes        INTEGER NOT NULL DEFAULT 0,
    raw_storage_path  TEXT    NULL,
    parsed            INTEGER NOT NULL DEFAULT 0,
    parse_error       TEXT    NULL
);

CREATE INDEX ix_messages_alias ON messages (alias_id, received_at DESC);
CREATE INDEX ix_messages_unparsed ON messages (parsed) WHERE parsed = 0;

CREATE TABLE message_bodies (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id INTEGER NOT NULL REFERENCES messages (id),
    text_body  TEXT NULL,
    html_body  TEXT NULL
);

CREATE INDEX ix_bodies_message ON message_bodies (message_id);

CREATE TABLE attachments (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id   INTEGER NOT NULL REFERENCES messages (id),
    filename     TEXT NULL,
    content_type TEXT NULL,
    size_bytes   INTEGER NULL,
    storage_path TEXT NOT NULL,
    sha256       TEXT NULL
);

CREATE INDEX ix_attachments_message ON attachments (message_id);

CREATE TABLE audit_log (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    actor       TEXT NULL,
    action      TEXT NOT NULL,
    target_type TEXT NOT NULL,
    target_id   TEXT NULL,
    created_at  TEXT NOT NULL,
    details     TEXT NULL
);
