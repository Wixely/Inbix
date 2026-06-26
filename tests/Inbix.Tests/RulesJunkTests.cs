using System.Text;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Inbix.Data;
using Inbix.Data.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inbix.Tests;

public sealed class RulesJunkTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inbix-rules-" + Guid.NewGuid().ToString("N"));
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

        // Enable the catch-all so any recipient is accepted (junk-on-arrival lands somewhere).
        var aliases = _sp.GetRequiredService<IAliasRepository>();
        var catchAll = await aliases.GetCatchAllAsync();
        await aliases.UpdateAsync(catchAll!.Id, enabled: true, notes: null);
    }

    // --- helpers ---

    private CachingBlacklistMatcher NewMatcher() =>
        new(_sp.GetRequiredService<IBlacklistRuleRepository>());

    private Task<BlacklistRule> AddRuleAsync(RuleTarget target, RuleMatch match, string pattern, RuleAction action) =>
        _sp.GetRequiredService<IBlacklistRuleRepository>().CreateAsync(new BlacklistRule
        {
            Target = target, MatchType = match, Pattern = pattern, Action = action,
            Enabled = true, CreatedAt = DateTimeOffset.UtcNow
        });

    private async Task<InboundSaveResult> DeliverAsync(string sender, string recipient)
    {
        var raw = Encoding.ASCII.GetBytes($"From: {sender}\r\nTo: {recipient}\r\nSubject: hi\r\n\r\nbody\r\n");
        return await _sp.GetRequiredService<IInboundMessageSink>().SaveAsync(new InboundMessage
        {
            Recipient = recipient, Sender = sender, RawMime = raw, ReceivedAt = DateTimeOffset.UtcNow
        });
    }

    private async Task<int> JunkCountAsync() =>
        (await _sp.GetRequiredService<IMessageRepository>().ListJunkWithPreviewAsync(100, 0)).Count;

    private async Task<int> CatchAllInboxCountAsync()
    {
        var aliases = _sp.GetRequiredService<IAliasRepository>();
        var ca = await aliases.GetCatchAllAsync();
        return (await _sp.GetRequiredService<IMessageRepository>().ListByAliasWithPreviewAsync(ca!.Id, 100, 0)).Count;
    }

    // --- matcher ---

    [Fact]
    public async Task Matcher_Literal_Sender_Matches()
    {
        await AddRuleAsync(RuleTarget.Sender, RuleMatch.Literal, "spam@bad.com", RuleAction.Junk);
        var matcher = NewMatcher();

        Assert.Equal(RuleAction.Junk, (await matcher.MatchAsync("SPAM@bad.com", "a@mydomain.com"))?.Action);
        Assert.Null(await matcher.MatchAsync("ok@good.com", "a@mydomain.com"));
    }

    [Fact]
    public async Task Matcher_Regex_Recipient_Matches()
    {
        await AddRuleAsync(RuleTarget.Recipient, RuleMatch.Regex, "^sales@", RuleAction.Junk);
        var matcher = NewMatcher();

        Assert.NotNull(await matcher.MatchAsync("x@y.com", "sales@mydomain.com"));
        Assert.Null(await matcher.MatchAsync("x@y.com", "spotify@mydomain.com"));
    }

    [Fact]
    public async Task Matcher_Precedence_RejectBeatsJunk()
    {
        await AddRuleAsync(RuleTarget.Sender, RuleMatch.Literal, "spam@bad.com", RuleAction.Junk);
        await AddRuleAsync(RuleTarget.Sender, RuleMatch.Literal, "spam@bad.com", RuleAction.Reject);
        var matcher = NewMatcher();

        Assert.Equal(RuleAction.Reject, (await matcher.MatchAsync("spam@bad.com", "a@mydomain.com"))?.Action);
    }

    [Fact]
    public async Task Matcher_Disabled_Rule_Ignored()
    {
        var repo = _sp.GetRequiredService<IBlacklistRuleRepository>();
        var rule = await AddRuleAsync(RuleTarget.Sender, RuleMatch.Literal, "spam@bad.com", RuleAction.Junk);
        await repo.SetEnabledAsync(rule.Id, false);

        Assert.Null(await NewMatcher().MatchAsync("spam@bad.com", "a@mydomain.com"));
    }

    // --- inbound pipeline ---

    [Fact]
    public async Task Sink_Junk_Stores_Tagged_And_Hidden_From_Inbox()
    {
        var rule = await AddRuleAsync(RuleTarget.Sender, RuleMatch.Literal, "spam@bad.com", RuleAction.Junk);

        Assert.Equal(InboundSaveResult.Stored, await DeliverAsync("spam@bad.com", "info@mydomain.com"));

        Assert.Equal(0, await CatchAllInboxCountAsync());     // not in the normal inbox
        var junk = await _sp.GetRequiredService<IMessageRepository>().ListJunkWithPreviewAsync(100, 0);
        Assert.Single(junk);
        Assert.Equal(rule.Id, junk[0].JunkRuleId);
        Assert.False(junk[0].JunkManual);
    }

    [Fact]
    public async Task Sink_Discard_Stores_Nothing()
    {
        await AddRuleAsync(RuleTarget.Sender, RuleMatch.Literal, "spam@bad.com", RuleAction.Discard);

        Assert.Equal(InboundSaveResult.Stored, await DeliverAsync("spam@bad.com", "info@mydomain.com"));

        Assert.Equal(0, await CatchAllInboxCountAsync());
        Assert.Equal(0, await JunkCountAsync());
    }

    // --- sweep / unsweep ---

    [Fact]
    public async Task SweepPreview_Counts_Without_Mutating()
    {
        await DeliverAsync("spam@bad.com", "info@mydomain.com");
        await DeliverAsync("spam@bad.com", "sales@mydomain.com");
        await DeliverAsync("ok@good.com", "info@mydomain.com");

        var service = _sp.GetRequiredService<IBlacklistService>();
        var preview = await service.SweepPreviewAsync(RuleTarget.Sender, RuleMatch.Literal, "spam@bad.com");

        Assert.Equal(2, preview.Count);
        Assert.Equal(2, preview.Sample.Count);
        Assert.Equal(0, await JunkCountAsync());              // preview mutates nothing
        Assert.Equal(3, await CatchAllInboxCountAsync());
    }

    [Fact]
    public async Task Sweep_Junks_Matches_And_Respects_Manual_Lock()
    {
        await DeliverAsync("spam@bad.com", "info@mydomain.com");
        await DeliverAsync("spam@bad.com", "sales@mydomain.com");

        var messages = _sp.GetRequiredService<IMessageRepository>();
        var service = _sp.GetRequiredService<IBlacklistService>();

        // Manually junk then unjunk one matching message: it becomes manual-locked (not junked).
        var first = (await messages.ListSweepCandidatesAsync()).First();
        await service.JunkMessageAsync(first.Id);
        await service.UnjunkMessageAsync(first.Id);
        Assert.Equal(0, await JunkCountAsync());

        var rule = await AddRuleAsync(RuleTarget.Sender, RuleMatch.Literal, "spam@bad.com", RuleAction.Junk);
        var swept = await service.SweepAsync(rule.Id);

        Assert.Equal(1, swept);                                // the manual-locked one is skipped
        Assert.Equal(1, await JunkCountAsync());
    }

    [Fact]
    public async Task Unsweep_Restores_NonManual_RuleTagged()
    {
        await DeliverAsync("spam@bad.com", "info@mydomain.com");
        await DeliverAsync("spam@bad.com", "sales@mydomain.com");

        var service = _sp.GetRequiredService<IBlacklistService>();
        var rule = await AddRuleAsync(RuleTarget.Sender, RuleMatch.Literal, "spam@bad.com", RuleAction.Junk);

        Assert.Equal(2, await service.SweepAsync(rule.Id));
        Assert.Equal(2, await JunkCountAsync());

        Assert.Equal(2, await service.UnsweepAsync(rule.Id));
        Assert.Equal(0, await JunkCountAsync());
        Assert.Equal(2, await CatchAllInboxCountAsync());      // restored to the catch-all inbox
    }

    // --- cleanup ---

    [Fact]
    public async Task Cleanup_Deletes_Expired_Junk()
    {
        await DeliverAsync("spam@bad.com", "info@mydomain.com");
        var messages = _sp.GetRequiredService<IMessageRepository>();
        var service = _sp.GetRequiredService<IBlacklistService>();

        var msg = (await messages.ListSweepCandidatesAsync()).Single();
        await service.JunkMessageAsync(msg.Id);
        Assert.Equal(1, await JunkCountAsync());

        // Nothing older than yesterday yet.
        Assert.Empty(await messages.ListJunkedBeforeAsync(DateTimeOffset.UtcNow.AddDays(-1)));

        // Everything junked before "now + 1 min" is eligible; delete it.
        var expired = await messages.ListJunkedBeforeAsync(DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Single(expired);
        await messages.DeleteAsync(expired[0]);

        Assert.Equal(0, await JunkCountAsync());
        Assert.Null(await messages.GetByIdAsync(msg.Id));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _sp?.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
