using System.Net;
using System.Net.Sockets;
using System.Text;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Inbix.Data;
using Inbix.Imap;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inbix.Tests;

/// <summary>Drives the real IMAP server with MailKit's IMAP client to prove client compatibility.</summary>
public sealed class ImapServerTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inbix-imapd-" + Guid.NewGuid().ToString("N"));
    private IHost _host = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        _port = FreePort();
        var options = new InbixOptions
        {
            Domains = ["mydomain.com"],
            Database = { Provider = "sqlite", ConnectionString = $"Data Source={Path.Combine(_tempDir, "test.db")}" },
            Storage = { RawPath = Path.Combine(_tempDir, "raw") },
            Imap = { Enabled = true, Port = _port, Username = "admin", Password = "admin" }
        };

        _host = new HostBuilder()
            .ConfigureServices(s =>
            {
                s.AddLogging();
                s.AddSingleton<IOptions<InbixOptions>>(Options.Create(options));
                s.AddInbixData();   // runs migrations on start
                s.AddInbixImap();   // starts the IMAP listener on start
            })
            .Build();
        await _host.StartAsync();

        // One alias + one stored raw message so INBOX has content.
        await _host.Services.GetRequiredService<IAliasRepository>().CreateAsync("spotify", "mydomain.com", null);
        var raw = Encoding.ASCII.GetBytes(
            "From: Spotify <noreply@spotify.com>\r\nTo: spotify@mydomain.com\r\nSubject: IMAP-HELLO\r\n\r\nhello over imap\r\n");
        await _host.Services.GetRequiredService<IInboundMessageSink>().SaveAsync(new InboundMessage
        {
            Recipient = "spotify@mydomain.com", Sender = "noreply@spotify.com", RawMime = raw, ReceivedAt = DateTimeOffset.UtcNow
        });

        await WaitForPortAsync(_port);
    }

    [Fact]
    public async Task Client_Can_Login_List_Folders_And_Read_A_Message()
    {
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", _port, SecureSocketOptions.None);
        await client.AuthenticateAsync("admin", "admin");

        // Folder list includes INBOX, per-alias, and Junk.
        var personal = client.GetFolder(client.PersonalNamespaces[0]);
        var names = (await personal.GetSubfoldersAsync(false)).Select(f => f.FullName).ToList();
        Assert.Contains("INBOX", names);
        Assert.Contains("Junk", names);
        Assert.Contains(names, n => n.StartsWith("Aliases", StringComparison.Ordinal));

        // Open INBOX (read-only) and read the message — MailKit fetches BODY[] and parses it.
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly);
        Assert.Equal(1, client.Inbox.Count);

        var msg = await client.Inbox.GetMessageAsync(0);
        Assert.Equal("IMAP-HELLO", msg.Subject);
        Assert.Contains("hello over imap", msg.TextBody);

        await client.DisconnectAsync(quit: true);
    }

    [Fact]
    public async Task Bad_Credentials_Are_Rejected()
    {
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", _port, SecureSocketOptions.None);
        await Assert.ThrowsAsync<AuthenticationException>(() => client.AuthenticateAsync("admin", "wrong"));
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task WaitForPortAsync(int port)
    {
        for (var i = 0; i < 50; i++)
        {
            try { using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, port); return; }
            catch { await Task.Delay(100); }
        }
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
