using System.Text;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Inbix.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inbix.Tests;

public sealed class DataLayerTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inbix-tests-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task Migrations_Are_Idempotent()
    {
        var applied = await _sp.GetRequiredService<IMigrationRunner>().MigrateAsync();
        Assert.Empty(applied); // already applied during init
    }

    [Fact]
    public async Task Create_Resolve_And_Store_Roundtrip()
    {
        var aliases = _sp.GetRequiredService<IAliasRepository>();
        var resolver = _sp.GetRequiredService<IAliasResolver>();
        var sink = _sp.GetRequiredService<IInboundMessageSink>();
        var messages = _sp.GetRequiredService<IMessageRepository>();

        var alias = await aliases.CreateAsync("spotify", "mydomain.com", "music");
        Assert.True(alias.Id > 0);
        Assert.True(alias.Enabled);

        Assert.True(await resolver.IsDeliverableAsync("spotify@mydomain.com"));
        Assert.False(await resolver.IsDeliverableAsync("unknown@mydomain.com"));
        Assert.False(await resolver.IsDeliverableAsync("spotify@otherdomain.com"));

        var raw = Encoding.ASCII.GetBytes("From: a@b.com\r\nSubject: Hi\r\n\r\nBody\r\n");
        var result = await sink.SaveAsync(new InboundMessage
        {
            Recipient = "spotify@mydomain.com",
            Sender = "a@b.com",
            RawMime = raw,
            ReceivedAt = DateTimeOffset.UtcNow
        });

        Assert.Equal(InboundSaveResult.Stored, result);

        var unparsed = await messages.ClaimUnparsedAsync(10);
        var stored = Assert.Single(unparsed);
        Assert.Equal(alias.Id, stored.AliasId);
        Assert.False(stored.Parsed);
        Assert.True(File.Exists(Path.Combine(_tempDir, "raw", stored.RawStoragePath!.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task Unknown_Recipient_Is_Rejected_By_Sink()
    {
        var sink = _sp.GetRequiredService<IInboundMessageSink>();
        var result = await sink.SaveAsync(new InboundMessage
        {
            Recipient = "ghost@mydomain.com",
            RawMime = Encoding.ASCII.GetBytes("test"),
            ReceivedAt = DateTimeOffset.UtcNow
        });
        Assert.Equal(InboundSaveResult.UnknownRecipient, result);
    }

    [Fact]
    public async Task Disabled_Alias_Is_Not_Deliverable()
    {
        var aliases = _sp.GetRequiredService<IAliasRepository>();
        var resolver = _sp.GetRequiredService<IAliasResolver>();

        var alias = await aliases.CreateAsync("github", "mydomain.com", null);
        await aliases.UpdateAsync(alias.Id, enabled: false, notes: null);

        // New resolver instance to bypass the positive cache from any earlier lookup.
        var fresh = new Inbix.Data.Services.CachingAliasResolver(aliases, Options.Create(new InbixOptions { Domains = ["mydomain.com"] }));
        Assert.False(await fresh.IsDeliverableAsync("github@mydomain.com"));
    }

    [Fact]
    public async Task Creating_Alias_Invalidates_Resolver_Negative_Cache()
    {
        var resolver = _sp.GetRequiredService<IAliasResolver>();
        var aliasService = _sp.GetRequiredService<IAliasService>();

        // Prime the negative cache: not deliverable yet, and the "no" is cached for the TTL.
        Assert.False(await resolver.IsDeliverableAsync("fresh@mydomain.com"));

        await aliasService.CreateAsync("fresh", "mydomain.com", null);

        // Without cache invalidation this would stay false for up to 30s; the new alias must accept mail now.
        Assert.True(await resolver.IsDeliverableAsync("fresh@mydomain.com"));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _sp?.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
