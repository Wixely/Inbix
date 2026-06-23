using System.Reflection;
using Dapper;
using Inbix.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Inbix.Data.Migrations;

/// <summary>
/// Applies migrations listed in an embedded, ordered version manifest. Applied versions are
/// recorded in <c>schema_migrations</c> so each runs at most once. Migration SQL is shipped as
/// embedded resources under <c>Migrations/&lt;provider&gt;/</c>, keeping the schema versioned in source.
/// </summary>
public sealed class ManifestMigrationRunner : IMigrationRunner
{
    private static readonly Assembly Asm = typeof(ManifestMigrationRunner).Assembly;

    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<ManifestMigrationRunner> _logger;

    public ManifestMigrationRunner(IDbConnectionFactory factory, ILogger<ManifestMigrationRunner> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken ct = default)
    {
        var provider = _factory.Provider;
        var versions = ReadManifest(provider);

        await using var connection = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS schema_migrations (version TEXT PRIMARY KEY, applied_at TEXT NOT NULL);")
            .ConfigureAwait(false);

        var applied = (await connection.QueryAsync<string>("SELECT version FROM schema_migrations;")
            .ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newlyApplied = new List<string>();

        foreach (var version in versions)
        {
            if (applied.Contains(version))
                continue;

            var sql = ReadResource($".{provider}.{version}");
            _logger.LogInformation("Applying migration {Version}", version);

            await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await connection.ExecuteAsync(sql, transaction: tx).ConfigureAwait(false);
                await connection.ExecuteAsync(
                    "INSERT INTO schema_migrations (version, applied_at) VALUES (@version, @appliedAt);",
                    new { version, appliedAt = DateTimeOffset.UtcNow },
                    tx).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                newlyApplied.Add(version);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        if (newlyApplied.Count == 0)
            _logger.LogInformation("Database schema is up to date.");
        else
            _logger.LogInformation("Applied {Count} migration(s): {Versions}", newlyApplied.Count, string.Join(", ", newlyApplied));

        return newlyApplied;
    }

    private static IReadOnlyList<string> ReadManifest(string provider)
    {
        var content = ReadResource($".{provider}.manifest.txt");
        return content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .ToArray();
    }

    private static string ReadResource(string suffix)
    {
        var name = Asm.GetManifestResourceNames()
            .SingleOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded migration resource ending in '{suffix}' was not found.");

        using var stream = Asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
