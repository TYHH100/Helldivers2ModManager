using Helldivers2ModManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Stores;

/// <summary>
/// Owns the currently displayed page ViewModel. Every navigation resolves the page from
/// a fresh <see cref="IServiceScope"/> so the DI container stops tracking the previous
/// page's IDisposable ViewModel when the scope is disposed. Without this, every page
/// opened during the app's lifetime stays strongly referenced by the root container's
/// disposables list (ServiceProviderEngineScope._disposables), which keeps model and
/// texture data alive until the process exits.
/// </summary>
internal sealed class NavigationStore
{
	public PageViewModelBase CurrentViewModel => _currentViewModel;

	public event EventHandler? Navigated;

	private readonly IServiceScopeFactory _scopeFactory;
	private readonly ILogger<NavigationStore> _logger;
	private IServiceScope _scope;
	private PageViewModelBase _currentViewModel;

	public NavigationStore(IServiceProvider provider)
	{
		_scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
		_logger = provider.GetRequiredService<ILogger<NavigationStore>>();
		_scope = _scopeFactory.CreateScope();
		_currentViewModel = _scope.ServiceProvider.GetRequiredService<DashboardPageViewModel>();
	}

	public void Navigate<T>() where T : PageViewModelBase
	{
		NavigateCore<T>(configure: null);
	}

	public void Navigate<T>(Action<T> configure) where T : PageViewModelBase
	{
		ArgumentNullException.ThrowIfNull(configure);
		NavigateCore<T>(configure);
	}

	private void NavigateCore<T>(Action<T>? configure) where T : PageViewModelBase
	{
		_logger.LogInformation("Resolving navigation for `{}`", typeof(T).Name);

		var oldScope = _scope;
		var newScope = _scopeFactory.CreateScope();
		try
		{
			var viewModel = newScope.ServiceProvider.GetRequiredService<T>();
			configure?.Invoke(viewModel);

			_scope = newScope;
			_currentViewModel = viewModel;
			oldScope.Dispose();
		}
		catch
		{
			newScope.Dispose();
			throw;
		}

		_logger.LogInformation("Navigating to \"{}\"", _currentViewModel.Title);
		Navigated?.Invoke(this, EventArgs.Empty);
	}
}
