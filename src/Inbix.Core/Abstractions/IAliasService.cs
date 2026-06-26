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

    /// <summary>Dry run: count + sample of messages that an expiry of <paramref name="days"/> days would delete from a mailbox.</summary>
    Task<SweepPreview> ExpiryPreviewAsync(long aliasId, int days, int sampleSize = 10, CancellationToken ct = default);

    /// <summary>
    /// Persist a mailbox's expiry settings. When enabling with <paramref name="deleteNow"/>, immediately
    /// deletes the mail already past the threshold. Returns how many were deleted now.
    /// </summary>
    Task<int> SetExpiryAsync(long aliasId, bool enabled, int days, bool deleteNow, CancellationToken ct = default);
}
