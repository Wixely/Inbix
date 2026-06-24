using System.Text;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Inbix.Data;
using Inbix.Data.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inbix.Tests;

public sealed class CatchAllTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inbix-catchall-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _sp = null!;
    private readonly InbixOptions _options = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        _options.Domains = ["mydomain.com"];
        _options.Database = new DatabaseOptions { Provider = "sqlite", ConnectionString = $"Data Source={Path.Combine(_tempDir, "test.db")}" };
        _options.Storage = new StorageOptions { RawPath = Path.Combine(_tempDir, "raw") };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<InbixOptions>>(Options.Create(_options));
        services.AddInbixData();
        _sp = services.BuildServiceProvider();

        await _sp.GetRequiredService<IMigrationRunner>().MigrateAsync();
    }

    [Fact]
    public async Task CatchAll_Record_Is_Seeded_And_Disabled()
    {
        var catchAll = await _sp.GetRequiredService<IAliasRepository>().GetCatchAllAsync();
        Assert.NotNull(catchAll);
        Assert.True(catchAll!.IsCatchAll);
        Assert.False(catchAll.Enabled);
    }

    [Fact]
    public async Task Disabled_CatchAll_Does_Not_Accept_Unknown()
    {
        var resolver = new CachingAliasResolver(_sp.GetRequiredService<IAliasRepository>(), Options.Create(_options));
        Assert.False(await resolver.IsDeliverableAsync("anything@mydomain.com"));
    }

    [Fact]
    public async Task Enabled_CatchAll_Accepts_Any_Local_On_Accepted_Domain_Only()
    {
        var aliases = _sp.GetRequiredService<IAliasRepository>();
        var catchAll = await aliases.GetCatchAllAsync();
        await aliases.UpdateAsync(catchAll!.Id, enabled: true, notes: null);

        var resolver = new CachingAliasResolver(aliases, Options.Create(_options));
        Assert.True(await resolver.IsDeliverableAsync("literally-anyone@mydomain.com"));
        Assert.False(await resolver.IsDeliverableAsync("anyone@notmydomain.com")); // domain still enforced
    }

    [Fact]
    public async Task Enabled_CatchAll_Stores_Unmatched_Mail_Under_CatchAll()
    {
        var aliases = _sp.GetRequiredService<IAliasRepository>();
        var catchAll = await aliases.GetCatchAllAsync();
        await aliases.UpdateAsync(catchAll!.Id, enabled: true, notes: null);

        var sink = _sp.GetRequiredService<IInboundMessageSink>();
        var result = await sink.SaveAsync(new InboundMessage
        {
            Recipient = "random-service@mydomain.com",
            Sender = "x@y.com",
            RawMime = Encoding.ASCII.GetBytes("From: x@y.com\r\nSubject: hi\r\n\r\nbody\r\n"),
            ReceivedAt = DateTimeOffset.UtcNow
        });

        Assert.Equal(InboundSaveResult.Stored, result);

        var messages = _sp.GetRequiredService<IMessageRepository>();
        var unparsed = await messages.ClaimUnparsedAsync(10);
        var stored = Assert.Single(unparsed);
        Assert.Equal(catchAll.Id, stored.AliasId);
        Assert.Equal("random-service@mydomain.com", stored.Recipient);
    }

    [Fact]
    public async Task Specific_Alias_Still_Wins_Over_CatchAll()
    {
        var aliases = _sp.GetRequiredService<IAliasRepository>();
        var catchAll = await aliases.GetCatchAllAsync();
        await aliases.UpdateAsync(catchAll!.Id, enabled: true, notes: null);
        var spotify = await aliases.CreateAsync("spotify", "mydomain.com", null);

        var sink = _sp.GetRequiredService<IInboundMessageSink>();
        await sink.SaveAsync(new InboundMessage
        {
            Recipient = "spotify@mydomain.com",
            RawMime = Encoding.ASCII.GetBytes("test"),
            ReceivedAt = DateTimeOffset.UtcNow
        });

        var stored = Assert.Single(await _sp.GetRequiredService<IMessageRepository>().ClaimUnparsedAsync(10));
        Assert.Equal(spotify.Id, stored.AliasId); // not the catch-all
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _sp?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
