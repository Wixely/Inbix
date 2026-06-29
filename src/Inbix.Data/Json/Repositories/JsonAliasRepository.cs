using Inbix.Core.Abstractions;
using Inbix.Core.Domain;

namespace Inbix.Data.Json.Repositories;

/// <summary>File/folder-backed <see cref="IAliasRepository"/>. Each alias is a folder under <c>mail/</c>
/// holding an <c>_alias.json</c> plus its message files.</summary>
public sealed class JsonAliasRepository : IAliasRepository
{
    private readonly JsonDataStore _store;

    public JsonAliasRepository(JsonDataStore store) => _store = store;

    public Task<Alias?> FindAsync(string localPart, string domain, CancellationToken ct = default) =>
        _store.ReadAsync(() => _store.Aliases.Values
            .Where(a => !a.Alias.IsCatchAll
                        && string.Equals(a.Alias.LocalPart, localPart, StringComparison.Ordinal)
                        && string.Equals(a.Alias.Domain, domain, StringComparison.Ordinal))
            .Select(a => Clone(a.Alias)).FirstOrDefault(), ct);

    public Task<Alias?> GetCatchAllAsync(CancellationToken ct = default) =>
        _store.ReadAsync(() => _store.Aliases.Values
            .Where(a => a.Alias.IsCatchAll).Select(a => Clone(a.Alias)).FirstOrDefault(), ct);

    public Task<Alias?> GetByIdAsync(long id, CancellationToken ct = default) =>
        _store.ReadAsync(() => _store.Aliases.TryGetValue(id, out var a) ? Clone(a.Alias) : null, ct);

    public Task<IReadOnlyList<Alias>> ListAsync(CancellationToken ct = default) =>
        _store.ReadAsync(() => (IReadOnlyList<Alias>)Ordered(_store.Aliases.Values).Select(a => Clone(a.Alias)).ToList(), ct);

    public Task<IReadOnlyList<Alias>> ListByIdentityAsync(long identityId, CancellationToken ct = default) =>
        _store.ReadAsync(() => (IReadOnlyList<Alias>)Ordered(_store.Aliases.Values
            .Where(a => a.Alias.IdentityId == identityId)).Select(a => Clone(a.Alias)).ToList(), ct);

    public Task<Alias> CreateAsync(string localPart, string domain, string? notes, CancellationToken ct = default) =>
        _store.WriteAsync(async c =>
        {
            if (_store.Aliases.Values.Any(a => !a.Alias.IsCatchAll
                    && string.Equals(a.Alias.LocalPart, localPart, StringComparison.Ordinal)
                    && string.Equals(a.Alias.Domain, domain, StringComparison.Ordinal)))
                // Message mirrors SQLite's so the API/UI duplicate-detection (matches "UNIQUE") works in both modes.
                throw new InvalidOperationException($"UNIQUE constraint failed: an alias for {localPart}@{domain} already exists.");

            var alias = new Alias
            {
                Id = _store.NextAliasId(),
                LocalPart = localPart,
                Domain = domain,
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                Notes = notes,
            };
            var stored = new StoredAlias { Alias = alias, FolderName = _store.AllocateFolderName(localPart, domain, false) };
            _store.Aliases[alias.Id] = stored;
            await _store.PersistAliasAsync(stored, c).ConfigureAwait(false);
            return Clone(alias);
        }, ct);

    public Task<Alias?> UpdateAsync(long id, bool? enabled, string? notes, CancellationToken ct = default) =>
        Mutate(id, a =>
        {
            if (enabled.HasValue)
            {
                if (!enabled.Value && a.Enabled) a.DisabledAt = DateTimeOffset.UtcNow;
                else if (enabled.Value) a.DisabledAt = null;
                a.Enabled = enabled.Value;
            }
            if (notes is not null) a.Notes = notes;
        }, ct);

    public Task<Alias?> UpdateColorAsync(long id, string color, CancellationToken ct = default) =>
        Mutate(id, a => a.Color = color, ct);

    public Task<Alias?> UpdateExpiryAsync(long id, bool enabled, int days, CancellationToken ct = default) =>
        Mutate(id, a => { a.ExpiryEnabled = enabled; a.ExpiryDays = days; }, ct);

    public Task<Alias?> UpdateShortnameAsync(long id, bool enabled, string shortname, CancellationToken ct = default) =>
        Mutate(id, a => { a.Shortname = shortname ?? string.Empty; a.ShortnameEnabled = enabled; }, ct);

    public Task<Alias?> SetIdentityAsync(long id, long? identityId, CancellationToken ct = default) =>
        Mutate(id, a => a.IdentityId = identityId, ct);

    public Task DeleteAsync(long id, CancellationToken ct = default) =>
        _store.WriteAsync(c =>
        {
            if (_store.Aliases.TryGetValue(id, out var stored) && !stored.Alias.IsCatchAll)
            {
                _store.Aliases.Remove(id);
                _store.DeleteAliasFolder(stored);
            }
            return Task.CompletedTask;
        }, ct);

    private Task<Alias?> Mutate(long id, Action<Alias> mutate, CancellationToken ct) =>
        _store.WriteAsync<Alias?>(async c =>
        {
            if (!_store.Aliases.TryGetValue(id, out var stored)) return null;
            mutate(stored.Alias);
            await _store.PersistAliasAsync(stored, c).ConfigureAwait(false);
            return Clone(stored.Alias);
        }, ct);

    private static IEnumerable<StoredAlias> Ordered(IEnumerable<StoredAlias> src) =>
        src.OrderBy(a => a.Alias.LocalPart, StringComparer.Ordinal).ThenBy(a => a.Alias.Domain, StringComparer.Ordinal);

    private static Alias Clone(Alias a) => new()
    {
        Id = a.Id,
        LocalPart = a.LocalPart,
        Domain = a.Domain,
        Enabled = a.Enabled,
        CreatedAt = a.CreatedAt,
        DisabledAt = a.DisabledAt,
        Notes = a.Notes,
        IsCatchAll = a.IsCatchAll,
        Color = a.Color,
        ExpiryEnabled = a.ExpiryEnabled,
        ExpiryDays = a.ExpiryDays,
        Shortname = a.Shortname,
        ShortnameEnabled = a.ShortnameEnabled,
        IdentityId = a.IdentityId,
    };
}
