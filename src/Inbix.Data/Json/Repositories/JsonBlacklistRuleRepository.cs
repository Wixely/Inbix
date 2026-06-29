using Inbix.Core.Abstractions;
using Inbix.Core.Domain;

namespace Inbix.Data.Json.Repositories;

/// <summary>File-backed <see cref="IBlacklistRuleRepository"/>; all rules live in <c>rules.json</c>.</summary>
public sealed class JsonBlacklistRuleRepository : IBlacklistRuleRepository
{
    private readonly JsonDataStore _store;

    public JsonBlacklistRuleRepository(JsonDataStore store) => _store = store;

    public Task<IReadOnlyList<BlacklistRule>> ListAsync(CancellationToken ct = default) =>
        _store.ReadAsync(() => (IReadOnlyList<BlacklistRule>)_store.Rules
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Select(Clone).ToList(), ct);

    public Task<BlacklistRule?> GetByIdAsync(long id, CancellationToken ct = default) =>
        _store.ReadAsync(() => _store.Rules.FirstOrDefault(r => r.Id == id) is { } r ? Clone(r) : null, ct);

    public Task<BlacklistRule> CreateAsync(BlacklistRule rule, CancellationToken ct = default) =>
        _store.WriteAsync(async c =>
        {
            var created = Clone(rule);
            created.Id = _store.NextRuleId();
            created.CreatedAt = DateTimeOffset.UtcNow;
            _store.Rules.Add(created);
            await _store.SaveRulesAsync(c).ConfigureAwait(false);
            return Clone(created);
        }, ct);

    public Task<BlacklistRule?> UpdateAsync(BlacklistRule rule, CancellationToken ct = default) =>
        _store.WriteAsync<BlacklistRule?>(async c =>
        {
            var existing = _store.Rules.FirstOrDefault(r => r.Id == rule.Id);
            if (existing is null) return null;
            existing.Name = rule.Name;
            existing.Target = rule.Target;
            existing.MatchType = rule.MatchType;
            existing.Pattern = rule.Pattern;
            existing.Action = rule.Action;
            existing.Enabled = rule.Enabled;
            await _store.SaveRulesAsync(c).ConfigureAwait(false);
            return Clone(existing);
        }, ct);

    public Task<BlacklistRule?> SetEnabledAsync(long id, bool enabled, CancellationToken ct = default) =>
        _store.WriteAsync<BlacklistRule?>(async c =>
        {
            var existing = _store.Rules.FirstOrDefault(r => r.Id == id);
            if (existing is null) return null;
            existing.Enabled = enabled;
            await _store.SaveRulesAsync(c).ConfigureAwait(false);
            return Clone(existing);
        }, ct);

    public Task DeleteAsync(long id, CancellationToken ct = default) =>
        _store.WriteAsync(async c =>
        {
            if (_store.Rules.RemoveAll(r => r.Id == id) > 0)
                await _store.SaveRulesAsync(c).ConfigureAwait(false);
        }, ct);

    private static BlacklistRule Clone(BlacklistRule r) => new()
    {
        Id = r.Id, Name = r.Name, Target = r.Target, MatchType = r.MatchType,
        Pattern = r.Pattern, Action = r.Action, Enabled = r.Enabled, CreatedAt = r.CreatedAt,
    };
}
