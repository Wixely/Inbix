using Dapper;
using Inbix.Core.Abstractions;

namespace Inbix.Data.Repositories;

public sealed class SettingsRepository : ISettingsRepository
{
    private readonly IDbConnectionFactory _factory;

    public SettingsRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<string?>(
            "SELECT value FROM settings WHERE key = @key;", new { key }).ConfigureAwait(false);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var c = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await c.ExecuteAsync(
            "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
            new { key, value }).ConfigureAwait(false);
    }

    public async Task<bool> GetBoolAsync(string key, bool defaultValue = false, CancellationToken ct = default)
    {
        var v = await GetAsync(key, ct).ConfigureAwait(false);
        if (v is null) return defaultValue;
        return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    public Task SetBoolAsync(string key, bool value, CancellationToken ct = default)
        => SetAsync(key, value ? "1" : "0", ct);
}
