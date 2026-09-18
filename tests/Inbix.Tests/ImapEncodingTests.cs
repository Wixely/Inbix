using System.Net;
using System.Net.Sockets;
using System.Text;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Inbix.Data;
using Inbix.Imap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inbix.Tests;

/// <summary>
/// Regression: BODYSTRUCTURE must quote body-fld-enc (RFC 3501). An unquoted <c>QUOTED-PRINTABLE</c> atom
/// makes strict clients skip decoding, so quoted-printable text shows literal "=" artifacts. Checked on the
/// raw wire because lenient clients (e.g. MailKit) parse the bad atom anyway.
/// </summary>
public sealed class ImapEncodingTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inbix-imapenc-" + Guid.NewGuid().ToString("N"));
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
                s.AddInbixData();
                s.AddInbixImap();
            })
            .Build();
        await _host.StartAsync();

        await _host.Services.GetRequiredService<IAliasRepository>().CreateAsync("qp", "mydomain.com", null);
        var raw = Encoding.ASCII.GetBytes(
            "From: a@b.com\r\nTo: qp@mydomain.com\r\nSubject: QP\r\nMIME-Version: 1.0\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n" +
            "soft wrapped=\r\nline and an equals =3D sign.\r\n");
        await _host.Services.GetRequiredService<IInboundMessageSink>().SaveAsync(new InboundMessage
        {
            Recipient = "qp@mydomain.com", Sender = "a@b.com", RawMime = raw, ReceivedAt = DateTimeOffset.UtcNow
        });
        await WaitForPortAsync(_port);
    }

    [Fact]
    public async Task BodyStructure_Quotes_The_Transfer_Encoding()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _port);
        using var reader = new StreamReader(tcp.GetStream());
        await using var writer = new StreamWriter(tcp.GetStream()) { NewLine = "\r\n", AutoFlush = true };

        await reader.ReadLineAsync();                                   // greeting
        await SendAsync(writer, reader, "a1 LOGIN admin admin", "a1");
        await SendAsync(writer, reader, "a2 SELECT INBOX", "a2");
        var fetch = await SendAsync(writer, reader, "a3 UID FETCH 1:* (BODYSTRUCTURE)", "a3");

        Assert.Contains("\"QUOTED-PRINTABLE\"", fetch);                 // quoted, per RFC 3501
        Assert.DoesNotContain(" QUOTED-PRINTABLE ", fetch);            // never a bare atom
    }

    [Theory]
    [InlineData("BODY.PEEK[]<0.4>", "BODY[]<0>", "From")]
    [InlineData("BODY[]<6.3>", "BODY[]<6>", "a@b")]
    [InlineData("BODY.PEEK[TEXT]<0.4>", "BODY[TEXT]<0>", "soft")]
    [InlineData("BODY.PEEK[1]<5.7>", "BODY[1]<5>", "wrapped")]
    [InlineData("BODY.PEEK[HEADER.FIELDS (SUBJECT)]<0.999>", "BODY[HEADER.FIELDS (SUBJECT)]<0>", "Subject: QP\r\n\r\n")]
    [InlineData("BODY.PEEK[HEADER.FIELDS (SUBJECT)]<9.2>", "BODY[HEADER.FIELDS (SUBJECT)]<9>", "QP")]
    [InlineData("BODY.PEEK[TEXT]<999999.10>", "BODY[TEXT]<999999>", "")]
    [InlineData("BODY.PEEK[TEXT]<4294967295.10>", "BODY[TEXT]<4294967295>", "")]
    [InlineData("BODY.PEEK[HEADER.FIELDS (SUBJECT)]", "BODY[HEADER.FIELDS (SUBJECT)]", "Subject: QP\r\n\r\n")]
    public async Task Fetch_Returns_Requested_Bytes_And_Origin(string item, string label, string expected)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _port);
        using var reader = new StreamReader(tcp.GetStream());
        await using var writer = new StreamWriter(tcp.GetStream()) { NewLine = "\r\n", AutoFlush = true };
        await reader.ReadLineAsync();
        await SendAsync(writer, reader, "a1 LOGIN admin admin", "a1");
        await SendAsync(writer, reader, "a2 EXAMINE INBOX", "a2");

        var fetch = await SendAsync(writer, reader, $"a3 UID FETCH 1 ({item} FLAGS)", "a3");
        Assert.Equal($"* 1 FETCH (UID 1 {label} {{{Encoding.UTF8.GetByteCount(expected)}}}\r\n" +
            expected + " FLAGS (\\Seen))\r\na3 OK UID FETCH completed\r\n", fetch);
    }

    [Theory]
    [InlineData("<0.0>")]
    [InlineData("<-1.10>")]
    [InlineData("<0.4294967296>")]
    [InlineData("<0>")]
    [InlineData("<0.10>garbage")]
    public async Task Invalid_Partial_Fetch_Is_Rejected_Without_Losing_Session(string partial)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _port);
        using var reader = new StreamReader(tcp.GetStream());
        await using var writer = new StreamWriter(tcp.GetStream()) { NewLine = "\r\n", AutoFlush = true };
        await reader.ReadLineAsync();
        await SendAsync(writer, reader, "a1 LOGIN admin admin", "a1");
        await SendAsync(writer, reader, "a2 EXAMINE INBOX", "a2");
        var fetch = await SendAsync(writer, reader, $"a3 UID FETCH 1 (BODY.PEEK[]{partial})", "a3");
        Assert.StartsWith("a3 BAD ", fetch);
        Assert.Equal("a4 OK NOOP completed\r\n", await SendAsync(writer, reader, "a4 NOOP", "a4"));
    }

    private static async Task<string> SendAsync(StreamWriter w, StreamReader r, string command, string tag)
    {
        await w.WriteLineAsync(command);
        var sb = new StringBuilder();
        string? line;
        while ((line = await r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))) is not null)
        {
            sb.Append(line).Append("\r\n");
            if (line.StartsWith(tag + " ", StringComparison.Ordinal)) break; // tagged completion
        }
        return sb.ToString();
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
