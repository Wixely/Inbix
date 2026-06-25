namespace Inbix.Core.Domain;

/// <summary>A permanent inbound-only alias such as spotify@mydomain.com.</summary>
public sealed class Alias
{
    public long Id { get; set; }
    public string LocalPart { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public string? Notes { get; set; }

    /// <summary>True for the single permanent catch-all record (stores mail for unmatched recipients).</summary>
    public bool IsCatchAll { get; set; }

    /// <summary>Accent colour (hex) for the sidebar inbox list and dashboard chips. Defaults to violet.</summary>
    public string Color { get; set; } = "#8b7cf6";

    public string Address => $"{LocalPart}@{Domain}";
}
