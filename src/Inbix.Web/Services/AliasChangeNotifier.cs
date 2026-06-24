namespace Inbix.Web.Services;

/// <summary>
/// Circuit-scoped pub/sub so the (interactive) aliases page can tell the (interactive) sidebar to
/// refresh its inbox list when an alias or the catch-all is created, edited, enabled or disabled.
/// </summary>
public sealed class AliasChangeNotifier
{
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
