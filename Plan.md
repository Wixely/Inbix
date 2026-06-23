# .NET Inbound-Only Alias Mail Server Plan

## Project Goal

Build a custom .NET service that receives real email for permanent aliases such as:

- spotify@mydomain.com
- github@mydomain.com
- amazon@mydomain.com

The system should:

- Receive real email directly via SMTP
- Accept only known aliases
- Reject unknown recipients
- Store full email data
- Provide read-only access through a web/API interface
- Never send email
- Support 1,000+ permanent aliases
- Work with Cloudflare DNS in DNS-only mode
- Initially support a MikroTik DDNS-backed home server setup

---

## Working Assumptions

- Domain is owned and managed in Cloudflare.
- Cloudflare proxy will not be used for SMTP.
- `mail.mydomain.com` will be DNS-only.
- The public IP changes rarely.
- No CGNAT.
- Port 25 can be opened.
- This is inbound-only.
- No reply/send functionality is required.
- Unknown aliases should be rejected during SMTP delivery.
- Aliases are permanent because they may be needed for password resets.

---

## Proposed DNS Layout

```text
MX    mydomain.com       -> mail.mydomain.com
CNAME mail.mydomain.com  -> your-mikrotik-ddns-hostname
```

Both records must effectively resolve to your public IP.

Important:

- `mail.mydomain.com` must be DNS-only in Cloudflare.
- Do not use the orange-cloud proxy for mail.
- SMTP delivery requires direct access to port 25.

---

## High-Level Architecture

```text
Internet mail sender
        ↓ SMTP port 25
.NET SMTP Receiver
        ↓
Alias validation
        ↓
Raw message storage
        ↓
Background MIME parser
        ↓
PostgreSQL / file storage
        ↓
ASP.NET Core API/UI
```

---

## Main Components

### 1. SMTP Receiver

A custom .NET service listening on port 25.

Minimum SMTP commands to support:

- `HELO`
- `EHLO`
- `MAIL FROM`
- `RCPT TO`
- `DATA`
- `RSET`
- `NOOP`
- `QUIT`

Optional but recommended later:

- `STARTTLS`
- connection limits
- greylisting
- sender rate limits
- spam filtering hooks

The SMTP receiver should:

- Check recipient aliases during `RCPT TO`
- Reject unknown aliases immediately
- Accept known aliases
- Receive message data
- Store the raw MIME message
- Return `250 OK` only after successful storage

---

### 2. Alias Registry

Stores valid aliases.

Example aliases:

```text
spotify@mydomain.com
github@mydomain.com
amazon@mydomain.com
```

Rules to decide:

- Lowercase only?
- Allow dots?
- Allow hyphens?
- Allow underscores?
- Minimum and maximum alias length?
- Can aliases be disabled?
- Can aliases be deleted?
- Can deleted aliases be reused?

Recommended rule:

- Store aliases lowercase.
- Allow letters, numbers, dots, hyphens, and underscores.
- Do not reuse deleted aliases by default.
- Disabled aliases should reject mail.

---

### 3. Message Storage

Store both:

1. Raw original email
2. Parsed metadata

Raw storage is important because parsing can be improved later.

Recommended storage:

- PostgreSQL for metadata
- File system or object storage for raw MIME and attachments

For a small/medium setup, PostgreSQL alone may be enough.

---

### 4. MIME Parser Worker

Run parsing outside the SMTP transaction.

The SMTP receiver should be fast and simple.

Flow:

```text
SMTP receiver stores raw message
        ↓
Background worker picks up unparsed message
        ↓
Parses headers, body, HTML, text, attachments
        ↓
Updates database
```

Possible .NET library:

- MimeKit

---

### 5. API / Web UI

Suggested API endpoints:

```http
POST   /aliases
GET    /aliases
GET    /aliases/{alias}
PATCH  /aliases/{alias}
GET    /aliases/{alias}/messages
GET    /messages/{messageId}
GET    /messages/{messageId}/raw
GET    /messages/{messageId}/attachments
```

Suggested UI pages:

- Dashboard
- Alias list
- Create alias
- Alias inbox
- Message detail
- Raw source viewer
- Settings
- Audit log

---

## Suggested Database Schema

```sql
CREATE TABLE aliases (
    id BIGSERIAL PRIMARY KEY,
    local_part TEXT NOT NULL UNIQUE,
    domain TEXT NOT NULL,
    enabled BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    disabled_at TIMESTAMPTZ NULL,
    notes TEXT NULL
);

CREATE TABLE smtp_sessions (
    id BIGSERIAL PRIMARY KEY,
    remote_ip INET,
    helo TEXT NULL,
    mail_from TEXT NULL,
    started_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    ended_at TIMESTAMPTZ NULL,
    result TEXT NULL
);

CREATE TABLE messages (
    id BIGSERIAL PRIMARY KEY,
    alias_id BIGINT NOT NULL REFERENCES aliases(id),
    smtp_session_id BIGINT NULL REFERENCES smtp_sessions(id),
    recipient TEXT NOT NULL,
    sender TEXT NULL,
    subject TEXT NULL,
    message_id_header TEXT NULL,
    received_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    raw_storage_path TEXT NULL,
    raw_mime TEXT NULL,
    parsed BOOLEAN NOT NULL DEFAULT false,
    parse_error TEXT NULL
);

CREATE TABLE message_bodies (
    id BIGSERIAL PRIMARY KEY,
    message_id BIGINT NOT NULL REFERENCES messages(id),
    text_body TEXT NULL,
    html_body TEXT NULL
);

CREATE TABLE attachments (
    id BIGSERIAL PRIMARY KEY,
    message_id BIGINT NOT NULL REFERENCES messages(id),
    filename TEXT NULL,
    content_type TEXT NULL,
    size_bytes BIGINT NULL,
    storage_path TEXT NOT NULL,
    sha256 TEXT NULL
);

CREATE TABLE audit_log (
    id BIGSERIAL PRIMARY KEY,
    actor TEXT NULL,
    action TEXT NOT NULL,
    target_type TEXT NOT NULL,
    target_id TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    details JSONB NULL
);
```

