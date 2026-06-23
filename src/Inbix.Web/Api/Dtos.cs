using Inbix.Core.Domain;

namespace Inbix.Web.Api;

public sealed record CreateAliasRequest(string LocalPart, string? Domain, string? Notes);
public sealed record UpdateAliasRequest(bool? Enabled, string? Notes);

public sealed record AliasDto(long Id, string LocalPart, string Domain, string Address, bool Enabled,
    DateTimeOffset CreatedAt, DateTimeOffset? DisabledAt, string? Notes)
{
    public static AliasDto From(Alias a) =>
        new(a.Id, a.LocalPart, a.Domain, a.Address, a.Enabled, a.CreatedAt, a.DisabledAt, a.Notes);
}

public sealed record MessageSummaryDto(long Id, long AliasId, string Recipient, string? Sender, string? Subject,
    DateTimeOffset ReceivedAt, long SizeBytes, bool Parsed)
{
    public static MessageSummaryDto From(Message m) =>
        new(m.Id, m.AliasId, m.Recipient, m.Sender, m.Subject, m.ReceivedAt, m.SizeBytes, m.Parsed);
}

public sealed record AttachmentDto(long Id, string? Filename, string? ContentType, long? SizeBytes, string? Sha256)
{
    public static AttachmentDto From(Attachment a) =>
        new(a.Id, a.Filename, a.ContentType, a.SizeBytes, a.Sha256);
}

public sealed record MessageDetailDto(MessageSummaryDto Message, string? TextBody, string? HtmlBody,
    string? ParseError, IReadOnlyList<AttachmentDto> Attachments);
