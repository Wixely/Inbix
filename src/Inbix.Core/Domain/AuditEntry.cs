namespace Inbix.Core.Domain;

/// <summary>An administrative action recorded for traceability.</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public string? Actor { get; set; }
    public string Action { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public string? TargetId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? Details { get; set; }
}
