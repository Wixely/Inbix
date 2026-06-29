using System.Text.Json.Serialization;
using Inbix.Core.Domain;

namespace Inbix.Data.Json;

/// <summary>An alias as held in memory, plus the on-disk folder name that holds its mail.</summary>
internal sealed class StoredAlias
{
    public required Alias Alias { get; set; }

    /// <summary>Folder under <c>mail/</c> that contains this alias's <c>_alias.json</c> and message files.</summary>
    public required string FolderName { get; set; }
}

/// <summary>
/// The full on-disk shape of one email (one JSON file). Holds the message metadata plus the parsed body
/// and attachment metadata embedded inline, so a single file is the complete record. Attachment and raw
/// MIME <i>bytes</i> still live in the raw store (see <see cref="StoredAttachment.StoragePath"/>).
/// </summary>
internal sealed class StoredMessage
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
    public DateTimeOffset? JunkedAt { get; set; }
    public long? JunkRuleId { get; set; }
    public bool JunkManual { get; set; }
    public DateTimeOffset? StateChangedAt { get; set; }

    // --- Parsed content, embedded (populated by the worker via MarkParsed). ---
    public bool BodyStored { get; set; }
    public long BodyId { get; set; }
    public string? TextBody { get; set; }
    public string? HtmlBody { get; set; }
    public List<StoredAttachment> Attachments { get; set; } = [];

    /// <summary>Where this file was actually loaded from / last written to. Runtime only — used to move/delete the real file.</summary>
    [JsonIgnore]
    public string? CurrentPath { get; set; }

    public static StoredMessage FromMessage(Message m) => new()
    {
        Id = m.Id,
        AliasId = m.AliasId,
        SmtpSessionId = m.SmtpSessionId,
        Recipient = m.Recipient,
        Sender = m.Sender,
        Subject = m.Subject,
        MessageIdHeader = m.MessageIdHeader,
        ReceivedAt = m.ReceivedAt,
        SizeBytes = m.SizeBytes,
        RawStoragePath = m.RawStoragePath,
        Parsed = m.Parsed,
        ParseError = m.ParseError,
        JunkedAt = m.JunkedAt,
        JunkRuleId = m.JunkRuleId,
        JunkManual = m.JunkManual,
        StateChangedAt = m.StateChangedAt,
    };

    public Message ToMessage() => new()
    {
        Id = Id,
        AliasId = AliasId,
        SmtpSessionId = SmtpSessionId,
        Recipient = Recipient,
        Sender = Sender,
        Subject = Subject,
        MessageIdHeader = MessageIdHeader,
        ReceivedAt = ReceivedAt,
        SizeBytes = SizeBytes,
        RawStoragePath = RawStoragePath,
        Parsed = Parsed,
        ParseError = ParseError,
        JunkedAt = JunkedAt,
        JunkRuleId = JunkRuleId,
        JunkManual = JunkManual,
        StateChangedAt = StateChangedAt,
    };

    public MessageBody? ToBody() =>
        BodyStored ? new MessageBody { Id = BodyId, MessageId = Id, TextBody = TextBody, HtmlBody = HtmlBody } : null;

    public IReadOnlyList<Attachment> ToAttachments() =>
        Attachments.Select(a => new Attachment
        {
            Id = a.Id,
            MessageId = Id,
            Filename = a.Filename,
            ContentType = a.ContentType,
            SizeBytes = a.SizeBytes,
            StoragePath = a.StoragePath,
            Sha256 = a.Sha256,
        }).ToList();

    /// <summary>First <paramref name="length"/> chars of the text body, mirroring SQLite's <c>substr(text_body,1,N)</c>.</summary>
    public string? Snippet(int length = 200) =>
        string.IsNullOrEmpty(TextBody) ? null : (TextBody.Length <= length ? TextBody : TextBody[..length]);
}

internal sealed class StoredAttachment
{
    public long Id { get; set; }
    public string? Filename { get; set; }
    public string? ContentType { get; set; }
    public long? SizeBytes { get; set; }
    public string StoragePath { get; set; } = string.Empty;
    public string? Sha256 { get; set; }
}
