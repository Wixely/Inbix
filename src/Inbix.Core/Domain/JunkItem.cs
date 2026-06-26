namespace Inbix.Core.Domain;

/// <summary>
/// A junked message for the Junk inbox card list: the inbox-card fields plus why it was junked
/// (the rule that caught it, or a manual lock). Settable-property class for Dapper snake_case mapping.
/// </summary>
public sealed class JunkItem
{
    public long Id { get; set; }
    public string? Sender { get; set; }
    public string? Subject { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; }
    public bool Parsed { get; set; }
    public string? Snippet { get; set; }

    public DateTimeOffset? JunkedAt { get; set; }
    public bool JunkManual { get; set; }
    public long? JunkRuleId { get; set; }

    /// <summary>Name of the rule that junked it (from a LEFT JOIN); null for manual or unnamed rules.</summary>
    public string? JunkRuleName { get; set; }
}
