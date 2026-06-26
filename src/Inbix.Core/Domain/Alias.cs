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

    /// <summary>When true, mail in this mailbox older than <see cref="ExpiryDays"/> is auto-deleted.</summary>
    public bool ExpiryEnabled { get; set; }

    /// <summary>Days of retention when <see cref="ExpiryEnabled"/> is set. Defaults to 60.</summary>
    public int ExpiryDays { get; set; } = 60;

    /// <summary>Optional friendly display name shown instead of the address when <see cref="ShortnameEnabled"/>.</summary>
    public string Shortname { get; set; } = string.Empty;

    /// <summary>When true (and a shortname is set), the mailbox shows as the shortname in the sidebar/inbox title.</summary>
    public bool ShortnameEnabled { get; set; }

    public string Address => $"{LocalPart}@{Domain}";

    /// <summary>What to label this mailbox with: the shortname when enabled/set, otherwise the address.</summary>
    public string DisplayName =>
        ShortnameEnabled && !string.IsNullOrWhiteSpace(Shortname) ? Shortname.Trim() : Address;
}
