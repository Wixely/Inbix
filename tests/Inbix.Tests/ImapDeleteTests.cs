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
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inbix.Tests;

/// <summary>When Inbix:Imap:AllowDelete is on, deleting in a client removes the message from the server.</summary>
public sealed class ImapDeleteTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inbix-imapdel-" + Guid.NewGuid().ToString("N"));
    private IHost _host = null!;
    private int _port;
    private long _aliasId;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        _port = FreePort();
        var options = new InbixOptions
        {
            Domains = ["mydomain.com"],
            Database = { Provider = "sqlite", ConnectionString = $"Data Source={Path.Combine(_tempDir, "test.db")}" },
            Storage = { RawPath = Path.Combine(_tempDir, "raw") },
            Imap = { Enabled = true, Port = _port, Username = "admin", Password = "admin", AllowDelete = true }
        };

        _host = new HostBuilder()
            .ConfigureServices(s =>
            {
                s.AddLogging();
                s.AddSingleton<IOptions<InbixOptions>>(Options.Create(options));
                s.AddInbixData();
                s.AddInbixImap();
            })
            .Build();
        await _host.StartAsync();

        _aliasId = (await _host.Services.GetRequiredService<IAliasRepository>().CreateAsync("spotify", "mydomain.com", null)).Id;
        var sink = _host.Services.GetRequiredService<IInboundMessageSink>();
        for (var i = 1; i <= 2; i++)
        {
            var raw = Encoding.ASCII.GetBytes($"From: a@b.com\r\nTo: spotify@mydomain.com\r\nSubject: Msg {i}\r\n\r\nbody {i}\r\n");
            await sink.SaveAsync(new InboundMessage { Recipient = "spotify@mydomain.com", Sender = "a@b.com", RawMime = raw, ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(i) });
        }
        await WaitForPortAsync(_port);
    }

    [Fact]
    public async Task Client_Delete_Removes_The_Message_From_The_Server()
    {
        var messages = _host.Services.GetRequiredService<IMessageRepository>();
        Assert.Equal(2, (await messages.ListByAliasAsync(_aliasId, 50, 0)).Count);

        using (var client = new ImapClient())
        {
            await client.ConnectAsync("127.0.0.1", _port, SecureSocketOptions.None);
            await client.AuthenticateAsync("admin", "admin");
            await client.Inbox.OpenAsync(FolderAccess.ReadWrite);
            Assert.Equal(FolderAccess.ReadWrite, client.Inbox.Access);        // AllowDelete => read-write
            Assert.Equal(2, client.Inbox.Count);

            var uids = await client.Inbox.SearchAsync(SearchQuery.All);
            await client.Inbox.AddFlagsAsync(uids[0], MessageFlags.Deleted, silent: true);
            await client.Inbox.ExpungeAsync();
            Assert.Equal(1, client.Inbox.Count);

            await client.DisconnectAsync(quit: true);
        }

        // Gone on the server (row + raw removed by IMessageRepository.DeleteAsync).
        Assert.Single(await messages.ListByAliasAsync(_aliasId, 50, 0));
    }

    [Fact]
    public async Task Examine_Is_Read_Only_Even_With_AllowDelete()
    {
        var messages = _host.Services.GetRequiredService<IMessageRepository>();
        Assert.Equal(2, (await messages.ListByAliasAsync(_aliasId, 50, 0)).Count);

        // A non-compliant client marks \Deleted on an EXAMINE (read-only) mailbox and CLOSEs.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _port);
        using var r = new StreamReader(tcp.GetStream());
        await using var w = new StreamWriter(tcp.GetStream()) { NewLine = "\r\n", AutoFlush = true };
        await r.ReadLineAsync();
        await Cmd(w, r, "a1 LOGIN admin admin", "a1");
        await Cmd(w, r, "a2 EXAMINE INBOX", "a2");                 // read-only open
        await Cmd(w, r, "a3 STORE 1 +FLAGS (\\Deleted)", "a3");
        await Cmd(w, r, "a4 CLOSE", "a4");                          // must NOT expunge from a read-only mailbox

        Assert.Equal(2, (await messages.ListByAliasAsync(_aliasId, 50, 0)).Count); // nothing deleted
    }

    private static async Task Cmd(StreamWriter w, StreamReader r, string command, string tag)
    {
        await w.WriteLineAsync(command);
        string? line;
        while ((line = await r.ReadLineAsync()) is not null)
            if (line.StartsWith(tag + " ", StringComparison.Ordinal)) break;
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