---

## SMTP Response Rules

### Valid alias

```text
RCPT TO:<spotify@mydomain.com>
250 2.1.5 Recipient OK
```

### Unknown alias

```text
RCPT TO:<random@mydomain.com>
550 5.1.1 User unknown
```

### Disabled alias

```text
RCPT TO:<oldalias@mydomain.com>
550 5.1.1 User unknown
```

### Message too large

```text
552 5.3.4 Message size exceeds fixed limit
```

### Temporary database failure

```text
451 4.3.0 Temporary local problem
```

Use temporary failures when the sender should retry later.

---

## MVP Scope

The first usable version should include:

- DNS configured
- Port 25 reachable
- .NET SMTP receiver
- Alias validation from PostgreSQL
- Unknown recipient rejection
- Raw MIME message storage
- Basic parser using MimeKit
- Basic API to create/list aliases
- Basic UI to view messages
- Logging
- Backups

Do not include in MVP:

- Sending mail
- IMAP
- POP3
- Complex spam filtering
- Multi-user permissions
- Mobile app
- Full-text search
- Attachment preview
- Auto-alias creation

---

## Testing Plan

### Connectivity tests

- Confirm `mail.mydomain.com` resolves to the public IP.
- Confirm port 25 is reachable externally.
- Confirm MX record points to `mail.mydomain.com`.

### Delivery tests

Send test emails from:

- Gmail
- Outlook
- Proton Mail
- iCloud
- GitHub
- Spotify or another service that sends account emails

### SMTP behavior tests

- Known alias accepted
- Unknown alias rejected
- Disabled alias rejected
- Large message rejected
- DB outage returns temporary failure
- Message is not acknowledged until safely stored

### Reliability tests

- Restart app during delivery
- Restart database
- Simulate internet outage
- Simulate IP change
- Fill disk warning test
- Backup restore test

---

## Operational Risks

### Residential IP reputation

Receiving mail is easier than sending, but some senders may still treat residential IPs suspiciously.

Mitigation:

- Test with major senders early.
- Keep logs.
- Consider fallback VPS later.

### IP changes

DDNS should update quickly, but mail delivery may pause during changes.

Mitigation:

- Use low DNS TTL if possible.
- Monitor public IP.
- Alert when MX target changes.

### Outages

If your home internet or power is down, senders usually retry for some time, but time-sensitive emails may be delayed.

Mitigation:

- UPS for router/server.
- Monitoring.
- Optional backup MX later.

### Data sensitivity

This system may hold password resets and account recovery emails.

Mitigation:

- Strong admin authentication.
- Encrypt backups.
- Restrict network access to the admin UI.
- Keep audit logs.
- Patch regularly.

---

## Future Enhancements

- STARTTLS
- SPF checking
- DKIM verification
- DMARC evaluation
- SpamAssassin or Rspamd integration
- Full-text search
- Attachment scanning
- Per-alias notes/tags
- Browser notifications
- Webhooks
- Backup MX
- Admin 2FA
- Encrypted message storage
- Import/export tools

---

## Open Questions

1. What operating system will host the service?
2. Will the SMTP receiver run directly on port 25 or behind port forwarding?
3. Will raw MIME be stored in PostgreSQL or on disk?
4. Will attachments be stored separately?
5. What is the maximum allowed message size?
6. Will there be one admin user or multiple users?
7. Should messages be deletable?
8. Should deleted messages be soft-deleted?
9. Should aliases be searchable/taggable?
10. Should the system support multiple domains later?
11. Should there be a backup MX?
12. Should the app require HTTPS-only for the UI?
13. Should the admin UI be exposed publicly or VPN-only?
14. Should message bodies be encrypted at rest?
15. What backup schedule is acceptable?

---

## Recommended Build Order

### Phase 1: Proof of SMTP

- Create DNS records.
- Open port 25.
- Build minimal .NET SMTP listener.
- Receive one raw message.
- Save raw message to disk.
- Test with Gmail and Outlook.

### Phase 2: Alias Validation

- Add PostgreSQL.
- Add aliases table.
- Validate `RCPT TO`.
- Reject unknown aliases.
- Add API to create aliases.

### Phase 3: Storage and Parsing

- Store message records.
- Parse headers, subject, sender, body.
- Save attachments.
- Add message list endpoint.

### Phase 4: Web UI

- Alias management page.
- Inbox page.
- Message detail page.
- Raw source view.

### Phase 5: Hardening

- Logging.
- Rate limits.
- Size limits.
- TLS.
- Backups.
- Monitoring.
- Security review.

---

## Strong Recommendation

Build the .NET SMTP receiver as a replaceable component.

Keep your internal interface like this:

```text
InboundMessage {
    Recipient
    Sender
    RemoteIp
    RawMime
    ReceivedAt
}
```

That way, if the custom SMTP receiver becomes painful later, you can replace only the SMTP layer with Postfix while keeping your database, API, parser, and UI unchanged.

Product name: Inbix
Code must be entirely c# projects, asp core .net 10
It should be able to run as a windows service or in a docker, with all important variables configurable.
include a sample docker compose file
include github actions to build and host the dockerfile