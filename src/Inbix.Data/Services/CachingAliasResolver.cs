using System.Collections.Concurrent;
using Inbix.Core.Abstractions;
using Inbix.Core.Options;
using Inbix.Core.Validation;
using Microsoft.Extensions.Options;

namespace Inbix.Data.Services;

/// <summary>
/// Recipient-acceptance check for the SMTP RCPT phase, with a short positive/negative cache so a
/// burst of recipients doesn't hammer the database. Cache entries expire quickly so alias
/// enable/disable changes take effect without a restart.
/// </summary>
public sealed class CachingAliasResolver : IAliasResolver
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly IAliasRepository _aliases;
    private readonly HashSet<string> _domains;
    private readonly ConcurrentDictionary<string, (bool deliverable, DateTimeOffset expires)> _cache = new();

    public CachingAliasResolver(IAliasRepository aliases, IOptions<InbixOptions> options)
    {
        _aliases = aliases;
        _domains = options.Value.Domains
            .Select(d => d.Trim().ToLowerInvariant())
            .Where(d => d.Length > 0)
            .ToHashSet();
    }

    public async Task<bool> IsDeliverableAsync(string address, CancellationToken ct = default)
    {
        if (!AliasRules.TrySplitAddress(address, out var localPart, out var domain))
            return false;

        if (_domains.Count > 0 && !_domains.Contains(domain))
            return false;

        var key = $"{localPart}@{domain}";
        var now = DateTimeOffset.UtcNow;

        if (_cache.TryGetValue(key, out var cached) && cached.expires > now)
            return cached.deliverable;

        var alias = await _aliases.FindAsync(localPart, domain, ct).ConfigureAwait(false);
        var deliverable = alias is { Enabled: true };

        _cache[key] = (deliverable, now.Add(Ttl));
        return deliverable;
    }
}
