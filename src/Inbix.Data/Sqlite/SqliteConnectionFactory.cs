using System.Data;
using System.Data.Common;
using Inbix.Core.Abstractions;
using Inbix.Core.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Inbix.Data.Sqlite;

/// <summary>
/// SQLite implementation of <see cref="IDbConnectionFactory"/>. Ensures the database directory exists
/// and applies per-connection pragmas (WAL, foreign keys, busy timeout).
///
/// Two modes:
/// <list type="bullet">
/// <item><b>Exclusive locking (default):</b> a single shared connection guarded by a one-at-a-time gate.
/// <c>locking_mode=EXCLUSIVE</c> is applied <i>before</i> WAL so SQLite keeps the WAL index in heap memory
/// rather than a <c>-shm</c> file — which is what allows WAL to work on NFS/SMB. Each
/// <see cref="OpenConnectionAsync"/> hands out a non-owning <see cref="LeasedSqliteConnection"/> whose
/// disposal releases the gate (the shared connection stays open to hold the exclusive lock).</item>
/// <item><b>Pooled</b> (<see cref="DatabaseOptions.PooledConnections"/>=true, local disk only): a fresh
/// pooled connection per call — full read/write concurrency, but unsafe on a network filesystem.</item>
/// </list>
/// </summary>
public sealed class SqliteConnectionFactory : IDbConnectionFactory, IAsyncDisposable, IDisposable
{
    // PRAGMA values cannot be parameterised, so the configurable journal mode is whitelisted to keep it
    // out of any injection surface.
    private static readonly HashSet<string> AllowedJournalModes =
        new(StringComparer.OrdinalIgnoreCase) { "WAL", "DELETE", "TRUNCATE", "PERSIST", "MEMORY", "OFF" };

    private readonly string _connectionString;
    private readonly string _pragmas;
    private readonly bool _exclusive;

    // Exclusive mode only: serialise all access through one shared connection.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _shared;

    public SqliteConnectionFactory(IOptions<InbixOptions> options)
    {
        var db = options.Value.Database;
        _exclusive = !db.PooledConnections; // exclusive locking is the default; opt out for local-disk pooling

        var journalMode = (db.JournalMode ?? "WAL").Trim();
        if (!AllowedJournalModes.Contains(journalMode))
            throw new InvalidOperationException(
                $"Invalid Inbix:Database:JournalMode '{journalMode}'. Allowed: {string.Join(", ", AllowedJournalModes)}.");

        var builder = new SqliteConnectionStringBuilder(db.ConnectionString)
        {
            ForeignKeys = true,
            DefaultTimeout = 30
        };

        // A single long-lived connection gains nothing from pooling, and Pooling=false guarantees the
        // exclusive lock is released the instant the connection is disposed (clean shutdown/restart).
        if (_exclusive)
            builder.Pooling = false;

        EnsureDirectory(builder.DataSource);
        _connectionString = builder.ToString();

        // Order matters in exclusive mode: locking_mode=EXCLUSIVE MUST precede the first WAL operation so
        // SQLite uses an in-heap WAL index (no -shm file). That is exactly what makes WAL usable on a
        // network filesystem, which cannot provide the shared memory a -shm file needs.
        _pragmas = _exclusive
            ? $"PRAGMA locking_mode=EXCLUSIVE; PRAGMA journal_mode={journalMode}; PRAGMA busy_timeout=30000; PRAGMA synchronous=NORMAL;"
            : $"PRAGMA journal_mode={journalMode}; PRAGMA busy_timeout=30000; PRAGMA synchronous=NORMAL;";
    }

    public string Provider => "sqlite";

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        if (!_exclusive)
        {
            var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ApplyPragmasAsync(connection, ct).ConfigureAwait(false);
            return connection;
        }

        // Exclusive mode: acquire the gate, ensure the shared connection is healthy, and hand out a
        // non-owning lease. The caller's dispose releases the gate (see LeasedSqliteConnection).
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_shared is null || _shared.State != ConnectionState.Open)
            {
                // Rebuild a connection that was never opened or has broken (e.g. an NFS hiccup). If a
                // genuinely broken connection cannot be rebuilt the health check will report unhealthy
                // and Docker can restart the container with a fresh handle.
                if (_shared is not null)
                    await _shared.DisposeAsync().ConfigureAwait(false);

                _shared = new SqliteConnection(_connectionString);
                await _shared.OpenAsync(ct).ConfigureAwait(false);
                await ApplyPragmasAsync(_shared, ct).ConfigureAwait(false);
            }

            return new LeasedSqliteConnection(_shared, _gate);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private async Task ApplyPragmasAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = _pragmas;
        await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_shared is not null)
        {
            await _shared.DisposeAsync().ConfigureAwait(false);
            _shared = null;
        }
        _gate.Dispose();
    }

    // The DI container calls Dispose() when the provider is disposed synchronously; without this it
    // throws because an async-only singleton can't be disposed on the sync path.
    public void Dispose()
    {
        _shared?.Dispose();
        _shared = null;
        _gate.Dispose();
    }

    private static void EnsureDirectory(string dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource)) return;
        // Skip special in-memory / shared sources.
        if (dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)) return;

        var full = Path.GetFullPath(dataSource);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
