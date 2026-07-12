namespace Inbix.Core.Abstractions;

/// <summary>What happened to a message.</summary>
public enum InboxEventKind
{
    /// <summary>A new message was stored (not yet parsed).</summary>
    Arrived,

    /// <summary>An existing message changed (e.g. finished parsing).</summary>
    Updated,
}

/// <summary>A lightweight, in-process notification that a message was stored or changed.</summary>
public readonly record struct InboxEvent(InboxEventKind Kind, long MessageId, long AliasId, bool Junked);

/// <summary>
/// In-process pub/sub so the UI can react to inbound mail in real time. The SMTP intake and the parser
/// worker publish; Blazor circuits subscribe and refresh. Everything runs in one process, so this is a
/// plain singleton event bus (see <c>DiagnosticsService.ResultsUpdated</c> for the same pattern).
/// </summary>
public interface IInboxNotifier
{
    /// <summary>Raised (off the UI thread) whenever a message is stored or updated. Marshal with InvokeAsync.</summary>
    event Action<InboxEvent>? Received;

    /// <summary>Publish that a new message was stored under <paramref name="aliasId"/>.</summary>
    void NotifyArrived(long messageId, long aliasId, bool junked);

    /// <summary>Publish that a stored message changed (e.g. finished parsing).</summary>
    void NotifyUpdated(long messageId, long aliasId, bool junked);
}
