using System.Globalization;
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
    private Dictionary<long, uint> _uids = [];
    private uint _validity;
    private readonly HashSet<long> _deleted = []; // selected snapshot of persisted mailbox delete flags

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
            catch (FormatException)
            {
                await SendAsync($"{tag} BAD Invalid command syntax\r\n", ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                await SendAsync($"{tag} NO Message data unavailable\r\n", ct).ConfigureAwait(false);
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
        if (_authenticated && cmd is ("LOGIN" or "AUTHENTICATE"))
        {
            await SendAsync($"{tag} BAD Already authenticated\r\n", ct); return false;
        }
        if (_selectedName is null && cmd is ("CHECK" or "CLOSE" or "EXPUNGE" or "FETCH" or "SEARCH" or "STORE" or "UID" or "COPY"))
        {
            await SendAsync($"{tag} BAD No mailbox selected\r\n", ct); return false;
        }

        switch (cmd)
        {
            case "CAPABILITY":
                await SendAsync($"* CAPABILITY {Capabilities()}\r\n{tag} OK CAPABILITY completed\r\n", ct).ConfigureAwait(false);
                return false;
            case "NOOP":
            case "CHECK":
                if (_selectedName is not null) await RefreshAsync(ct);
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
                if (tokens.Count != 2) throw new FormatException();
                await _mailboxes.SubscribeAsync(tokens[1], cmd == "SUBSCRIBE", ct);
                await SendAsync($"{tag} OK {cmd} completed\r\n", ct).ConfigureAwait(false); break;
            case "CREATE":
            case "DELETE":
            case "RENAME":
            case "APPEND":
            case "COPY":
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
        using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        authCts.CancelAfter(PreAuthTimeout);
        string? b64;
        try { b64 = await ReadLineAsync(authCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { _closing = true; await SendAsync("* BYE authentication timeout\r\n", ct); return; }
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
        var subscriptions = cmd == "LSUB" ? await _mailboxes.SubscriptionsAsync(ct) : null;

        if (pattern.Length == 0)
        {
            // Delimiter probe: LIST "" "" returns just the hierarchy delimiter.
            sb.Append($"* {cmd} (\\Noselect) \"/\" \"\"\r\n");
        }
        else
        {
            var rx = PatternRegex(reference + pattern);
            var available = await _mailboxes.ListAsync(ct).ConfigureAwait(false);
            IEnumerable<ImapMailbox> listed = available;
            if (subscriptions is not null)
            {
                var subscribed = subscriptions.ToDictionary(n => n, n => new ImapMailbox(n, available.Any(m => m.Name == n && m.Selectable)));
                // LSUB retains subscribed names even after an alias is removed. With %, expose parents
                // needed to reach subscribed descendants, without subscribing those parents implicitly.
                if (pattern.Contains('%')) foreach (var name in subscriptions)
                {
                    for (var slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
                        subscribed.TryAdd(name[..slash], new(name[..slash], false));
                }
                listed = subscribed.Values.OrderBy(m => m.Name, StringComparer.Ordinal);
            }
            foreach (var mb in listed)
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
        _selectedName = null; _selected = []; _uids = []; _deleted.Clear(); _writable = false;
        var snapshot = await _mailboxes.SnapshotAsync(name, ct).ConfigureAwait(false);
        if (snapshot is null) { await SendAsync($"{tag} NO [NONEXISTENT] Mailbox does not exist\r\n", ct).ConfigureAwait(false); return; }

        _selectedName = name;
        _selected = snapshot.Messages.ToList();
        _uids = snapshot.Uids.ToDictionary();
        _validity = snapshot.Validity;
        _deleted.Clear();
        foreach (var m in _selected) if (snapshot.Deleted.Contains(Uid(m))) _deleted.Add(m.Id);

        // SELECT is read-write only when deletes are allowed; EXAMINE is always read-only.
        var writable = _options.AllowDelete && !examine;
        _writable = writable;
        var uidNext = snapshot.NextUid;
        var sb = new StringBuilder();
        sb.Append(writable ? "* FLAGS (\\Seen \\Deleted)\r\n" : "* FLAGS (\\Seen)\r\n");
        sb.Append($"* {_selected.Count} EXISTS\r\n");
        sb.Append("* 0 RECENT\r\n");
        sb.Append($"* OK [UIDVALIDITY {_validity}] UIDs valid\r\n");
        sb.Append($"* OK [UIDNEXT {uidNext}] Predicted next UID\r\n");
        sb.Append(writable ? "* OK [PERMANENTFLAGS (\\Deleted)] Delete flags persist\r\n" : "* OK [PERMANENTFLAGS ()] Read-only\r\n");
        sb.Append($"{tag} OK [{(writable ? "READ-WRITE" : "READ-ONLY")}] SELECT completed\r\n");
        await SendAsync(sb.ToString(), ct).ConfigureAwait(false);
    }

    private async Task StatusAsync(string tag, List<string> tokens, CancellationToken ct)
    {
        if (tokens.Count < 2) { await SendAsync($"{tag} BAD missing mailbox\r\n", ct).ConfigureAwait(false); return; }
        var name = tokens[1];
        if (tokens.Count != 3) throw new FormatException();
        var snapshot = await _mailboxes.SnapshotAsync(name, ct).ConfigureAwait(false);
        if (snapshot is null) { await SendAsync($"{tag} NO Mailbox does not exist\r\n", ct).ConfigureAwait(false); return; }
        var fields = Tokenize(tokens[2].Trim('(', ')')).Select(f => f.ToUpperInvariant()).Select(f => f switch
        {
            "MESSAGES" => $"MESSAGES {snapshot.Messages.Count}", "RECENT" => "RECENT 0", "UNSEEN" => "UNSEEN 0",
            "UIDNEXT" => $"UIDNEXT {snapshot.NextUid}", "UIDVALIDITY" => $"UIDVALIDITY {snapshot.Validity}",
            _ => throw new FormatException()
        });
        await SendAsync(
            $"* STATUS {ImapFormat.NString(name)} ({string.Join(' ', fields)})\r\n" +
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
        if (tokens.Count != 3) { await SendAsync($"{tag} BAD FETCH expects a set and items\r\n", ct).ConfigureAwait(false); return; }

        var targets = Resolve(tokens[1], byUid);
        List<FetchItem> items;
        try { items = ParseFetchItems(tokens[2]); }
        catch (FormatException)
        {
            await SendAsync($"{tag} BAD Invalid FETCH items\r\n", ct).ConfigureAwait(false);
            return;
        }

        foreach (var (seq, msg) in targets)
            await WriteFetchAsync(seq, msg, items, byUid, ct).ConfigureAwait(false);

        await SendAsync($"{tag} OK {(byUid ? "UID " : "")}FETCH completed\r\n", ct).ConfigureAwait(false);
    }

    private async Task WriteFetchAsync(int seq, Message msg, List<FetchItem> items, bool byUid, CancellationToken ct)
    {
        // UID FETCH always includes UID in the response even if not requested.
        var wantUid = byUid || items.Any(i => i.Name == "UID");
        var needRaw = items.Any(i => i.Name is "BODY" or "BODYSTRUCTURE" or "BODY[section]" or "ENVELOPE");
        var raw = needRaw ? await LoadRawAsync(msg, ct).ConfigureAwait(false) : [];

        using var parsed = items.Any(i => i.Name is "ENVELOPE" or "BODY" or "BODYSTRUCTURE") ? ParseMime(raw) : null;
        using var w = new MemoryStream();
        void Text(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write(b, 0, b.Length); }

        Text($"* {seq} FETCH (");
        var first = true;
        void Sep() { if (!first) Text(" "); first = false; }

        if (wantUid) { Sep(); Text($"UID {Uid(msg)}"); }

        foreach (var item in items)
        {
            switch (item.Name)
            {
                case "UID": break; // already emitted
                case "FLAGS": Sep(); Text($"FLAGS ({FlagsFor(msg)})"); break;
                case "INTERNALDATE": Sep(); Text($"INTERNALDATE {ImapFormat.InternalDate(msg.ReceivedAt)}"); break;
                case "RFC822.SIZE": Sep(); Text($"RFC822.SIZE {msg.SizeBytes}"); break;
                case "ENVELOPE": Sep(); Text($"ENVELOPE {ImapFormat.Envelope(parsed!)}"); break;
                case "BODY":
                case "BODYSTRUCTURE":
                    Sep(); Text($"{item.Name} {ImapFormat.BodyStructure(parsed!.Body)}"); break;
                default: // BODY[section] / BODY.PEEK[section] / RFC822[.HEADER/.TEXT]
                    var data = ImapFormat.Section(raw, item.Section ?? "");
                    if (data is null) { Sep(); Text($"{item.Label} NIL"); break; }
                    if (item.Offset is { } offset)
                    {
                        // IMAP partials count octets after section/header filtering, not characters.
                        data = offset >= data.Length ? [] : data.AsSpan((int)offset,
                            (int)Math.Min(item.Count, (uint)(data.Length - (int)offset))).ToArray();
                    }
                    Sep();
                    Text($"{item.Label} {{{data.Length}}}\r\n");
                    w.Write(data, 0, data.Length);
                    break;
            }
        }

        Text(")\r\n");
        await SendBytesAsync(w.ToArray(), ct).ConfigureAwait(false);
    }

    private static MimeKit.MimeMessage ParseMime(byte[] raw)
    {
        try
        {
            using var ms = new MemoryStream(raw);
            return MimeKit.MimeMessage.Load(ms);
        }
        catch (FormatException ex) { throw new IOException("Invalid stored MIME message.", ex); }
    }

    private async Task<byte[]> LoadRawAsync(Message msg, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(msg.RawStoragePath)) throw new IOException("Missing raw message path.");
        await using var s = await _rawStore.OpenReadAsync(msg.RawStoragePath, ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct).ConfigureAwait(false);
        if (ms.Length == 0) throw new IOException("Empty stored MIME message.");
        return ms.ToArray();
    }

    // ---- SEARCH / STORE ----

    private async Task SearchAsync(string tag, List<string> tokens, bool byUid, CancellationToken ct)
    {
        if (_selectedName is null) { await SendAsync($"{tag} NO No mailbox selected\r\n", ct).ConfigureAwait(false); return; }

        var criteria = tokens.Skip(1).ToList();
        if (criteria.Count >= 2 && criteria[0].Equals("CHARSET", StringComparison.OrdinalIgnoreCase))
        {
            if (criteria[1].ToUpperInvariant() is not ("US-ASCII" or "UTF-8"))
            { await SendAsync($"{tag} NO [BADCHARSET (US-ASCII UTF-8)] Unsupported charset\r\n", ct); return; }
            criteria.RemoveRange(0, 2);
        }
        var search = new ImapSearch(_selected.Count, _selected.Count == 0 ? 0 : Uid(_selected[^1]));
        var predicate = search.Parse(criteria);
        var ids = new List<long>();
        for (var i = 0; i < _selected.Count; i++)
        {
            var message = _selected[i];
            using var mime = search.NeedsMime ? ParseMime(await LoadRawAsync(message, ct)) : null;
            if (predicate(new(message, i + 1, Uid(message), _deleted.Contains(message.Id), mime)))
                ids.Add(byUid ? Uid(message) : i + 1);
        }
        await SendAsync($"* SEARCH {string.Join(' ', ids)}\r\n{tag} OK {(byUid ? "UID " : "")}SEARCH completed\r\n", ct).ConfigureAwait(false);
    }

    private async Task StoreAsync(string tag, List<string> tokens, bool byUid, CancellationToken ct)
    {
        if (_selectedName is null) { await SendAsync($"{tag} NO No mailbox selected\r\n", ct).ConfigureAwait(false); return; }
        if (tokens.Count != 4) throw new FormatException();
        var targets = Resolve(tokens[1], byUid);
        var item = tokens[2].ToUpperInvariant();
        if (item is not ("FLAGS" or "+FLAGS" or "-FLAGS" or "FLAGS.SILENT" or "+FLAGS.SILENT" or "-FLAGS.SILENT")) throw new FormatException();
        var silent = item.EndsWith(".SILENT", StringComparison.Ordinal);

        // \Deleted is only honoured on a read-write selection (SELECT + AllowDelete, not EXAMINE); otherwise
        // STORE is a no-op that just echoes \Seen so clients don't error.
        if (!_writable)
        {
            if (!silent) foreach (var (seq, msg) in targets)
                await SendAsync($"* {seq} FETCH ({(byUid ? $"UID {Uid(msg)} " : "")}FLAGS ({FlagsFor(msg)}))\r\n", ct).ConfigureAwait(false);
            await SendAsync($"{tag} OK {(byUid ? "UID " : "")}STORE completed\r\n", ct).ConfigureAwait(false);
            return;
        }

        // Honour \Deleted so EXPUNGE can remove mail; other flags aren't persisted.
        var flags = tokens[3];
        if (!flags.StartsWith('(') || !flags.EndsWith(')')) throw new FormatException();
        var hasDeleted = Tokenize(flags[1..^1]).Contains("\\Deleted", StringComparer.OrdinalIgnoreCase);
        var op = item.StartsWith("+FLAGS", StringComparison.Ordinal) ? '+'
               : item.StartsWith("-FLAGS", StringComparison.Ordinal) ? '-' : '=';

        foreach (var (seq, msg) in targets)
        {
            var deleted = await _mailboxes.StoreDeletedAsync(_selectedName!, Uid(msg), op, hasDeleted, ct);
            if (deleted) _deleted.Add(msg.Id); else _deleted.Remove(msg.Id);

            if (!silent)
                await SendAsync($"* {seq} FETCH ({(byUid ? $"UID {Uid(msg)} " : "")}FLAGS ({FlagsFor(msg)}))\r\n", ct).ConfigureAwait(false);
        }
        await SendAsync($"{tag} OK {(byUid ? "UID " : "")}STORE completed\r\n", ct).ConfigureAwait(false);
    }

    private string FlagsFor(Message m) => _deleted.Contains(m.Id) ? "\\Seen \\Deleted" : "\\Seen";
    private uint Uid(Message m) => _uids[m.Id];

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
        await RefreshAsync(ct);
        await DoExpungeAsync(silent: false, restrictUids, ct).ConfigureAwait(false);
        await SendAsync($"{tag} OK {(byUid ? "UID " : "")}EXPUNGE completed\r\n", ct).ConfigureAwait(false);
    }

    // Permanently remove every \Deleted message (optionally limited to restrictUids). EXPUNGE responses go
    // out highest-seq first so the sequence numbers stay valid as messages are removed.
    private async Task DoExpungeAsync(bool silent, HashSet<long>? restrictUids, CancellationToken ct)
    {
        var snapshot = await _mailboxes.SnapshotAsync(_selectedName!, ct);
        if (snapshot is null || snapshot.Validity != _validity) throw new IOException("Mailbox changed.");
        var seqs = new List<int>();
        for (var i = 0; i < _selected.Count; i++)
        {
            var id = _selected[i].Id;
            if (snapshot.Uids.TryGetValue(id, out var uid) && uid == Uid(_selected[i]) && snapshot.Deleted.Contains(uid) &&
                (restrictUids is null || restrictUids.Contains(id)))
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

    private async Task RefreshAsync(CancellationToken ct)
    {
        if (_selectedName is null) return;
        var snapshot = await _mailboxes.SnapshotAsync(_selectedName, ct);
        if (snapshot is null || snapshot.Validity != _validity)
        {
            _selectedName = null; _selected = []; _uids = []; _deleted.Clear(); _writable = false;
            throw new IOException("Selected mailbox no longer exists.");
        }
        for (var i = _selected.Count - 1; i >= 0; i--)
        {
            var old = _selected[i];
            if (!snapshot.Uids.TryGetValue(old.Id, out var uid) || uid != Uid(old))
            {
                _selected.RemoveAt(i);
                _deleted.Remove(old.Id);
                await SendAsync($"* {i + 1} EXPUNGE\r\n", ct);
            }
        }
        var changed = _selected.Count != snapshot.Messages.Count;
        _selected = snapshot.Messages.ToList();
        _uids = snapshot.Uids.ToDictionary();
        if (changed) await SendAsync($"* {_selected.Count} EXISTS\r\n", ct);
        for (var i = 0; i < _selected.Count; i++)
        {
            var m = _selected[i];
            var deleted = snapshot.Deleted.Contains(Uid(m));
            var wasDeleted = _deleted.Contains(m.Id);
            if (deleted) _deleted.Add(m.Id); else _deleted.Remove(m.Id);
            if (wasDeleted != deleted) await SendAsync($"* {i + 1} FETCH (UID {Uid(m)} FLAGS ({FlagsFor(m)}))\r\n", ct);
        }
    }

    private async Task<bool> IdleAsync(string tag, CancellationToken ct)
    {
        if (_selectedName is null) { await SendAsync($"{tag} BAD No mailbox selected\r\n", ct); return false; }
        await RefreshAsync(ct);
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        void OnEvent(InboxEvent _) => signal.Writer.TryWrite(true);
        _notifier.Received += OnEvent;
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleCts.CancelAfter(IdleTimeout);
        await SendAsync("+ idling\r\n", ct);
        var readDone = ReadLineAsync(idleCts.Token);
        try
        {
            while (!readDone.IsCompleted)
            {
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(idleCts.Token);
                poll.CancelAfter(TimeSpan.FromSeconds(5));
                var wake = signal.Reader.WaitToReadAsync(poll.Token).AsTask();
                await Task.WhenAny(readDone, wake);
                poll.Cancel();
                try { await wake; } catch (OperationCanceledException) { }
                if (readDone.IsCompleted) break;
                while (signal.Reader.TryRead(out _)) { }
                await RefreshAsync(ct);
            }
            var done = await readDone;
            if (done is null) return true;
            await SendAsync(done.Equals("DONE", StringComparison.OrdinalIgnoreCase)
                ? $"{tag} OK IDLE terminated\r\n" : $"{tag} BAD Expected DONE\r\n", ct);
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await SendAsync("* BYE idle timeout\r\n", ct);
            return true;
        }
        finally
        {
            _notifier.Received -= OnEvent;
            idleCts.Cancel();
            try { await readDone; } catch (OperationCanceledException) { }
        }
    }
    // ---- Sequence / item parsing ----

    private List<(int seq, Message m)> Resolve(string set, bool byUid)
    {
        var result = new List<(int, Message)>();
        if (_selected.Count == 0) { ParseSet(set, 0); return result; }

        if (byUid)
        {
            var maxUid = Uid(_selected[^1]);
            var wanted = ParseSet(set, maxUid);
            for (var i = 0; i < _selected.Count; i++)
                if (wanted(Uid(_selected[i]))) result.Add((i + 1, _selected[i]));
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
    internal static Func<long, bool> ParseSet(string set, long max)
    {
        var ranges = new List<(long lo, long hi)>();
        if (string.IsNullOrEmpty(set) || set.Split(',').Any(p => p.Length == 0)) throw new FormatException();
        foreach (var part in set.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = part.Split(':');
            if (seg.Length > 2) throw new FormatException();
            long lo = Bound(seg[0], max);
            long hi = seg.Length > 1 ? Bound(seg[1], max) : lo;
            if (lo > hi) (lo, hi) = (hi, lo);
            ranges.Add((lo, hi));
        }
        return v => ranges.Any(r => v >= r.lo && v <= r.hi);

        static long Bound(string s, long max) => s == "*" ? max :
            uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new FormatException();
    }

    private readonly record struct FetchItem(string Name, string? Section, string Label,
        uint? Offset = null, uint Count = 0);

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
                var head = upper[..bracket];
                if (end < bracket || head is not ("BODY" or "BODY.PEEK"))
                    throw new FormatException();
                var section = t[(bracket + 1)..end];
                var label = $"BODY[{section}]";
                uint? offset = null;
                uint count = 0;
                var partial = t[(end + 1)..];
                if (partial.Length > 0)
                {
                    var dot = partial.IndexOf('.');
                    if (!partial.StartsWith('<') || !partial.EndsWith('>') || dot <= 1 ||
                        !uint.TryParse(partial.AsSpan(1, dot - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
                        !uint.TryParse(partial.AsSpan(dot + 1, partial.Length - dot - 2), NumberStyles.None, CultureInfo.InvariantCulture, out count) ||
                        count == 0)
                        throw new FormatException();
                    offset = start;
                    label += "<" + start.ToString(CultureInfo.InvariantCulture) + ">";
                }
                items.Add(new FetchItem("BODY[section]", section, label, offset, count));
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
                default: throw new FormatException();
            }
        }
        if (items.Count == 0) throw new FormatException();
        return items;

        static void AddAll(List<FetchItem> l, params string[] names)
        {
            foreach (var n in names) l.Add(new(n, null, n));
        }
    }

    internal static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (s[i] == ')') throw new FormatException();
            if (s[i] == '"')
            {
                var sb = new StringBuilder(); i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i += 2; }
                    else { sb.Append(s[i]); i++; }
                }
                if (i >= s.Length) throw new FormatException();
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
                if (depth != 0) throw new FormatException();
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
            if (ms.Length >= 65536) throw new IOException("IMAP command line too long.");
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
