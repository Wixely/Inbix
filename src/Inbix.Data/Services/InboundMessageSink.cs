using Inbix.Core.Abstractions;
using Inbix.Core.Domain;
using Inbix.Core.Options;
using Inbix.Core.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inbix.Data.Services;

/// <summary>
/// Durably persists an <see cref="InboundMessage"/>: re-validates the recipient and size, writes
/// the raw MIME to the raw store, then records message metadata (unparsed). The MIME parser worker
/// fills in subject/body/attachments later. Returns only after the raw bytes and the row are stored,
/// so the SMTP layer can safely acknowledge with 250.
/// </summary>
public sealed class InboundMessageSink : IInboundMessageSink
{
    private readonly IAliasRepository _aliases;
    private readonly IMessageRepository _messages;
    private readonly IRawMessageStore _rawStore;
    private readonly IBlacklistMatcher _matcher;
    private readonly InbixOptions _options;
    private readonly ILogger<InboundMessageSink> _logger;

    public InboundMessageSink(
        IAliasRepository aliases,
        IMessageRepository messages,
        IRawMessageStore rawStore,
        IBlacklistMatcher matcher,
        IOptions<InbixOptions> options,
        ILogger<InboundMessageSink> logger)
    {
        _aliases = aliases;
        _messages = messages;
        _rawStore = rawStore;
        _matcher = matcher;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<InboundSaveResult> SaveAsync(InboundMessage message, CancellationToken ct = default)
    {
        if (message.SizeBytes > _options.Smtp.MaxMessageSizeBytes)
        {
            _logger.LogWarning("Rejecting oversized message to {Recipient} ({Size} bytes)", message.Recipient, message.SizeBytes);
            return InboundSaveResult.TooLarge;
        }

        if (!AliasRules.TrySplitAddress(message.Recipient, out var localPart, out var domain))
            return InboundSaveResult.UnknownRecipient;

        try
        {
            // Prefer a specific enabled alias; otherwise fall back to the catch-all (if enabled and
            // the recipient is on an accepted domain). Mail is stored under whichever matched.
            long aliasId;
            var alias = await _aliases.FindAsync(localPart, domain, ct).ConfigureAwait(false);
            if (alias is { Enabled: true })
            {
                aliasId = alias.Id;
            }
            else
            {
                var catchAll = await _aliases.GetCatchAllAsync(ct).ConfigureAwait(false);
                if (catchAll is { Enabled: true } && IsAcceptedDomain(domain))
                    aliasId = catchAll.Id;
                else
                    return InboundSaveResult.UnknownRecipient;
            }

            // Blacklist: reject/discard rules accept at SMTP but store nothing; a junk rule stores the
            // message tagged with the rule. (Reject is normally caught at RCPT; handle it here too.)
            var match = await _matcher.MatchAsync(message.Sender, message.Recipient, ct).ConfigureAwait(false);
            if (match is { Action: RuleAction.Reject or RuleAction.Discard })
            {
                _logger.LogInformation("Discarding message to {Recipient} (blacklist rule {RuleId}, {Action})",
                    message.Recipient, match.Value.RuleId, match.Value.Action);
                return InboundSaveResult.Stored; // accepted at SMTP, intentionally dropped
            }

            var junkedAt = match is { Action: RuleAction.Junk } ? message.ReceivedAt : (DateTimeOffset?)null;
            var junkRuleId = match is { Action: RuleAction.Junk } ? match.Value.RuleId : (long?)null;

            // Persist the raw source first; if metadata write fails we still hold the original.
            var rawPath = await _rawStore.SaveRawAsync(message.RawMime, message.ReceivedAt, ct).ConfigureAwait(false);

            var row = new Message
            {
                AliasId = aliasId,
                SmtpSessionId = message.SmtpSessionId,
                Recipient = message.Recipient,
                Sender = message.Sender,
                ReceivedAt = message.ReceivedAt,
                SizeBytes = message.SizeBytes,
                RawStoragePath = rawPath,
                Parsed = false,
                JunkedAt = junkedAt,
                JunkRuleId = junkRuleId,
                JunkManual = false
            };

            var id = await _messages.CreateAsync(row, ct).ConfigureAwait(false);
            _logger.LogInformation("Stored message {MessageId} for {Recipient} ({Size} bytes)", id, message.Recipient, message.SizeBytes);
            return InboundSaveResult.Stored;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store inbound message for {Recipient}; signalling temporary failure", message.Recipient);
            return InboundSaveResult.TemporaryFailure;
        }
    }

    private bool IsAcceptedDomain(string domain) =>
        _options.Domains.Length == 0 ||
        _options.Domains.Any(d => string.Equals(d.Trim(), domain, StringComparison.OrdinalIgnoreCase));
}
