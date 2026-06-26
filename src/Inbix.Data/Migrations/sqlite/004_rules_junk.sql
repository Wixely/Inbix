-- Rules (blacklist) + hidden Junk inbox.
--
-- Blacklist rules match incoming mail on the sender or recipient, by literal string or regex, and
-- choose an action: reject (550 at RCPT), discard (accept then drop), or junk (file into the hidden
-- Junk inbox tagged with the matching rule). Junk membership is a flag on the message row, so a
-- message keeps its home alias and un-junking is a simple flag clear.

CREATE TABLE blacklist_rules (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT    NULL,
    target      TEXT    NOT NULL,                 -- 'sender' | 'recipient'
    match_type  TEXT    NOT NULL,                 -- 'literal' | 'regex'
    pattern     TEXT    NOT NULL,
    action      TEXT    NOT NULL DEFAULT 'junk',  -- 'reject' | 'discard' | 'junk'
    enabled     INTEGER NOT NULL DEFAULT 1,
    created_at  TEXT    NOT NULL
);

-- Junk flags on messages. Junk membership = junked_at IS NOT NULL. junk_manual=1 marks a manual
-- junk/unjunk that locks the message so rule sweeps/unsweeps skip it.
ALTER TABLE messages ADD COLUMN junked_at    TEXT    NULL;
ALTER TABLE messages ADD COLUMN junk_rule_id INTEGER NULL REFERENCES blacklist_rules (id);
ALTER TABLE messages ADD COLUMN junk_manual  INTEGER NOT NULL DEFAULT 0;

CREATE INDEX ix_messages_junk ON messages (junked_at);

-- Small key/value store for app settings (first use: Junk-inbox sidebar visibility).
CREATE TABLE settings (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
