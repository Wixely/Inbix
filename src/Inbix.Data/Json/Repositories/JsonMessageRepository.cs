using Inbix.Core.Abstractions;
using Inbix.Core.Domain;

namespace Inbix.Data.Json.Repositories;

/// <summary>File/folder-backed <see cref="IMessageRepository"/>. Each email is a single JSON file in its
/// alias folder (or the <c>junk/</c> folder when junked). All queries run over the in-memory index.</summary>
public sealed class JsonMessageRepository : IMessageRepository
{
    private const int SnippetLength = 200;

    private readonly JsonDataStore _store;
    private readonly IRawMessageStore _rawStore;

    public JsonMessageRepository(JsonDataStore store, IRawMessageStore rawStore)
    {
        _store = store;
        _rawStore = rawStore;
    }

    public Task<long> CreateAsync(Message message, CancellationToken ct = default) =>
        _store.WriteAsync(async c =>
        {
            var sm = StoredMessage.FromMessage(message);
            sm.Id = _store.NextMessageId();
            _store.Messages[sm.Id] = sm;
            await _store.PersistMessageAsync(sm, c).ConfigureAwait(false);
            return sm.Id;
        }, ct);

    public Task<Message?> GetByIdAsync(long id, CancellationToken ct = default) =>
        _store.ReadAsync(() => _store.Messages.TryGetValue(id, out var m) ? m.ToMessage() : null, ct);

    public Task<IReadOnlyList<Message>> ListByAliasAsync(long aliasId, int limit, int offset, CancellationToken ct = default) =>
        _store.ReadAsync(() => (IReadOnlyList<Message>)Newest(_store.Messages.Values
            .Where(m => m.AliasId == aliasId && m.JunkedAt is null))
            .Skip(offset).Take(limit).Select(m => m.ToMessage()).ToList(), ct);

    public Task<IReadOnlyList<InboxItem>> ListByAliasWithPreviewAsync(long aliasId, int limit, int offset, CancellationToken ct = default) =>
        _store.ReadAsync(() => (IReadOnlyList<InboxItem>)Newest(_store.Messages.Values
            .Where(m => m.AliasId == aliasId && m.JunkedAt is null))
            .Skip(offset).Take(limit).Select(ToInboxItem).ToList(), ct);

    public Task<IReadOnlyList<RecentMessage>> ListRecentAsync(int limit, CancellationToken ct = default) =>
        _store.ReadAsync(() => (IReadOnlyList<RecentMessage>)Newest(_store.Messages.Values
            .Where(m => m.JunkedAt is null))
            .Take(limit)
            .Select(ToRecent)
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList(), ct);

    public Task<MessageBody?> GetBodyAsync(long messageId, CancellationToken ct = default) =>
        _store.ReadAsync(() => _store.Messages.TryGetValue(messageId, out var m) ? m.ToBody() : null, ct);

    public Task<IReadOnlyList<Attachment>> ListAttachmentsAsync(long messageId, CancellationToken ct = default) =>
        _store.ReadAsync(() => _store.Messages.TryGetValue(messageId, out var m)
            ? (IReadOnlyList<Attachment>)m.ToAttachments().OrderBy(a => a.Id).ToList()
            : [], ct);

    public Task<Attachment?> GetAttachmentAsync(long attachmentId, CancellationToken ct = default) =>
        _store.ReadAsync<Attachment?>(() =>
        {
            foreach (var m in _store.Messages.Values)
                if (m.Attachments.Any(a => a.Id == attachmentId))
                    return m.ToAttachments().First(a => a.Id == attachmentId);
            return null;
        }, ct);

    public Task<IReadOnlyList<Message>> ClaimUnparsedAsync(int batchSize, CancellationToken ct = default) =>
        _store.ReadAsync(() => (IReadOnlyList<Message>)_store.Messages.Values
            .Where(m => !m.Parsed && m.ParseError is null)
            .OrderBy(m => m.Id)
            .Take(batchSize)
            .Select(m => m.ToMessage())
            .ToList(), ct);

