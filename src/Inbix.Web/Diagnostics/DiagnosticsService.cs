using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using DnsClient;
using DnsClient.Protocol;
using Inbix.Core.Abstractions;
using Inbix.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Inbix.Web.Diagnostics;

/// <summary>
/// Runs configuration / environment diagnostics for the status page: database and storage health,
/// alias/auth/TLS configuration, and DNS-facing checks (public IP, MX records, MX→A match, rDNS)
/// that catch the most common inbound-mail misconfigurations.
/// </summary>
public sealed class DiagnosticsService
{
    private readonly IDbConnectionFactory _db;
    private readonly IAliasRepository _aliases;
    private readonly IBackupService _backups;
    private readonly ILookupClient _dns;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IHostEnvironment _env;
    private readonly InbixOptions _options;

    public DiagnosticsService(
        IDbConnectionFactory db, IAliasRepository aliases, IBackupService backups,
        ILookupClient dns, IHttpClientFactory httpFactory, IHostEnvironment env, IOptions<InbixOptions> options)
    {
        _db = db;
        _aliases = aliases;
        _backups = backups;
        _dns = dns;
        _httpFactory = httpFactory;
        _env = env;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<DiagnosticResult>> RunAllAsync(CancellationToken ct = default)
    {
        var results = new List<DiagnosticResult>();

        await CheckDatabaseAsync(results, ct);
        CheckStorage(results);
        CheckBackups(results);
        await CheckAliasesAndDomainsAsync(results, ct);
        CheckSecurity(results);
        CheckTls(results);
        await CheckSmtpListenerAsync(results, ct);
        await CheckDnsAsync(results, ct);

        return results;
    }

    private async Task CheckDatabaseAsync(List<DiagnosticResult> results, CancellationToken ct)
    {
        const string cat = "Storage & data";
        try
        {
            await using var conn = await _db.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
            var applied = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            results.Add(new(cat, "Database", DiagnosticStatus.Ok, $"Reachable ({_db.Provider}).", $"{applied} migration(s) applied."));
        }
        catch (Exception ex)
        {
            results.Add(new(cat, "Database", DiagnosticStatus.Error, "Not reachable or not migrated.", ex.Message));
        }
    }

    private void CheckStorage(List<DiagnosticResult> results)
    {
        const string cat = "Storage & data";
        var path = Path.GetFullPath(_options.Storage.RawPath);
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".inbix-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            results.Add(new(cat, "Raw storage", DiagnosticStatus.Ok, "Writable.", path));
        }
        catch (Exception ex)
        {
            results.Add(new(cat, "Raw storage", DiagnosticStatus.Error, "Not writable.", $"{path}: {ex.Message}"));
        }
    }

    private void CheckBackups(List<DiagnosticResult> results)
    {
        const string cat = "Storage & data";
        if (!_backups.Enabled)
        {
            results.Add(new(cat, "Backups", DiagnosticStatus.Warning, "Scheduled backups are disabled.", "Set Inbix:Backups:Enabled=true."));
            return;
        }

        var backups = _backups.ListBackups();
        if (backups.Count == 0)
        {
            results.Add(new(cat, "Backups", DiagnosticStatus.Warning, "Enabled but no backups found yet."));
            return;
        }

        var newest = backups[0];
        var age = DateTimeOffset.UtcNow - newest.CreatedAt;
        var stale = age > TimeSpan.FromHours(Math.Max(1, _options.Backups.IntervalHours) * 2);
        results.Add(new(cat, "Backups",
            stale ? DiagnosticStatus.Warning : DiagnosticStatus.Ok,
            $"{backups.Count} backup(s); newest {FormatAge(age)} ago.",
            newest.FileName));
    }

    private async Task CheckAliasesAndDomainsAsync(List<DiagnosticResult> results, CancellationToken ct)
    {
        const string cat = "Aliases";
        var domains = _options.Domains.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
        results.Add(domains.Count > 0
            ? new(cat, "Domains", DiagnosticStatus.Ok, $"{domains.Count} domain(s) configured.", string.Join(", ", domains))
            : new(cat, "Domains", DiagnosticStatus.Error, "No domains configured.", "Set Inbix:Domains; all mail is rejected otherwise."));

        try
        {
            var aliases = await _aliases.ListAsync(ct);
            var enabled = aliases.Count(a => a.Enabled);
            results.Add(aliases.Count > 0
                ? new(cat, "Aliases", DiagnosticStatus.Ok, $"{aliases.Count} alias(es), {enabled} enabled.")
                : new(cat, "Aliases", DiagnosticStatus.Warning, "No aliases defined.", "No mail will be accepted until you add one."));
        }
        catch (Exception ex)
        {
            results.Add(new(cat, "Aliases", DiagnosticStatus.Error, "Could not read aliases.", ex.Message));
        }
    }

