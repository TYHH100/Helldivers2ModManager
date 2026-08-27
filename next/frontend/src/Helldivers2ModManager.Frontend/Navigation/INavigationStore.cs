namespace Helldivers2ModManager.Frontend.Navigation;

public interface INavigationStore : IDisposable
{
    object CurrentPage { get; }

    string CurrentRouteKey { get; }

    event EventHandler? CurrentPageChanged;

    void Navigate(string routeKey);
}
