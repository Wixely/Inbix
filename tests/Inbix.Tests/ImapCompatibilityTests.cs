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

public sealed class ImapCompatibilityTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inbix-compat-" + Guid.NewGuid().ToString("N"));
    private IHost _host = null!;
    private int _port;
    private IMessageRepository Messages => _host.Services.GetRequiredService<IMessageRepository>();
    private ImapMailboxProvider Mailboxes => _host.Services.GetRequiredService<ImapMailboxProvider>();
    private const string RichMail = "Date: Mon, 01 Jan 2024 10:00:00 +0000\r\n" +
        "From: =?utf-8?b?Sm9zw6k=?= <from@example.test>\r\nSender: sender@example.test\r\n" +
        "Reply-To: reply@example.test\r\nTo: other@example.test\r\nCc: cc@example.test\r\n" +
        "Subject: =?utf-8?b?Y2Fmw6k=?=\r\nMessage-ID: <rich@example.test>\r\nIn-Reply-To: <parent@example.test>\r\n" +
        "MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nneedle body\r\n";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await StartAsync();
        await _host.Services.GetRequiredService<IAliasRepository>().CreateAsync("box", "example.test", null);
        await SaveAsync(RichMail);
        await SaveAsync("From: second@example.test\r\nTo: box@example.test\r\nSubject: Second\r\n\r\nsecond body\r\n");
    }

    private async Task StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); _port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var options = new InbixOptions
        {
            Domains = ["example.test"], Database = { Provider = "json" },
            Storage = { JsonPath = Path.Combine(_root, "store"), RawPath = Path.Combine(_root, "raw") },
            Imap = { Enabled = true, Port = _port, Username = "admin", Password = "admin", AllowDelete = true }
        };
        _host = new HostBuilder().ConfigureServices(s =>
        {
            s.AddLogging(); s.AddSingleton<IOptions<InbixOptions>>(Options.Create(options));
            s.AddInbixData("json"); s.AddInbixImap();
        }).Build();
        await _host.StartAsync();
        for (var i = 0; i < 50; i++)
        {
            try { using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, _port); return; }
            catch (SocketException) { await Task.Delay(20); }
        }
        throw new TimeoutException();
    }

    private Task<InboundSaveResult> SaveAsync(string raw) => _host.Services.GetRequiredService<IInboundMessageSink>().SaveAsync(new InboundMessage
    {
        Recipient = "box@example.test", Sender = "envelope@example.test", RawMime = Encoding.UTF8.GetBytes(raw),
        ReceivedAt = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero)
    });

    private async Task<ImapClient> ClientAsync(bool writable = false)
    {
        var client = new ImapClient { Timeout = 10000 };
        await client.ConnectAsync("127.0.0.1", _port, SecureSocketOptions.None);
        await client.AuthenticateAsync("admin", "admin");
        await client.Inbox.OpenAsync(writable ? FolderAccess.ReadWrite : FolderAccess.ReadOnly);
        return client;
    }

    [Theory]
    [InlineData("UNSEEN", "")]
    [InlineData("NOT ALL", "")]
    [InlineData("SEEN UNDELETED", "1 2")]
    [InlineData("UID 1 UNSEEN", "")]
    [InlineData("UID 1:2 NOT UID 2", "1")]
    [InlineData("OR (SUBJECT Second) (BODY needle)", "1 2")]
    [InlineData("HEADER Reply-To reply@example.test", "1")]
    [InlineData("TO other@example.test", "1")]
    [InlineData("SENTBEFORE 1-Jan-2025", "1")]
    [InlineData("BEFORE 1-Jan-2025", "")]
    [InlineData("SINCE 18-Sep-2026", "1 2")]
    [InlineData("CHARSET UTF-8 SUBJECT caf", "1")]
    [InlineData("CHARSET UTF-8 SUBJECT caf\u00e9", "1")]
    [InlineData("2 UID 1:2", "2")]
    public async Task Search_Evaluates_Criteria(string criteria, string expected)
    {
        await using var wire = await Wire.OpenAsync(_port);
        await wire.CommandAsync("EXAMINE INBOX");
        var response = await wire.CommandAsync("UID SEARCH " + criteria);
        Assert.Contains("* SEARCH " + expected + "\r\n", response);
        Assert.Contains(" OK UID SEARCH completed", response);
    }

    [Theory]
    [InlineData("UID FETCH 1 (FLAGS UNKNOWN)")]
    [InlineData("UID FETCH 0 (FLAGS)")]
    [InlineData("UID FETCH 1:2:3 (FLAGS)")]
    [InlineData("UID FETCH 1 (FLAGS))")]
    [InlineData("UID SEARCH OR ALL")]
    [InlineData("UID SEARCH UNKNOWN")]
    public async Task Invalid_Commands_Are_Bounded_And_Leave_Connection_Usable(string command)
    {
        await using var wire = await Wire.OpenAsync(_port);
        await wire.CommandAsync("EXAMINE INBOX");
        Assert.Contains(" BAD ", await wire.CommandAsync(command));
        Assert.Contains(" OK NOOP completed", await wire.CommandAsync("NOOP"));
    }

    [Fact]
    public async Task Failed_Select_Deselects_And_Authentication_Is_Not_Reentrant()
    {
        await using var wire = await Wire.OpenAsync(_port);
        Assert.Contains(" BAD ", await wire.CommandAsync("CHECK"));
        Assert.Contains(" BAD ", await wire.CommandAsync("LOGIN admin admin"));
        await wire.CommandAsync("EXAMINE INBOX");
        Assert.Contains(" NO ", await wire.CommandAsync("SELECT nonexistent"));
        Assert.Contains(" BAD ", await wire.CommandAsync("UID FETCH 1 (UID)"));
    }

    [Fact]
    public async Task Envelope_Uses_Original_Headers_Before_Worker_Parsing()
    {
        using var client = await ClientAsync();
        var summary = Assert.Single(await client.Inbox.FetchAsync(0, 0, MessageSummaryItems.Envelope));
        Assert.NotNull(summary.Envelope);
        Assert.Equal("caf\u00e9", summary.Envelope.Subject);
        Assert.Equal("Jos\u00e9", summary.Envelope.From.Mailboxes.Single().Name);
        Assert.Equal("sender@example.test", summary.Envelope.Sender.Mailboxes.Single().Address);
        Assert.Equal("reply@example.test", summary.Envelope.ReplyTo.Mailboxes.Single().Address);
        Assert.Equal("other@example.test", summary.Envelope.To.Mailboxes.Single().Address);
        Assert.Equal("cc@example.test", summary.Envelope.Cc.Mailboxes.Single().Address);
        Assert.Equal(2024, summary.Envelope.Date!.Value.Year);
        Assert.Equal("parent@example.test", summary.Envelope.InReplyTo);
    }

    [Fact]
    public async Task Nested_Message_Structure_And_Sections_Are_Readable()
    {
        await SaveAsync("From: outer@example.test\r\nTo: box@example.test\r\nSubject: Forward\r\n" +
            "MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=parts\r\n\r\n" +
            "--parts\r\nContent-Type: text/plain\r\n\r\nouter body\r\n" +
            "--parts\r\nContent-Type: message/rfc822\r\n\r\n" + RichMail + "\r\n--parts--\r\n");
        using var client = await ClientAsync();
        var summary = Assert.Single(await client.Inbox.FetchAsync(2, 2, MessageSummaryItems.BodyStructure));
        var multipart = Assert.IsType<BodyPartMultipart>(summary.Body);
        var nested = Assert.IsType<BodyPartMessage>(multipart.BodyParts[1]);
        Assert.NotNull(nested.Envelope);
        Assert.Equal("caf\u00e9", nested.Envelope.Subject);
        Assert.NotNull(nested.Body);
        using var header = await client.Inbox.GetStreamAsync(2, "2.HEADER");
        var headerText = await new StreamReader(header).ReadToEndAsync();
        Assert.Contains("Reply-To: reply@example.test", headerText);
        Assert.DoesNotContain("needle body", headerText);
        using var text = await client.Inbox.GetStreamAsync(2, "2.TEXT");
        Assert.Equal("needle body\r\n", await new StreamReader(text).ReadToEndAsync());
        using var leaf = await client.Inbox.GetStreamAsync(2, "2.1");
        Assert.Equal("needle body\r\n", await new StreamReader(leaf).ReadToEndAsync());
        using var subset = await client.Inbox.GetStreamAsync(2, "2.HEADER.FIELDS (SUBJECT)");
        var subsetText = await new StreamReader(subset).ReadToEndAsync();
        Assert.Contains("Subject:", subsetText);
        Assert.DoesNotContain("From:", subsetText);
    }

    [Fact]
    public async Task Uids_Survive_Restart_And_Never_Reuse_Deleted_Highest_Id()
    {
        var before = (await Mailboxes.SnapshotAsync("INBOX", default))!;
        var highest = before.Messages[^1].Id;
        await Messages.DeleteAsync(highest);
        var reduced = (await Mailboxes.SnapshotAsync("INBOX", default))!;
        Assert.Equal(before.NextUid, reduced.NextUid);
        await _host.StopAsync(); _host.Dispose(); await StartAsync();
        var reopened = (await Mailboxes.SnapshotAsync("INBOX", default))!;
        Assert.Equal(before.Validity, reopened.Validity);
        Assert.Equal(before.Uids[before.Messages[0].Id], reopened.Uids[reopened.Messages[0].Id]);
        await SaveAsync("Subject: Replacement\r\n\r\nreplacement\r\n");
        var after = (await Mailboxes.SnapshotAsync("INBOX", default))!;
        Assert.True(after.Messages[^1].Id > highest);
        Assert.True(after.Uids[after.Messages[^1].Id] > before.Uids[highest]);
    }

    [Fact]
    public async Task Returning_Old_Mail_Gets_New_Uid_And_Noop_Reports_Expunge_Before_Exists()
    {
        await using var wire = await Wire.OpenAsync(_port);
        await wire.CommandAsync("EXAMINE INBOX");
        var before = (await Mailboxes.SnapshotAsync("INBOX", default))!;
        var id = before.Messages[0].Id;
        await Messages.SetJunkAsync(id, DateTimeOffset.UtcNow, null, true);
        var removed = await wire.CommandAsync("NOOP");
        Assert.Contains("* 1 EXPUNGE\r\n", removed);
        Assert.DoesNotContain("* 1 EXISTS", removed);
        await Messages.ClearJunkAsync(id, true);
        var added = await wire.CommandAsync("NOOP");
        Assert.Contains("* 2 EXISTS\r\n", added);
        var after = (await Mailboxes.SnapshotAsync("INBOX", default))!;
        Assert.True(after.Uids[id] >= before.NextUid);
        Assert.Equal(id, after.Messages[^1].Id);
    }

    [Fact]
    public async Task Idle_Detects_Unnotified_Deletion_Without_Shrinking_Exists()
    {
        await using var wire = await Wire.OpenAsync(_port);
        await wire.CommandAsync("EXAMINE INBOX");
        await wire.Writer.WriteLineAsync("idle IDLE");
        Assert.StartsWith("+", await wire.Reader.ReadLineAsync());
        var snapshot = (await Mailboxes.SnapshotAsync("INBOX", default))!;
        await Messages.DeleteAsync(snapshot.Messages[0].Id);
        Assert.Equal("* 1 EXPUNGE", await wire.Reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        await wire.Writer.WriteLineAsync("DONE");
        Assert.Equal("idle OK IDLE terminated", await wire.Reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Delete_Flags_Are_Persistent_And_Uid_Store_Identifies_The_Message()
    {
        await using (var wire = await Wire.OpenAsync(_port))
        {
            await wire.CommandAsync("SELECT INBOX");
            Assert.Contains("UID 1 FLAGS (\\Seen \\Deleted)", await wire.CommandAsync("UID STORE 1 +FLAGS (\\Deleted)"));
            Assert.Contains("FLAGS (\\Seen \\Deleted)", await wire.CommandAsync("UID FETCH 1 (FLAGS)"));
        }
        await using var next = await Wire.OpenAsync(_port);
        await next.CommandAsync("EXAMINE INBOX");
        Assert.Contains("FLAGS (\\Seen \\Deleted)", await next.CommandAsync("UID FETCH 1 (FLAGS)"));
        Assert.DoesNotContain("FETCH", await next.CommandAsync("UID STORE 1 +FLAGS.SILENT (\\Seen)"));
        Assert.Contains("* SEARCH 1\r\n", await next.CommandAsync("UID SEARCH DELETED"));
    }

    [Fact]
    public async Task Subscriptions_Persist_And_Status_Only_Returns_Requested_Items()
    {
        await using (var wire = await Wire.OpenAsync(_port))
        {
            await wire.CommandAsync("UNSUBSCRIBE Junk");
            Assert.DoesNotContain("Junk", await wire.CommandAsync("LSUB \"\" \"*\""));
            Assert.Contains("Junk", await wire.CommandAsync("LIST \"\" \"*\""));
            Assert.Contains("* STATUS \"INBOX\" (MESSAGES 2)\r\n", await wire.CommandAsync("STATUS INBOX (MESSAGES)"));
        }
        await _host.StopAsync(); _host.Dispose(); await StartAsync();
        await using var next = await Wire.OpenAsync(_port);
        Assert.DoesNotContain("Junk", await next.CommandAsync("LSUB \"\" \"*\""));
        await next.CommandAsync("SUBSCRIBE Junk");
        Assert.Contains("Junk", await next.CommandAsync("LSUB \"\" \"*\""));
    }

    [Fact]
    public async Task Missing_Raw_Data_Returns_No_Not_An_Empty_Message()
    {
        var message = (await Mailboxes.SnapshotAsync("INBOX", default))!.Messages[0];
        await _host.Services.GetRequiredService<IRawMessageStore>().DeleteAsync(message.RawStoragePath!);
        await using var wire = await Wire.OpenAsync(_port);
        await wire.CommandAsync("EXAMINE INBOX");
        var result = await wire.CommandAsync("UID FETCH 1 (BODY.PEEK[])");
        Assert.Contains(" NO Message data unavailable", result);
        Assert.DoesNotContain("FETCH (", result);
    }

    [Fact]
    public async Task Equal_Count_Replacement_And_New_Arrival_Preserve_Sequence_Order()
    {
        await using var wire = await Wire.OpenAsync(_port);
        await wire.CommandAsync("EXAMINE INBOX");
        var before = (await Mailboxes.SnapshotAsync("INBOX", default))!;
        await Messages.DeleteAsync(before.Messages[0].Id);
        await SaveAsync("Subject: New\r\n\r\nnew\r\n");
        var response = await wire.CommandAsync("NOOP");
        Assert.Contains("* 1 EXPUNGE\r\n* 2 EXISTS\r\n", response);
        var fetched = await wire.CommandAsync("FETCH 1:* (UID)");
        Assert.Contains("* 1 FETCH (UID 2)", fetched);
        Assert.Contains("* 2 FETCH (UID 3)", fetched);
    }

    [Fact]
    public async Task Unobserved_Junk_And_Return_Still_Allocates_New_Uid()
    {
        var before = (await Mailboxes.SnapshotAsync("INBOX", default))!;
        var id = before.Messages[0].Id;
        await Messages.SetJunkAsync(id, DateTimeOffset.UtcNow, null, true);
        await Messages.ClearJunkAsync(id, true);
        var after = (await Mailboxes.SnapshotAsync("INBOX", default))!;
        Assert.True(after.Uids[id] >= before.NextUid);
    }

    [Fact]
    public async Task Other_Sessions_Observe_Delete_Flags_And_Clearing_Prevents_Expunge()
    {
        await using var first = await Wire.OpenAsync(_port);
        await using var second = await Wire.OpenAsync(_port);
        await first.CommandAsync("SELECT INBOX");
        await second.CommandAsync("SELECT INBOX");
        await first.CommandAsync("UID STORE 1 +FLAGS.SILENT (\\Deleted)");
        // A second client with a stale snapshot must not erase another client's delete flag
        // when it tries to set an unrelated (unsupported) flag.
        Assert.Contains("UID 1 FLAGS (\\Seen \\Deleted)", await second.CommandAsync("UID STORE 1 +FLAGS (\\Seen)"));
        await first.CommandAsync("UID STORE 2 +FLAGS.SILENT (\\Deleted)");
        Assert.Contains("UID 2 FLAGS (\\Seen \\Deleted)", await second.CommandAsync("NOOP"));
        await second.CommandAsync("UID STORE 1 -FLAGS.SILENT (\\Deleted)");
        await second.CommandAsync("UID STORE 2 -FLAGS.SILENT (\\Deleted)");
        Assert.DoesNotContain("EXPUNGE\r\n", await first.CommandAsync("EXPUNGE"));
        Assert.Equal(2, (await Mailboxes.SnapshotAsync("INBOX", default))!.Messages.Count);
    }

    [Fact]
    public async Task Subscription_To_Missing_Name_Is_Still_Listed()
    {
        await using var wire = await Wire.OpenAsync(_port);
        await wire.CommandAsync("SUBSCRIBE Aliases/missing@example.test");
        Assert.Contains("Aliases/missing@example.test", await wire.CommandAsync("LSUB \"\" \"*\""));
        Assert.Contains("\\Noselect", await wire.CommandAsync("LSUB \"\" \"%\""));
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync(); _host.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class Wire : IAsyncDisposable
    {
        private readonly TcpClient _tcp = new();
        private int _tag;
        public StreamReader Reader { get; private set; } = null!;
        public StreamWriter Writer { get; private set; } = null!;
        public static async Task<Wire> OpenAsync(int port)
        {
            var wire = new Wire();
            await wire._tcp.ConnectAsync(IPAddress.Loopback, port);
            wire.Reader = new StreamReader(wire._tcp.GetStream());
            wire.Writer = new StreamWriter(wire._tcp.GetStream()) { AutoFlush = true, NewLine = "\r\n" };
            await wire.Reader.ReadLineAsync();
            await wire.CommandAsync("LOGIN admin admin");
            return wire;
        }
        public async Task<string> CommandAsync(string command)
        {
            var tag = "t" + ++_tag;
            await Writer.WriteLineAsync(tag + " " + command);
            var result = new StringBuilder();
            while (true)
            {
                var line = await Reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.NotNull(line);
                result.Append(line).Append("\r\n");
                if (line.StartsWith(tag + " ", StringComparison.Ordinal)) return result.ToString();
            }
        }
        public ValueTask DisposeAsync() { _tcp.Dispose(); Reader.Dispose(); Writer.Dispose(); return ValueTask.CompletedTask; }
    }
}
