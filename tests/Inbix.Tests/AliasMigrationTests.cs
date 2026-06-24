using System.Text;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Inbix.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inbix.Tests;

public sealed class AliasMigrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inbix-migrate-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var options = new InbixOptions
        {
            Domains = ["mydomain.com"],
            Database = { Provider = "sqlite", ConnectionString = $"Data Source={Path.Combine(_tempDir, "test.db")}" },
            Storage = { RawPath = Path.Combine(_tempDir, "raw") }
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<InbixOptions>>(Options.Create(options));
        services.AddInbixData();
        _sp = services.BuildServiceProvider();
        await _sp.GetRequiredService<IMigrationRunner>().MigrateAsync();
    }

    private async Task DeliverAsync(string recipient)
    {
        var raw = Encoding.ASCII.GetBytes($"From: x@y.com\r\nTo: {recipient}\r\nSubject: hi\r\n\r\nbody\r\n");
        var result = await _sp.GetRequiredService<IInboundMessageSink>().SaveAsync(new InboundMessage
        {
            Recipient = recipient,
            RawMime = raw,
            ReceivedAt = DateTimeOffset.UtcNow
        });
        Assert.Equal(InboundSaveResult.Stored, result);
    }

    private async Task<int> CountAsync(long aliasId) =>
        (await _sp.GetRequiredService<IMessageRepository>().ListByAliasAsync(aliasId, 100, 0)).Count;

    [Fact]
    public async Task Creating_Alias_Claims_Existing_CatchAll_Mail()
    {
        var aliases = _sp.GetRequiredService<IAliasRepository>();
        var service = _sp.GetRequiredService<IAliasService>();

        var catchAll = await aliases.GetCatchAllAsync();
        await aliases.UpdateAsync(catchAll!.Id, enabled: true, notes: null);

        await DeliverAsync("spotify@mydomain.com");   // lands in the catch-all (no alias yet)
        await DeliverAsync("other@mydomain.com");      // should stay in the catch-all
        Assert.Equal(2, await CountAsync(catchAll.Id));

        var (alias, migrated) = await service.CreateAsync("spotify", "mydomain.com", null);

        Assert.Equal(1, migrated);
        Assert.Equal(1, await CountAsync(alias.Id));       // spotify now owns its message
        Assert.Equal(1, await CountAsync(catchAll.Id));    // the unrelated one remains
    }

    [Fact]
    public async Task Deleting_Alias_Moves_Its_Mail_To_CatchAll()
    {
        var aliases = _sp.GetRequiredService<IAliasRepository>();
        var service = _sp.GetRequiredService<IAliasService>();
        var catchAll = await aliases.GetCatchAllAsync();

        var (github, _) = await service.CreateAsync("github", "mydomain.com", null);
        await DeliverAsync("github@mydomain.com");
        await DeliverAsync("github@mydomain.com");
        Assert.Equal(2, await CountAsync(github.Id));

        var moved = await service.DeleteAsync(github.Id);

        Assert.Equal(2, moved);
        Assert.Null(await aliases.GetByIdAsync(github.Id));      // alias gone
        Assert.Equal(2, await CountAsync(catchAll!.Id));         // its mail preserved under catch-all
    }

    [Fact]
    public async Task CatchAll_Cannot_Be_Deleted()
    {
        var aliases = _sp.GetRequiredService<IAliasRepository>();
        var service = _sp.GetRequiredService<IAliasService>();
        var catchAll = await aliases.GetCatchAllAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(catchAll!.Id));
        Assert.NotNull(await aliases.GetCatchAllAsync()); // still there
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _sp?.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
