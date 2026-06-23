namespace Inbix.Core.Abstractions;

/// <summary>Applies pending schema migrations tracked by a version manifest.</summary>
public interface IMigrationRunner
{
    /// <summary>Applies any migrations not yet recorded as applied. Returns the versions applied this run.</summary>
    Task<IReadOnlyList<string>> MigrateAsync(CancellationToken ct = default);
}
