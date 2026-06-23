using MimeKit;

namespace Inbix.Worker;

/// <summary>Result of parsing a raw MIME message. Pure data, no storage concerns.</summary>
public sealed record ParsedEmail(
    string? Subject,
    string? Sender,
    string? MessageId,
    string? TextBody,
    string? HtmlBody,
    IReadOnlyList<ParsedAttachment> Attachments);

public sealed record ParsedAttachment(string? FileName, string? ContentType, byte[] Content);

/// <summary>Parses raw MIME using MimeKit. Kept free of I/O so it can be unit tested directly.</summary>
public static class MimeEmailParser
{
    public static async Task<ParsedEmail> ParseAsync(Stream rawMime, CancellationToken ct = default)
    {
        var message = await MimeMessage.LoadAsync(rawMime, ct).ConfigureAwait(false);

        var sender = message.From?.Mailboxes?.FirstOrDefault()?.Address
                     ?? message.Sender?.Address;

        var attachments = new List<ParsedAttachment>();
        foreach (var entity in message.Attachments)
        {
            if (entity is not MimePart { Content: not null } part)
                continue;

            using var ms = new MemoryStream();
            await part.Content.DecodeToAsync(ms, ct).ConfigureAwait(false);

            attachments.Add(new ParsedAttachment(
                part.FileName,
                part.ContentType?.MimeType,
                ms.ToArray()));
        }

        return new ParsedEmail(
            Subject: string.IsNullOrWhiteSpace(message.Subject) ? null : message.Subject,
            Sender: sender,
            MessageId: string.IsNullOrWhiteSpace(message.MessageId) ? null : message.MessageId,
            TextBody: message.TextBody,
            HtmlBody: message.HtmlBody,
            Attachments: attachments);
    }
}
