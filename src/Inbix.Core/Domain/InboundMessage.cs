namespace Inbix.Core.Domain;

/// <summary>
/// The stable boundary between the SMTP receiver and the rest of the system.
/// Any SMTP implementation (the built-in SmtpServer receiver, or Postfix later)
/// only needs to produce one of these and hand it to <c>IInboundMessageSink</c>.
/// </summary>
public sealed record InboundMessage
{
    public required string Recipient { get; init; }
    public string? Sender { get; init; }
    public string? RemoteIp { get; init; }
    public string? Helo { get; init; }
    public required ReadOnlyMemory<byte> RawMime { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Optional id of the SMTP session row this message arrived on.</summary>
    public long? SmtpSessionId { get; init; }

    public long SizeBytes => RawMime.Length;
}

/// <summary>Outcome of persisting an <see cref="InboundMessage"/>, mapped to an SMTP reply.</summary>
public enum InboundSaveResult
{
    /// <summary>Stored durably; reply 250.</summary>
    Stored,

    /// <summary>Recipient is not a known/enabled alias; reply 550.</summary>
    UnknownRecipient,

    /// <summary>Message exceeds the configured size limit; reply 552.</summary>
    TooLarge,

    /// <summary>Transient problem (e.g. database/disk); reply 451 so the sender retries.</summary>
    TemporaryFailure
}
