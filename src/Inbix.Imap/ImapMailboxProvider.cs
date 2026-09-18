using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Validation;
using System.Text.Json;

namespace Inbix.Imap;

/// <summary>Whether a mailbox can be SELECTed, and the messages it currently holds.</summary>
public sealed record ImapMailbox(string Name, bool Selectable);

/// <summary>
/// Maps Inbix aliases/messages onto the IMAP mailbox model: <c>INBOX</c> = all non-junk mail across every
/// alias, <c>Aliases/&lt;address&gt;</c> per alias (catch-all → <c>Aliases/catch-all</c>), and <c>Junk</c>.
/// Each mailbox persists its own UID allocation independently of repository row ids.
/// </summary>
public sealed class ImapMailboxProvider
{
    internal const int MaxMessages = 100_000;
    internal const string AliasesFolder = "Aliases";
    internal const string CatchAllLeaf = "catch-all";
    internal const string JunkFolder = "Junk";

    private readonly IAliasRepository _aliases;
    private readonly IMessageRepository _messages;
    private readonly ISettingsRepository _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ImapMailboxProvider(IAliasRepository aliases, IMessageRepository messages, ISettingsRepository settings)
    {
        _aliases = aliases;
        _messages = messages;
        _settings = settings;
    }

    public async Task<ImapSnapshot?> SnapshotAsync(string mailbox, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            mailbox = CanonicalName(mailbox);
            var messages = await GetMessagesAsync(mailbox, ct);
            if (messages is null) return null;
            var key = "imap.v2.mailbox." + mailbox;
            var saved = await GetSettingAsync(key, ct);
            var state = saved is null ? new ImapMailboxState() :
                JsonSerializer.Deserialize<ImapMailboxState>(saved) ?? throw new InvalidDataException("Invalid IMAP state.");
            if (state.NextUid == 0 || state.Members.Values.Any(uid => uid == 0 || uid >= state.NextUid) ||
                state.Members.Values.Distinct().Count() != state.Members.Count)
                throw new InvalidDataException("Invalid IMAP UID allocation.");
            if (state.Validity == 0)
            {
                var previous = await GetSettingAsync("imap.v2.validity", ct);
                var value = previous is null ? (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() : uint.Parse(previous);
                state.Validity = checked(value + 1);
                await SetSettingAsync("imap.v2.validity", state.Validity.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
            }
            var active = new Dictionary<string, uint>();
            var uids = new Dictionary<long, uint>();
            foreach (var message in messages)
            {
                var identity = ImapMailboxState.Identity(message);
                if (!state.Members.TryGetValue(identity, out var uid))
                {
                    uid = state.NextUid;
                    state.NextUid = checked(uid + 1);
                }
                active.Add(identity, uid);
                uids.Add(message.Id, uid);
            }
            state.Members = active;
            state.Deleted.IntersectWith(active.Values);
            var serialized = JsonSerializer.Serialize(state);
            if (serialized != saved) await SetSettingAsync(key, serialized, ct);
            return new(state.Validity, state.NextUid, messages.OrderBy(m => uids[m.Id]).ToList(), uids, state.Deleted);
        }
        finally { _gate.Release(); }
    }

    public async Task<HashSet<string>> SubscriptionsAsync(CancellationToken ct)
    {
        var saved = await GetSettingAsync("imap.subscriptions", ct);
        return saved is null ? (await ListAsync(ct)).Where(m => m.Selectable).Select(m => m.Name).ToHashSet(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<HashSet<string>>(saved) ?? [];
    }

    public async Task<bool> StoreDeletedAsync(string mailbox, uint uid, char operation, bool hasDeleted, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            mailbox = CanonicalName(mailbox);
            var key = "imap.v2.mailbox." + mailbox;
            var state = JsonSerializer.Deserialize<ImapMailboxState>(await GetSettingAsync(key, ct) ?? throw new IOException("Mailbox state missing."))!;
            if (!state.Members.ContainsValue(uid)) throw new IOException("Message no longer in mailbox.");
            var wasDeleted = state.Deleted.Contains(uid);
            var deleted = operation == '-' ? wasDeleted && !hasDeleted : operation == '+' ? wasDeleted || hasDeleted : hasDeleted;
            if (deleted) state.Deleted.Add(uid); else state.Deleted.Remove(uid);
            if (wasDeleted != deleted) await SetSettingAsync(key, JsonSerializer.Serialize(state), ct);
            return deleted;
        }
        finally { _gate.Release(); }
    }

    public async Task SubscribeAsync(string mailbox, bool subscribe, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            mailbox = CanonicalName(mailbox);
            var names = await SubscriptionsAsync(ct);
            if (subscribe) names.Add(mailbox); else names.Remove(mailbox);
            await SetSettingAsync("imap.subscriptions", JsonSerializer.Serialize(names), ct);
        }
        finally { _gate.Release(); }
    }

