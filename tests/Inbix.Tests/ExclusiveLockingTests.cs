using System.Text;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Inbix.Data;
using Inbix.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inbix.Tests;

/// <summary>
/// Exercises the SQLite connection factory's exclusive-locking mode (PRAGMA locking_mode=EXCLUSIVE +
/// a single shared connection behind a one-at-a-time gate), which is what makes the database usable on
/// a network filesystem (NFS/SMB) where WAL's shared-memory -shm file is unavailable. NFS itself can't
/// be simulated in CI, but these tests prove the whole data stack — Dapper queries, transactions, the
/// online backup, and concurrent access — works correctly through the non-owning lease the factory
/// hands out in this mode.
/// </summary>
public sealed class ExclusiveLockingTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inbix-excl-" + Guid.NewGuid().ToString("N"));

    private ServiceProvider Build(bool exclusive, string journalMode = "WAL")
    {
        Directory.CreateDirectory(_tempDir);
        var options = new InbixOptions
        {
            Domains = ["mydomain.com"],
            Database =
            {
                Provider = "sqlite",
                ConnectionString = $"Data Source={Path.Combine(_tempDir, "test.db")}",
                PooledConnections = !exclusive,
                JournalMode = journalMode
            },
            Storage = { RawPath = Path.Combine(_tempDir, "raw") },
            Backups = { Enabled = true, Directory = Path.Combine(_tempDir, "backups"), RetentionCount = 3 }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<InbixOptions>>(Options.Create(options));
        services.AddInbixData();
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("WAL")]    // WAL-without-shared-memory via exclusive locking — the NFS configuration.
    [InlineData("DELETE")] // Rollback-journal alternative.
    public async Task Full_Roundtrip_Works_In_Exclusive_Mode(string journalMode)
    {
        await using var sp = Build(exclusive: true, journalMode);

        // Migrations run their statements inside transactions — proves transactions work via the lease.
        await sp.GetRequiredService<IMigrationRunner>().MigrateAsync();

        var aliases = sp.GetRequiredService<IAliasRepository>();
        var sink = sp.GetRequiredService<IInboundMessageSink>();
        var messages = sp.GetRequiredService<IMessageRepository>();

        var alias = await aliases.CreateAsync("spotify", "mydomain.com", "music");
        Assert.True(alias.Id > 0);

        var raw = Encoding.ASCII.GetBytes("From: a@b.com\r\nSubject: Hi\r\n\r\nBody\r\n");
        var result = await sink.SaveAsync(new InboundMessage
        {
            Recipient = "spotify@mydomain.com",
            Sender = "a@b.com",
            RawMime = raw,
            ReceivedAt = DateTimeOffset.UtcNow
        });
        Assert.Equal(InboundSaveResult.Stored, result);

        var stored = Assert.Single(await messages.ClaimUnparsedAsync(10));

        // Transactional update path through the lease.
        await messages.MarkParsedAsync(stored.Id, "Hi", "a@b.com", null,
            new MessageBody { TextBody = "Body" }, []);
        var fetched = await messages.GetByIdAsync(stored.Id);
        Assert.NotNull(fetched);
        Assert.True(fetched!.Parsed);

        // Multi-statement transactional delete (rows + raw file) through the lease.
        await messages.DeleteAsync(stored.Id);
        Assert.Null(await messages.GetByIdAsync(stored.Id));
    }

    [Fact]
    public async Task Concurrent_Access_Is_Serialized_Without_Deadlock()
    {
        await using var sp = Build(exclusive: true);
        await sp.GetRequiredService<IMigrationRunner>().MigrateAsync();
        var aliases = sp.GetRequiredService<IAliasRepository>();

        // Baseline accounts for any rows seeded by migrations (e.g. the disabled catch-all).
        var before = (await aliases.ListAsync()).Count;

        // Fan out many concurrent writes + reads against the single shared connection. The gate must
        // serialize them cleanly: no SQLITE_BUSY, and crucially no deadlock — a nested connection open
        // would hang the single-permit gate and fail this test by timeout.
        var work = Enumerable.Range(0, 40).Select(async i =>
        {
            await aliases.CreateAsync($"user{i}", "mydomain.com", null);
            _ = await aliases.ListAsync();
        });
        await Task.WhenAll(work);

        // Every one of the 40 concurrent inserts must have persisted (no lost or duplicated writes).
        Assert.Equal(before + 40, (await aliases.ListAsync()).Count);
    }

    [Fact]
    public async Task Lease_Disposal_Keeps_The_Shared_Connection_Open()
    {
        await using var sp = Build(exclusive: true);
        await sp.GetRequiredService<IMigrationRunner>().MigrateAsync();
        var factory = sp.GetRequiredService<IDbConnectionFactory>();

        // Repeated open/dispose cycles must keep working: each dispose only releases the gate, leaving
        // the underlying connection (and its exclusive lock) intact for the next caller.
        for (var i = 0; i < 5; i++)
        {
            await using var conn = await factory.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1;";
            Assert.Equal(1L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task Backup_Works_In_Exclusive_Mode()
    {
        await using var sp = Build(exclusive: true);
        await sp.GetRequiredService<IMigrationRunner>().MigrateAsync();
        await sp.GetRequiredService<IAliasRepository>().CreateAsync("spotify", "mydomain.com", null);

        // The backup must unwrap the lease to reach the real SqliteConnection for the online backup API.
        var info = await sp.GetRequiredService<IBackupService>().CreateBackupAsync();
        Assert.True(File.Exists(info.Path));

        await using var conn = new SqliteConnection($"Data Source={info.Path}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM aliases WHERE local_part = 'spotify';";
        Assert.Equal(1L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
    }

    [Fact]
    public void Invalid_Journal_Mode_Is_Rejected()
    {
        var options = Options.Create(new InbixOptions
        {
            Database =
            {
                ConnectionString = $"Data Source={Path.Combine(_tempDir, "x.db")}",
                JournalMode = "BOGUS"
            }
        });

        Assert.Throws<InvalidOperationException>(() => new SqliteConnectionFactory(options));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
