namespace Inbix.Core.Domain;

/// <summary>A single inbound SMTP conversation, recorded for auditing.</summary>
public sealed class SmtpSession
{
    public long Id { get; set; }
    public string? RemoteIp { get; set; }
    public string? Helo { get; set; }
    public string? MailFrom { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? Result { get; set; }
}
