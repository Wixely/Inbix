using Dapper;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;

namespace Inbix.Data.Repositories;

public sealed class MessageRepository : IMessageRepository
{
    private const string MessageColumns =
        "id, alias_id, smtp_session_id, recipient, sender, subject, message_id_header, " +
        "received_at, size_bytes, raw_storage_path, parsed, parse_error, " +
        "junked_at, junk_rule_id, junk_manual, state_changed_at";

    private readonly IDbConnectionFactory _factory;
    private readonly IRawMessageStore _rawStore;

    public MessageRepository(IDbConnectionFactory factory, IRawMessageStore rawStore)
    {
        _factory = factory;
        _rawStore = rawStore;
    }

    public async Task<long> CreateAsync(Message m, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleAsync<long>(
            """
            INSERT INTO messages
                (alias_id, smtp_session_id, recipient, sender, subject, message_id_header,
                 received_at, size_bytes, raw_storage_path, parsed, parse_error,
                 junked_at, junk_rule_id, junk_manual)
            VALUES
                (@AliasId, @SmtpSessionId, @Recipient, @Sender, @Subject, @MessageIdHeader,
                 @ReceivedAt, @SizeBytes, @RawStoragePath, @Parsed, @ParseError,
                 @JunkedAt, @JunkRuleId, @JunkManual)
            RETURNING id;
            """, m).ConfigureAwait(false);
    }

