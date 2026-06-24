using Inbix.Core.Domain;

namespace Inbix.Core.Abstractions;

public interface IMessageRepository
{
    Task<long> CreateAsync(Message message, CancellationToken ct = default);

    Task<Message?> GetByIdAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<Message>> ListByAliasAsync(long aliasId, int limit, int offset, CancellationToken ct = default);

    /// <summary>List messages for an alias with a short text-body snippet, for the inbox card list.</summary>
    Task<IReadOnlyList<InboxItem>> ListByAliasWithPreviewAsync(long aliasId, int limit, int offset, CancellationToken ct = default);

    /// <summary>Most recent messages across all mailboxes (with owning alias + snippet), for the dashboard.</summary>
    Task<IReadOnlyList<RecentMessage>> ListRecentAsync(int limit, CancellationToken ct = default);

    Task<MessageBody?> GetBodyAsync(long messageId, CancellationToken ct = default);

    Task<IReadOnlyList<Attachment>> ListAttachmentsAsync(long messageId, CancellationToken ct = default);

    Task<Attachment?> GetAttachmentAsync(long attachmentId, CancellationToken ct = default);

    // --- Parser worker surface ---

    /// <summary>Claim up to <paramref name="batchSize"/> unparsed messages for processing.</summary>
    Task<IReadOnlyList<Message>> ClaimUnparsedAsync(int batchSize, CancellationToken ct = default);

    /// <summary>Persist parse results (subject/sender/message-id, body, attachments) and mark parsed.</summary>
    Task MarkParsedAsync(long messageId, string? subject, string? sender, string? messageIdHeader,
        MessageBody body, IReadOnlyList<Attachment> attachments, CancellationToken ct = default);

    /// <summary>Record a parse failure so the message isn't retried forever.</summary>
    Task MarkParseFailedAsync(long messageId, string error, CancellationToken ct = default);

    /// <summary>Move messages addressed to a specific recipient from one alias to another (e.g. catch-all → a new alias). Returns the number moved.</summary>
    Task<int> ReassignByRecipientAsync(long fromAliasId, long toAliasId, string recipient, CancellationToken ct = default);

    /// <summary>Move all messages from one alias to another (e.g. a deleted alias → catch-all). Returns the number moved.</summary>
    Task<int> ReassignAllAsync(long fromAliasId, long toAliasId, CancellationToken ct = default);
}
