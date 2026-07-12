using Inbix.Core.Abstractions;

namespace Inbix.Data.Services;

/// <summary>Default in-process <see cref="IInboxNotifier"/>. Registered as a singleton so the SMTP intake,
/// the parser worker and every Blazor circuit share one event bus.</summary>
public sealed class InboxNotifier : IInboxNotifier
{
    public event Action<InboxEvent>? Received;

    public void NotifyArrived(long messageId, long aliasId, bool junked) =>
        Received?.Invoke(new InboxEvent(InboxEventKind.Arrived, messageId, aliasId, junked));

    public void NotifyUpdated(long messageId, long aliasId, bool junked) =>
        Received?.Invoke(new InboxEvent(InboxEventKind.Updated, messageId, aliasId, junked));
}
