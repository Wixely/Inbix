namespace Inbix.Web.Components;

/// <summary>Shared date/time formatting for the mail UI.</summary>
public static class TimeFormat
{
    /// <summary>Absolute local timestamp, e.g. "Jun 24, 2026 15:58".</summary>
    public static string Timestamp(DateTimeOffset t) => t.ToLocalTime().ToString("MMM d, yyyy HH:mm");

    /// <summary>Relative age, e.g. "just now", "5 min ago", "3 h ago", "2 d ago".</summary>
    public static string Ago(DateTimeOffset when)
    {
        var d = DateTimeOffset.UtcNow - when;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        return $"{(int)d.TotalDays} d ago";
    }
}
