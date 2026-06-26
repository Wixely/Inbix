-- Allow one identity to be linked to many aliases. The link moves from identities.alias_id (1:1)
-- to aliases.identity_id: each alias points at 0-or-1 identity, and an identity may be reused by
-- many aliases. ON DELETE SET NULL means deleting an identity unlinks its aliases (they survive).

-- Preserve existing 1:1 links before we drop identities.alias_id.
CREATE TABLE _identity_link_map AS
    SELECT id AS identity_id, alias_id FROM identities WHERE alias_id IS NOT NULL;

-- SQLite can't DROP a UNIQUE/indexed/foreign-key column in place, so rebuild identities without alias_id.
CREATE TABLE identities_new (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    country           TEXT    NOT NULL,
    title             TEXT    NULL,
    gender            TEXT    NULL,
    first_name        TEXT    NOT NULL,
    last_name         TEXT    NOT NULL,
    username          TEXT    NOT NULL,
    password          TEXT    NOT NULL,
    date_of_birth     TEXT    NOT NULL,
    email             TEXT    NULL,
    phone             TEXT    NULL,
    street            TEXT    NOT NULL,
    city              TEXT    NOT NULL,
    state_county      TEXT    NULL,
    postcode          TEXT    NOT NULL,
    security_question TEXT    NULL,
    security_answer   TEXT    NULL,
    notes             TEXT    NULL,
    created_at        TEXT    NOT NULL
);

INSERT INTO identities_new
    (id, country, title, gender, first_name, last_name, username, password, date_of_birth,
     email, phone, street, city, state_county, postcode, security_question, security_answer, notes, created_at)
SELECT
     id, country, title, gender, first_name, last_name, username, password, date_of_birth,
     email, phone, street, city, state_county, postcode, security_question, security_answer, notes, created_at
FROM identities;

DROP TABLE identities;                              -- no inbound FKs yet (aliases.identity_id not added)
ALTER TABLE identities_new RENAME TO identities;

-- New link side: an alias references at most one identity; an identity may be referenced by many aliases.
ALTER TABLE aliases ADD COLUMN identity_id INTEGER NULL REFERENCES identities (id) ON DELETE SET NULL;
UPDATE aliases SET identity_id = (
    SELECT m.identity_id FROM _identity_link_map m WHERE m.alias_id = aliases.id
);
CREATE INDEX ix_aliases_identity ON aliases (identity_id);

DROP TABLE _identity_link_map;
