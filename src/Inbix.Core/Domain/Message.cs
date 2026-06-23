namespace Inbix.Core.Domain;

/// <summary>Stored inbound message metadata. Raw MIME lives in the raw store (see RawStoragePath).</summary>
public sealed class Message
{
    public long Id { get; set; }
    public long AliasId { get; set; }
    public long? SmtpSessionId { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public string? Sender { get; set; }
    public string? Subject { get; set; }
    public string? MessageIdHeader { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public long SizeBytes { get; set; }
    public string? RawStoragePath { get; set; }
    public bool Parsed { get; set; }
    public string? ParseError { get; set; }
}
