using System.Globalization;
using System.Text;
using MimeKit;

namespace Inbix.Imap;

/// <summary>IMAP wire-format helpers: quoted strings, ENVELOPE, BODYSTRUCTURE, and body-section extraction.</summary>
internal static class ImapFormat
{
    public static string NString(string? s)
    {
        if (s is null) return "NIL";
        if (s.Any(c => c < 32 || c > 126))
            return "{" + System.Text.Encoding.UTF8.GetByteCount(s).ToString(CultureInfo.InvariantCulture) + "}\r\n" + s;
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    public static string InternalDate(DateTimeOffset dt) => "\"" + dt.ToString("dd-MMM-yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + Offset(dt) + "\"";

    private static string Offset(DateTimeOffset dt)
    {
        var o = dt.Offset;
        return (o < TimeSpan.Zero ? "-" : "+") + Math.Abs(o.Hours).ToString("00") + Math.Abs(o.Minutes).ToString("00");
    }

    // Read the original RFC 5322 headers, including messages not yet processed by the worker.
    public static string Envelope(MimeMessage m)
    {
        var from = AddressList(m.From);
        var sender = m.Sender is null ? from : AddressList(new InternetAddressList { m.Sender });
        var reply = m.ReplyTo.Count == 0 ? from : AddressList(m.ReplyTo);
        return $"({NString(m.Headers[HeaderId.Date])} {NString(HeaderText(m.Subject))} {from} {sender} {reply} " +
            $"{AddressList(m.To)} {AddressList(m.Cc)} {AddressList(m.Bcc)} " +
            $"{NString(m.Headers[HeaderId.InReplyTo])} {NString(m.Headers[HeaderId.MessageId])})";
    }

    private static string? HeaderText(string? text) => text is null ? null :
        System.Text.Encoding.ASCII.GetString(MimeKit.Utils.Rfc2047.EncodeText(System.Text.Encoding.UTF8, text))
            .Replace("\r", "").Replace("\n", " ");

    private static string AddressList(InternetAddressList addresses)
    {
        if (addresses.Count == 0) return "NIL";
        var result = new StringBuilder("(");
        void Add(InternetAddress address)
        {
            if (address is GroupAddress group)
            {
                result.Append($"(NIL NIL {NString(HeaderText(group.Name))} NIL)");
                foreach (var member in group.Members) Add(member);
                result.Append("(NIL NIL NIL NIL)");
            }
            else if (address is MailboxAddress mailbox)
            {
                var at = mailbox.Address.LastIndexOf('@');
                result.Append($"({NString(HeaderText(string.IsNullOrEmpty(mailbox.Name) ? null : mailbox.Name))} NIL " +
                    $"{NString(at < 0 ? mailbox.Address : mailbox.Address[..at])} {NString(at < 0 ? null : mailbox.Address[(at + 1)..])})");
            }
        }
        foreach (var address in addresses) Add(address);
        return result.Append(')').ToString();
    }

    public static string BodyStructure(MimeEntity? entity)
    {
        if (entity is null) return "(\"TEXT\" \"PLAIN\" NIL NIL NIL \"7BIT\" 0 0)";
        if (entity is Multipart multipart)
            return "(" + string.Concat(multipart.Select(BodyStructure)) + " " + NString(multipart.ContentType.MediaSubtype) + ")";
        var ct = entity.ContentType;
        var body = TextBlock(Serialize(entity));
        var lines = body.Count(b => b == '\n');
        var encoding = entity.Headers[HeaderId.ContentTransferEncoding] ?? "7BIT";
        var fields = $"{NString(ct.MediaType)} {NString(ct.MediaSubtype)} {Params(ct)} " +
            $"{NString(entity.Headers[HeaderId.ContentId])} {NString(HeaderText(entity.Headers[HeaderId.ContentDescription]))} " +
            $"{NString(encoding.ToUpperInvariant())} {body.Length}";
        if (entity is MessagePart nested && nested.Message is not null)
            return $"({fields} {Envelope(nested.Message)} {BodyStructure(nested.Message.Body)} {lines})";
        return ct.MediaType.Equals("text", StringComparison.OrdinalIgnoreCase)
            ? $"({fields} {lines})" : $"({fields})";
    }

    private static string Params(ContentType ct) => ct.Parameters.Count == 0 ? "NIL" :
        "(" + string.Join(" ", ct.Parameters.Select(p => NString(p.Name) + " " + NString(HeaderText(p.Value)))) + ")";

    private static byte[] Serialize(MimeEntity entity)
    {
        using var stream = new MemoryStream();
        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        entity.WriteTo(options, stream);
        return stream.ToArray();
    }

    private static byte[] Serialize(MimeMessage message)
    {
        using var stream = new MemoryStream();
        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        message.WriteTo(options, stream);
        return stream.ToArray();
    }
    // ---- Body-section extraction (BODY[<section>]) ----
    // Returns section bytes, or null for a nonexistent section (the FETCH response then uses NIL).

    public static byte[]? Section(byte[] raw, string section)
    {
        section = section.Trim();
        if (section.Length == 0) return raw;

        var upper = section.ToUpperInvariant();
        if (upper == "HEADER") return HeaderBlock(raw);
        if (upper == "TEXT") return TextBlock(raw);
        if (upper.StartsWith("HEADER.FIELDS", StringComparison.Ordinal)) return HeaderFields(raw, section);

        // Numbered part sections (e.g. "1", "1.2", "2.MIME"), resolved with MimeKit.
        try
        {
            using var stream = new MemoryStream(raw);
            using var message = MimeMessage.Load(stream);
            return message.Body is null ? null : PartSection(message.Body, upper);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? PartSection(MimeEntity root, string spec)
    {
        var match = System.Text.RegularExpressions.Regex.Match(spec, @"^(\d+(?:\.\d+)*)(?:\.(.*))?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success) return null;
        var numbers = match.Groups[1].Value.Split('.');
        MimeEntity? entity = root;
        for (var i = 0; i < numbers.Length; i++)
        {
            if (!int.TryParse(numbers[i], out var n) || n < 1) return null;
            var embedded = i > 0 && entity is MessagePart;
            if (embedded) entity = ((MessagePart)entity!).Message?.Body;
            if (entity is Multipart mp)
            {
                if (n > mp.Count) return null;
                entity = mp[n - 1];
            }
            else if (entity is null || n != 1 || i > 0 && !embedded)
                return null;
        }
        if (entity is null) return null;
        var suffix = match.Groups[2].Value;
        if (suffix == "MIME") return HeaderBlock(Serialize(entity));
        if (suffix.Length == 0) return TextBlock(Serialize(entity));
        if (entity is MessagePart nested && nested.Message is not null)
            return Section(Serialize(nested.Message), suffix);
        return null;
    }
    // ---- Raw header/body splitting (no MIME parse) ----

    private static int BodyStart(byte[] raw)
    {
        for (var i = 0; i + 3 < raw.Length; i++)
            if (raw[i] == '\r' && raw[i + 1] == '\n' && raw[i + 2] == '\r' && raw[i + 3] == '\n')
                return i + 4;
        // Fall back to bare-LF blank line.
        for (var i = 0; i + 1 < raw.Length; i++)
            if (raw[i] == '\n' && raw[i + 1] == '\n')
                return i + 2;
        return raw.Length;
    }

    private static byte[] HeaderBlock(byte[] raw) => raw[..BodyStart(raw)];
    private static byte[] TextBlock(byte[] raw) { var s = BodyStart(raw); return s >= raw.Length ? [] : raw[s..]; }

    private static byte[] HeaderFields(byte[] raw, string section)
    {
        var not = section.Contains("HEADER.FIELDS.NOT", StringComparison.OrdinalIgnoreCase);
        var open = section.IndexOf('(');
        var close = section.LastIndexOf(')');
        var wanted = open >= 0 && close > open
            ? section[(open + 1)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(f => f.Trim('"').ToLowerInvariant()).ToHashSet()
            : [];

        var header = System.Text.Encoding.ASCII.GetString(HeaderBlock(raw));
        var lines = header.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        var keep = false;
        foreach (var line in lines)
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                if (keep) sb.Append(line).Append("\r\n"); // header continuation
                continue;
            }
            var colon = line.IndexOf(':');
            var name = colon > 0 ? line[..colon].Trim().ToLowerInvariant() : line.Trim().ToLowerInvariant();
            keep = name.Length > 0 && (not ? !wanted.Contains(name) : wanted.Contains(name));
            if (keep) sb.Append(line).Append("\r\n");
        }
        sb.Append("\r\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }
}
