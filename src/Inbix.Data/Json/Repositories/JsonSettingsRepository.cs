using Inbix.Core.Abstractions;

namespace Inbix.Data.Json.Repositories;

/// <summary>File-backed <see cref="ISettingsRepository"/>; the key/value map lives in <c>settings.json</c>.</summary>
public sealed class JsonSettingsRepository : ISettingsRepository
{
    private readonly JsonDataStore _store;

    public JsonSettingsRepository(JsonDataStore store) => _store = store;

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        _store.ReadAsync(() => _store.Settings.TryGetValue(key, out var v) ? v : null, ct);

    public Task SetAsync(string key, string value, CancellationToken ct = default) =>
        _store.WriteAsync(async c =>
        {
            _store.Settings[key] = value;
            await _store.SaveSettingsAsync(c).ConfigureAwait(false);
        }, ct);

    public async Task<bool> GetBoolAsync(string key, bool defaultValue = false, CancellationToken ct = default)
    {
        var v = await GetAsync(key, ct).ConfigureAwait(false);
        if (v is null) return defaultValue;
        return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    public Task SetBoolAsync(string key, bool value, CancellationToken ct = default)
        => SetAsync(key, value ? "1" : "0", ct);
}
