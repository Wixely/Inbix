using Inbix.Core.Abstractions;

namespace Inbix.Data.Services;

/// <summary>
/// <see cref="IReloadableStore"/> for SQL providers, which read from the database on every query and so
/// have nothing to reload. Lets the diagnostics page inject the service unconditionally.
/// </summary>
public sealed class NoOpReloadableStore : IReloadableStore
{
    public bool CanReload => false;

    public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
}
