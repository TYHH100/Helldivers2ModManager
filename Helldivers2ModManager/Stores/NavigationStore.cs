using Helldivers2ModManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Stores;

internal sealed class NavigationStore(IServiceProvider provider, PageViewModelBase initialViewModel)
{
    public PageViewModelBase CurrentViewModel => _currentViewModel;

    public event EventHandler? Navigated;

    private readonly IServiceProvider _provider = provider;
    private readonly ILogger<NavigationStore> _logger = provider.GetRequiredService<ILogger<NavigationStore>>();
    private PageViewModelBase _currentViewModel = initialViewModel;
    private readonly Stack<PageViewModelBase> _backStack = new();

    public bool CanGoBack => _backStack.Count > 0;

    public void Navigate(PageViewModelBase viewModel, bool addToBackStack = true)
    {
        _logger.LogInformation("Navigating to \"{}\"", viewModel.Title);

        var oldViewModel = _currentViewModel;
        _currentViewModel = viewModel;
        if (addToBackStack)
            _backStack.Push(oldViewModel);
        else
        {
            (oldViewModel as IDisposable)?.Dispose();
            while (_backStack.TryPop(out var stacked))
                (stacked as IDisposable)?.Dispose();
        }

        Navigated?.Invoke(this, EventArgs.Empty);
    }

    public void Navigate<T>() where T : PageViewModelBase
    {
        _logger.LogInformation("Resolving navigation for `{}`", typeof(T).Name);
        Navigate(_provider.GetRequiredService<T>());
    }

    public void NavigateRoot<T>() where T : PageViewModelBase
    {
        _logger.LogInformation("Resolving root navigation for `{}`", typeof(T).Name);
        Navigate(_provider.GetRequiredService<T>(), addToBackStack: false);
    }

    public bool GoBack()
    {
        if (!_backStack.TryPop(out var previous))
            return false;
        (_currentViewModel as IDisposable)?.Dispose();
        _currentViewModel = previous;
        Navigated?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Navigate(Type destinationType, bool root)
    {
        ArgumentNullException.ThrowIfNull(destinationType);
        if (!typeof(PageViewModelBase).IsAssignableFrom(destinationType))
            throw new ArgumentException("Navigation destinations must be page view models.", nameof(destinationType));
        Navigate((PageViewModelBase)_provider.GetRequiredService(destinationType), addToBackStack: !root);
    }
}
