using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace Inbix.Data.Sqlite;

/// <summary>
/// A non-owning <see cref="DbConnection"/> facade over a single shared <see cref="SqliteConnection"/>,
/// handed out by <see cref="SqliteConnectionFactory"/> when exclusive-locking mode is enabled (required
/// for the database to live on a network filesystem). Every member delegates to the shared connection;
/// the only behavioural difference is disposal: instead of closing the underlying connection (which is
/// owned by the factory and must stay open to hold the exclusive lock), disposing the lease releases the
/// factory's access gate so the next operation can proceed.
///
/// This lets all existing call sites keep their natural <c>await using var c = OpenConnectionAsync()</c>
/// idiom unchanged. Commands and transactions are created on the real connection, so Dapper (which binds
/// commands via <c>CreateCommand()</c> and never reassigns <c>cmd.Connection</c>) works transparently.
/// </summary>
internal sealed class LeasedSqliteConnection : DbConnection
{
    private readonly SemaphoreSlim _gate;
    private int _released;

    public LeasedSqliteConnection(SqliteConnection inner, SemaphoreSlim gate)
    {
        Inner = inner;
        _gate = gate;
    }

    /// <summary>The shared, factory-owned connection this lease delegates to.</summary>
    public SqliteConnection Inner { get; }

    [AllowNull]
    public override string ConnectionString
    {
        get => Inner.ConnectionString;
        // The shared connection is configured once by the factory; ignore attempts to reassign it.
        set { }
    }

    public override string Database => Inner.Database;
    public override string DataSource => Inner.DataSource;
    public override string ServerVersion => Inner.ServerVersion;
    public override ConnectionState State => Inner.State;

    public override void ChangeDatabase(string databaseName) => Inner.ChangeDatabase(databaseName);

    // The shared connection is already open and stays open for its lifetime; open/close on the lease
    // are no-ops. (Dapper only opens a connection it found Closed, and State always reports the inner
    // connection's real state, so this path is not exercised in practice.)
    public override void Open() { }
    public override Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override void Close() { }

    protected override DbCommand CreateDbCommand() => Inner.CreateCommand();

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => Inner.BeginTransaction(isolationLevel);

    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel, CancellationToken cancellationToken)
        => await Inner.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);

    protected override void Dispose(bool disposing)
    {
        // Never dispose Inner — it is shared and owned by the factory. Just release the access gate.
        if (disposing)
            ReleaseGate();
    }

    public override ValueTask DisposeAsync()
    {
        ReleaseGate();
        return ValueTask.CompletedTask;
    }

    private void ReleaseGate()
    {
        // Guard against double-release if both Dispose and DisposeAsync run.
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _gate.Release();
    }
}

internal static class LeasedConnectionExtensions
{
    /// <summary>
    /// Returns the underlying <see cref="SqliteConnection"/>, transparently unwrapping a
    /// <see cref="LeasedSqliteConnection"/> if the factory is in exclusive-locking mode. Returns
    /// <c>null</c> if the connection is neither (i.e. a non-SQLite provider).
    /// </summary>
    public static SqliteConnection? AsSqliteConnection(this DbConnection connection) =>
        connection as SqliteConnection ?? (connection as LeasedSqliteConnection)?.Inner;
}
