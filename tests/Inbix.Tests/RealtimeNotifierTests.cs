using System.Text;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Inbix.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inbix.Tests;

/// <summary>The inbound sink publishes an <see cref="IInboxNotifier"/> event so the UI can update live.</summary>
public sealed class RealtimeNotifierTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inbix-rt-" + Guid.NewGuid().ToString("N"));
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

    private InboundMessage Msg(string recipient) => new()
    {
        Recipient = recipient,
        Sender = "a@b.com",
        RawMime = Encoding.ASCII.GetBytes("From: a@b.com\r\nSubject: Hi\r\n\r\nBody\r\n"),
        ReceivedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Storing_A_Message_Raises_Arrived_For_Its_Alias()
    {
        var alias = await _sp.GetRequiredService<IAliasRepository>().CreateAsync("spotify", "mydomain.com", null);
        var sink = _sp.GetRequiredService<IInboundMessageSink>();

        var events = new List<InboxEvent>();
        _sp.GetRequiredService<IInboxNotifier>().Received += e => events.Add(e);

        var result = await sink.SaveAsync(Msg("spotify@mydomain.com"));

        Assert.Equal(InboundSaveResult.Stored, result);
        var e = Assert.Single(events);
        Assert.Equal(InboxEventKind.Arrived, e.Kind);
        Assert.Equal(alias.Id, e.AliasId);
        Assert.False(e.Junked);
        Assert.True(e.MessageId > 0);
    }

    [Fact]
    public async Task Unknown_Recipient_Raises_No_Event()
    {
        var sink = _sp.GetRequiredService<IInboundMessageSink>();

        var events = new List<InboxEvent>();
        _sp.GetRequiredService<IInboxNotifier>().Received += e => events.Add(e);

        var result = await sink.SaveAsync(Msg("ghost@mydomain.com"));

        Assert.Equal(InboundSaveResult.UnknownRecipient, result);
        Assert.Empty(events);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _sp?.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