    private Task<string?> GetSettingAsync(string key, CancellationToken ct) => _settings.GetAsync(key, ct);

    private static string CanonicalName(string mailbox)
    {
        if (mailbox.Equals("INBOX", StringComparison.OrdinalIgnoreCase)) return "INBOX";
        if (mailbox.StartsWith(AliasesFolder + "/", StringComparison.Ordinal))
        {
            var leaf = mailbox[(AliasesFolder.Length + 1)..];
            if (leaf.Equals(CatchAllLeaf, StringComparison.Ordinal)) return mailbox;
            if (AliasRules.TrySplitAddress(leaf, out var local, out var domain)) return $"{AliasesFolder}/{local}@{domain}";
        }
        return mailbox;
    }

    private Task SetSettingAsync(string key, string value, CancellationToken ct)
        => _settings.SetAsync(key, value, ct);

    /// <summary>The full folder list a client sees via LIST.</summary>
    public async Task<IReadOnlyList<ImapMailbox>> ListAsync(CancellationToken ct)
    {
        var aliases = await _aliases.ListAsync(ct).ConfigureAwait(false);
        var list = new List<ImapMailbox> { new("INBOX", Selectable: true) };

        if (aliases.Count > 0)
            list.Add(new(AliasesFolder, Selectable: false)); // hierarchy parent

        foreach (var a in aliases.OrderBy(a => a.IsCatchAll ? 0 : 1).ThenBy(a => a.Address, StringComparer.OrdinalIgnoreCase))
            list.Add(new($"{AliasesFolder}/{(a.IsCatchAll ? CatchAllLeaf : a.Address)}", Selectable: true));

        list.Add(new(JunkFolder, Selectable: true));
        return list;
    }

    /// <summary>Repository messages; use SnapshotAsync for persistent IMAP UIDs and sequence order.</summary>
    public async Task<IReadOnlyList<Message>?> GetMessagesAsync(string mailbox, CancellationToken ct)
    {
        if (mailbox.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
        {
            var all = new List<Message>();
            foreach (var a in await _aliases.ListAsync(ct).ConfigureAwait(false))
                all.AddRange(await _messages.ListByAliasAsync(a.Id, MaxMessages, 0, ct).ConfigureAwait(false));
            return Sorted(all);
        }

        if (mailbox.Equals(JunkFolder, StringComparison.Ordinal))
        {
            var junk = await _messages.ListJunkWithPreviewAsync(MaxMessages, 0, ct).ConfigureAwait(false);
            var msgs = new List<Message>();
            foreach (var j in junk)
                if (await _messages.GetByIdAsync(j.Id, ct).ConfigureAwait(false) is { } m)
                    msgs.Add(m);
            return Sorted(msgs);
        }

        if (mailbox.StartsWith(AliasesFolder + "/", StringComparison.Ordinal))
        {
            var leaf = mailbox[(AliasesFolder.Length + 1)..];
            Alias? alias = leaf.Equals(CatchAllLeaf, StringComparison.Ordinal)
                ? await _aliases.GetCatchAllAsync(ct).ConfigureAwait(false)
                : AliasRules.TrySplitAddress(leaf, out var local, out var domain)
                    ? await _aliases.FindAsync(local, domain, ct).ConfigureAwait(false)
                    : null;
            if (alias is null) return null;
            return Sorted(await _messages.ListByAliasAsync(alias.Id, MaxMessages, 0, ct).ConfigureAwait(false));
        }

        return null;
    }

    /// <summary>Permanently delete a message (row + raw MIME + attachments). Used when AllowDelete is on.</summary>
    public Task DeleteAsync(long messageId, CancellationToken ct) => _messages.DeleteAsync(messageId, ct);

    private static List<Message> Sorted(IEnumerable<Message> m) => m.OrderBy(x => x.Id).ToList();
}
