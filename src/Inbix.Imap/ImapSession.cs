using System.Text;
using System.Threading.Channels;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Inbix.Core.Security;
using Microsoft.Extensions.Logging;

namespace Inbix.Imap;

/// <summary>
/// A single read-only IMAP connection: parses commands, authenticates against <see cref="ImapOptions"/>,
/// and serves mailboxes/messages from the repositories. Everything is read-only — writes are refused and
/// flag changes are accepted but not persisted.
/// </summary>
public sealed class ImapSession
{
    private readonly Stream _stream;
    private readonly ImapMailboxProvider _mailboxes;
    private readonly IRawMessageStore _rawStore;
    private readonly ImapOptions _options;
    private readonly IInboxNotifier _notifier;
    private readonly ILogger _logger;

    // Read timeouts stop idle/slowloris connections from holding a session slot forever.
    private static readonly TimeSpan PreAuthTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    private const int MaxFailedLogins = 5;
    private const int PreAuthLiteralCap = 8 * 1024;          // literals before auth are tiny (LOGIN)
    private const int PostAuthLiteralCap = 64 * 1024 * 1024;

    private bool _authenticated;
    private int _failedLogins;
    private bool _closing;               // set after too many failed logins → drop the connection
    private string? _selectedName;
    private bool _writable;              // current selection allows \Deleted/EXPUNGE (SELECT + AllowDelete, not EXAMINE)
    private List<Message> _selected = [];
    private readonly HashSet<long> _deleted = []; // message ids flagged \Deleted this session (AllowDelete)

    private readonly byte[] _rbuf = new byte[16384];
    private int _rlen, _rpos;

    public ImapSession(Stream stream, ImapMailboxProvider mailboxes, IRawMessageStore rawStore,
        ImapOptions options, IInboxNotifier notifier, ILogger logger)
    {
        _stream = stream;
        _mailboxes = mailboxes;
        _rawStore = rawStore;
        _options = options;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await SendAsync("* OK [CAPABILITY " + Capabilities() + "] Inbix IMAP ready\r\n", ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            string? line;
            using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                readCts.CancelAfter(_authenticated ? IdleTimeout : PreAuthTimeout);
                try
                {
                    line = await ReadCommandAsync(readCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await SendAsync("* BYE idle timeout\r\n", ct).ConfigureAwait(false); // slowloris / idle protection
                    break;
                }
            }
            if (line is null) break;

            var sp = line.IndexOf(' ');
            if (sp <= 0) { await SendAsync("* BAD missing tag\r\n", ct).ConfigureAwait(false); continue; }
            var tag = line[..sp];
            var rest = line[(sp + 1)..];

            try
            {
                if (await DispatchAsync(tag, rest, ct).ConfigureAwait(false)) break; // LOGOUT / forced close
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Log only the command verb, never the arguments (they can contain a LOGIN password).
                _logger.LogWarning(ex, "IMAP command failed: {Command}", Verb(rest));
                await SendAsync($"{tag} BAD internal error\r\n", ct).ConfigureAwait(false);
            }
        }
    }

    private static string Verb(string rest)
    {
        var sp = rest.IndexOf(' ');
        return sp < 0 ? rest : rest[..sp];
    }

    private static string Capabilities() => "IMAP4rev1 IDLE AUTH=PLAIN";

