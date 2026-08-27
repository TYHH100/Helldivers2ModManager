using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class HelpPageViewModel : PageViewModelBase
{
	public override string Title => _localizationService["MainWindow.Help"];

	private readonly NavigationStore _navigationStore;
	private readonly LocalizationService _localizationService;

	public HelpPageViewModel(NavigationStore navigationStore, LocalizationService localizationService)
	{
		_navigationStore = navigationStore;
		_localizationService = localizationService;

		_localizationService.PropertyChanged += (_, _) =>
		{
			OnPropertyChanged(nameof(Title));
		};
	}

	[RelayCommand]
	void Back()
	{
		_navigationStore.Navigate<DashboardPageViewModel>();
	}
}
