using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Windows.Media;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class MainViewModel : ObservableObject, IDisposable
{
	public string Title => $"{_localizationService["Common.AppName"]} {Version} - {CurrentViewModel.Title}";

	public PageViewModelBase CurrentViewModel => _navigationStore.CurrentViewModel;

	public Brush Background => _background;

	public string Version => string.IsNullOrEmpty(App.VersionAddition) ? $"v{App.Version}" : $"v{App.Version} {App.VersionAddition}";

	private static readonly ProcessStartInfo s_helpStartInfo = new(@"https://teutinsa.github.io/hd2mm-site/index.html") { UseShellExecute = true };
	private static readonly ProcessStartInfo s_reportBugStartInfo = new(@"https://github.com/TYHH100/Helldivers2ModManager/issues") { UseShellExecute = true };
	private readonly NavigationStore _navigationStore;
	private readonly SolidColorBrush _background;
	private readonly LocalizationService _localizationService;
	private bool _disposed;

	public MainViewModel(NavigationStore navigationStore, LocalizationService localizationService)
	{
		_navigationStore = navigationStore;
		_localizationService = localizationService;
		_background = new SolidColorBrush(Color.FromScRgb(0.7f, 0, 0, 0));

		_navigationStore.Navigated += NavigationStore_Navigated;
	}

	private void NavigationStore_Navigated(object? sender, EventArgs e)
	{
		OnPropertyChanged(nameof(CurrentViewModel));
		OnPropertyChanged(nameof(Title));
	}

	[RelayCommand]
	void Help()
	{
		Process.Start(s_helpStartInfo);
	}

	[RelayCommand]
	void ReportBug()
	{
		Process.Start(s_reportBugStartInfo);
	}

	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	private void Dispose(bool disposing)
	{
		if (_disposed) return;

		if (disposing)
		{
			_navigationStore.Navigated -= NavigationStore_Navigated;
		}

		_disposed = true;
	}
}
