namespace Inbix.Core.Domain;

/// <summary>Stored inbound message metadata. Raw MIME lives in the raw store (see RawStoragePath).</summary>
public sealed class Message
{
    public long Id { get; set; }
    public long AliasId { get; set; }
    public long? SmtpSessionId { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public string? Sender { get; set; }
    public string? Subject { get; set; }
    public string? MessageIdHeader { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public long SizeBytes { get; set; }
    public string? RawStoragePath { get; set; }
    public bool Parsed { get; set; }
    public string? ParseError { get; set; }

    // --- Junk (blacklist) state. Junk membership = JunkedAt is not null. ---

    /// <summary>When the message was moved to Junk (UTC), or null if it is in its normal inbox.</summary>
    public DateTimeOffset? JunkedAt { get; set; }

    /// <summary>The blacklist rule that junked it (null for a manual junk).</summary>
    public long? JunkRuleId { get; set; }

    /// <summary>True when a manual junk/unjunk locked the message so rule sweeps skip it.</summary>
    public bool JunkManual { get; set; }

    /// <summary>Last time the junk state changed (junk/unjunk/sweep/unsweep). Null = never moved; retention then counts from <see cref="ReceivedAt"/>.</summary>
    public DateTimeOffset? StateChangedAt { get; set; }
}