    private async Task<bool> DispatchAsync(string tag, string rest, CancellationToken ct)
    {
        var tokens = Tokenize(rest);
        var cmd = tokens.Count > 0 ? tokens[0].ToUpperInvariant() : "";

        switch (cmd)
        {
            case "CAPABILITY":
                await SendAsync($"* CAPABILITY {Capabilities()}\r\n{tag} OK CAPABILITY completed\r\n", ct).ConfigureAwait(false);
                return false;
            case "NOOP":
            case "CHECK":
                await SendAsync($"{tag} OK {cmd} completed\r\n", ct).ConfigureAwait(false);
                return false;
            case "LOGOUT":
                await SendAsync($"* BYE Inbix logging out\r\n{tag} OK LOGOUT completed\r\n", ct).ConfigureAwait(false);
                return true;
            case "LOGIN":
                await LoginAsync(tag, tokens, ct).ConfigureAwait(false);
                return _closing;
            case "AUTHENTICATE":
                await AuthenticateAsync(tag, tokens, ct).ConfigureAwait(false);
                return _closing;
        }

        if (!_authenticated)
        {
            await SendAsync($"{tag} NO Not authenticated\r\n", ct).ConfigureAwait(false);
            return false;
        }

        switch (cmd)
        {
            case "LIST":
            case "LSUB":
                await ListAsync(tag, cmd, tokens, ct).ConfigureAwait(false); break;
            case "SELECT":
            case "EXAMINE":
                await SelectAsync(tag, tokens, examine: cmd == "EXAMINE", ct).ConfigureAwait(false); break;
            case "STATUS":
                await StatusAsync(tag, tokens, ct).ConfigureAwait(false); break;
            case "FETCH":
                await FetchAsync(tag, tokens, byUid: false, ct).ConfigureAwait(false); break;
            case "SEARCH":
                await SearchAsync(tag, tokens, byUid: false, ct).ConfigureAwait(false); break;
            case "STORE":
                await StoreAsync(tag, tokens, byUid: false, ct).ConfigureAwait(false); break;
            case "UID":
                await UidAsync(tag, tokens, ct).ConfigureAwait(false); break;
            case "EXPUNGE":
                await ExpungeAsync(tag, restrictUids: null, byUid: false, ct).ConfigureAwait(false); break;
            case "IDLE":
                if (await IdleAsync(tag, ct).ConfigureAwait(false)) return true; // idle timeout → drop
                break;
            case "CLOSE":
                // Only expunge on CLOSE when the mailbox was opened read-write (SELECT + AllowDelete).
                // A read-only (EXAMINE) mailbox must never delete on close.
                if (_writable && _selectedName is not null)
                    await DoExpungeAsync(silent: true, restrictUids: null, ct).ConfigureAwait(false);
                _selectedName = null; _selected = []; _deleted.Clear(); _writable = false;
                await SendAsync($"{tag} OK CLOSE completed\r\n", ct).ConfigureAwait(false); break;
            case "SUBSCRIBE":
            case "UNSUBSCRIBE":
                await SendAsync($"{tag} OK {cmd} completed\r\n", ct).ConfigureAwait(false); break;
            case "CREATE":
            case "DELETE":
            case "RENAME":
            case "APPEND":
                await SendAsync($"{tag} NO [CANNOT] Inbix mailboxes are read-only\r\n", ct).ConfigureAwait(false); break;
            default:
                await SendAsync($"{tag} BAD Unknown command\r\n", ct).ConfigureAwait(false); break;
        }
        return false;
    }

    // ---- Authentication ----

    private async Task LoginAsync(string tag, List<string> tokens, CancellationToken ct)
    {
        if (tokens.Count < 3) { await SendAsync($"{tag} BAD LOGIN expects username and password\r\n", ct).ConfigureAwait(false); return; }
        await FinishAuthAsync(tag, tokens[1], tokens[2], ct).ConfigureAwait(false);
    }

