using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Frontend.Navigation;

public sealed class NavigationStore(IServiceScopeFactory scopeFactory) : INavigationStore
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private IServiceScope? _scope;
    private object _currentPage = null!;
    private string _currentRouteKey = string.Empty;

    public object CurrentPage => _currentPage;

    public string CurrentRouteKey => _currentRouteKey;

    public event EventHandler? CurrentPageChanged;

    public void Navigate(string routeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        var route = FrontendRouteRegistry.Get(routeKey);
        var newScope = _scopeFactory.CreateScope();
        try
        {
            var page = newScope.ServiceProvider.GetRequiredService(route.ViewModelType);
            var oldScope = _scope;
            _scope = newScope;
            _currentPage = page;
            _currentRouteKey = route.Key;
            oldScope?.Dispose();
        }
        catch
        {
            newScope.Dispose();
            throw;
        }

        CurrentPageChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _scope?.Dispose();
        _scope = null;
    }
}