    public Task MarkParsedAsync(long messageId, string? subject, string? sender, string? messageIdHeader,
        MessageBody body, IReadOnlyList<Attachment> attachments, CancellationToken ct = default) =>
        _store.WriteAsync(async c =>
        {
            if (!_store.Messages.TryGetValue(messageId, out var m)) return;
            m.Subject = subject;
            m.Sender = sender;
            m.MessageIdHeader = messageIdHeader;
            m.Parsed = true;
            m.ParseError = null;
            m.BodyStored = true;
            m.BodyId = _store.NextBodyId();
            m.TextBody = body.TextBody;
            m.HtmlBody = body.HtmlBody;
            m.Attachments = attachments.Select(a => new StoredAttachment
            {
                Id = _store.NextAttachmentId(),
                Filename = a.Filename,
                ContentType = a.ContentType,
                SizeBytes = a.SizeBytes,
                StoragePath = a.StoragePath,
                Sha256 = a.Sha256,
            }).ToList();
            await _store.PersistMessageAsync(m, c).ConfigureAwait(false);
        }, ct);

    public Task MarkParseFailedAsync(long messageId, string error, CancellationToken ct = default) =>
        _store.WriteAsync(async c =>
        {
            if (!_store.Messages.TryGetValue(messageId, out var m)) return;
            m.ParseError = error;
            await _store.PersistMessageAsync(m, c).ConfigureAwait(false);
        }, ct);

    public Task<int> ReassignByRecipientAsync(long fromAliasId, long toAliasId, string recipient, CancellationToken ct = default) =>
        Reassign(m => m.AliasId == fromAliasId
            && string.Equals(m.Recipient, recipient, StringComparison.OrdinalIgnoreCase), toAliasId, ct);

    public Task<int> ReassignAllAsync(long fromAliasId, long toAliasId, CancellationToken ct = default) =>
        Reassign(m => m.AliasId == fromAliasId, toAliasId, ct);

    public Task<IReadOnlyList<JunkItem>> ListJunkWithPreviewAsync(int limit, int offset, CancellationToken ct = default) =>
        _store.ReadAsync(() => (IReadOnlyList<JunkItem>)_store.Messages.Values
            .Where(m => m.JunkedAt is not null)
            .OrderByDescending(m => m.JunkedAt!.Value).ThenByDescending(m => m.Id)
            .Skip(offset).Take(limit)
            .Select(ToJunkItem).ToList(), ct);

    public Task SetJunkAsync(long messageId, DateTimeOffset junkedAt, long? ruleId, bool manual, CancellationToken ct = default) =>
        _store.WriteAsync(async c =>
        {
            if (!_store.Messages.TryGetValue(messageId, out var m)) return;
            m.JunkedAt = junkedAt;
            m.JunkRuleId = ruleId;
            m.JunkManual = manual;
            m.StateChangedAt = junkedAt;
            await _store.PersistMessageAsync(m, c).ConfigureAwait(false);
        }, ct);

    public Task ClearJunkAsync(long messageId, bool manual, CancellationToken ct = default) =>
        _store.WriteAsync(async c =>
        {
            if (!_store.Messages.TryGetValue(messageId, out var m)) return;
            m.JunkedAt = null;
            m.JunkRuleId = null;
            m.JunkManual = manual;
            m.StateChangedAt = DateTimeOffset.UtcNow;
            await _store.PersistMessageAsync(m, c).ConfigureAwait(false);
        }, ct);

    public Task<IReadOnlyList<SweepCandidate>> ListSweepCandidatesAsync(CancellationToken ct = default) =>
        _store.ReadAsync(() => (IReadOnlyList<SweepCandidate>)Newest(_store.Messages.Values
            .Where(m => m.JunkedAt is null && !m.JunkManual))
            .Select(ToSweepCandidate).ToList(), ct);

    public Task<int> UnsweepByRuleAsync(long ruleId, CancellationToken ct = default) =>
        _store.WriteAsync(async c =>
        {
            var affected = _store.Messages.Values
                .Where(m => m.JunkRuleId == ruleId && !m.JunkManual && m.JunkedAt is not null).ToList();
            foreach (var m in affected)
            {
                m.JunkedAt = null;
                m.JunkRuleId = null;
                m.StateChangedAt = DateTimeOffset.UtcNow;
                await _store.PersistMessageAsync(m, c).ConfigureAwait(false);
            }
            return affected.Count;
        }, ct);

