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

public sealed class AliasExpiryTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inbix-expiry-" + Guid.NewGuid().ToString("N"));
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

    private async Task<long> CreateAliasAsync(string localPart)
    {
        var (alias, _) = await _sp.GetRequiredService<IAliasService>().CreateAsync(localPart, "mydomain.com", null);
        return alias.Id;
    }

    private async Task DeliverAtAsync(string recipient, DateTimeOffset receivedAt)
    {
        var raw = Encoding.ASCII.GetBytes($"From: x@y.com\r\nTo: {recipient}\r\nSubject: hi\r\n\r\nbody\r\n");
        var r = await _sp.GetRequiredService<IInboundMessageSink>().SaveAsync(new InboundMessage
        {
            Recipient = recipient, Sender = "x@y.com", RawMime = raw, ReceivedAt = receivedAt
        });
        Assert.Equal(InboundSaveResult.Stored, r);
    }

    private async Task<long> NewestMessageIdAsync(long aliasId) =>
        (await _sp.GetRequiredService<IMessageRepository>().ListByAliasAsync(aliasId, 1, 0))[0].Id;

    private async Task<int> InboxCountAsync(long aliasId) =>
        (await _sp.GetRequiredService<IMessageRepository>().ListByAliasWithPreviewAsync(aliasId, 200, 0)).Count;

    [Fact]
    public async Task Preview_Counts_Old_NonJunked_Mail()
    {
        var aliasId = await CreateAliasAsync("keep");
        await DeliverAtAsync("keep@mydomain.com", DateTimeOffset.UtcNow.AddDays(-100));
        await DeliverAtAsync("keep@mydomain.com", DateTimeOffset.UtcNow.AddDays(-90));
        await DeliverAtAsync("keep@mydomain.com", DateTimeOffset.UtcNow.AddDays(-5)); // recent

        var preview = await _sp.GetRequiredService<IAliasService>().ExpiryPreviewAsync(aliasId, 60);

        Assert.Equal(2, preview.Count);
        Assert.Equal(2, preview.Sample.Count);
    }

    [Fact]
    public async Task Preview_Excludes_Junked_Mail()
    {
        var aliasId = await CreateAliasAsync("keep");
        await DeliverAtAsync("keep@mydomain.com", DateTimeOffset.UtcNow.AddDays(-100));
        var id = await NewestMessageIdAsync(aliasId);

        await _sp.GetRequiredService<IBlacklistService>().JunkMessageAsync(id); // now junked

        var preview = await _sp.GetRequiredService<IAliasService>().ExpiryPreviewAsync(aliasId, 60);
        Assert.Equal(0, preview.Count); // junked mail is handled by junk retention, not mailbox expiry
    }

    [Fact]
    public async Task Expiry_Counts_From_Last_State_Change_Not_Received()
    {
        var aliasId = await CreateAliasAsync("keep");
        await DeliverAtAsync("keep@mydomain.com", DateTimeOffset.UtcNow.AddDays(-100));
        var id = await NewestMessageIdAsync(aliasId);

        var blacklist = _sp.GetRequiredService<IBlacklistService>();
        await blacklist.JunkMessageAsync(id);     // state change -> now
        await blacklist.UnjunkMessageAsync(id);   // back in the inbox, state change -> now

        // Received 100 days ago, but last moved just now: not expired under a 60-day policy.
        var preview = await _sp.GetRequiredService<IAliasService>().ExpiryPreviewAsync(aliasId, 60);
        Assert.Equal(0, preview.Count);
    }

    [Fact]
    public async Task SetExpiry_DeleteNow_Deletes_Old_And_Persists_Settings()
    {
        var aliasId = await CreateAliasAsync("keep");
        await DeliverAtAsync("keep@mydomain.com", DateTimeOffset.UtcNow.AddDays(-100));
        await DeliverAtAsync("keep@mydomain.com", DateTimeOffset.UtcNow.AddDays(-5)); // recent, survives
        Assert.Equal(2, await InboxCountAsync(aliasId));

        var deleted = await _sp.GetRequiredService<IAliasService>().SetExpiryAsync(aliasId, enabled: true, days: 60, deleteNow: true);

        Assert.Equal(1, deleted);
        Assert.Equal(1, await InboxCountAsync(aliasId));     // the recent one remains

        var alias = await _sp.GetRequiredService<IAliasRepository>().GetByIdAsync(aliasId);
        Assert.True(alias!.ExpiryEnabled);
        Assert.Equal(60, alias.ExpiryDays);
    }

    [Fact]
    public async Task SetExpiry_WithoutDeleteNow_Persists_But_Keeps_Mail()
    {
        var aliasId = await CreateAliasAsync("keep");
        await DeliverAtAsync("keep@mydomain.com", DateTimeOffset.UtcNow.AddDays(-100));

        var deleted = await _sp.GetRequiredService<IAliasService>().SetExpiryAsync(aliasId, enabled: true, days: 60, deleteNow: false);

        Assert.Equal(0, deleted);
        Assert.Equal(1, await InboxCountAsync(aliasId));
        Assert.True((await _sp.GetRequiredService<IAliasRepository>().GetByIdAsync(aliasId))!.ExpiryEnabled);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _sp?.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
