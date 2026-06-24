using Inbix.Core.Domain;

namespace Inbix.Core.Abstractions;

public interface IAliasRepository
{
    /// <summary>Look up an alias by its address parts. Used during RCPT TO validation.</summary>
    Task<Alias?> FindAsync(string localPart, string domain, CancellationToken ct = default);

    /// <summary>The single permanent catch-all record, or null if absent.</summary>
    Task<Alias?> GetCatchAllAsync(CancellationToken ct = default);

    Task<Alias?> GetByIdAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<Alias>> ListAsync(CancellationToken ct = default);

    /// <summary>Create an alias. Returns the created row. Throws if the address already exists.</summary>
    Task<Alias> CreateAsync(string localPart, string domain, string? notes, CancellationToken ct = default);

    /// <summary>Enable/disable an alias and/or update notes. Returns the updated row, or null if missing.</summary>
    Task<Alias?> UpdateAsync(long id, bool? enabled, string? notes, CancellationToken ct = default);
}
