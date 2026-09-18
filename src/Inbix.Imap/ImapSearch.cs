using System.Globalization;
using Inbix.Core.Domain;
using MimeKit;

namespace Inbix.Imap;

internal sealed class ImapSearch
{
    internal sealed record Item(Message Message, int Sequence, uint Uid, bool Deleted, MimeMessage? Mime);
    public bool NeedsMime { get; private set; }
    private readonly long _maxSequence, _maxUid;

    public ImapSearch(long maxSequence, long maxUid) => (_maxSequence, _maxUid) = (maxSequence, maxUid);

    public Func<Item, bool> Parse(List<string> tokens, int depth = 0)
    {
        if (depth > 32 || tokens.Count == 0) throw new FormatException();
        var index = 0;
        string Next() => index < tokens.Count ? tokens[index++] : throw new FormatException();
        Func<Item, bool> Key(int level)
        {
            if (level > 32) throw new FormatException();
            var token = Next();
            if (token.Length == 0) throw new FormatException();
            if (token.StartsWith('(') && token.EndsWith(')'))
                return Parse(ImapSession.Tokenize(token[1..^1]), level + 1);
            var key = token.ToUpperInvariant();
            if (key == "NOT") { var child = Key(level + 1); return item => !child(item); }
            if (key == "OR") { var left = Key(level + 1); var right = Key(level + 1); return item => left(item) || right(item); }
            if (key == "UID") { var set = ImapSession.ParseSet(Next(), _maxUid); return item => set(item.Uid); }
            if (char.IsAsciiDigit(token[0]) || token[0] == '*')
            { var set = ImapSession.ParseSet(token, _maxSequence); return item => set(item.Sequence); }
            switch (key)
            {
                case "ALL": case "SEEN": case "OLD": case "UNANSWERED": case "UNDRAFT": case "UNFLAGGED": return _ => true;
                case "UNSEEN": case "RECENT": case "NEW": case "ANSWERED": case "DRAFT": case "FLAGGED": return _ => false;
                case "DELETED": return i => i.Deleted;
                case "UNDELETED": return i => !i.Deleted;
                case "KEYWORD": Next(); return _ => false;
                case "UNKEYWORD": Next(); return _ => true;
                case "LARGER": case "SMALLER":
                    if (!uint.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out var size)) throw new FormatException();
                    return i => key == "LARGER" ? i.Message.SizeBytes > size : i.Message.SizeBytes < size;
                case "BEFORE": case "ON": case "SINCE": case "SENTBEFORE": case "SENTON": case "SENTSINCE":
                    if (!DateTime.TryParseExact(Next(), new[] { "d-MMM-yyyy", "dd-MMM-yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw new FormatException();
                    var sent = key.StartsWith("SENT", StringComparison.Ordinal);
                    NeedsMime |= sent;
                    return i =>
                    {
                        if (sent && i.Mime!.Headers[HeaderId.Date] is null) return false;
                        var actual = sent ? i.Mime!.Date.Date : i.Message.ReceivedAt.Date;
                        return key.EndsWith("BEFORE", StringComparison.Ordinal) ? actual < date :
                            key.EndsWith("SINCE", StringComparison.Ordinal) ? actual >= date : actual == date;
                    };
                case "HEADER": case "FROM": case "TO": case "CC": case "BCC": case "SUBJECT": case "BODY": case "TEXT":
                    NeedsMime = true;
                    var field = key == "HEADER" ? Next() : key;
                    var value = Next();
                    return i =>
                    {
                        var mime = i.Mime!;
                        var header = mime.Headers.Any(h => (key == "TEXT" || h.Field.Equals(field, StringComparison.OrdinalIgnoreCase)) && Contains(h.Value, value));
                        if (key is not ("BODY" or "TEXT")) return header;
                        return key == "TEXT" && header || mime.BodyParts.OfType<TextPart>().Any(p => Contains(p.Text, value));
                    };
                default: throw new FormatException();
            }
        }
        var predicates = new List<Func<Item, bool>>();
        while (index < tokens.Count) predicates.Add(Key(depth));
        return item => predicates.All(p => p(item));
    }

    private static bool Contains(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
