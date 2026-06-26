using Inbix.Core.Domain;

namespace Inbix.Core.Abstractions;

/// <summary>Identity orchestration with audit logging. Wraps <see cref="IIdentityRepository"/>.</summary>
public interface IIdentityService
{
    Task<Identity> CreateAsync(Identity identity, CancellationToken ct = default);

    Task<Identity?> UpdateAsync(Identity identity, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Link the identity to an alias (or unlink with <paramref name="aliasId"/> = null). When linking,
    /// the identity's email is auto-filled from the alias address. Returns the updated row, or null.
    /// </summary>
    Task<Identity?> LinkAsync(long id, long? aliasId, CancellationToken ct = default);
}
