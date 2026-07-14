using System.Text;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Inbix.Core.Security;
using Inbix.Data;
using Inbix.Imap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inbix.Tests;

public sealed class ImapUnitTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inbix-imap-" + Guid.NewGuid().ToString("N"));
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
    public void ImapOptions_Defaults_Are_Disabled_AdminAdmin()
    {
        var o = new ImapOptions();
        Assert.False(o.Enabled);
        Assert.Equal("admin", o.Username);
        Assert.Equal("admin", o.Password);
        Assert.Equal(143, o.Port);
    }

    [Theory]
    [InlineData("admin", true)]        // default
    [InlineData("password", true)]     // common
    [InlineData("short1!", true)]      // < 12
    [InlineData("alllowercase1", true)]// < 3 classes
    [InlineData("Str0ng-Passphrase!", false)]
    public void PasswordStrength_Flags_Weak(string password, bool weak)
        => Assert.Equal(weak, PasswordStrength.Weakness(password) is not null);

    [Fact]
    public void PasswordHasher_Verifies_Its_Own_Hash()
    {
        var hash = PasswordHasher.Hash("Str0ng-Passphrase!");
        Assert.True(PasswordHasher.Verify("Str0ng-Passphrase!", hash));
        Assert.False(PasswordHasher.Verify("wrong", hash));
    }

    [Fact]
    public async Task Mailbox_Model_Maps_Aliases_And_Uid_Is_Message_Id()
    {
        var alias = await _sp.GetRequiredService<IAliasRepository>().CreateAsync("spotify", "mydomain.com", null);
        var sink = _sp.GetRequiredService<IInboundMessageSink>();
        var raw = Encoding.ASCII.GetBytes("From: a@b.com\r\nTo: spotify@mydomain.com\r\nSubject: Hi\r\n\r\nBody\r\n");
        Assert.Equal(InboundSaveResult.Stored, await sink.SaveAsync(new InboundMessage
        {
            Recipient = "spotify@mydomain.com", Sender = "a@b.com", RawMime = raw, ReceivedAt = DateTimeOffset.UtcNow
        }));

        var provider = new ImapMailboxProvider(
            _sp.GetRequiredService<IAliasRepository>(), _sp.GetRequiredService<IMessageRepository>());

        var folders = (await provider.ListAsync(default)).Select(f => f.Name).ToList();
        Assert.Contains("INBOX", folders);
        Assert.Contains("Aliases", folders);
        Assert.Contains("Aliases/spotify@mydomain.com", folders);
        Assert.Contains("Junk", folders);

        var inbox = await provider.GetMessagesAsync("INBOX", default);
        var m = Assert.Single(inbox!);
        var aliasFolder = await provider.GetMessagesAsync("Aliases/spotify@mydomain.com", default);
        Assert.Equal(m.Id, Assert.Single(aliasFolder!).Id);           // UID == message id
        Assert.Equal(alias.Id, m.AliasId);

        Assert.Null(await provider.GetMessagesAsync("Aliases/nope@mydomain.com", default)); // unknown mailbox
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _sp?.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
