using Dapper;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;

namespace Inbix.Data.Repositories;

public sealed class MessageRepository : IMessageRepository
{
    private const string MessageColumns =
        "id, alias_id, smtp_session_id, recipient, sender, subject, message_id_header, " +
        "received_at, size_bytes, raw_storage_path, parsed, parse_error";

    private readonly IDbConnectionFactory _factory;

    public MessageRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> CreateAsync(Message m, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleAsync<long>(
            """
            INSERT INTO messages
                (alias_id, smtp_session_id, recipient, sender, subject, message_id_header,
                 received_at, size_bytes, raw_storage_path, parsed, parse_error)
            VALUES
                (@AliasId, @SmtpSessionId, @Recipient, @Sender, @Subject, @MessageIdHeader,
                 @ReceivedAt, @SizeBytes, @RawStoragePath, @Parsed, @ParseError)
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
             WHERE alias_id = @aliasId
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
            WHERE m.alias_id = @aliasId
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
                   a.local_part AS alias_local_part, a.domain AS alias_domain, a.is_catch_all AS alias_is_catch_all
            FROM messages m
            JOIN aliases a ON a.id = m.alias_id
            LEFT JOIN message_bodies b ON b.message_id = m.id
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
}
