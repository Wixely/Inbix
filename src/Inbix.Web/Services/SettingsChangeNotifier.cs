namespace Inbix.Web.Services;

/// <summary>
/// Circuit-scoped pub/sub so the Rules page can tell the sidebar to refresh when a setting that
/// affects it changes (e.g. toggling the Junk inbox's visibility).
/// </summary>
public sealed class SettingsChangeNotifier
{
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
