-- Saved fake identities for online registrations, optionally linked 1:1 to an alias.
-- alias_id is UNIQUE (one identity per alias) and ON DELETE SET NULL so deleting an alias
-- unlinks the identity but keeps it (the registration details remain useful).

CREATE TABLE identities (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    alias_id          INTEGER NULL UNIQUE REFERENCES aliases (id) ON DELETE SET NULL,
    country           TEXT    NOT NULL,            -- 'uk' | 'us' (drives format/display)
    title             TEXT    NULL,                -- Mr/Ms/Mx
    gender            TEXT    NULL,                -- 'male' | 'female'
    first_name        TEXT    NOT NULL,
    last_name         TEXT    NOT NULL,
    username          TEXT    NOT NULL,
    password          TEXT    NOT NULL,
    date_of_birth     TEXT    NOT NULL,            -- 'yyyy-MM-dd'
    email             TEXT    NULL,                -- auto-filled from the linked alias; editable
    phone             TEXT    NULL,
    street            TEXT    NOT NULL,
    city              TEXT    NOT NULL,
    state_county      TEXT    NULL,                -- US state / UK county
    postcode          TEXT    NOT NULL,            -- ZIP / UK postcode
    security_question TEXT    NULL,
    security_answer   TEXT    NULL,
    notes             TEXT    NULL,
    created_at        TEXT    NOT NULL
);

CREATE INDEX ix_identities_alias ON identities (alias_id);
