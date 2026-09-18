using System.Text.Json;
using Inbix.Core.Domain;

namespace Inbix.Imap;

public sealed record ImapSnapshot(uint Validity, uint NextUid, IReadOnlyList<Message> Messages,
    IReadOnlyDictionary<long, uint> Uids, IReadOnlySet<uint> Deleted);

// Stored through the application's settings repository, for both SQLite and JSON deployments.
// UIDs describe membership in a mailbox, not a reusable database row id.
internal sealed class ImapMailboxState
{
    public uint Validity { get; set; }
    public uint NextUid { get; set; } = 1;
    public Dictionary<string, uint> Members { get; set; } = [];
    public HashSet<uint> Deleted { get; set; } = [];

    public static string Identity(Message m) => JsonSerializer.Serialize(new
    {
        m.Id, m.RawStoragePath, m.ReceivedAt, m.StateChangedAt
    });
}