    public Task<IReadOnlyList<long>> ListJunkedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
        _store.ReadAsync(() => (IReadOnlyList<long>)_store.Messages.Values
            .Where(m => m.JunkedAt is not null && m.JunkedAt.Value < cutoff)
            .Select(m => m.Id).ToList(), ct);

    public Task<IReadOnlyList<SweepCandidate>> ListExpiryCandidatesAsync(long aliasId, DateTimeOffset cutoff, CancellationToken ct = default) =>
        _store.ReadAsync(() => (IReadOnlyList<SweepCandidate>)_store.Messages.Values
            .Where(m => m.AliasId == aliasId && m.JunkedAt is null && Effective(m) < cutoff)
            .OrderBy(m => Effective(m)).ThenBy(m => m.Id)
            .Select(ToSweepCandidate).ToList(), ct);

    public Task DeleteAsync(long messageId, CancellationToken ct = default) =>
        _store.WriteAsync(async c =>
        {
            if (!_store.Messages.TryGetValue(messageId, out var m)) return;
            var rawPath = m.RawStoragePath;
            var attachmentPaths = m.Attachments.Select(a => a.StoragePath).ToList();

            _store.Messages.Remove(messageId);
            _store.DeleteMessageFile(m);

            if (!string.IsNullOrEmpty(rawPath))
                await _rawStore.DeleteAsync(rawPath, c).ConfigureAwait(false);
            foreach (var path in attachmentPaths)
                await _rawStore.DeleteAsync(path, c).ConfigureAwait(false);
        }, ct);

    private Task<int> Reassign(Func<StoredMessage, bool> predicate, long toAliasId, CancellationToken ct) =>
        _store.WriteAsync(async c =>
        {
            var affected = _store.Messages.Values.Where(predicate).ToList();
            foreach (var m in affected)
            {
                m.AliasId = toAliasId;
                await _store.PersistMessageAsync(m, c).ConfigureAwait(false);
            }
            return affected.Count;
        }, ct);

    private static IEnumerable<StoredMessage> Newest(IEnumerable<StoredMessage> src) =>
        src.OrderByDescending(m => m.ReceivedAt).ThenByDescending(m => m.Id);

    private static DateTimeOffset Effective(StoredMessage m) => m.StateChangedAt ?? m.ReceivedAt;

    private static InboxItem ToInboxItem(StoredMessage m) => new()
    {
        Id = m.Id, Sender = m.Sender, Subject = m.Subject, Recipient = m.Recipient,
        ReceivedAt = m.ReceivedAt, SizeBytes = m.SizeBytes, Parsed = m.Parsed, Snippet = m.Snippet(SnippetLength),
    };

    private RecentMessage? ToRecent(StoredMessage m)
    {
        if (!_store.Aliases.TryGetValue(m.AliasId, out var a)) return null; // mirrors the inner JOIN on aliases
        return new RecentMessage
        {
            Id = m.Id, AliasId = m.AliasId, Sender = m.Sender, Subject = m.Subject, Recipient = m.Recipient,
            ReceivedAt = m.ReceivedAt, SizeBytes = m.SizeBytes, Parsed = m.Parsed, Snippet = m.Snippet(SnippetLength),
            AliasLocalPart = a.Alias.LocalPart, AliasDomain = a.Alias.Domain,
            AliasIsCatchAll = a.Alias.IsCatchAll, AliasColor = a.Alias.Color,
        };
    }

    private JunkItem ToJunkItem(StoredMessage m) => new()
    {
        Id = m.Id, Sender = m.Sender, Subject = m.Subject, Recipient = m.Recipient,
        ReceivedAt = m.ReceivedAt, Parsed = m.Parsed, Snippet = m.Snippet(SnippetLength),
        JunkedAt = m.JunkedAt, JunkManual = m.JunkManual, JunkRuleId = m.JunkRuleId,
        JunkRuleName = m.JunkRuleId is { } rid ? _store.Rules.FirstOrDefault(r => r.Id == rid)?.Name : null,
    };

    private static SweepCandidate ToSweepCandidate(StoredMessage m) => new()
    {
        Id = m.Id, AliasId = m.AliasId, Sender = m.Sender, Recipient = m.Recipient,
        Subject = m.Subject, ReceivedAt = m.ReceivedAt, Parsed = m.Parsed,
    };
}
