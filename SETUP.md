# Inbix — Setup & Deployment Guide

A practical checklist for standing up Inbix so it actually receives mail from the internet.
This is the "what you need from the outside world, and in what order" guide; for the full
configuration reference and architecture, see the [README](README.md).

> **What Inbix is:** an inbound-only alias mailbox. It receives mail over SMTP for addresses you
> define (e.g. `spotify@yourdomain.com`), stores it, and lets you read it. **It never sends.**
> That single fact removes a lot of the usual mail-server setup burden (see
> [What you do *not* need](#what-you-do-not-need)).

---

## 1. External requirements (get these first)

You cannot receive internet mail without all of these. Tick them off before touching the app.

| # | Requirement | Notes |
|---|---|---|
| 1 | **A domain name you control** | With access to its DNS records (Cloudflare, Namecheap, Route 53, etc.). |
| 2 | **A host reachable from the internet** | A VPS, cloud VM, or a home server behind a router you can port-forward. |
| 3 | **A stable public IP** | A static IP, or a **Dynamic DNS** hostname if your IP changes. |
| 4 | **Inbound TCP port 25 open, end-to-end** | The single most common blocker — see [§3](#3-networking--firewall). |
| 5 | **Container or .NET runtime on the host** | Docker (recommended) **or** .NET 10 for a Windows Service / bare run. |
| 6 | *(Optional)* **A TLS certificate (PFX)** | Enables STARTTLS on port 25. Opportunistic; not required to receive. |
| 7 | *(Optional)* **A reverse proxy / VPN** | To expose the web UI safely over HTTPS. |

### What you do *not* need

Because Inbix is inbound-only, you can **skip everything that exists for sending mail**:

- ❌ **SPF, DKIM, DMARC** records — these authorise *outbound* mail. Inbix sends none.
- ❌ A reverse-DNS (PTR) record for deliverability — PTR matters when *you* send. (The Status
  page still reports your PTR for completeness; it's nice-to-have, not required.)
- ❌ An outbound smarthost / relay, an SMTP submission port (587), or sender reputation warm-up.

---

## 2. DNS records

Mail finds you via an **MX** record that points at a **hostname**, which resolves (via an **A**/**AAAA**
record) to your public IP. That's the whole chain.

```
; Tell the world where mail for the domain goes:
MX     yourdomain.com.        10  mail.yourdomain.com.

; Point that mail host at your server's public IP:
A      mail.yourdomain.com.       203.0.113.10
AAAA   mail.yourdomain.com.       2001:db8::10        ; only if you serve mail over IPv6
```

- The number after `MX` (`10`) is **priority** — lowest wins. One record is fine.
- The MX target **must be a hostname, not an IP** (per RFC 5321).
- If you use a **Dynamic DNS** hostname, point the MX at *that* hostname (or CNAME `mail.yourdomain.com`
  to it) so it tracks your changing IP.

> ⚠️ **Cloudflare users:** the mail host's A/AAAA record must be **DNS-only (grey cloud)**, not
> proxied. Cloudflare's proxy does not forward port 25, so a proxied (orange-cloud) record will
> silently swallow all inbound mail.

DNS changes take time to propagate (minutes to hours depending on TTL). Verify with:

```bash
dig +short MX yourdomain.com
dig +short A  mail.yourdomain.com
```

---

## 3. Networking & firewall

Mail servers connect to you on **TCP port 25**. Every hop in between must allow it:

1. **Your ISP / cloud provider.** Many residential ISPs **block inbound port 25**, and some cloud
   providers (AWS, GCP, Azure, Oracle) block it by default and require a support request to open.
   **Verify this first — it's the usual reason "nothing arrives."**
2. **Router / NAT.** On a home network, forward external `25 → <server LAN IP>:25`.
3. **Host firewall.** Allow inbound `25` (e.g. `ufw allow 25/tcp`, or a Windows Firewall rule).

Test that port 25 is reachable **from outside** your network (use a phone on cellular, or an online
port checker). A local test only proves the app is listening, not that the internet can reach it.

The **web UI / API** (port `8080` in Docker, `5080` locally) is a separate concern: **do not expose it
to the internet directly.** Keep it on a LAN/VPN, or put it behind a reverse proxy with HTTPS.

---

## 4. Install & run

Pick one. Full details for each are in the [README](README.md); the essentials:

### Docker (recommended)

```bash
cp docker-compose.yml docker-compose.override.yml   # or edit in place
# set Inbix__Domains__0, Inbix__Admin__Password, Inbix__Smtp__ServerName
docker compose up -d --build
```

All state (database, raw mail, backups, login keys) lives under the single **`/data`** volume.

### Windows Service

```powershell
dotnet publish src/Inbix.Web -c Release -o C:\Inbix
New-Service -Name Inbix -BinaryPathName "C:\Inbix\Inbix.Web.exe" -StartupType Automatic
Start-Service Inbix
```

> On Windows, binding SMTP to port **25** requires the service to run with sufficient privilege and
> no other listener (e.g. IIS SMTP) on 25.

---

## 5. First-run application config

The minimum to be safe and functional. Set via environment variables (`Inbix__Section__Key`) or
`appsettings.json`. See the README's [Configuration table](README.md#configuration) for everything.

1. **Domains** — the domains you accept mail for. Mail to any other domain is rejected.
   ```
   Inbix__Domains__0 = yourdomain.com
   ```
2. **Admin password** — ⚠️ **without this the UI and API are completely open.** Prefer a hash:
   ```bash
   # Docker:
   docker compose run --rm inbix dotnet Inbix.Web.dll hash-password "your-strong-password"
   # Local:
   dotnet run --project src/Inbix.Web -- hash-password "your-strong-password"
   ```
   Then set the printed value as `Inbix__Admin__PasswordHash`.
3. **SMTP server name** — what Inbix announces in its banner; use your mail host:
   ```
   Inbix__Smtp__ServerName = mail.yourdomain.com
   ```
4. **HTTPS** — set `Inbix__RequireHttps = true` if TLS terminates at a reverse proxy (enables
   HSTS, forwarded-proto handling, and a Secure auth cookie).
5. **DataProtection keys** — persist these or **every restart logs everyone out**. The Docker image
   already sets `Inbix__DataProtectionKeysPath=/data/keys`; for a bare run, point it at a stable dir.
6. **Backups** — turn on scheduled DB snapshots:
   ```
   Inbix__Backups__Enabled = true
   ```

### Create your aliases

Once running and logged in, add aliases in the UI (**Aliases → New alias**) or via the API:

```bash
curl -X POST http://localhost:8080/api/aliases \
  -H "X-Api-Key: <key>" -H "Content-Type: application/json" \
  -d '{"localPart":"spotify"}'
```

Or enable the **catch-all** (Aliases page) to capture mail for *any* address on your domain, then
promote the useful ones to real aliases later (the "Create alias" button on a catch-all message does
this and migrates its existing mail across).

---

## 6. STARTTLS (optional but recommended)

Sending servers will use opportunistic TLS if you offer it. Provide a PFX certificate:

```
Inbix__Smtp__CertificatePath     = /data/mail.pfx
Inbix__Smtp__CertificatePassword = <pfx-password>
```

You can convert a Let's Encrypt certificate to PFX:

```bash
openssl pkcs12 -export -out mail.pfx -inkey privkey.pem -in fullchain.pem
```

Use the same hostname as your MX target (`mail.yourdomain.com`). Renewals mean re-exporting the PFX
and restarting Inbix.

---

## 7. Verify it works

1. **Status page** — open `/status` in the UI. It checks, on demand: database & migrations, raw
   storage writable, domains configured, at least one enabled alias, admin password set, HTTPS,
   STARTTLS cert validity, the local SMTP listener, your public IP, **MX records per domain**, whether
   the MX host resolves to your public IP, and reverse DNS. Green here means the basics are right.
2. **Health endpoints** (for monitors): `GET /health` (liveness), `GET /health/ready` (503 until the
   DB is reachable).
3. **Send a real test** — from a machine *outside* your network, or an external mail account:
   ```bash
   swaks --to spotify@yourdomain.com --server mail.yourdomain.com:25 --body "hello inbix"
   ```
   It should appear on the dashboard within a few seconds. (Create the `spotify` alias first, or
   enable the catch-all.)
4. **External MX check** — use a tool like MXToolbox to confirm the world sees your MX and can open
   port 25.

---

## 8. Suggestions & tips

**Security**
- Always set an admin password *before* the host is reachable. A startup warning is logged if none is set.
- Keep the web UI off the public internet — VPN or reverse proxy + HTTPS only. Port 25 is the only
  port that needs to face the world.
- Inbix can hold password-reset and account-recovery mail, plus any saved **Identities** (whose
  passwords are stored in clear text so they can be retrieved). Treat the database and backups as
  secrets: encrypt backups and restrict who can read `/data`.

**Receiving reliably**
- Run on a host with a clean IP. Some senders consult DNS blocklists (DNSBLs); a previously-abused
  residential IP may get mail refused by the *sender*. Check your IP on a DNSBL lookup if mail from
  specific providers never arrives.
- Offer STARTTLS ([§6](#6-starttls-optional-but-recommended)) — a few senders require TLS.
- Keep `MaxMessageSizeBytes` realistic for what you expect; oversized mail is rejected with `552`.

**Operations**
- Back up **both** the database *and* the raw store (`Inbix__Storage__RawPath`). Message rows
  reference raw files on disk; restoring only the DB leaves bodies unreadable. In Docker both live on
  the `/data` volume, so snapshot the whole volume — and copy it **off the host**.
- Use the per-IP rate limit (`Inbix__Smtp__MaxConnectionsPerMinutePerIp`) and session cap
  (`Inbix__Smtp__MaxConcurrentSessions`) if you see abusive connection floods.
- Monitor `/health/ready` from an external uptime checker so you find out before your senders do.
- Watch the **Audit log** page for alias changes and backup events.

**Storage on a network filesystem (NFS/SMB)**
- SQLite is happiest on **local disk**, but Inbix works on NFS/SMB **out of the box**: it defaults to
  exclusive locking, keeping the WAL index in heap memory (no `-shm` file) and routing all access through a
  single connection. That avoids the *"unable to open database file"* (14) / *"locking protocol"* (15)
  errors plain WAL hits on a share, trading write concurrency for compatibility (fine for Inbix's low
  write volume).
- The share must still support file **locking** (NFSv4, or NFSv3 with `lockd`/`statd`; **not** mounted
  `nolock`) and should be a **`hard`** mount. If it still errors, switch off WAL with
  `Inbix__Database__JournalMode=DELETE` (rollback journal, POSIX locks only).
- **Still preferred:** keep the live database on a **local** volume and point backups at the share
  (`Inbix__Backups__Directory`) — the NAS for durability without running the live DB on it. On local disk
  you can opt into more concurrency with `Inbix__Database__PooledConnections=true` (do **not** set that on
  a network filesystem).
- When changing modes, stop the container cleanly; you can delete a stale `inbix.db-shm` once if needed
  (never delete `inbix.db-wal` — it may hold un-checkpointed data).

**Workflow**
- Start with the **catch-all enabled** while you learn what arrives, then create real aliases for the
  senders worth keeping. Deleting an alias moves its mail back to the catch-all (nothing is lost).

---

## 9. Troubleshooting

| Symptom | Likely cause |
|---|---|
| No mail arrives at all | Port 25 blocked by ISP/cloud, or not port-forwarded. Test from outside the network. |
| `dig MX` returns nothing | DNS not propagated yet, or MX record missing/typo'd. |
| Mail bounces with "relay denied" / `550` | Recipient isn't a known enabled alias and the catch-all is off. |
| Senders connect but mail vanishes | MX A-record proxied through Cloudflare — switch it to DNS-only. |
| Logged out on every restart | DataProtection keys not persisted — set `Inbix__DataProtectionKeysPath`. |
| SQLite "unable to open database file" (14) / "locking protocol" (15) | `/data` is on a network filesystem (NFS/SMB) **and** `Inbix__Database__PooledConnections=true` is set (remove it — exclusive locking is the default), or the share has locking disabled (`nolock`/NFSv3 without `lockd`). See [§8 Storage](#8-suggestions--tips). |
| UI/API needs no login | No admin password configured — set `Inbix__Admin__Password`/`PasswordHash`. |
| Status page: "MX does not resolve to this IP" | MX host's A record points elsewhere, or your public IP changed (update DDNS). |

---

See the [README](README.md) for the full configuration reference, API, backups/restore details, and
the security notes.
