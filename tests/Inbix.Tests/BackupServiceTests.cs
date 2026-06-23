using Inbix.Core.Abstractions;
using Inbix.Core.Options;
using Inbix.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inbix.Tests;

public sealed class BackupServiceTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inbix-backup-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var options = new InbixOptions
        {
            Domains = ["mydomain.com"],
            Database = { Provider = "sqlite", ConnectionString = $"Data Source={Path.Combine(_tempDir, "test.db")}" },
            Storage = { RawPath = Path.Combine(_tempDir, "raw") },
            Backups = { Enabled = true, Directory = Path.Combine(_tempDir, "backups"), RetentionCount = 2 }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<InbixOptions>>(Options.Create(options));
        services.AddInbixData();
        _sp = services.BuildServiceProvider();

        await _sp.GetRequiredService<IMigrationRunner>().MigrateAsync();
    }

    [Fact]
    public async Task Backup_Is_A_Consistent_Restorable_Copy()
    {
        await _sp.GetRequiredService<IAliasRepository>().CreateAsync("spotify", "mydomain.com", null);

        var backup = _sp.GetRequiredService<IBackupService>();
        var info = await backup.CreateBackupAsync();

        Assert.True(File.Exists(info.Path));
        Assert.True(info.SizeBytes > 0);
        Assert.Contains(backup.ListBackups(), b => b.FileName == info.FileName);

        // Open the backup independently and confirm the alias is present (i.e. it is restorable).
        await using var conn = new SqliteConnection($"Data Source={info.Path}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM aliases WHERE local_part = 'spotify';";
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Retention_Keeps_Only_Newest()
    {
        var backup = _sp.GetRequiredService<IBackupService>();
        for (var i = 0; i < 4; i++)
            await backup.CreateBackupAsync();

        // RetentionCount = 2
        Assert.Equal(2, backup.ListBackups().Count);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _sp?.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
