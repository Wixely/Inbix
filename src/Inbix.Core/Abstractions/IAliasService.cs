using Inbix.Core.Domain;

namespace Inbix.Core.Abstractions;

/// <summary>Result of creating an alias, including any catch-all mail migrated into it.</summary>
public sealed record AliasCreated(Alias Alias, int MigratedFromCatchAll);

/// <summary>
/// Alias create/delete with catch-all migration: creating an alias claims existing catch-all mail
/// addressed to it; deleting an alias hands its mail back to the catch-all.
/// </summary>
public interface IAliasService
{
    Task<AliasCreated> CreateAsync(string localPart, string domain, string? notes, CancellationToken ct = default);

    /// <summary>Delete an alias, moving its messages to the catch-all. Returns the number moved. Throws for the catch-all.</summary>
    Task<int> DeleteAsync(long aliasId, CancellationToken ct = default);
}
