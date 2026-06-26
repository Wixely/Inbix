using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Storage;

namespace Inbix.Smtp;

/// <summary>
/// Validates senders and recipients during the SMTP transaction. As an inbound-only server we
/// accept any sender (subject to the SIZE hint and the connection governor) but only accept
/// recipients that are known, enabled aliases — unknown recipients are rejected at RCPT TO with 550.
/// </summary>
public sealed class AliasMailboxFilter : MailboxFilter
{
    private readonly IAliasResolver _resolver;
    private readonly IBlacklistMatcher _matcher;
    private readonly SmtpConnectionGovernor _governor;
    private readonly SmtpOptions _smtp;
    private readonly ILogger<AliasMailboxFilter> _logger;

    public AliasMailboxFilter(
        IAliasResolver resolver, IBlacklistMatcher matcher, SmtpConnectionGovernor governor,
        IOptions<InbixOptions> options, ILogger<AliasMailboxFilter> logger)
    {
        _resolver = resolver;
        _matcher = matcher;
        _governor = governor;
        _smtp = options.Value.Smtp;
        _logger = logger;
    }

    public override Task<bool> CanAcceptFromAsync(
        ISessionContext context, IMailbox from, int size, CancellationToken cancellationToken)
    {
        // size is the SIZE hint from MAIL FROM (0 when not supplied). Reject obviously oversized mail early.
        if (size > 0 && size > _smtp.MaxMessageSizeBytes)
            return Task.FromResult(false);

        // Abuse controls: concurrency cap and per-IP rate limit.
        var ip = SmtpSessionContext.GetRemoteIp(context);
        if (!_governor.TryAdmit(ip, out var reason))
        {
            _logger.LogWarning("Rejecting SMTP session from {Ip}: {Reason}", ip ?? "unknown", reason);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public override async Task<bool> CanDeliverToAsync(
        ISessionContext context, IMailbox to, IMailbox from, CancellationToken cancellationToken)
    {
        var address = $"{to.User}@{to.Host}";
        if (!await _resolver.IsDeliverableAsync(address, cancellationToken).ConfigureAwait(false))
            return false;

        // A blacklist 'reject' rule (on sender or recipient) bounces the recipient here with 550.
        // 'discard' / 'junk' rules accept at SMTP and are applied later in the message sink.
        var sender = from is { } f && (f.User.Length > 0 || f.Host.Length > 0) ? $"{f.User}@{f.Host}" : null;
        var match = await _matcher.MatchAsync(sender, address, cancellationToken).ConfigureAwait(false);
        if (match is { Action: RuleAction.Reject })
        {
            _logger.LogInformation("Rejecting mail to {Recipient} from {Sender}: blacklist rule {RuleId}",
                address, sender ?? "(empty)", match.Value.RuleId);
            return false;
        }

        return true;
    }
}
