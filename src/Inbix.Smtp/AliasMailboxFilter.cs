using Inbix.Core.Abstractions;
using Inbix.Core.Options;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Storage;

namespace Inbix.Smtp;

/// <summary>
/// Validates senders and recipients during the SMTP transaction. As an inbound-only server we
/// accept any sender (subject to the SIZE hint) but only accept recipients that are known,
/// enabled aliases — unknown recipients are rejected at RCPT TO with 550.
/// </summary>
public sealed class AliasMailboxFilter : MailboxFilter
{
    private readonly IAliasResolver _resolver;
    private readonly SmtpOptions _smtp;

    public AliasMailboxFilter(IAliasResolver resolver, IOptions<InbixOptions> options)
    {
        _resolver = resolver;
        _smtp = options.Value.Smtp;
    }

    public override Task<bool> CanAcceptFromAsync(
        ISessionContext context, IMailbox from, int size, CancellationToken cancellationToken)
    {
        // size is the SIZE hint from MAIL FROM (0 when not supplied). Reject obviously oversized mail early.
        if (size > 0 && size > _smtp.MaxMessageSizeBytes)
            return Task.FromResult(false);

        return Task.FromResult(true);
    }

    public override async Task<bool> CanDeliverToAsync(
        ISessionContext context, IMailbox to, IMailbox from, CancellationToken cancellationToken)
    {
        var address = $"{to.User}@{to.Host}";
        return await _resolver.IsDeliverableAsync(address, cancellationToken).ConfigureAwait(false);
    }
}
