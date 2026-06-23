using System.Text;
using Inbix.Worker;
using Xunit;

namespace Inbix.Tests;

public class MimeEmailParserTests
{
    [Fact]
    public async Task Parses_Headers_Body_And_Attachment()
    {
        const string raw =
            "From: Sender Name <sender@example.com>\r\n" +
            "To: spotify@mydomain.com\r\n" +
            "Subject: Hello Inbix\r\n" +
            "Message-Id: <abc123@example.com>\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"BOUND\"\r\n" +
            "\r\n" +
            "--BOUND\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n\r\n" +
            "This is the body.\r\n" +
            "--BOUND\r\n" +
            "Content-Type: text/plain; name=\"note.txt\"\r\n" +
            "Content-Disposition: attachment; filename=\"note.txt\"\r\n\r\n" +
            "attached text\r\n" +
            "--BOUND--\r\n";

        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));
        var parsed = await MimeEmailParser.ParseAsync(stream);

        Assert.Equal("Hello Inbix", parsed.Subject);
        Assert.Equal("sender@example.com", parsed.Sender);
        Assert.Equal("abc123@example.com", parsed.MessageId);
        Assert.Contains("This is the body.", parsed.TextBody);
        Assert.Single(parsed.Attachments);
        Assert.Equal("note.txt", parsed.Attachments[0].FileName);
        Assert.Equal("attached text", Encoding.ASCII.GetString(parsed.Attachments[0].Content).Trim());
    }

    [Fact]
    public async Task Parses_Message_With_No_Attachments()
    {
        const string raw =
            "From: a@b.com\r\nTo: c@d.com\r\nSubject: Plain\r\n\r\nJust text.\r\n";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));
        var parsed = await MimeEmailParser.ParseAsync(stream);

        Assert.Equal("Plain", parsed.Subject);
        Assert.Empty(parsed.Attachments);
        Assert.Contains("Just text.", parsed.TextBody);
    }
}
