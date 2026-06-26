using Inbix.Core.Domain;

namespace Inbix.Core.Abstractions;

public interface IIdentityRepository
{
    Task<IReadOnlyList<Identity>> ListAsync(CancellationToken ct = default);

    Task<Identity?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>The identity linked to an alias, or null. (alias_id is unique, so at most one.)</summary>
    Task<Identity?> GetByAliasIdAsync(long aliasId, CancellationToken ct = default);

    /// <summary>Insert a new identity. Returns the created row. Throws on a duplicate alias link.</summary>
    Task<Identity> CreateAsync(Identity identity, CancellationToken ct = default);

    /// <summary>Update all editable fields by <see cref="Identity.Id"/>. Returns the row, or null if missing.</summary>
    Task<Identity?> UpdateAsync(Identity identity, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);
}
