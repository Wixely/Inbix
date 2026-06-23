namespace Inbix.Core.Domain;

/// <summary>Parsed text/HTML bodies for a message, produced by the MIME parser worker.</summary>
public sealed class MessageBody
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public string? TextBody { get; set; }
    public string? HtmlBody { get; set; }
}