    private void CheckSecurity(List<DiagnosticResult> results)
    {
        const string cat = "Security";
        var admin = _options.Admin;
        var authConfigured = !string.IsNullOrEmpty(admin.Password) || !string.IsNullOrEmpty(admin.PasswordHash);
        results.Add(authConfigured
            ? new(cat, "Admin authentication", DiagnosticStatus.Ok, "Password configured.")
            : new(cat, "Admin authentication", DiagnosticStatus.Error, "No admin password set - the UI and API are OPEN.", "Set Inbix:Admin:Password or PasswordHash."));

        if (_options.RequireHttps)
            results.Add(new(cat, "HTTPS", DiagnosticStatus.Ok, "RequireHttps is enabled."));
        else
            results.Add(new(cat, "HTTPS",
                _env.IsDevelopment() ? DiagnosticStatus.Info : DiagnosticStatus.Warning,
                "RequireHttps is disabled.",
                "Enable it (or terminate TLS at a reverse proxy) before exposing the UI."));
    }

    private void CheckTls(List<DiagnosticResult> results)
    {
        const string cat = "Security";
        var smtp = _options.Smtp;
        if (string.IsNullOrWhiteSpace(smtp.CertificatePath))
        {
            results.Add(new(cat, "STARTTLS", DiagnosticStatus.Info, "Disabled (no certificate configured)."));
            return;
        }

        if (!File.Exists(smtp.CertificatePath))
        {
            results.Add(new(cat, "STARTTLS", DiagnosticStatus.Error, "Certificate file not found.", smtp.CertificatePath));
            return;
        }

        try
        {
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(smtp.CertificatePath, smtp.CertificatePassword);
            var remaining = cert.NotAfter - DateTime.Now;
            if (remaining <= TimeSpan.Zero)
                results.Add(new(cat, "STARTTLS", DiagnosticStatus.Error, "Certificate has expired.", $"Expired {cert.NotAfter:yyyy-MM-dd}."));
            else if (remaining < TimeSpan.FromDays(14))
                results.Add(new(cat, "STARTTLS", DiagnosticStatus.Warning, $"Certificate expires in {remaining.Days} day(s).", $"Subject {cert.Subject}."));
            else
                results.Add(new(cat, "STARTTLS", DiagnosticStatus.Ok, $"Enabled; valid until {cert.NotAfter:yyyy-MM-dd}.", $"Subject {cert.Subject}."));
        }
        catch (Exception ex)
        {
            results.Add(new(cat, "STARTTLS", DiagnosticStatus.Error, "Certificate failed to load.", ex.Message));
        }
    }

