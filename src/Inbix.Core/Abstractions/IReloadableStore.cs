namespace Inbix.Core.Abstractions;

/// <summary>
/// A storage backend whose in-memory state is loaded from disk and can be re-read on demand. The JSON
/// file/folder store reads everything into memory at startup and only writes on change, so editing files
/// on disk (or recovering from a backup) needs a reload to take effect. SQL providers register a no-op.
/// </summary>
public interface IReloadableStore
{
    /// <summary>True when this backend actually re-reads from disk (JSON mode). False for SQL providers.</summary>
    bool CanReload { get; }

    /// <summary>Re-read the store from disk into memory, replacing the current in-memory state.</summary>
    Task ReloadAsync(CancellationToken ct = default);
}
