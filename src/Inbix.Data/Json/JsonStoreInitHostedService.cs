using Microsoft.Extensions.Hosting;

namespace Inbix.Data.Json;

/// <summary>
/// Loads the JSON store from disk into memory at startup. Registered before every other hosted service
/// (seeder, SMTP listener, parser worker, retention) so the in-memory index is ready before they run.
/// </summary>
public sealed class JsonStoreInitHostedService : IHostedService
{
    private readonly JsonDataStore _store;

    public JsonStoreInitHostedService(JsonDataStore store) => _store = store;

    public Task StartAsync(CancellationToken cancellationToken) => _store.InitializeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
