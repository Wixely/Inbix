using System.Text.RegularExpressions;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;

namespace Inbix.Data.Services;

/// <summary>
/// Evaluates enabled blacklist rules against a message's sender/recipient. The compiled rule set is
/// cached for a short TTL (like the alias resolver) so rule edits take effect within ~30s without a
/// restart. When several rules match, the most severe action wins: reject &gt; discard &gt; junk.
/// </summary>
public sealed class CachingBlacklistMatcher : IBlacklistMatcher
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly IBlacklistRuleRepository _rules;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private volatile CompiledRule[] _compiled = [];
    private DateTimeOffset _expires = DateTimeOffset.MinValue;

    public CachingBlacklistMatcher(IBlacklistRuleRepository rules) => _rules = rules;

    public async Task<BlacklistMatch?> MatchAsync(string? sender, string recipient, CancellationToken ct = default)
    {
        var rules = await GetRulesAsync(ct).ConfigureAwait(false);
        if (rules.Length == 0) return null;

        BlacklistMatch? best = null;
        foreach (var r in rules)
        {
            var value = r.Target == RuleTarget.Sender ? sender : recipient;
            if (string.IsNullOrEmpty(value) || !r.IsMatch(value)) continue;

            // reject(0) > discard(1) > junk(2): keep the lowest enum value seen.
            if (best is null || r.Action < best.Value.Action)
                best = new BlacklistMatch(r.Action, r.Id);
            if (best.Value.Action == RuleAction.Reject) break;
        }
        return best;
    }

    private async Task<CompiledRule[]> GetRulesAsync(CancellationToken ct)
    {
        if (_expires > DateTimeOffset.UtcNow) return _compiled;

        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_expires > DateTimeOffset.UtcNow) return _compiled;
            var rules = await _rules.ListAsync(ct).ConfigureAwait(false);
            _compiled = rules.Where(r => r.Enabled).Select(CompiledRule.From).ToArray();
            _expires = DateTimeOffset.UtcNow.Add(Ttl);
            return _compiled;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private sealed class CompiledRule
    {
        public long Id { get; private init; }
        public RuleTarget Target { get; private init; }
        public RuleAction Action { get; private init; }
        private RuleMatch MatchType { get; init; }
        private string Pattern { get; init; } = string.Empty;
        private Regex? Regex { get; init; }

        public static CompiledRule From(BlacklistRule r)
        {
            Regex? rx = null;
            if (r.MatchType == RuleMatch.Regex)
            {
                try { rx = new Regex(r.Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout); }
                catch (ArgumentException) { rx = null; } // an invalid stored regex simply never matches
            }
            return new CompiledRule
            {
                Id = r.Id, Target = r.Target, Action = r.Action,
                MatchType = r.MatchType, Pattern = r.Pattern, Regex = rx
            };
        }

        public bool IsMatch(string value)
        {
            if (MatchType == RuleMatch.Literal)
                return string.Equals(value, Pattern, StringComparison.OrdinalIgnoreCase);
            if (Regex is null) return false;
            try { return Regex.IsMatch(value); }
            catch (RegexMatchTimeoutException) { return false; }
        }
    }
}
