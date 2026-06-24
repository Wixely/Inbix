using Dapper;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;

namespace Inbix.Data.Repositories;

public sealed class AliasRepository : IAliasRepository
{
    private const string Columns =
        "id, local_part, domain, enabled, created_at, disabled_at, notes, is_catch_all";

    private readonly IDbConnectionFactory _factory;

    public AliasRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<Alias?> FindAsync(string localPart, string domain, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<Alias>(
            $"SELECT {Columns} FROM aliases WHERE local_part = @localPart AND domain = @domain AND is_catch_all = 0;",
            new { localPart, domain }).ConfigureAwait(false);
    }

    public async Task<Alias?> GetCatchAllAsync(CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<Alias>(
            $"SELECT {Columns} FROM aliases WHERE is_catch_all = 1 LIMIT 1;").ConfigureAwait(false);
    }

    public async Task<Alias?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<Alias>(
            $"SELECT {Columns} FROM aliases WHERE id = @id;", new { id }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Alias>> ListAsync(CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<Alias>(
            $"SELECT {Columns} FROM aliases ORDER BY local_part, domain;").ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<Alias> CreateAsync(string localPart, string domain, string? notes, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleAsync<Alias>(
            $"""
             INSERT INTO aliases (local_part, domain, enabled, created_at, notes)
             VALUES (@localPart, @domain, 1, @createdAt, @notes)
             RETURNING {Columns};
             """,
            new { localPart, domain, createdAt = DateTimeOffset.UtcNow, notes }).ConfigureAwait(false);
    }

    public async Task<Alias?> UpdateAsync(long id, bool? enabled, string? notes, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<Alias>(
            $"""
             UPDATE aliases
             SET enabled     = COALESCE(@enabled, enabled),
                 notes       = COALESCE(@notes, notes),
                 disabled_at = CASE
                                   WHEN @enabled = 0 AND enabled = 1 THEN @now
                                   WHEN @enabled = 1 THEN NULL
                                   ELSE disabled_at
                               END
             WHERE id = @id
             RETURNING {Columns};
             """,
            new
            {
                id,
                enabled = enabled.HasValue ? (enabled.Value ? 1 : 0) : (int?)null,
                notes,
                now = DateTimeOffset.UtcNow
            }).ConfigureAwait(false);
    }
}
