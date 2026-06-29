using System.Text.Json;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inbix.Data.Json;

/// <summary>
/// The file/folder JSON storage engine. Holds the whole dataset in memory (loaded once on startup),
/// serves all reads from memory, and writes through to disk on every change. Every operation runs behind
/// a single async gate so there is exactly one writer at a time (mirroring SQLite's exclusive-locking
/// design), and each file write is an atomic temp+rename, so the store survives unreliable network storage
/// with at most one damaged file rather than a corrupt whole-database.
/// </summary>
public sealed class JsonDataStore : IReloadableStore
{
    internal const string JunkFolder = "junk";
    internal const string CatchAllFolder = "catchall";
    private const string AliasFileName = "_alias.json";

    private readonly JsonFileIo _io;
    private readonly ILogger<JsonDataStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly string _root;
    private readonly string _mailDir;
    private readonly string _sessionsDir;
    private readonly string _settingsPath;
    private readonly string _rulesPath;
    private readonly string _identitiesPath;
    private readonly string _auditPath;

    // In-memory index. Reassigned wholesale on reload; only ever touched under the gate.
    internal Dictionary<long, StoredAlias> Aliases { get; private set; } = [];
    internal Dictionary<long, StoredMessage> Messages { get; private set; } = [];
    internal List<BlacklistRule> Rules { get; private set; } = [];
    internal List<Identity> Identities { get; private set; } = [];
    internal Dictionary<string, string> Settings { get; private set; } = new(StringComparer.Ordinal);
    internal List<AuditEntry> Audit { get; private set; } = [];

    private long _nextAlias, _nextMessage, _nextRule, _nextIdentity, _nextBody, _nextAttachment, _nextAudit, _nextSession;

    public JsonDataStore(IOptions<InbixOptions> options, ILogger<JsonDataStore> logger)
    {
        _logger = logger;
        var storage = options.Value.Storage;
        _io = new JsonFileIo(TimeSpan.FromSeconds(Math.Max(1, storage.WriteRetrySeconds)));
        _root = Path.GetFullPath(storage.JsonPath);
        _mailDir = Path.Combine(_root, "mail");
        _sessionsDir = Path.Combine(_root, "sessions");
        _settingsPath = Path.Combine(_root, "settings.json");
        _rulesPath = Path.Combine(_root, "rules.json");
        _identitiesPath = Path.Combine(_root, "identities.json");
        _auditPath = Path.Combine(_root, "audit.jsonl");
    }

    public bool CanReload => true;
    internal string RootPath => _root;
    internal JsonFileIo Io => _io;

    // --- Gated operation helpers -------------------------------------------------------------------

