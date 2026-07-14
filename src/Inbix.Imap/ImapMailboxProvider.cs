using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Validation;

namespace Inbix.Imap;

/// <summary>Whether a mailbox can be SELECTed, and the messages it currently holds.</summary>
public sealed record ImapMailbox(string Name, bool Selectable);

/// <summary>
/// Maps Inbix aliases/messages onto the IMAP mailbox model: <c>INBOX</c> = all non-junk mail across every
/// alias, <c>Aliases/&lt;address&gt;</c> per alias (catch-all → <c>Aliases/catch-all</c>), and <c>Junk</c>.
/// Message id is the UID (globally unique + monotonic, so ascending within any subset). Read-only.
/// </summary>
public sealed class ImapMailboxProvider
{
    internal const int MaxMessages = 100_000;
    internal const string AliasesFolder = "Aliases";
    internal const string CatchAllLeaf = "catch-all";
    internal const string JunkFolder = "Junk";

    private readonly IAliasRepository _aliases;
    private readonly IMessageRepository _messages;

    public ImapMailboxProvider(IAliasRepository aliases, IMessageRepository messages)
    {
        _aliases = aliases;
        _messages = messages;
    }

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

    /// <summary>Messages in a mailbox (ascending by id = UID order), or null if the mailbox doesn't exist.</summary>
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
