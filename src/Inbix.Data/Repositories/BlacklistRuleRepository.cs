using Dapper;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;

namespace Inbix.Data.Repositories;

public sealed class BlacklistRuleRepository : IBlacklistRuleRepository
{
    private const string Columns = "id, name, target, match_type, pattern, action, enabled, created_at";

    private readonly IDbConnectionFactory _factory;

    public BlacklistRuleRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<BlacklistRule>> ListAsync(CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<BlacklistRule>(
            $"SELECT {Columns} FROM blacklist_rules ORDER BY created_at DESC, id DESC;").ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<BlacklistRule?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<BlacklistRule>(
            $"SELECT {Columns} FROM blacklist_rules WHERE id = @id;", new { id }).ConfigureAwait(false);
    }

    public async Task<BlacklistRule> CreateAsync(BlacklistRule rule, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleAsync<BlacklistRule>(
            $"""
             INSERT INTO blacklist_rules (name, target, match_type, pattern, action, enabled, created_at)
             VALUES (@Name, @Target, @MatchType, @Pattern, @Action, @Enabled, @CreatedAt)
             RETURNING {Columns};
             """,
            ToParams(rule, DateTimeOffset.UtcNow)).ConfigureAwait(false);
    }

    public async Task<BlacklistRule?> UpdateAsync(BlacklistRule rule, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<BlacklistRule>(
            $"""
             UPDATE blacklist_rules
             SET name = @Name, target = @Target, match_type = @MatchType,
                 pattern = @Pattern, action = @Action, enabled = @Enabled
             WHERE id = @Id
             RETURNING {Columns};
             """,
            ToParams(rule, rule.CreatedAt)).ConfigureAwait(false);
    }

    public async Task<BlacklistRule?> SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<BlacklistRule>(
            $"UPDATE blacklist_rules SET enabled = @enabled WHERE id = @id RETURNING {Columns};",
            new { id, enabled = enabled ? 1 : 0 }).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await c.ExecuteAsync("DELETE FROM blacklist_rules WHERE id = @id;", new { id }).ConfigureAwait(false);
    }

    // Enums are stored as lowercase text; bool as 0/1. (Dapper maps text → enum case-insensitively on read.)
    private static object ToParams(BlacklistRule r, DateTimeOffset createdAt) => new
    {
        r.Id,
        r.Name,
        Target = r.Target.ToString().ToLowerInvariant(),
        MatchType = r.MatchType.ToString().ToLowerInvariant(),
        r.Pattern,
        Action = r.Action.ToString().ToLowerInvariant(),
        Enabled = r.Enabled ? 1 : 0,
        CreatedAt = createdAt
    };
}
