using System.Data.Common;

namespace Inbix.Core.Abstractions;

/// <summary>
/// Creates open ADO.NET connections for the configured database provider.
/// Swapping providers (SQLite -> Postgres/SQL Server) means swapping this implementation only.
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>The provider key, e.g. "sqlite".</summary>
    string Provider { get; }

    /// <summary>Open a new connection. Caller disposes it.</summary>
    Task<DbConnection> OpenConnectionAsync(CancellationToken ct = default);
}
