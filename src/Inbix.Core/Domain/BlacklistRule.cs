namespace Inbix.Core.Domain;

/// <summary>What part of the message a rule matches against.</summary>
public enum RuleTarget { Sender, Recipient }

/// <summary>How a rule's pattern is interpreted.</summary>
public enum RuleMatch { Literal, Regex }

/// <summary>What happens to a message that matches a rule.</summary>
public enum RuleAction { Reject, Discard, Junk }

/// <summary>
/// A blacklist rule. Matches incoming mail on the sender or recipient, by literal string or regex,
/// and applies an action (reject at SMTP, accept-then-discard, or file into the hidden Junk inbox).
/// Settable-property class so Dapper's snake_case column mapping applies.
/// </summary>
public sealed class BlacklistRule
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public RuleTarget Target { get; set; }
    public RuleMatch MatchType { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public RuleAction Action { get; set; } = RuleAction.Junk;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A candidate message considered during a sweep: enough to match a rule (sender/recipient) and to
/// render a preview card with a deep-link to the message in its home inbox.
/// </summary>
public sealed class SweepCandidate
{
    public long Id { get; set; }
    public long AliasId { get; set; }
    public string? Sender { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public string? Subject { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public bool Parsed { get; set; }
}
