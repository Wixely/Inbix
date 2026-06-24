using System.Collections.Concurrent;
using Inbix.Core.Abstractions;
using Inbix.Core.Options;
using Inbix.Core.Validation;
using Microsoft.Extensions.Options;

namespace Inbix.Data.Services;

/// <summary>
/// Recipient-acceptance check for the SMTP RCPT phase, with a short positive/negative cache so a
/// burst of recipients doesn't hammer the database. A recipient is deliverable if it matches an
/// enabled specific alias, or — when the catch-all is enabled — any address on an accepted domain.
/// Cache entries expire quickly so enable/disable changes take effect without a restart.
/// </summary>
public sealed class CachingAliasResolver : IAliasResolver
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly IAliasRepository _aliases;
    private readonly HashSet<string> _domains;
    private readonly ConcurrentDictionary<string, (bool deliverable, DateTimeOffset expires)> _cache = new();
    private (bool enabled, DateTimeOffset expires) _catchAll;

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

        var now = DateTimeOffset.UtcNow;
        var key = $"{localPart}@{domain}";

        bool specific;
        if (_cache.TryGetValue(key, out var cached) && cached.expires > now)
        {
            specific = cached.deliverable;
        }
        else
        {
            var alias = await _aliases.FindAsync(localPart, domain, ct).ConfigureAwait(false);
            specific = alias is { Enabled: true };
            _cache[key] = (specific, now.Add(Ttl));
        }

        if (specific)
            return true;

        // Domain already confirmed accepted above; accept anything if the catch-all is enabled.
        return await CatchAllEnabledAsync(now, ct).ConfigureAwait(false);
    }

    private async Task<bool> CatchAllEnabledAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_catchAll.expires > now)
            return _catchAll.enabled;

        var catchAll = await _aliases.GetCatchAllAsync(ct).ConfigureAwait(false);
        var enabled = catchAll is { Enabled: true };
        _catchAll = (enabled, now.Add(Ttl));
        return enabled;
    }
}
