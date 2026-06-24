namespace Inbix.Core.Domain;

/// <summary>
/// A message row for the inbox list, including a short body snippet for the card preview.
/// A settable-property class (not a positional record) so Dapper's snake_case column mapping applies.
/// </summary>
public sealed class InboxItem
{
    public long Id { get; set; }
    public string? Sender { get; set; }
    public string? Subject { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; }
    public long SizeBytes { get; set; }
    public bool Parsed { get; set; }
    public string? Snippet { get; set; }
}
