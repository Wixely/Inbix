using System.Text;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Inbix.Core.Validation;
using Inbix.Data;
using Inbix.Data.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inbix.Tests;

/// <summary>End-to-end tests for the JSON file/folder storage provider.</summary>
public sealed class JsonStorageTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inbix-json-" + Guid.NewGuid().ToString("N"));
    private string _storeDir = null!;
    private string _mailDir = null!;
    private ServiceProvider _sp = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        _storeDir = Path.Combine(_tempDir, "store");
        _mailDir = Path.Combine(_storeDir, "mail");

        var options = new InbixOptions
        {
            Domains = ["mydomain.com"],
            Database = { Provider = "json" },
            Storage = { RawPath = Path.Combine(_tempDir, "raw"), JsonPath = _storeDir }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<InbixOptions>>(Options.Create(options));
        services.AddInbixData("json");
        _sp = services.BuildServiceProvider();

        // Hosted services don't run under BuildServiceProvider; load the store explicitly.
        await _sp.GetRequiredService<JsonDataStore>().InitializeAsync();
    }

    private T Get<T>() where T : notnull => _sp.GetRequiredService<T>();

    [Fact]
    public async Task CatchAll_Is_Seeded_On_First_Load()
    {
        var catchAll = await Get<IAliasRepository>().GetCatchAllAsync();
        Assert.NotNull(catchAll);
        Assert.True(catchAll!.IsCatchAll);
        Assert.True(File.Exists(Path.Combine(_mailDir, "catchall", "_alias.json")));
    }

    [Fact]
    public async Task Create_Alias_Writes_Folder_And_Is_Deliverable()
    {
        var alias = await Get<IAliasRepository>().CreateAsync("spotify", "mydomain.com", "music");
        Assert.True(alias.Id > 0);
        Assert.True(File.Exists(Path.Combine(_mailDir, "spotify", "_alias.json")));
        Assert.True(await Get<IAliasResolver>().IsDeliverableAsync("spotify@mydomain.com"));
    }

    [Fact]
    public void Reserved_Local_Parts_Are_Rejected()
    {
        Assert.NotNull(AliasRules.ValidateLocalPart("junk"));
        Assert.NotNull(AliasRules.ValidateLocalPart("catchall"));
        Assert.NotNull(AliasRules.ValidateLocalPart("catch-all"));
        Assert.Null(AliasRules.ValidateLocalPart("spotify"));
    }

    [Fact]
    public async Task Message_Lifecycle_Store_Parse_Junk_Unjunk_Delete()
    {
        var aliases = Get<IAliasRepository>();
        var sink = Get<IInboundMessageSink>();
        var messages = Get<IMessageRepository>();
        var rules = Get<IBlacklistRuleRepository>();

        var alias = await aliases.CreateAsync("github", "mydomain.com", null);

        var raw = Encoding.ASCII.GetBytes("From: a@b.com\r\nSubject: Hi\r\n\r\nHello world body\r\n");
        Assert.Equal(InboundSaveResult.Stored, await sink.SaveAsync(new InboundMessage
        {
            Recipient = "github@mydomain.com", Sender = "a@b.com", RawMime = raw, ReceivedAt = DateTimeOffset.UtcNow
        }));

        var stored = Assert.Single(await messages.ClaimUnparsedAsync(10));
        Assert.Equal(alias.Id, stored.AliasId);
        var aliasMsgPath = Path.Combine(_mailDir, "github", stored.Id.ToString("D10") + ".json");
        Assert.True(File.Exists(aliasMsgPath));

        // Parse → body + snippet.
        await messages.MarkParsedAsync(stored.Id, "Hi", "a@b.com", "<id@b.com>",
            new MessageBody { TextBody = "Hello world body", HtmlBody = "<p>Hello</p>" }, [], default);
        var inbox = await messages.ListByAliasWithPreviewAsync(alias.Id, 10, 0);
        Assert.Equal("Hello world body", Assert.Single(inbox).Snippet);

        // Junk via a rule → file physically moves to junk/.
        var rule = await rules.CreateAsync(new BlacklistRule
        {
            Name = "block-ab", Target = RuleTarget.Sender, MatchType = RuleMatch.Literal,
            Pattern = "a@b.com", Action = RuleAction.Junk
        });
        await messages.SetJunkAsync(stored.Id, DateTimeOffset.UtcNow, rule.Id, manual: false);

        Assert.False(File.Exists(aliasMsgPath));
        Assert.True(File.Exists(Path.Combine(_mailDir, "junk", stored.Id.ToString("D10") + ".json")));
        Assert.Empty(await messages.ListByAliasAsync(alias.Id, 10, 0));               // hidden from the inbox
        var junk = Assert.Single(await messages.ListJunkWithPreviewAsync(10, 0));
        Assert.Equal("block-ab", junk.JunkRuleName);                                   // rule name resolved

        // Unjunk → file returns to the alias folder.
        await messages.ClearJunkAsync(stored.Id, manual: true);
        Assert.True(File.Exists(aliasMsgPath));
        Assert.False(File.Exists(Path.Combine(_mailDir, "junk", stored.Id.ToString("D10") + ".json")));
        Assert.Single(await messages.ListByAliasAsync(alias.Id, 10, 0));

        // Delete → file + raw gone.
        var rawRelative = stored.RawStoragePath!.Replace('/', Path.DirectorySeparatorChar);
        var rawFull = Path.Combine(_tempDir, "raw", rawRelative);
        Assert.True(File.Exists(rawFull));
        await messages.DeleteAsync(stored.Id);
        Assert.False(File.Exists(aliasMsgPath));
        Assert.False(File.Exists(rawFull));
    }

    [Fact]
    public async Task ListRecent_Includes_Owning_Alias_And_Snippet()
    {
        var aliases = Get<IAliasRepository>();
        var sink = Get<IInboundMessageSink>();
        var messages = Get<IMessageRepository>();

        var alias = await aliases.CreateAsync("disney", "mydomain.com", null);
        await sink.SaveAsync(new InboundMessage
        {
            Recipient = "disney@mydomain.com", Sender = "x@y.com",
            RawMime = Encoding.ASCII.GetBytes("From: x@y.com\r\nSubject: S\r\n\r\nbody\r\n"),
            ReceivedAt = DateTimeOffset.UtcNow
        });
        var stored = (await messages.ClaimUnparsedAsync(10)).Single(m => m.AliasId == alias.Id);
        await messages.MarkParsedAsync(stored.Id, "S", "x@y.com", null,
            new MessageBody { TextBody = "recent body" }, [], default);

        var recent = await messages.ListRecentAsync(50);
        var mine = recent.First(r => r.Id == stored.Id);
        Assert.Equal("disney", mine.AliasLocalPart);
        Assert.Equal("mydomain.com", mine.AliasDomain);
        Assert.False(mine.AliasIsCatchAll);
        Assert.Equal("recent body", mine.Snippet);
    }

    [Fact]
    public async Task Reload_Reads_State_Back_From_Disk()
    {
        var aliases = Get<IAliasRepository>();
        var settings = Get<ISettingsRepository>();
        var store = Get<JsonDataStore>();

        await aliases.CreateAsync("netflix", "mydomain.com", null);
        await settings.SetAsync("k", "v");

        await store.ReloadAsync();

        Assert.NotNull(await aliases.FindAsync("netflix", "mydomain.com"));
        Assert.Equal("v", await settings.GetAsync("k"));
    }

    [Fact]
    public async Task Deleting_Identity_Unlinks_Its_Aliases()
    {
        var aliases = Get<IAliasRepository>();
        var identities = Get<IIdentityRepository>();

        var identity = await identities.CreateAsync(new Identity
        {
            Country = "uk", FirstName = "Ada", LastName = "Lovelace", Username = "ada", Password = "pw",
            DateOfBirth = new DateOnly(1990, 1, 1), Street = "1 St", City = "London", Postcode = "AB1 2CD"
        });
        var alias = await aliases.CreateAsync("amazon", "mydomain.com", null);
        await aliases.SetIdentityAsync(alias.Id, identity.Id);
        Assert.Equal(identity.Id, (await aliases.GetByIdAsync(alias.Id))!.IdentityId);

        await identities.DeleteAsync(identity.Id);

        Assert.Null((await aliases.GetByIdAsync(alias.Id))!.IdentityId); // mirrors ON DELETE SET NULL
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _sp?.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
