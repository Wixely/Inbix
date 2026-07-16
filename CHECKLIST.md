# Inbix — Setup Checklist

A tickable, start-to-finish checklist for getting Inbix receiving real mail. Work top to bottom.
For the detailed *why* behind each step, see the [Setup & Deployment Guide](SETUP.md); for every
config key, the [README Configuration table](README.md#configuration).

> **The one fact that makes this easy:** Inbix is **inbound-only — it never sends.** Everything that
> makes running a mail server hard exists to make *outbound* mail deliverable. You skip all of it.
> See [Why you can skip DKIM / SPF / DMARC (and why a dynamic IP is fine)](#why-you-can-skip-dkim--spf--dmarc).

---

## 1. Get a domain

- [ ] **Buy a *brand-new, dedicated* domain** just for Inbix (see the warning below for why).
      **Cloudflare Registrar is one of the cheapest options** (it sells domains at cost, no markup),
      and you can always **transfer the domain away from Cloudflare later** if you want to.
- [ ] Make sure you can **edit its DNS records**. Using **Cloudflare** for DNS is recommended — it
      supports the CNAME trick below and is free.
- [ ] Decide the mail hostname you'll use, e.g. `mail.yourdomain.com`.

> ⚠️ **Use a separate domain — don't reuse one that also fronts a Cloudflare-proxied site.**
> Mail can't be proxied (Cloudflare doesn't forward port 25), so your mail host **must** be a
> **DNS-only (grey-cloud)** record that publishes your server's **real public IP**. If that same IP
> also hosts a website you protect behind Cloudflare's proxy (orange cloud), the grey-cloud mail
> record **leaks the origin IP** — anyone can look up `mail.yourdomain.com`, learn the IP, and bypass
> Cloudflare's proxy/WAF/DDoS protection to hit the origin directly. A fresh, dedicated domain (ideally
> on a host/IP that runs nothing you're trying to hide) keeps Inbix's exposed IP unlinked from
> anything you're protecting.

## 2. Get a host the internet can reach

- [ ] Have a machine to run Inbix on: a VPS/cloud VM, or a **home server** behind a router you control.
- [ ] Note its **public IP**. Static is simplest; a **changing (dynamic) IP is fine** — see step 4.

## 3. Open inbound port 25 (the usual blocker)

- [ ] Confirm your **ISP / cloud provider allows inbound TCP port 25.** Many residential ISPs and
      cloud providers (AWS, GCP, Azure, Oracle) **block it by default** — check first, this is the
      #1 reason "nothing arrives."
- [ ] On a home network, **port-forward** external `25 → <server LAN IP>:25` on your router.
- [ ] Allow inbound `25` in the **host firewall** (`ufw allow 25/tcp`, or a Windows Firewall rule).
- [ ] **Test from outside** your network (phone on cellular, or an online port checker). A local test
      only proves the app is listening — not that the world can reach it.

## 4. Set up DNS records

The chain is: **MX → a hostname → an A/AAAA record → your IP.** That's all mail needs to find you.

- [ ] **MX** record: `yourdomain.com  →  mail.yourdomain.com` (priority `10`). One is enough.
      The MX target must be a **hostname, not an IP** (RFC 5321).
- [ ] **A** (and **AAAA** if you use IPv6) record: `mail.yourdomain.com → your public IP`.
- [ ] **Cloudflare users:** set the `mail.yourdomain.com` record to **DNS-only (grey cloud)**, *not*
      proxied. Cloudflare's proxy does not forward port 25 — a proxied (orange-cloud) record silently
      swallows all inbound mail. Remember this record **exposes your real IP** (see [step 1](#1-get-a-domain)):
      keep this on a dedicated domain/host, not next to a proxied site you're trying to shield.
- [ ] Verify propagation (can take minutes to hours):
      ```bash
      dig +short MX yourdomain.com
      dig +short A  mail.yourdomain.com
      ```

### If your public IP changes (dynamic IP)

A home connection whose IP changes still works — just keep DNS pointed at the current IP with
**Dynamic DNS (DDNS)**:

- [ ] Sign up for a **DDNS service** (e.g. your router's built-in one, No-IP, DuckDNS, Cloudflare's
      API via a DDNS updater). You get a hostname like `yourhome.duckdns.org` that auto-updates to
      your current IP.
- [ ] Point Inbix's mail host at that DDNS hostname. An MX record itself can't be a CNAME (RFC), so
      **CNAME the MX *target*** instead:
      ```
      MX     yourdomain.com        ->  mail.yourdomain.com   (priority 10)
      CNAME  mail.yourdomain.com   ->  yourhome.duckdns.org   (DNS-only / grey cloud)
      ```
      Now whenever your IP changes, the DDNS updater moves `yourhome.duckdns.org`, and
      `mail.yourdomain.com` follows automatically. Cloudflare handles this CNAME fine.

> ✅ **This is proven and tested to work.** A residential connection with a dynamic IP happily
> receives mail this way — *because Inbix never sends* (next section).

## 5. Install & run Inbix

**Easiest: use the prebuilt image** published to GitHub Container Registry by the release pipeline —
`ghcr.io/wixely/inbix:latest` (also tagged per release, e.g. `:1.11.1`). No local build needed.

> 🔐 **The image is behind GitHub authentication.** Log in to GHCR first with a **GitHub Personal
> Access Token** that has the **`read:packages`** scope:
> ```bash
> echo <YOUR_PAT> | docker login ghcr.io -u <your-github-username> --password-stdin
> ```

- [ ] **Authenticate** to `ghcr.io` (command above) and pull:
      ```bash
      docker pull ghcr.io/wixely/inbix:latest
      ```
- [ ] Run it — point the sample compose at the pulled image instead of building. In
      `docker-compose.yml`, replace the `build: .` / `image: inbix:local` lines with:
      ```yaml
      image: ghcr.io/wixely/inbix:latest
      ```
      then `docker compose up -d` (no `--build`).
- [ ] *(Alternative)* **Build locally** if you don't want to authenticate to GHCR, or run as a
      **Windows Service / bare .NET 10** — see [SETUP.md §4](SETUP.md#4-install--run):
      ```bash
      cp docker-compose.yml docker-compose.override.yml   # edit values
      docker compose up -d --build
      ```

## 6. First-run configuration (minimum safe setup)

- [ ] **Domains** — `Inbix__Domains__0 = yourdomain.com` (mail to any other domain is rejected).
- [ ] **Admin password** — ⚠️ without this the UI **and** API are wide open. Prefer a hash:
      ```bash
      docker compose run --rm inbix dotnet Inbix.Web.dll hash-password "your-strong-password"
      ```
      then set `Inbix__Admin__PasswordHash` to the printed value.
- [ ] **SMTP banner name** — `Inbix__Smtp__ServerName = mail.yourdomain.com`.
- [ ] **DataProtection keys** — persisted so restarts don't log everyone out (the Docker image already
      sets `Inbix__DataProtectionKeysPath=/data/keys`).
- [ ] **Backups** — `Inbix__Backups__Enabled = true` (optional but wise).
- [ ] **Keep the web UI off the public internet** — LAN/VPN or a reverse proxy with HTTPS. Only port 25
      needs to face the world.

## 7. Create aliases & verify

- [ ] Log in, then create an alias (**Aliases → New alias**) or enable the **catch-all** to accept any
      address on your domain.
- [ ] Open **`/status`** — the diagnostics page should be green (DB, storage, domains, admin password,
      MX records, and whether your MX resolves to your current public IP).
- [ ] **Send a real test** from *outside* your network:
      ```bash
      swaks --to spotify@yourdomain.com --server mail.yourdomain.com:25 --body "hello inbix"
      ```
      It should land on the dashboard within seconds.
- [ ] *(Optional)* Confirm the world sees you with an external tool like **MXToolbox**.

## 8. Optional hardening

- [ ] **STARTTLS** — offer opportunistic TLS on port 25 with a PFX cert
      (`Inbix__Smtp__CertificatePath`); see [SETUP.md §6](SETUP.md#6-starttls-optional-but-recommended).
- [ ] **Rate limits** — `Inbix__Smtp__MaxConnectionsPerMinutePerIp` / `MaxConcurrentSessions` if you
      see connection floods.
- [ ] **Encrypt backups** — Inbix can hold password-reset mail and saved Identities (stored in
      plaintext so they're retrievable). Treat the database, `/data`, and backups as secrets.

---

## Why you can skip DKIM / SPF / DMARC

**SPF, DKIM, DMARC, and reverse-DNS (PTR) all authenticate a *sender*.** They let the rest of the
internet trust that mail claiming to be from your domain really came from your server, and they're
tied to the **sending IP's** identity and reputation. Inbix **sends nothing**, so:

- ❌ No **SPF / DKIM / DMARC** records to publish.
- ❌ No **PTR / reverse-DNS** requirement.
- ❌ No **static IP with clean reputation**, no smarthost, no port-587 submission, no IP warm-up.

Because none of your setup is bound to a specific, reputable, static sending IP, **a dynamic IP is
completely fine** — a receiving server only has to be findable, and the MX → CNAME → DDNS chain in
[step 4](#if-your-public-ip-changes-dynamic-ip) keeps it findable no matter how often your IP changes.
This is proven and tested in practice.

> The Status page still *reports* your PTR for completeness, but it's informational — not required to
> receive mail.

---

See [SETUP.md](SETUP.md) for the full guide and [§9 Troubleshooting](SETUP.md#9-troubleshooting) if
something doesn't work.
