using System.Globalization;
using System.Text;
using Inbix.Core.Domain;
using MimeKit;

namespace Inbix.Imap;

/// <summary>IMAP wire-format helpers: quoted strings, ENVELOPE, BODYSTRUCTURE, and body-section extraction.</summary>
internal static class ImapFormat
{
    public static string NString(string? s)
    {
        if (s is null) return "NIL";
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    public static string InternalDate(DateTimeOffset dt) => "\"" + dt.ToString("dd-MMM-yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + Offset(dt) + "\"";

    private static string Rfc2822(DateTimeOffset dt) => dt.ToString("ddd, dd MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + Offset(dt);

    private static string Offset(DateTimeOffset dt)
    {
        var o = dt.Offset;
        return (o < TimeSpan.Zero ? "-" : "+") + Math.Abs(o.Hours).ToString("00") + Math.Abs(o.Minutes).ToString("00");
    }

    // ENVELOPE = (date subject from sender reply-to to cc bcc in-reply-to message-id)
    public static string Envelope(Message m)
    {
        var from = AddressList(m.Sender);
        var to = AddressList(m.Recipient);
        return $"({NString(Rfc2822(m.ReceivedAt))} {NString(m.Subject)} {from} {from} {from} {to} NIL NIL NIL {NString(m.MessageIdHeader)})";
    }

    private static string AddressList(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "NIL";
        string? name = null, mailbox, host = null;
        try
        {
            var mb = MailboxAddress.Parse(address);
            name = string.IsNullOrEmpty(mb.Name) ? null : mb.Name;
            var at = mb.Address.LastIndexOf('@');
            (mailbox, host) = at > 0 ? (mb.Address[..at], mb.Address[(at + 1)..]) : (mb.Address, null);
        }
        catch
        {
            var at = address.LastIndexOf('@');
            (mailbox, host) = at > 0 ? (address[..at], address[(at + 1)..]) : (address, null);
        }
        return $"(({NString(name)} NIL {NString(mailbox)} {NString(host)}))";
    }

    // ---- BODYSTRUCTURE ----

    public static string BodyStructure(MimeEntity entity)
    {
        if (entity is Multipart mp)
        {
            var sb = new StringBuilder("(");
            foreach (var child in mp) sb.Append(BodyStructure(child));
            sb.Append(' ').Append(NString(mp.ContentType.MediaSubtype));
            sb.Append(')');
            return sb.ToString();
        }

        var ct = entity.ContentType;
        var type = ct.MediaType;
        var (octets, lines) = Measure(entity);
        var paramList = Params(ct);
        var id = NString(entity.ContentId);
        // body-fld-enc is a STRING (RFC 3501) — must be quoted, or strict clients skip decoding
        // (e.g. quoted-printable text then shows literal "=" artifacts).
        var enc = NString(Encoding(entity is MimePart p ? p.ContentTransferEncoding : ContentEncoding.SevenBit));

        if (type.Equals("text", StringComparison.OrdinalIgnoreCase))
            return $"({NString(type)} {NString(ct.MediaSubtype)} {paramList} {id} NIL {enc} {octets} {lines})";

        return $"({NString(type)} {NString(ct.MediaSubtype)} {paramList} {id} NIL {enc} {octets})";
    }

    private static string Params(ContentType ct)
    {
        var ps = new List<string>();
        if (!string.IsNullOrEmpty(ct.Charset)) { ps.Add(NString("CHARSET")); ps.Add(NString(ct.Charset)); }
        if (!string.IsNullOrEmpty(ct.Name)) { ps.Add(NString("NAME")); ps.Add(NString(ct.Name)); }
        return ps.Count == 0 ? "NIL" : "(" + string.Join(" ", ps) + ")";
    }

    private static string Encoding(ContentEncoding e) => e switch
    {
        ContentEncoding.EightBit => "8BIT",
        ContentEncoding.Binary => "BINARY",
        ContentEncoding.Base64 => "BASE64",
        ContentEncoding.QuotedPrintable => "QUOTED-PRINTABLE",
        ContentEncoding.UUEncode => "X-UUENCODE",
        _ => "7BIT",
    };

    private static (long octets, int lines) Measure(MimeEntity entity)
    {
        if (entity is not MimePart part || part.Content is null) return (0, 0);
        using var ms = new MemoryStream();
        part.Content.WriteTo(ms);
        var bytes = ms.GetBuffer();
        var len = ms.Length;
        var lines = 0;
        for (long i = 0; i < len; i++) if (bytes[i] == (byte)'\n') lines++;
        return (len, lines);
    }

    // ---- Body-section extraction (BODY[<section>]) ----
    // Returns the bytes for a section, or null if it can't be resolved (caller falls back to whole message).

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
            var message = MimeMessage.Load(stream);
            return message.Body is null ? null : PartSection(message.Body, upper);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? PartSection(MimeEntity root, string spec)
    {
        string? suffix = null;
        foreach (var s in new[] { ".MIME", ".HEADER", ".TEXT" })
            if (spec.EndsWith(s, StringComparison.Ordinal)) { suffix = s; spec = spec[..^s.Length]; break; }

        var nums = new List<int>();
        foreach (var p in spec.Split('.', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(p, out var n)) nums.Add(n); else return null;

        var entity = Navigate(root, nums);
        if (entity is null) return null;

        if (suffix == ".MIME") return HeadersOf(entity);
        if (entity is MimePart part && suffix is null)
        {
            using var ms = new MemoryStream();
            part.Content?.WriteTo(ms);
            return ms.ToArray();
        }
        // Fallback: write the whole entity (headers + content).
        using var all = new MemoryStream();
        entity.WriteTo(all);
        return all.ToArray();
    }

    private static MimeEntity? Navigate(MimeEntity current, List<int> nums)
    {
        for (var depth = 0; depth < nums.Count; depth++)
        {
            var n = nums[depth];
            if (current is Multipart mp)
            {
                if (n < 1 || n > mp.Count) return null;
                current = mp[n - 1];
            }
            else if (current is MessagePart msg && msg.Message is not null)
            {
                current = msg.Message.Body;
                if (current is Multipart inner) { if (n < 1 || n > inner.Count) return null; current = inner[n - 1]; }
                else if (n != 1) return null;
            }
            else
            {
                // Single leaf part: section "1" refers to it; anything deeper is invalid.
                if (n != 1 || depth != nums.Count - 1) return null;
            }
        }
        return current;
    }

    private static byte[] HeadersOf(MimeEntity entity)
    {
        using var ms = new MemoryStream();
        entity.Headers.WriteTo(ms);
        return ms.ToArray();
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