    private async Task AuthenticateAsync(string tag, List<string> tokens, CancellationToken ct)
    {
        if (tokens.Count < 2 || !tokens[1].Equals("PLAIN", StringComparison.OrdinalIgnoreCase))
        {
            await SendAsync($"{tag} NO Only AUTHENTICATE PLAIN is supported\r\n", ct).ConfigureAwait(false);
            return;
        }
        await SendAsync("+ \r\n", ct).ConfigureAwait(false);
        var b64 = await ReadLineAsync(ct).ConfigureAwait(false);
        if (b64 is null) return;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64.Trim())).Split('\0'); // authzid \0 authcid \0 pass
            var user = parts.Length >= 3 ? parts[1] : "";
            var pass = parts.Length >= 3 ? parts[2] : "";
            await FinishAuthAsync(tag, user, pass, ct).ConfigureAwait(false);
        }
        catch
        {
            await SendAsync($"{tag} NO Invalid credentials\r\n", ct).ConfigureAwait(false);
        }
    }

    private async Task FinishAuthAsync(string tag, string user, string pass, CancellationToken ct)
    {
        if (Verify(user, pass))
        {
            _authenticated = true;
            _failedLogins = 0;
            await SendAsync($"{tag} OK LOGIN completed\r\n", ct).ConfigureAwait(false);
            return;
        }

        await Task.Delay(300, ct).ConfigureAwait(false); // throttle
        await SendAsync($"{tag} NO [AUTHENTICATIONFAILED] Invalid credentials\r\n", ct).ConfigureAwait(false);
        if (++_failedLogins >= MaxFailedLogins)
        {
            await SendAsync("* BYE too many failed login attempts\r\n", ct).ConfigureAwait(false);
            _closing = true; // drop the connection to blunt brute-forcing
        }
    }

    private bool Verify(string user, string pass)
    {
        var userOk = FixedEquals(user, _options.Username);
        var passOk = !string.IsNullOrEmpty(_options.PasswordHash)
            ? PasswordHasher.Verify(pass, _options.PasswordHash)
            : FixedEquals(pass, _options.Password);
        return userOk && passOk;
    }

    private static bool FixedEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    // ---- Mailboxes ----

    private async Task ListAsync(string tag, string cmd, List<string> tokens, CancellationToken ct)
    {
        var reference = tokens.Count > 1 ? tokens[1] : "";
        var pattern = tokens.Count > 2 ? tokens[2] : "*";
        var sb = new StringBuilder();

        if (pattern.Length == 0)
        {
            // Delimiter probe: LIST "" "" returns just the hierarchy delimiter.
            sb.Append($"* {cmd} (\\Noselect) \"/\" \"\"\r\n");
        }
        else
        {
            var rx = PatternRegex(reference + pattern);
            foreach (var mb in await _mailboxes.ListAsync(ct).ConfigureAwait(false))
                if (rx.IsMatch(mb.Name))
                {
                    var attrs = mb.Selectable ? "\\HasNoChildren" : "\\Noselect \\HasChildren";
                    sb.Append($"* {cmd} ({attrs}) \"/\" {ImapFormat.NString(mb.Name)}\r\n");
                }
        }

        sb.Append($"{tag} OK {cmd} completed\r\n");
        await SendAsync(sb.ToString(), ct).ConfigureAwait(false);
    }

    private static System.Text.RegularExpressions.Regex PatternRegex(string pattern)
    {
        // IMAP wildcards: * matches anything (incl. hierarchy), % matches within one level.
        var escaped = System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("%", "[^/]*");
        return new System.Text.RegularExpressions.Regex("^" + escaped + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); // guard against ReDoS
    }

    private async Task SelectAsync(string tag, List<string> tokens, bool examine, CancellationToken ct)
    {
        if (tokens.Count < 2) { await SendAsync($"{tag} BAD missing mailbox\r\n", ct).ConfigureAwait(false); return; }
        var name = tokens[1];
        var msgs = await _mailboxes.GetMessagesAsync(name, ct).ConfigureAwait(false);
        if (msgs is null) { await SendAsync($"{tag} NO [NONEXISTENT] Mailbox does not exist\r\n", ct).ConfigureAwait(false); return; }

        _selectedName = name;
        _selected = msgs.ToList();
        _deleted.Clear();

        // SELECT is read-write only when deletes are allowed; EXAMINE is always read-only.
        var writable = _options.AllowDelete && !examine;
        _writable = writable;
        var uidNext = (_selected.Count > 0 ? _selected[^1].Id : 0) + 1;
        var sb = new StringBuilder();
        sb.Append(writable ? "* FLAGS (\\Seen \\Deleted)\r\n" : "* FLAGS (\\Seen)\r\n");
        sb.Append($"* {_selected.Count} EXISTS\r\n");
        sb.Append("* 0 RECENT\r\n");
        sb.Append("* OK [UIDVALIDITY 1] UIDs valid\r\n");
        sb.Append($"* OK [UIDNEXT {uidNext}] Predicted next UID\r\n");
        sb.Append(writable
            ? "* OK [PERMANENTFLAGS (\\Deleted)] Deletes are permanent\r\n"
            : "* OK [PERMANENTFLAGS ()] No permanent flags (read-only)\r\n");
        sb.Append($"{tag} OK [{(writable ? "READ-WRITE" : "READ-ONLY")}] SELECT completed\r\n");
        await SendAsync(sb.ToString(), ct).ConfigureAwait(false);
    }

    private async Task StatusAsync(string tag, List<string> tokens, CancellationToken ct)
    {
        if (tokens.Count < 2) { await SendAsync($"{tag} BAD missing mailbox\r\n", ct).ConfigureAwait(false); return; }
        var name = tokens[1];
        var msgs = await _mailboxes.GetMessagesAsync(name, ct).ConfigureAwait(false);
        if (msgs is null) { await SendAsync($"{tag} NO Mailbox does not exist\r\n", ct).ConfigureAwait(false); return; }
        var uidNext = (msgs.Count > 0 ? msgs[^1].Id : 0) + 1;
        await SendAsync(
            $"* STATUS {ImapFormat.NString(name)} (MESSAGES {msgs.Count} RECENT 0 UIDNEXT {uidNext} UIDVALIDITY 1 UNSEEN 0)\r\n" +
            $"{tag} OK STATUS completed\r\n", ct).ConfigureAwait(false);
    }

    // ---- FETCH ----

    private Task UidAsync(string tag, List<string> tokens, CancellationToken ct)
    {
        var sub = tokens.Count > 1 ? tokens[1].ToUpperInvariant() : "";
        var inner = tokens.Skip(1).ToList(); // sub becomes tokens[0] of inner
        return sub switch
        {
            "FETCH" => FetchAsync(tag, inner, byUid: true, ct),
            "SEARCH" => SearchAsync(tag, inner, byUid: true, ct),
            "STORE" => StoreAsync(tag, inner, byUid: true, ct),
            "EXPUNGE" => ExpungeAsync(tag, restrictUids: UidSet(inner.Count > 1 ? inner[1] : ""), byUid: true, ct),
            _ => SendAsync($"{tag} BAD Unsupported UID command\r\n", ct),
        };
    }

    private async Task FetchAsync(string tag, List<string> tokens, bool byUid, CancellationToken ct)
    {
        if (_selectedName is null) { await SendAsync($"{tag} NO No mailbox selected\r\n", ct).ConfigureAwait(false); return; }
        if (tokens.Count < 3) { await SendAsync($"{tag} BAD FETCH expects a set and items\r\n", ct).ConfigureAwait(false); return; }

        var targets = Resolve(tokens[1], byUid);
        var items = ParseFetchItems(tokens[2]);

        foreach (var (seq, msg) in targets)
            await WriteFetchAsync(seq, msg, items, byUid, ct).ConfigureAwait(false);

        await SendAsync($"{tag} OK {(byUid ? "UID " : "")}FETCH completed\r\n", ct).ConfigureAwait(false);
    }

    private async Task WriteFetchAsync(int seq, Message msg, List<FetchItem> items, bool byUid, CancellationToken ct)
    {
        // UID FETCH always includes UID in the response even if not requested.
        var wantUid = byUid || items.Any(i => i.Name == "UID");
        var needRaw = items.Any(i => i.Name is "BODY" or "BODYSTRUCTURE" or "BODY[section]");
        var raw = needRaw ? await LoadRawAsync(msg, ct).ConfigureAwait(false) : [];

        var w = new MemoryStream();
        void Text(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write(b, 0, b.Length); }

        Text($"* {seq} FETCH (");
        var first = true;
        void Sep() { if (!first) Text(" "); first = false; }

        if (wantUid) { Sep(); Text($"UID {msg.Id}"); }

        foreach (var item in items)
        {
            switch (item.Name)
            {
                case "UID": break; // already emitted
                case "FLAGS": Sep(); Text("FLAGS (\\Seen)"); break;
                case "INTERNALDATE": Sep(); Text($"INTERNALDATE {ImapFormat.InternalDate(msg.ReceivedAt)}"); break;
                case "RFC822.SIZE": Sep(); Text($"RFC822.SIZE {msg.SizeBytes}"); break;
                case "ENVELOPE": Sep(); Text($"ENVELOPE {ImapFormat.Envelope(msg)}"); break;
                case "BODY":
                case "BODYSTRUCTURE":
                    Sep(); Text($"{item.Name} {BodyStructureOf(raw)}"); break;
                default: // BODY[section] / BODY.PEEK[section] / RFC822[.HEADER/.TEXT]
                    var data = ImapFormat.Section(raw, item.Section ?? "") ?? [];
                    Sep();
                    Text($"{item.Label} {{{data.Length}}}\r\n");
                    w.Write(data, 0, data.Length);
                    break;
            }
        }

        Text(")\r\n");
        await SendBytesAsync(w.ToArray(), ct).ConfigureAwait(false);
    }

    private static string BodyStructureOf(byte[]? raw)
    {
        if (raw is null || raw.Length == 0) return "(\"TEXT\" \"PLAIN\" NIL NIL NIL \"7BIT\" 0 0)";
        try
        {
            using var ms = new MemoryStream(raw);
            var msg = MimeKit.MimeMessage.Load(ms);
            return msg.Body is null ? "(\"TEXT\" \"PLAIN\" NIL NIL NIL \"7BIT\" 0 0)" : ImapFormat.BodyStructure(msg.Body);
        }
        catch { return "(\"TEXT\" \"PLAIN\" NIL NIL NIL \"7BIT\" 0 0)"; }
    }

    private async Task<byte[]> LoadRawAsync(Message msg, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(msg.RawStoragePath)) return [];
        try
        {
            await using var s = await _rawStore.OpenReadAsync(msg.RawStoragePath, ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms, ct).ConfigureAwait(false);
            return ms.ToArray();
        }
        catch { return []; }
    }

    // ---- SEARCH / STORE ----

    private async Task SearchAsync(string tag, List<string> tokens, bool byUid, CancellationToken ct)
    {
        if (_selectedName is null) { await SendAsync($"{tag} NO No mailbox selected\r\n", ct).ConfigureAwait(false); return; }

        // Minimal: ALL (everything) and UID <set>. Anything else → ALL (safe for read-only browsing).
        IEnumerable<(int seq, Message m)> hits = _selected.Select((m, i) => (i + 1, m));
        var arg = tokens.Count > 1 ? tokens[1].ToUpperInvariant() : "ALL";
        if (arg == "UID" && tokens.Count > 2)
            hits = Resolve(tokens[2], byUid: true);

        var ids = hits.Select(h => byUid ? h.m.Id : h.seq);
        await SendAsync($"* SEARCH {string.Join(' ', ids)}\r\n{tag} OK {(byUid ? "UID " : "")}SEARCH completed\r\n", ct).ConfigureAwait(false);
    }

    private async Task StoreAsync(string tag, List<string> tokens, bool byUid, CancellationToken ct)
    {
        if (_selectedName is null) { await SendAsync($"{tag} NO No mailbox selected\r\n", ct).ConfigureAwait(false); return; }
        var targets = Resolve(tokens.Count > 1 ? tokens[1] : "", byUid);

        // \Deleted is only honoured on a read-write selection (SELECT + AllowDelete, not EXAMINE); otherwise
        // STORE is a no-op that just echoes \Seen so clients don't error.
        if (!_writable)
        {
            foreach (var (seq, _) in targets)
                await SendAsync($"* {seq} FETCH (FLAGS (\\Seen))\r\n", ct).ConfigureAwait(false);
            await SendAsync($"{tag} OK {(byUid ? "UID " : "")}STORE completed\r\n", ct).ConfigureAwait(false);
            return;
        }

        // Honour \Deleted so EXPUNGE can remove mail; other flags aren't persisted.
        var item = tokens.Count > 2 ? tokens[2].ToUpperInvariant() : "";
        var silent = item.Contains(".SILENT", StringComparison.Ordinal);
        var flags = tokens.Count > 3 ? tokens[3] : "";
        var hasDeleted = flags.Contains("\\Deleted", StringComparison.OrdinalIgnoreCase);
        var op = item.StartsWith("+FLAGS", StringComparison.Ordinal) ? '+'
               : item.StartsWith("-FLAGS", StringComparison.Ordinal) ? '-' : '=';

        foreach (var (seq, msg) in targets)
        {
            if (op == '-') { if (hasDeleted) _deleted.Remove(msg.Id); }
            else if (op == '+') { if (hasDeleted) _deleted.Add(msg.Id); }
            else { if (hasDeleted) _deleted.Add(msg.Id); else _deleted.Remove(msg.Id); } // FLAGS (replace)

            if (!silent)
                await SendAsync($"* {seq} FETCH (FLAGS ({FlagsFor(msg)}))\r\n", ct).ConfigureAwait(false);
        }
        await SendAsync($"{tag} OK {(byUid ? "UID " : "")}STORE completed\r\n", ct).ConfigureAwait(false);
    }

    private string FlagsFor(Message m) => _deleted.Contains(m.Id) ? "\\Seen \\Deleted" : "\\Seen";

    private HashSet<long>? UidSet(string set)
    {
        if (string.IsNullOrWhiteSpace(set)) return null;
        return Resolve(set, byUid: true).Select(t => t.m.Id).ToHashSet();
    }

    private async Task ExpungeAsync(string tag, HashSet<long>? restrictUids, bool byUid, CancellationToken ct)
    {
        if (_selectedName is null) { await SendAsync($"{tag} NO No mailbox selected\r\n", ct).ConfigureAwait(false); return; }
        if (!_writable)
        {
            // Read-only (or EXAMINE, or AllowDelete off): never expunge.
            await SendAsync($"{tag} NO [CANNOT] Mailbox is read-only (open with SELECT and set Inbix:Imap:AllowDelete to enable deletes)\r\n", ct).ConfigureAwait(false);
            return;
        }
        await DoExpungeAsync(silent: false, restrictUids, ct).ConfigureAwait(false);
        await SendAsync($"{tag} OK {(byUid ? "UID " : "")}EXPUNGE completed\r\n", ct).ConfigureAwait(false);
    }

    // Permanently remove every \Deleted message (optionally limited to restrictUids). EXPUNGE responses go
    // out highest-seq first so the sequence numbers stay valid as messages are removed.
    private async Task DoExpungeAsync(bool silent, HashSet<long>? restrictUids, CancellationToken ct)
    {
        var seqs = new List<int>();
        for (var i = 0; i < _selected.Count; i++)
        {
            var id = _selected[i].Id;
            if (_deleted.Contains(id) && (restrictUids is null || restrictUids.Contains(id)))
                seqs.Add(i + 1);
        }
        seqs.Sort();
        seqs.Reverse();

        foreach (var seq in seqs)
        {
            var m = _selected[seq - 1];
            await _mailboxes.DeleteAsync(m.Id, ct).ConfigureAwait(false);
            _deleted.Remove(m.Id);
            _selected.RemoveAt(seq - 1);
            if (!silent) await SendAsync($"* {seq} EXPUNGE\r\n", ct).ConfigureAwait(false);
        }
    }

    // ---- IDLE ----

    // Returns true when the connection should be dropped (idle timeout).
    private async Task<bool> IdleAsync(string tag, CancellationToken ct)
    {
        if (_selectedName is null) { await SendAsync($"{tag} BAD No mailbox selected\r\n", ct).ConfigureAwait(false); return false; }

        var signal = Channel.CreateUnbounded<bool>();
        void OnEvent(InboxEvent _) => signal.Writer.TryWrite(true);
        _notifier.Received += OnEvent;
        await SendAsync("+ idling\r\n", ct).ConfigureAwait(false);

        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleCts.CancelAfter(IdleTimeout); // don't let an IDLE connection hold a slot forever
        var readDone = ReadLineAsync(idleCts.Token);
        var timedOut = false;
        try
        {
            while (true)
            {
                var wake = signal.Reader.WaitToReadAsync(ct).AsTask();
                if (await Task.WhenAny(readDone, wake).ConfigureAwait(false) == readDone) break; // client sent DONE / disconnected / timed out

                while (signal.Reader.TryRead(out _)) { }
                var refreshed = await _mailboxes.GetMessagesAsync(_selectedName, ct).ConfigureAwait(false);
                if (refreshed is not null && refreshed.Count != _selected.Count)
                {
                    _selected = refreshed.ToList();
                    await SendAsync($"* {_selected.Count} EXISTS\r\n", ct).ConfigureAwait(false);
                }
            }
            try { await readDone.ConfigureAwait(false); } // observe the read (avoid unobserved exception)
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { timedOut = true; }
        }
        finally
        {
            _notifier.Received -= OnEvent;
        }

        if (timedOut) { await SendAsync("* BYE idle timeout\r\n", ct).ConfigureAwait(false); return true; }
        await SendAsync($"{tag} OK IDLE terminated\r\n", ct).ConfigureAwait(false);
        return false;
    }

    // ---- Sequence / item parsing ----

    private List<(int seq, Message m)> Resolve(string set, bool byUid)
    {
        var result = new List<(int, Message)>();
        if (_selected.Count == 0 || string.IsNullOrWhiteSpace(set)) return result;

        if (byUid)
        {
            var maxUid = _selected[^1].Id;
            var wanted = ParseSet(set, maxUid);
            for (var i = 0; i < _selected.Count; i++)
                if (wanted(_selected[i].Id)) result.Add((i + 1, _selected[i]));
        }
        else
        {
            var wanted = ParseSet(set, _selected.Count);
            for (var i = 0; i < _selected.Count; i++)
                if (wanted(i + 1)) result.Add((i + 1, _selected[i]));
        }
        return result;
    }

    // Returns a predicate matching an id/seq against a set like "1:5,7,9:*".
    private static Func<long, bool> ParseSet(string set, long max)
    {
        var ranges = new List<(long lo, long hi)>();
        foreach (var part in set.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = part.Split(':');
            long lo = Bound(seg[0], max);
            long hi = seg.Length > 1 ? Bound(seg[1], max) : lo;
            if (lo > hi) (lo, hi) = (hi, lo);
            ranges.Add((lo, hi));
        }
        return v => ranges.Any(r => v >= r.lo && v <= r.hi);

        static long Bound(string s, long max) => s.Trim() == "*" ? max : (long.TryParse(s, out var n) ? n : 0);
    }

    private readonly record struct FetchItem(string Name, string? Section, string Label);

    private static List<FetchItem> ParseFetchItems(string spec)
    {
        var body = spec.Trim();
        if (body.StartsWith('(') && body.EndsWith(')')) body = body[1..^1];

        var items = new List<FetchItem>();
        foreach (var raw in Tokenize(body))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            var upper = t.ToUpperInvariant();

            // Macros.
            if (upper == "ALL") { AddAll(items, "FLAGS", "INTERNALDATE", "RFC822.SIZE", "ENVELOPE"); continue; }
            if (upper == "FAST") { AddAll(items, "FLAGS", "INTERNALDATE", "RFC822.SIZE"); continue; }
            if (upper == "FULL") { AddAll(items, "FLAGS", "INTERNALDATE", "RFC822.SIZE", "ENVELOPE", "BODY"); continue; }

            var bracket = t.IndexOf('[');
            if (bracket >= 0)
            {
                var end = t.LastIndexOf(']');
                var section = end > bracket ? t[(bracket + 1)..end] : "";
                var head = upper[..bracket]; // BODY or BODY.PEEK or RFC822
                var label = $"BODY[{section}]";
                items.Add(new FetchItem("BODY[section]", section, label));
                continue;
            }

            switch (upper)
            {
                case "RFC822": items.Add(new("BODY[section]", "", "RFC822")); break;
                case "RFC822.HEADER": items.Add(new("BODY[section]", "HEADER", "RFC822.HEADER")); break;
                case "RFC822.TEXT": items.Add(new("BODY[section]", "TEXT", "RFC822.TEXT")); break;
                case "UID":
                case "FLAGS":
                case "INTERNALDATE":
                case "RFC822.SIZE":
                case "ENVELOPE":
                case "BODY":
                case "BODYSTRUCTURE":
                    items.Add(new(upper, null, upper)); break;
                default: break; // ignore unknown item
            }
        }
        return items;

        static void AddAll(List<FetchItem> l, params string[] names)
        {
            foreach (var n in names) l.Add(new(n, null, n));
        }
    }

    private static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (s[i] == '"')
            {
                var sb = new StringBuilder(); i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i += 2; }
                    else { sb.Append(s[i]); i++; }
                }
                i++; tokens.Add(sb.ToString());
            }
            else if (s[i] == '(')
            {
                var start = i; var depth = 0;
                while (i < s.Length)
                {
                    if (s[i] == '(') depth++;
                    else if (s[i] == ')') { depth--; if (depth == 0) { i++; break; } }
                    else if (s[i] == '"') { i++; while (i < s.Length && s[i] != '"') { if (s[i] == '\\') i++; i++; } }
                    i++;
                }
                tokens.Add(s[start..Math.Min(i, s.Length)]);
            }
            else
            {
                var start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '(' && s[i] != ')')
                {
                    if (s[i] == '[') { var d = 0; while (i < s.Length) { if (s[i] == '[') d++; else if (s[i] == ']') { d--; if (d == 0) { i++; break; } } i++; } }
                    else i++;
                }
                tokens.Add(s[start..i]);
            }
        }
        return tokens;
    }

    // ---- Network I/O (binary-safe line + literal handling) ----

    private async Task<string?> ReadCommandAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var line = await ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return sb.Length > 0 ? sb.ToString() : null;

            var brace = line.LastIndexOf('{');
            if (brace >= 0 && line.EndsWith('}'))
            {
                var inner = line[(brace + 1)..^1];
                var nonSync = inner.EndsWith('+');
                if (nonSync) inner = inner[..^1];
                // Cap literals — tiny before auth (only LOGIN needs one) — so an unauthenticated peer
                // can't force a large allocation. An over-cap literal falls through and the command is rejected.
                var literalCap = _authenticated ? PostAuthLiteralCap : PreAuthLiteralCap;
                if (int.TryParse(inner, out var n) && n >= 0 && n <= literalCap)
                {
                    sb.Append(line[..brace]);
                    if (!nonSync) await SendAsync("+ Ready for literal\r\n", ct).ConfigureAwait(false);
                    var bytes = await ReadExactAsync(n, ct).ConfigureAwait(false);
                    var literal = Encoding.UTF8.GetString(bytes);
                    sb.Append('"').Append(literal.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                    continue;
                }
            }
            sb.Append(line);
            return sb.ToString();
        }
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            if (_rpos >= _rlen)
            {
                _rlen = await _stream.ReadAsync(_rbuf, ct).ConfigureAwait(false);
                _rpos = 0;
                if (_rlen <= 0) return ms.Length > 0 ? Decode(ms) : null;
            }
            var b = _rbuf[_rpos++];
            if (b == (byte)'\n') { var s = Decode(ms); return s.EndsWith('\r') ? s[..^1] : s; }
            ms.WriteByte(b);
        }

        static string Decode(MemoryStream m) => Encoding.UTF8.GetString(m.GetBuffer(), 0, (int)m.Length);
    }

    private async Task<byte[]> ReadExactAsync(int n, CancellationToken ct)
    {
        var buf = new byte[n];
        var got = 0;
        while (got < n)
        {
            if (_rpos >= _rlen)
            {
                _rlen = await _stream.ReadAsync(_rbuf, ct).ConfigureAwait(false);
                _rpos = 0;
                if (_rlen <= 0) break;
            }
            var take = Math.Min(n - got, _rlen - _rpos);
            Array.Copy(_rbuf, _rpos, buf, got, take);
            _rpos += take; got += take;
        }
        return buf;
    }

    private Task SendAsync(string text, CancellationToken ct) => SendBytesAsync(Encoding.UTF8.GetBytes(text), ct);

    private async Task SendBytesAsync(byte[] data, CancellationToken ct)
    {
        await _stream.WriteAsync(data, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
