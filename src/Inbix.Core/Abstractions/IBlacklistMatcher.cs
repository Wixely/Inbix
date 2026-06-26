using Inbix.Core.Domain;

namespace Inbix.Core.Abstractions;

/// <summary>The winning rule for a message: its action and the rule's id.</summary>
public readonly record struct BlacklistMatch(RuleAction Action, long RuleId);

/// <summary>
/// Evaluates the enabled blacklist rules against a message's sender/recipient. Results are cached
/// briefly. When several rules match, the most severe action wins: reject &gt; discard &gt; junk.
/// </summary>
public interface IBlacklistMatcher
{
    Task<BlacklistMatch?> MatchAsync(string? sender, string recipient, CancellationToken ct = default);
}
