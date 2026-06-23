using Inbix.Core.Domain;

namespace Inbix.Core.Abstractions;

/// <summary>
/// Receives a fully-assembled <see cref="InboundMessage"/> from the SMTP layer and persists it
/// durably. Returns only after the message is safely stored (or a definite outcome is known),
/// so the SMTP layer can decide what reply to send.
/// </summary>
public interface IInboundMessageSink
{
    Task<InboundSaveResult> SaveAsync(InboundMessage message, CancellationToken ct = default);
}
