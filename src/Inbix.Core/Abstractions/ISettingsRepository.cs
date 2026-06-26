namespace Inbix.Core.Abstractions;

/// <summary>A tiny key/value store for small app settings (e.g. Junk-inbox sidebar visibility).</summary>
public interface ISettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string value, CancellationToken ct = default);

    Task<bool> GetBoolAsync(string key, bool defaultValue = false, CancellationToken ct = default);

    Task SetBoolAsync(string key, bool value, CancellationToken ct = default);
}

/// <summary>Well-known setting keys.</summary>
public static class SettingKeys
{
    public const string ShowJunkInbox = "junk.show_in_sidebar";

    /// <summary>CSV of enabled identity-generator country codes (e.g. "us,uk").</summary>
    public const string IdentityRegions = "identity.regions";
}
