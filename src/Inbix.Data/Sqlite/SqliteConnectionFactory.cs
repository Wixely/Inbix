using System.Data.Common;
using Inbix.Core.Abstractions;
using Inbix.Core.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Inbix.Data.Sqlite;

/// <summary>
/// SQLite implementation of <see cref="IDbConnectionFactory"/>. Ensures the database
/// directory exists and applies per-connection pragmas (WAL, foreign keys, busy timeout).
/// </summary>
public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(IOptions<InbixOptions> options)
    {
        var db = options.Value.Database;
        var builder = new SqliteConnectionStringBuilder(db.ConnectionString)
        {
            ForeignKeys = true,
            DefaultTimeout = 30
        };

        EnsureDirectory(builder.DataSource);
        _connectionString = builder.ToString();
    }

    public string Provider => "sqlite";

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA synchronous=NORMAL;";
        await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return connection;
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
