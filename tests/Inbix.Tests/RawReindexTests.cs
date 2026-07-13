using System.Text;
using Inbix.Core.Abstractions;
using Inbix.Core.Options;
using Inbix.Data;
using Inbix.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inbix.Tests;

/// <summary>Re-indexing recovers messages from the raw store when the index has lost their entries.</summary>
public sealed class RawReindexTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inbix-reidx-" + Guid.NewGuid().ToString("N"));
    private InbixOptions _options = null!;
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        _options = new InbixOptions
        {
            Domains = ["mydomain.com"],
            Database = { Provider = "sqlite", ConnectionString = $"Data Source={Path.Combine(_tempDir, "test.db")}" },
            Storage = { RawPath = Path.Combine(_tempDir, "raw") }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<InbixOptions>>(Options.Create(_options));
        services.AddInbixData();
        _sp = services.BuildServiceProvider();

        await _sp.GetRequiredService<IMigrationRunner>().MigrateAsync();
    }

    private RawReindexer NewReindexer() => new(
        _sp.GetRequiredService<IRawMessageStore>(),
        _sp.GetRequiredService<IMessageRepository>(),
        _sp.GetRequiredService<IAliasRepository>(),
        Options.Create(_options),
        NullLogger<RawReindexer>.Instance);

    private static byte[] Raw(string to) => Encoding.ASCII.GetBytes(
        $"From: noreply@spotify.com\r\nTo: {to}\r\nSubject: Recovered\r\n\r\nhello\r\n");

    [Fact]
    public async Task Reindex_Recovers_Orphan_Raw_And_Is_Idempotent()
    {
        var aliases = _sp.GetRequiredService<IAliasRepository>();
        var messages = _sp.GetRequiredService<IMessageRepository>();
        var rawStore = _sp.GetRequiredService<IRawMessageStore>();

        var alias = await aliases.CreateAsync("spotify", "mydomain.com", null);

        // A raw file exists on disk but no message row references it (simulates a lost index).
        var key = await rawStore.SaveRawAsync(Raw("spotify@mydomain.com"), DateTimeOffset.UtcNow);
        Assert.Empty(await messages.ListByAliasAsync(alias.Id, 50, 0));

        var r1 = await NewReindexer().ReindexAsync();
        Assert.Equal(1, r1.Recovered);
        Assert.Equal(0, r1.Skipped);

        var recovered = Assert.Single(await messages.ListByAliasAsync(alias.Id, 50, 0));
        Assert.Equal(key, recovered.RawStoragePath);
        Assert.False(recovered.Parsed);                     // left for the parser worker
        Assert.Equal("spotify@mydomain.com", recovered.Recipient);

        // Running again recovers nothing — the raw file is now indexed.
        var r2 = await NewReindexer().ReindexAsync();
        Assert.Equal(0, r2.Recovered);
        Assert.Equal(1, r2.Skipped);
        Assert.Single(await messages.ListByAliasAsync(alias.Id, 50, 0));
    }

    [Fact]
    public async Task Unroutable_Recipient_Falls_Back_To_CatchAll()
    {
        var messages = _sp.GetRequiredService<IMessageRepository>();
        var rawStore = _sp.GetRequiredService<IRawMessageStore>();
        var catchAll = await _sp.GetRequiredService<IAliasRepository>().GetCatchAllAsync();
        Assert.NotNull(catchAll);

        await rawStore.SaveRawAsync(Raw("someone@elsewhere.org"), DateTimeOffset.UtcNow); // domain we don't serve

        var r = await NewReindexer().ReindexAsync();
        Assert.Equal(1, r.Recovered);
        Assert.Single(await messages.ListByAliasAsync(catchAll!.Id, 50, 0));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _sp?.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