    public async Task<Message?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<Message>(
            $"SELECT {MessageColumns} FROM messages WHERE id = @id;", new { id }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Message>> ListByAliasAsync(long aliasId, int limit, int offset, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<Message>(
            $"""
             SELECT {MessageColumns} FROM messages
             WHERE alias_id = @aliasId AND junked_at IS NULL
             ORDER BY received_at DESC, id DESC
             LIMIT @limit OFFSET @offset;
             """, new { aliasId, limit, offset }).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<InboxItem>> ListByAliasWithPreviewAsync(long aliasId, int limit, int offset, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<InboxItem>(
            """
            SELECT m.id, m.sender, m.subject, m.recipient, m.received_at, m.size_bytes, m.parsed,
                   substr(b.text_body, 1, 200) AS snippet
            FROM messages m
            LEFT JOIN message_bodies b ON b.message_id = m.id
            WHERE m.alias_id = @aliasId AND m.junked_at IS NULL
            ORDER BY m.received_at DESC, m.id DESC
            LIMIT @limit OFFSET @offset;
            """, new { aliasId, limit, offset }).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<RecentMessage>> ListRecentAsync(int limit, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<RecentMessage>(
            """
            SELECT m.id, m.alias_id, m.sender, m.subject, m.recipient, m.received_at, m.size_bytes, m.parsed,
                   substr(b.text_body, 1, 200) AS snippet,
                   a.local_part AS alias_local_part, a.domain AS alias_domain, a.is_catch_all AS alias_is_catch_all,
                   a.color AS alias_color
            FROM messages m
            JOIN aliases a ON a.id = m.alias_id
            LEFT JOIN message_bodies b ON b.message_id = m.id
            WHERE m.junked_at IS NULL
            ORDER BY m.received_at DESC, m.id DESC
            LIMIT @limit;
            """, new { limit }).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<MessageBody?> GetBodyAsync(long messageId, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<MessageBody>(
            "SELECT id, message_id, text_body, html_body FROM message_bodies WHERE message_id = @messageId;",
            new { messageId }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Attachment>> ListAttachmentsAsync(long messageId, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<Attachment>(
            "SELECT id, message_id, filename, content_type, size_bytes, storage_path, sha256 " +
            "FROM attachments WHERE message_id = @messageId ORDER BY id;",
            new { messageId }).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<Attachment?> GetAttachmentAsync(long attachmentId, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<Attachment>(
            "SELECT id, message_id, filename, content_type, size_bytes, storage_path, sha256 " +
            "FROM attachments WHERE id = @attachmentId;",
            new { attachmentId }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Message>> ClaimUnparsedAsync(int batchSize, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<Message>(
            $"""
             SELECT {MessageColumns} FROM messages
             WHERE parsed = 0 AND parse_error IS NULL
             ORDER BY id
             LIMIT @batchSize;
             """, new { batchSize }).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task MarkParsedAsync(long messageId, string? subject, string? sender, string? messageIdHeader,
        MessageBody body, IReadOnlyList<Attachment> attachments, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await c.ExecuteAsync(
                """
                UPDATE messages
                SET subject = @subject, sender = @sender, message_id_header = @messageIdHeader,
                    parsed = 1, parse_error = NULL
                WHERE id = @messageId;
                """,
                new { messageId, subject, sender, messageIdHeader }, tx).ConfigureAwait(false);

            await c.ExecuteAsync(
                """
                INSERT INTO message_bodies (message_id, text_body, html_body)
                VALUES (@messageId, @TextBody, @HtmlBody);
                """,
                new { messageId, body.TextBody, body.HtmlBody }, tx).ConfigureAwait(false);

            foreach (var a in attachments)
            {
                await c.ExecuteAsync(
                    """
                    INSERT INTO attachments (message_id, filename, content_type, size_bytes, storage_path, sha256)
                    VALUES (@messageId, @Filename, @ContentType, @SizeBytes, @StoragePath, @Sha256);
                    """,
                    new { messageId, a.Filename, a.ContentType, a.SizeBytes, a.StoragePath, a.Sha256 }, tx)
                    .ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task MarkParseFailedAsync(long messageId, string error, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await c.ExecuteAsync(
            "UPDATE messages SET parse_error = @error WHERE id = @messageId;",
            new { messageId, error }).ConfigureAwait(false);
    }

    public async Task<int> ReassignByRecipientAsync(long fromAliasId, long toAliasId, string recipient, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.ExecuteAsync(
            "UPDATE messages SET alias_id = @toAliasId WHERE alias_id = @fromAliasId AND lower(recipient) = lower(@recipient);",
            new { fromAliasId, toAliasId, recipient }).ConfigureAwait(false);
    }

    public async Task<int> ReassignAllAsync(long fromAliasId, long toAliasId, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.ExecuteAsync(
            "UPDATE messages SET alias_id = @toAliasId WHERE alias_id = @fromAliasId;",
            new { fromAliasId, toAliasId }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JunkItem>> ListJunkWithPreviewAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<JunkItem>(
            """
            SELECT m.id, m.sender, m.subject, m.recipient, m.received_at, m.parsed,
                   substr(b.text_body, 1, 200) AS snippet,
                   m.junked_at, m.junk_manual, m.junk_rule_id, r.name AS junk_rule_name
            FROM messages m
            LEFT JOIN message_bodies b ON b.message_id = m.id
            LEFT JOIN blacklist_rules r ON r.id = m.junk_rule_id
            WHERE m.junked_at IS NOT NULL
            ORDER BY m.junked_at DESC, m.id DESC
            LIMIT @limit OFFSET @offset;
            """, new { limit, offset }).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task SetJunkAsync(long messageId, DateTimeOffset junkedAt, long? ruleId, bool manual, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await c.ExecuteAsync(
            "UPDATE messages SET junked_at = @junkedAt, junk_rule_id = @ruleId, junk_manual = @manual, state_changed_at = @junkedAt WHERE id = @messageId;",
            new { messageId, junkedAt, ruleId, manual }).ConfigureAwait(false);
    }

    public async Task ClearJunkAsync(long messageId, bool manual, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await c.ExecuteAsync(
            "UPDATE messages SET junked_at = NULL, junk_rule_id = NULL, junk_manual = @manual, state_changed_at = @now WHERE id = @messageId;",
            new { messageId, manual, now = DateTimeOffset.UtcNow }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SweepCandidate>> ListSweepCandidatesAsync(CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<SweepCandidate>(
            """
            SELECT id, alias_id, sender, recipient, subject, received_at, parsed
            FROM messages
            WHERE junked_at IS NULL AND junk_manual = 0
            ORDER BY received_at DESC, id DESC;
            """).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<int> UnsweepByRuleAsync(long ruleId, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.ExecuteAsync(
            "UPDATE messages SET junked_at = NULL, junk_rule_id = NULL, state_changed_at = @now WHERE junk_rule_id = @ruleId AND junk_manual = 0;",
            new { ruleId, now = DateTimeOffset.UtcNow }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SweepCandidate>> ListExpiryCandidatesAsync(long aliasId, DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<SweepCandidate>(
            """
            SELECT id, alias_id, sender, recipient, subject, received_at, parsed
            FROM messages
            WHERE alias_id = @aliasId AND junked_at IS NULL
              AND COALESCE(state_changed_at, received_at) < @cutoff
            ORDER BY COALESCE(state_changed_at, received_at) ASC, id ASC;
            """, new { aliasId, cutoff }).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<long>> ListJunkedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<long>(
            "SELECT id FROM messages WHERE junked_at IS NOT NULL AND junked_at < @cutoff;",
            new { cutoff }).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task DeleteAsync(long messageId, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        // Collect the on-disk paths first so we can remove the files after the rows are gone.
        var rawPath = await c.QuerySingleOrDefaultAsync<string?>(
            "SELECT raw_storage_path FROM messages WHERE id = @messageId;", new { messageId }).ConfigureAwait(false);
        var attachmentPaths = (await c.QueryAsync<string>(
            "SELECT storage_path FROM attachments WHERE message_id = @messageId;", new { messageId }).ConfigureAwait(false)).ToList();

        await using (var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await c.ExecuteAsync("DELETE FROM attachments WHERE message_id = @messageId;", new { messageId }, tx).ConfigureAwait(false);
                await c.ExecuteAsync("DELETE FROM message_bodies WHERE message_id = @messageId;", new { messageId }, tx).ConfigureAwait(false);
                await c.ExecuteAsync("DELETE FROM messages WHERE id = @messageId;", new { messageId }, tx).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        // Rows are gone; remove the backing files (best-effort).
        if (!string.IsNullOrEmpty(rawPath))
            await _rawStore.DeleteAsync(rawPath, ct).ConfigureAwait(false);
        foreach (var path in attachmentPaths)
            await _rawStore.DeleteAsync(path, ct).ConfigureAwait(false);
    }
}
