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

    // --- Junk inbox + blacklist sweep ---

    /// <summary>List junked messages (junked_at not null) with a snippet and the junking rule's name, for the Junk inbox.</summary>
    Task<IReadOnlyList<JunkItem>> ListJunkWithPreviewAsync(int limit, int offset, CancellationToken ct = default);

    /// <summary>Set a message's junk state.</summary>
    Task SetJunkAsync(long messageId, DateTimeOffset junkedAt, long? ruleId, bool manual, CancellationToken ct = default);

    /// <summary>Clear a message's junk state (restore to its home inbox); <paramref name="manual"/> sets the lock flag.</summary>
    Task ClearJunkAsync(long messageId, bool manual, CancellationToken ct = default);

    /// <summary>All sweep-eligible messages (not junked, not manual-locked) with enough to match a rule and preview it.</summary>
    Task<IReadOnlyList<SweepCandidate>> ListSweepCandidatesAsync(CancellationToken ct = default);

    /// <summary>Restore mail junked by a rule (skipping manual-locked) back to its home inbox. Returns count.</summary>
    Task<int> UnsweepByRuleAsync(long ruleId, CancellationToken ct = default);

    /// <summary>Ids of junked messages whose junked_at is older than the cutoff (for retention cleanup).</summary>
    Task<IReadOnlyList<long>> ListJunkedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>
    /// Non-junked messages in an alias whose effective date (last state change, else received) is older
    /// than the cutoff — i.e. eligible for per-mailbox expiry. Oldest first, for preview and cleanup.
    /// </summary>
    Task<IReadOnlyList<SweepCandidate>> ListExpiryCandidatesAsync(long aliasId, DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>Hard-delete a message and its body/attachments rows plus the raw + attachment files.</summary>
    Task DeleteAsync(long messageId, CancellationToken ct = default);

    /// <summary>Every non-null raw storage path currently referenced by a message (for re-index de-duplication).</summary>
    Task<IReadOnlyList<string>> ListRawStoragePathsAsync(CancellationToken ct = default);
}