    internal async Task<T> ReadAsync<T>(Func<T> read, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return read(); }
        finally { _gate.Release(); }
    }

    internal async Task<T> WriteAsync<T>(Func<CancellationToken, Task<T>> write, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await write(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    internal Task WriteAsync(Func<CancellationToken, Task> write, CancellationToken ct) =>
        WriteAsync<bool>(async c => { await write(c).ConfigureAwait(false); return true; }, ct);

    // --- Lifecycle ---------------------------------------------------------------------------------

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("JSON store loaded from {Root}: {Aliases} aliases, {Messages} messages.",
                _root, Aliases.Count, Messages.Count);
        }
        finally { _gate.Release(); }
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("JSON store reloaded from {Root}: {Aliases} aliases, {Messages} messages.",
                _root, Aliases.Count, Messages.Count);
        }
        finally { _gate.Release(); }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_mailDir);

        var aliases = new Dictionary<long, StoredAlias>();
        var messages = new Dictionary<long, StoredMessage>();

        var settings = await _io.ReadAsync<Dictionary<string, string>>(_settingsPath, ct).ConfigureAwait(false)
                       ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var rules = await _io.ReadAsync<List<BlacklistRule>>(_rulesPath, ct).ConfigureAwait(false) ?? [];
        var identities = await _io.ReadAsync<List<Identity>>(_identitiesPath, ct).ConfigureAwait(false) ?? [];
        var audit = await LoadAuditAsync(ct).ConfigureAwait(false);

        foreach (var dir in Directory.GetDirectories(_mailDir))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, JunkFolder, StringComparison.OrdinalIgnoreCase))
            {
                await LoadMessagesAsync(dir, messages, ct).ConfigureAwait(false);
                continue;
            }

            var alias = await _io.ReadAsync<Alias>(Path.Combine(dir, AliasFileName), ct).ConfigureAwait(false);
            if (alias is not null)
                aliases[alias.Id] = new StoredAlias { Alias = alias, FolderName = name };
            await LoadMessagesAsync(dir, messages, ct).ConfigureAwait(false);
        }

        // Commit the freshly built index.
        Aliases = aliases;
        Messages = messages;
        Rules = rules;
        Identities = identities;
        Settings = settings;
        Audit = audit;

        _nextAlias = NextFrom(aliases.Keys);
        _nextMessage = NextFrom(messages.Keys);
        _nextRule = NextFrom(rules.Select(r => r.Id));
        _nextIdentity = NextFrom(identities.Select(i => i.Id));
        _nextAudit = NextFrom(audit.Select(a => a.Id));
        _nextBody = NextFrom(messages.Values.Where(m => m.BodyStored).Select(m => m.BodyId));
        _nextAttachment = NextFrom(messages.Values.SelectMany(m => m.Attachments).Select(a => a.Id));
        _nextSession = NextFrom(SessionIds());

        // Seed the permanent catch-all if absent (SQLite does this via migration 002).
        if (!aliases.Values.Any(a => a.Alias.IsCatchAll))
            await SeedCatchAllAsync(ct).ConfigureAwait(false);
    }

    private async Task LoadMessagesAsync(string dir, Dictionary<long, StoredMessage> messages, CancellationToken ct)
    {
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            if (string.Equals(Path.GetFileName(file), AliasFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            StoredMessage? m;
            try { m = await _io.ReadAsync<StoredMessage>(file, ct).ConfigureAwait(false); }
            catch (JsonException ex) { _logger.LogWarning(ex, "Skipping unreadable message file {File}", file); continue; }
            if (m is null) continue;
            m.CurrentPath = file;

            // De-duplicate by id (a crash mid-move can leave two copies): keep the newest, delete the older.
            if (messages.TryGetValue(m.Id, out var existing))
            {
                var keepNew = File.GetLastWriteTimeUtc(file) >= File.GetLastWriteTimeUtc(existing.CurrentPath!);
                if (keepNew) { _io.TryDelete(existing.CurrentPath!); messages[m.Id] = m; }
                else { _io.TryDelete(file); }
            }
            else
            {
                messages[m.Id] = m;
            }
        }
    }

    private async Task<List<AuditEntry>> LoadAuditAsync(CancellationToken ct)
    {
        var list = new List<AuditEntry>();
        if (!File.Exists(_auditPath)) return list;
        foreach (var line in await File.ReadAllLinesAsync(_auditPath, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<AuditEntry>(line, JsonFileIo.CompactOptions);
                if (e is not null) list.Add(e);
            }
            catch (JsonException) { /* tolerate a torn last line from a crash */ }
        }
        return list;
    }

    private IEnumerable<long> SessionIds()
    {
        if (!Directory.Exists(_sessionsDir)) yield break;
        foreach (var f in Directory.GetFiles(_sessionsDir, "*.json"))
            if (long.TryParse(Path.GetFileNameWithoutExtension(f), out var id))
                yield return id;
    }

    private static long NextFrom(IEnumerable<long> ids)
    {
        long max = 0;
        foreach (var id in ids) if (id > max) max = id;
        return max + 1;
    }

    private async Task SeedCatchAllAsync(CancellationToken ct)
    {
        var alias = new Alias
        {
            Id = _nextAlias++,
            LocalPart = "*",
            Domain = "*",
            Enabled = false,
            CreatedAt = DateTimeOffset.UtcNow,
            Notes = "Catch-all: stores mail for any address on accepted domains.",
            IsCatchAll = true,
        };
        var stored = new StoredAlias { Alias = alias, FolderName = CatchAllFolder };
        Aliases[alias.Id] = stored;
        await PersistAliasAsync(stored, ct).ConfigureAwait(false);
        _logger.LogInformation("Seeded the permanent catch-all in the JSON store.");
    }

    // --- Id allocation (callers already hold the gate) ---------------------------------------------

    internal long NextAliasId() => _nextAlias++;
    internal long NextMessageId() => _nextMessage++;
    internal long NextRuleId() => _nextRule++;
    internal long NextIdentityId() => _nextIdentity++;
    internal long NextBodyId() => _nextBody++;
    internal long NextAttachmentId() => _nextAttachment++;
    internal long NextAuditId() => _nextAudit++;
    internal long NextSessionId() => _nextSession++;

    // --- Persistence helpers ----------------------------------------------------------------------

    internal string AliasFolderName(long aliasId) =>
        Aliases.TryGetValue(aliasId, out var a) ? a.FolderName : aliasId.ToString("D10");

    internal string MessagePathFor(StoredMessage m)
    {
        var folder = m.JunkedAt.HasValue ? JunkFolder : AliasFolderName(m.AliasId);
        return Path.Combine(_mailDir, folder, m.Id.ToString("D10") + ".json");
    }

    /// <summary>Write a message to its current location, moving the file if its junk/alias state changed.</summary>
    internal async Task PersistMessageAsync(StoredMessage m, CancellationToken ct)
    {
        var newPath = MessagePathFor(m);
        await _io.WriteAsync(newPath, m, ct).ConfigureAwait(false);
        if (m.CurrentPath is not null && !PathsEqual(m.CurrentPath, newPath))
            _io.TryDelete(m.CurrentPath);
        m.CurrentPath = newPath;
    }

    internal void DeleteMessageFile(StoredMessage m) => _io.TryDelete(m.CurrentPath ?? MessagePathFor(m));

    internal Task PersistAliasAsync(StoredAlias a, CancellationToken ct) =>
        _io.WriteAsync(Path.Combine(_mailDir, a.FolderName, AliasFileName), a.Alias, ct);

    internal void DeleteAliasFolder(StoredAlias a)
    {
        var dir = Path.Combine(_mailDir, a.FolderName);
        _io.TryDelete(Path.Combine(dir, AliasFileName));
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: false); }
        catch { /* leave a non-empty folder rather than risk deleting mail */ }
    }

    internal Task SaveRulesAsync(CancellationToken ct) => _io.WriteAsync(_rulesPath, Rules, ct);
    internal Task SaveIdentitiesAsync(CancellationToken ct) => _io.WriteAsync(_identitiesPath, Identities, ct);
    internal Task SaveSettingsAsync(CancellationToken ct) => _io.WriteAsync(_settingsPath, Settings, ct);

    internal Task AppendAuditAsync(AuditEntry e, CancellationToken ct) =>
        _io.AppendLineAsync(_auditPath, JsonSerializer.Serialize(e, JsonFileIo.CompactOptions), ct);

    internal string SessionPath(long id) => Path.Combine(_sessionsDir, id.ToString("D10") + ".json");

    /// <summary>Pick a unique, filesystem-safe folder name for a new alias.</summary>
    internal string AllocateFolderName(string localPart, string domain, bool isCatchAll)
    {
        if (isCatchAll) return CatchAllFolder;

        var taken = new HashSet<string>(Aliases.Values.Select(a => a.FolderName), StringComparer.OrdinalIgnoreCase)
        {
            JunkFolder, CatchAllFolder,
        };

        var baseName = SafeFolder(localPart);
        if (!taken.Contains(baseName)) return baseName;

        var withDomain = SafeFolder($"{localPart}@{domain}");
        if (!taken.Contains(withDomain)) return withDomain;

        return $"{baseName}-{Guid.NewGuid():N}"[..Math.Min(baseName.Length + 9, 40)];
    }

    private static readonly HashSet<string> WindowsReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    private static string SafeFolder(string name)
    {
        var stem = name.Split('.')[0];
        return WindowsReserved.Contains(stem) ? name + "_" : name;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