    private async Task CheckSmtpListenerAsync(List<DiagnosticResult> results, CancellationToken ct)
    {
        const string cat = "SMTP";
        var port = _options.Smtp.Port;
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            results.Add(new(cat, "SMTP listener", DiagnosticStatus.Ok, $"Accepting connections on port {port} (local).",
                "Note: external reachability of port 25 must be tested from outside your network."));
        }
        catch (Exception ex)
        {
            results.Add(new(cat, "SMTP listener", DiagnosticStatus.Error, $"Not accepting connections on port {port}.", ex.Message));
        }
    }

    private async Task CheckDnsAsync(List<DiagnosticResult> results, CancellationToken ct)
    {
        const string cat = "DNS & connectivity";

        IPAddress? publicIp = await TryGetPublicIpAsync(results, cat, ct);

        var domains = _options.Domains.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
        foreach (var domain in domains)
        {
            List<MxRecord> mx;
            try
            {
                var response = await _dns.QueryAsync(domain, QueryType.MX, cancellationToken: ct);
                mx = response.Answers.MxRecords().OrderBy(r => r.Preference).ToList();
            }
            catch (Exception ex)
            {
                results.Add(new(cat, $"MX {domain}", DiagnosticStatus.Error, "MX lookup failed.", ex.Message));
                continue;
            }

            if (mx.Count == 0)
            {
                results.Add(new(cat, $"MX {domain}", DiagnosticStatus.Warning, "No MX record found.", "Mail senders won't know where to deliver."));
                continue;
            }

            var exchanges = string.Join(", ", mx.Select(r => $"{r.Exchange.Value.TrimEnd('.')} (pref {r.Preference})"));
            results.Add(new(cat, $"MX {domain}", DiagnosticStatus.Ok, $"{mx.Count} MX record(s).", exchanges));

            // Resolve the primary MX host and (if known) compare it against this server's public IP.
            var host = mx[0].Exchange.Value.TrimEnd('.');
            try
            {
                var aResp = await _dns.QueryAsync(host, QueryType.A, cancellationToken: ct);
                var addrs = aResp.Answers.ARecords().Select(r => r.Address).ToList();
                if (addrs.Count == 0)
                {
                    results.Add(new(cat, $"MX target {host}", DiagnosticStatus.Warning, "MX host has no A record."));
                }
                else if (publicIp is not null)
                {
                    var match = addrs.Any(a => a.Equals(publicIp));
                    results.Add(new(cat, $"MX target {host}",
                        match ? DiagnosticStatus.Ok : DiagnosticStatus.Warning,
                        match ? $"Resolves to this server's public IP ({publicIp})."
                              : $"Does not match public IP ({publicIp}).",
                        "Resolves to: " + string.Join(", ", addrs.Select(a => a.ToString()))));
                }
                else
                {
                    results.Add(new(cat, $"MX target {host}", DiagnosticStatus.Info, "Resolved (public IP unknown, can't compare).",
                        string.Join(", ", addrs.Select(a => a.ToString()))));
                }
            }
            catch (Exception ex)
            {
                results.Add(new(cat, $"MX target {host}", DiagnosticStatus.Warning, "A lookup failed.", ex.Message));
            }
        }

        // Reverse DNS (PTR) for the public IP aids deliverability/acceptance.
        if (publicIp is not null)
        {
            try
            {
                var ptr = await _dns.QueryReverseAsync(publicIp, ct);
                var names = ptr.Answers.PtrRecords().Select(p => p.PtrDomainName.Value.TrimEnd('.')).ToList();
                results.Add(names.Count > 0
                    ? new(cat, "Reverse DNS (PTR)", DiagnosticStatus.Ok, "PTR record present.", string.Join(", ", names))
                    : new(cat, "Reverse DNS (PTR)", DiagnosticStatus.Warning, "No PTR record for the public IP.", "Some senders treat missing rDNS with suspicion."));
            }
            catch (Exception ex)
            {
                results.Add(new(cat, "Reverse DNS (PTR)", DiagnosticStatus.Warning, "PTR lookup failed.", ex.Message));
            }
        }
    }

    private async Task<IPAddress?> TryGetPublicIpAsync(List<DiagnosticResult> results, string cat, CancellationToken ct)
    {
        var url = _options.Diagnostics.PublicIpLookupUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            results.Add(new(cat, "Public IP", DiagnosticStatus.Info, "Lookup disabled (Inbix:Diagnostics:PublicIpLookupUrl is empty)."));
            return null;
        }

        try
        {
            var client = _httpFactory.CreateClient("diagnostics");
            client.Timeout = TimeSpan.FromSeconds(5);
            var text = (await client.GetStringAsync(url, ct)).Trim();
            if (IPAddress.TryParse(text, out var ip))
            {
                results.Add(new(cat, "Public IP", DiagnosticStatus.Info, ip.ToString(), $"via {url}"));
                return ip;
            }

            results.Add(new(cat, "Public IP", DiagnosticStatus.Warning, "Lookup returned an unparseable response.", text));
            return null;
        }
        catch (Exception ex)
        {
            results.Add(new(cat, "Public IP", DiagnosticStatus.Warning, "Lookup failed.", ex.Message));
            return null;
        }
    }

    private static string FormatAge(TimeSpan age) => age switch
    {
        { TotalMinutes: < 60 } => $"{(int)age.TotalMinutes}m",
        { TotalHours: < 48 } => $"{(int)age.TotalHours}h",
        _ => $"{(int)age.TotalDays}d"
    };
}
