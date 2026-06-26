using Inbix.Core.Domain;

namespace Inbix.Core.Abstractions;

/// <summary>Identity orchestration with audit logging. Wraps <see cref="IIdentityRepository"/>.</summary>
public interface IIdentityService
{
    Task<Identity> CreateAsync(Identity identity, CancellationToken ct = default);

    Task<Identity?> UpdateAsync(Identity identity, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Link an alias to an identity, or unlink the alias when <paramref name="identityId"/> is null.
    /// One identity may be linked to many aliases. Returns the identity now linked to the alias
    /// (null when unlinked).
    /// </summary>
    Task<Identity?> LinkAliasAsync(long aliasId, long? identityId, CancellationToken ct = default);
}
