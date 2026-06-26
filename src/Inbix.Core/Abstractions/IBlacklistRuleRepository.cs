using Inbix.Core.Domain;

namespace Inbix.Core.Abstractions;

public interface IBlacklistRuleRepository
{
    Task<IReadOnlyList<BlacklistRule>> ListAsync(CancellationToken ct = default);

    Task<BlacklistRule?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>Insert a rule. Returns the created row.</summary>
    Task<BlacklistRule> CreateAsync(BlacklistRule rule, CancellationToken ct = default);

    /// <summary>Update a rule's fields by <c>rule.Id</c>. Returns the updated row, or null if missing.</summary>
    Task<BlacklistRule?> UpdateAsync(BlacklistRule rule, CancellationToken ct = default);

    /// <summary>Enable/disable a rule. Returns the updated row, or null if missing.</summary>
    Task<BlacklistRule?> SetEnabledAsync(long id, bool enabled, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);
}
