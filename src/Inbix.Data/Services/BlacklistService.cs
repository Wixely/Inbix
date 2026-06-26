using System.Text.RegularExpressions;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Inbix.Data.Services;

/// <summary>
/// Orchestrates blacklist rules and junk operations with audit logging: rule CRUD, sweep preview
/// (dry run), sweep/unsweep of a rule's matches, and manual junk/unjunk of individual messages.
/// </summary>
public sealed class BlacklistService : IBlacklistService
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly IBlacklistRuleRepository _rules;
    private readonly IMessageRepository _messages;
    private readonly IAuditRepository _audit;
    private readonly ILogger<BlacklistService> _logger;

    public BlacklistService(
        IBlacklistRuleRepository rules, IMessageRepository messages,
        IAuditRepository audit, ILogger<BlacklistService> logger)
    {
        _rules = rules;
        _messages = messages;
        _audit = audit;
        _logger = logger;
    }

    public async Task<BlacklistRule> CreateAsync(BlacklistRule rule, CancellationToken ct = default)
    {
        var created = await _rules.CreateAsync(rule, ct).ConfigureAwait(false);
        await AuditRule("rule.create", created.Id, ct).ConfigureAwait(false);
        return created;
    }

    public async Task<BlacklistRule?> UpdateAsync(BlacklistRule rule, CancellationToken ct = default)
    {
        var updated = await _rules.UpdateAsync(rule, ct).ConfigureAwait(false);
        if (updated is not null) await AuditRule("rule.update", updated.Id, ct).ConfigureAwait(false);
        return updated;
    }

    public async Task<int> DeleteAsync(long id, bool unsweep, CancellationToken ct = default)
    {
        var restored = unsweep ? await _messages.UnsweepByRuleAsync(id, ct).ConfigureAwait(false) : 0;
        await _rules.DeleteAsync(id, ct).ConfigureAwait(false);
        await AuditRule("rule.delete", id, ct).ConfigureAwait(false);
        return restored;
    }

    public async Task<SweepPreview> SweepPreviewAsync(
        RuleTarget target, RuleMatch matchType, string pattern, int sampleSize = 10, CancellationToken ct = default)
    {
        var matches = await MatchCandidatesAsync(target, matchType, pattern, ct).ConfigureAwait(false);
        var sample = matches.Take(Math.Max(0, sampleSize)).ToList(); // candidates are newest-first
        return new SweepPreview(matches.Count, sample);
    }

    public async Task<int> SweepAsync(long ruleId, CancellationToken ct = default)
    {
        var rule = await _rules.GetByIdAsync(ruleId, ct).ConfigureAwait(false);
        if (rule is null) return 0;

        var matches = await MatchCandidatesAsync(rule.Target, rule.MatchType, rule.Pattern, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        foreach (var m in matches)
            await _messages.SetJunkAsync(m.Id, now, ruleId, manual: false, ct).ConfigureAwait(false);

        await AuditRule("rule.sweep", ruleId, ct).ConfigureAwait(false);
        _logger.LogInformation("Swept {Count} message(s) into Junk for rule {RuleId}", matches.Count, ruleId);
        return matches.Count;
    }

    public async Task<int> UnsweepAsync(long ruleId, CancellationToken ct = default)
    {
        var restored = await _messages.UnsweepByRuleAsync(ruleId, ct).ConfigureAwait(false);
        await AuditRule("rule.unsweep", ruleId, ct).ConfigureAwait(false);
        _logger.LogInformation("Unswept {Count} message(s) for rule {RuleId}", restored, ruleId);
        return restored;
    }

    public async Task JunkMessageAsync(long messageId, CancellationToken ct = default)
    {
        await _messages.SetJunkAsync(messageId, DateTimeOffset.UtcNow, ruleId: null, manual: true, ct).ConfigureAwait(false);
        await AuditMessage("message.junk", messageId, ct).ConfigureAwait(false);
    }

    public async Task UnjunkMessageAsync(long messageId, CancellationToken ct = default)
    {
        await _messages.ClearJunkAsync(messageId, manual: true, ct).ConfigureAwait(false);
        await AuditMessage("message.unjunk", messageId, ct).ConfigureAwait(false);
    }

    private async Task<List<SweepCandidate>> MatchCandidatesAsync(
        RuleTarget target, RuleMatch matchType, string pattern, CancellationToken ct)
    {
        var predicate = BuildPredicate(matchType, pattern);
        var candidates = await _messages.ListSweepCandidatesAsync(ct).ConfigureAwait(false);
        return candidates
            .Where(m => predicate(target == RuleTarget.Sender ? m.Sender : m.Recipient))
            .ToList();
    }

    private static Func<string?, bool> BuildPredicate(RuleMatch matchType, string pattern)
    {
        if (matchType == RuleMatch.Regex)
        {
            Regex? rx = null;
            try { rx = new Regex(pattern, RegexOptions.IgnoreCase, RegexTimeout); }
            catch (ArgumentException) { /* invalid pattern never matches */ }
            return value =>
            {
                if (string.IsNullOrEmpty(value) || rx is null) return false;
                try { return rx.IsMatch(value); }
                catch (RegexMatchTimeoutException) { return false; }
            };
        }
        return value => !string.IsNullOrEmpty(value) && string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private Task AuditRule(string action, long ruleId, CancellationToken ct) =>
        _audit.WriteAsync(new AuditEntry
        {
            Action = action, TargetType = "rule", TargetId = ruleId.ToString(),
            Actor = "ui", CreatedAt = DateTimeOffset.UtcNow
        }, ct);

    private Task AuditMessage(string action, long messageId, CancellationToken ct) =>
        _audit.WriteAsync(new AuditEntry
        {
            Action = action, TargetType = "message", TargetId = messageId.ToString(),
            Actor = "ui", CreatedAt = DateTimeOffset.UtcNow
        }, ct);
}
