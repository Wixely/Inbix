namespace Inbix.Core.Domain;

/// <summary>
/// A recent message across all mailboxes, for the dashboard. Includes the owning alias so the UI can
/// label which mailbox it landed in and link straight to it. Settable-property class so Dapper's
/// snake_case column mapping applies.
/// </summary>
public sealed class RecentMessage
{
    public long Id { get; set; }
    public long AliasId { get; set; }
    public string? Sender { get; set; }
    public string? Subject { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; }
    public long SizeBytes { get; set; }
    public bool Parsed { get; set; }
    public string? Snippet { get; set; }

    public string AliasLocalPart { get; set; } = string.Empty;
    public string AliasDomain { get; set; } = string.Empty;
    public bool AliasIsCatchAll { get; set; }

    /// <summary>Human label for the mailbox this message landed in.</summary>
    public string MailboxLabel => AliasIsCatchAll ? "Catch-all" : $"{AliasLocalPart}@{AliasDomain}";
}
