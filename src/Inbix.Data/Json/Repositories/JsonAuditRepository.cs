using Inbix.Core.Abstractions;
using Inbix.Core.Domain;

namespace Inbix.Data.Json.Repositories;

/// <summary>File-backed <see cref="IAuditRepository"/>; entries are appended to <c>audit.jsonl</c>.</summary>
public sealed class JsonAuditRepository : IAuditRepository
{
    private readonly JsonDataStore _store;

    public JsonAuditRepository(JsonDataStore store) => _store = store;

    public Task WriteAsync(AuditEntry e, CancellationToken ct = default) =>
        _store.WriteAsync(async c =>
        {
            var entry = new AuditEntry
            {
                Id = _store.NextAuditId(),
                Actor = e.Actor,
                Action = e.Action,
                TargetType = e.TargetType,
                TargetId = e.TargetId,
                CreatedAt = e.CreatedAt == default ? DateTimeOffset.UtcNow : e.CreatedAt,
                Details = e.Details,
            };
            _store.Audit.Add(entry);
            await _store.AppendAuditAsync(entry, c).ConfigureAwait(false);
        }, ct);

    public Task<IReadOnlyList<AuditEntry>> ListAsync(int limit, int offset, CancellationToken ct = default) =>
        _store.ReadAsync(() => (IReadOnlyList<AuditEntry>)_store.Audit
            .OrderByDescending(a => a.Id).Skip(offset).Take(limit)
            .Select(Clone).ToList(), ct);

    private static AuditEntry Clone(AuditEntry a) => new()
    {
        Id = a.Id, Actor = a.Actor, Action = a.Action, TargetType = a.TargetType,
        TargetId = a.TargetId, CreatedAt = a.CreatedAt, Details = a.Details,
    };
}
