using Inbix.Core.Domain;

namespace Inbix.Core.Abstractions;

/// <summary>A dry-run sweep result: how many messages would be junked, plus a sample for preview.</summary>
public sealed record SweepPreview(int Count, IReadOnlyList<SweepCandidate> Sample);

/// <summary>
/// Orchestrates blacklist rules and junk operations (with audit logging): rule CRUD, sweep preview
/// (dry run), sweep/unsweep, and manual junk/unjunk of individual messages.
/// </summary>
public interface IBlacklistService
{
    Task<BlacklistRule> CreateAsync(BlacklistRule rule, CancellationToken ct = default);

    Task<BlacklistRule?> UpdateAsync(BlacklistRule rule, CancellationToken ct = default);

    /// <summary>Delete a rule. When <paramref name="unsweep"/>, first restore mail it junked. Returns restored count.</summary>
    Task<int> DeleteAsync(long id, bool unsweep, CancellationToken ct = default);

    /// <summary>Dry run: count + sample of messages a rule definition would move to Junk. No mutation.</summary>
    Task<SweepPreview> SweepPreviewAsync(RuleTarget target, RuleMatch matchType, string pattern,
        int sampleSize = 10, CancellationToken ct = default);

    /// <summary>Move all current matches of a saved rule into Junk (skipping manual-locked). Returns count.</summary>
    Task<int> SweepAsync(long ruleId, CancellationToken ct = default);

    /// <summary>Restore mail this rule junked (skipping manual-locked) back to its home inbox. Returns count.</summary>
    Task<int> UnsweepAsync(long ruleId, CancellationToken ct = default);

    /// <summary>Manually move a message to Junk (locks it against sweeps).</summary>
    Task JunkMessageAsync(long messageId, CancellationToken ct = default);

    /// <summary>Manually restore a message from Junk (locks it against sweeps).</summary>
    Task UnjunkMessageAsync(long messageId, CancellationToken ct = default);
}
