using System.Buffers;
using System.Net;
using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Microsoft.Extensions.Logging;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace Inbix.Smtp;

/// <summary>
/// Receives the raw DATA buffer from the SMTP layer and hands it to the inbound sink for durable
/// storage — one stored message per accepted recipient. The SMTP reply reflects the storage
/// outcome: 250 on success, 451 on a transient problem (so the sender retries), 552 if too large.
/// </summary>
public sealed class InbixMessageStore : MessageStore
{
    private readonly IInboundMessageSink _sink;
    private readonly ISmtpSessionRepository _sessions;
    private readonly ILogger<InbixMessageStore> _logger;

    private static readonly SmtpResponse TemporaryFailure =
        new((SmtpReplyCode)451, "Temporary local problem, please try again later");

    public InbixMessageStore(IInboundMessageSink sink, ISmtpSessionRepository sessions, ILogger<InbixMessageStore> logger)
    {
        _sink = sink;
        _sessions = sessions;
        _logger = logger;
    }

    public override async Task<SmtpResponse> SaveAsync(
        ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
    {
        var receivedAt = DateTimeOffset.UtcNow;
        var remoteIp = TryGetRemoteIp(context);
        var sender = FormatMailbox(transaction.From);
        var raw = buffer.ToArray();

        long sessionId;
        try
        {
            sessionId = await _sessions.CreateAsync(new SmtpSession
            {
                RemoteIp = remoteIp,
                MailFrom = sender,
                StartedAt = receivedAt
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open SMTP session row; signalling temporary failure");
            return TemporaryFailure;
        }

        var recipients = transaction.To.Select(FormatMailbox).Where(a => a is not null).Cast<string>().ToList();
        if (recipients.Count == 0)
            return SmtpResponse.NoValidRecipientsGiven;

        var anyTemporary = false;
        var anyStored = false;

        foreach (var recipient in recipients)
        {
            var result = await _sink.SaveAsync(new InboundMessage
            {
                Recipient = recipient,
                Sender = sender,
                RemoteIp = remoteIp,
                RawMime = raw,
                ReceivedAt = receivedAt,
                SmtpSessionId = sessionId
            }, cancellationToken).ConfigureAwait(false);

            switch (result)
            {
                case InboundSaveResult.Stored: anyStored = true; break;
                case InboundSaveResult.TooLarge:
                    await CompleteSession(sessionId, "552 too large", cancellationToken).ConfigureAwait(false);
                    return SmtpResponse.SizeLimitExceeded;
                case InboundSaveResult.TemporaryFailure: anyTemporary = true; break;
                case InboundSaveResult.UnknownRecipient: /* filtered at RCPT; ignore here */ break;
            }
        }

        if (anyTemporary)
        {
            await CompleteSession(sessionId, "451 temporary failure", cancellationToken).ConfigureAwait(false);
            return TemporaryFailure;
        }

        if (!anyStored)
        {
            await CompleteSession(sessionId, "550 no deliverable recipients", cancellationToken).ConfigureAwait(false);
            return SmtpResponse.MailboxUnavailable;
        }

        await CompleteSession(sessionId, "250 stored", cancellationToken).ConfigureAwait(false);
        return SmtpResponse.Ok;
    }

    private async Task CompleteSession(long sessionId, string result, CancellationToken ct)
    {
        try
        {
            await _sessions.CompleteAsync(sessionId, result, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to finalise SMTP session {SessionId}", sessionId);
        }
    }

    private static string? TryGetRemoteIp(ISessionContext context)
    {
        if (context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var value) && value is IPEndPoint ep)
            return ep.Address.ToString();
        return null;
    }

    private static string? FormatMailbox(IMailbox? mailbox)
    {
        if (mailbox is null) return null;
        if (string.IsNullOrEmpty(mailbox.User) && string.IsNullOrEmpty(mailbox.Host)) return null; // <> null sender
        return $"{mailbox.User}@{mailbox.Host}";
    }
}
