using Dapper;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;

namespace Inbix.Data.Repositories;

public sealed class AuditRepository : IAuditRepository
{
    private readonly IDbConnectionFactory _factory;

    public AuditRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task WriteAsync(AuditEntry e, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await c.ExecuteAsync(
            """
            INSERT INTO audit_log (actor, action, target_type, target_id, created_at, details)
            VALUES (@Actor, @Action, @TargetType, @TargetId, @CreatedAt, @Details);
            """,
            new { e.Actor, e.Action, e.TargetType, e.TargetId, CreatedAt = e.CreatedAt == default ? DateTimeOffset.UtcNow : e.CreatedAt, e.Details })
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<AuditEntry>(
            """
            SELECT id, actor, action, target_type, target_id, created_at, details
            FROM audit_log ORDER BY id DESC LIMIT @limit OFFSET @offset;
            """, new { limit, offset }).ConfigureAwait(false);
        return rows.ToList();
    }
}
