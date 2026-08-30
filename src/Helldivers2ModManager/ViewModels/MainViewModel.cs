using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using System.Windows.Media;
using Helldivers2ModManager.Services.Infrastructure;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class MainViewModel : ObservableObject, IDisposable, IRecipient<ReplayFirstRunTutorialMessage>
{
	public string Title => $"{_localizationService["Common.AppName"]} {Version} - {CurrentViewModel.Title}";

	public PageViewModelBase CurrentViewModel => _navigationStore.CurrentViewModel;

	public Brush Background => _background;

	/// <summary>
	/// 自定义背景图片（根据设置加载；未启用或加载失败时为 null）。
	/// </summary>
	public ImageSource? BackgroundImageSource { get; private set; }

	/// <summary>
	/// 自定义背景图片不透明度（0..1），半透明露出深色底保证前景可读。
	/// 防御性：SettingsService 未初始化时返回默认 0.6。
	/// </summary>
	public double BackgroundImageOpacity => _settingsService.Initialized ? _settingsService.BackgroundOpacity : 0.6f;

	/// <summary>
	/// 是否已启用自定义背景图片。
	/// </summary>
	public bool HasBackgroundImage => BackgroundImageSource is not null;

	public string Version => string.IsNullOrEmpty(App.VersionAddition) ? $"v{App.Version}" : $"v{App.Version} {App.VersionAddition}";

	public bool IsFirstRunTutorialVisible => _isFirstRunTutorialVisible;

	public int FirstRunTutorialStep => _firstRunTutorialStep;

	public bool IsFirstRunTutorialFirstStep => _firstRunTutorialStep == 0;

	public bool IsFirstRunTutorialLastStep => _firstRunTutorialStep >= TutorialStepCount - 1;

	public bool IsFirstRunTutorialNotLastStep => !IsFirstRunTutorialLastStep;

	public string FirstRunTutorialTargetName => s_tutorialTargetNames[Math.Clamp(_firstRunTutorialStep, 0, TutorialStepCount - 1)];

	public string FirstRunTutorialTitle => _localizationService[s_tutorialTitleKeys[Math.Clamp(_firstRunTutorialStep, 0, TutorialStepCount - 1)]];

	public string FirstRunTutorialDescription => _localizationService[s_tutorialDescriptionKeys[Math.Clamp(_firstRunTutorialStep, 0, TutorialStepCount - 1)]];

	public string FirstRunTutorialIcon => s_tutorialIconGlyphs[Math.Clamp(_firstRunTutorialStep, 0, TutorialStepCount - 1)];

	public string FirstRunTutorialStepText => $"{_firstRunTutorialStep + 1}/{TutorialStepCount}";

	private static readonly ProcessStartInfo s_helpStartInfo = new(@"https://teutinsa.github.io/hd2mm-site/index.html") { UseShellExecute = true };
	private static readonly ProcessStartInfo s_reportBugStartInfo = new(@"https://github.com/TYHH100/Helldivers2ModManager/issues") { UseShellExecute = true };

	private const int TutorialStepCount = 12;

	private static readonly string[] s_tutorialTitleKeys =
	[
		"FirstRunTutorial.WelcomeTitle",
		"FirstRunTutorial.AddModTitle",
		"FirstRunTutorial.ManageTitle",
		"FirstRunTutorial.CheckTitle",
		"FirstRunTutorial.DeployTitle",
		"FirstRunTutorial.BackgroundTasksTitle",
		"FirstRunTutorial.ArmorReuseTitle",
		"FirstRunTutorial.PatchResourceViewerTitle",
		"FirstRunTutorial.BisectTitle",
		"FirstRunTutorial.TagManagementTitle",
		"FirstRunTutorial.SettingsTitle",
		"FirstRunTutorial.FinishTitle",
	];

	private static readonly string[] s_tutorialDescriptionKeys =
	[
		"FirstRunTutorial.WelcomeDescription",
		"FirstRunTutorial.AddModDescription",
		"FirstRunTutorial.ManageDescription",
		"FirstRunTutorial.CheckDescription",
		"FirstRunTutorial.DeployDescription",
		"FirstRunTutorial.BackgroundTasksDescription",
		"FirstRunTutorial.ArmorReuseDescription",
		"FirstRunTutorial.PatchResourceViewerDescription",
		"FirstRunTutorial.BisectDescription",
		"FirstRunTutorial.TagManagementDescription",
		"FirstRunTutorial.SettingsDescription",
		"FirstRunTutorial.FinishDescription",
	];

	private static readonly string[] s_tutorialIconGlyphs =
	[
		"\uE8F1",
		"\uE710",
		"\uE721",
		"\uE9D9",
		"\uE896",
		"\uE9F5",
		"\uE7BA",
		"\uE9D9",
		"\uE9E9",
		"\uE8EC",
		"\uE713",
		"\uE73E",
	];

	private static readonly string[] s_tutorialTargetNames =
	[
		string.Empty,
		"AddCreatePanel",
		"SearchArea",
		"VersionCheckPanel",
		"BottomActionButtons",
		"BackgroundTasksButton",
		"ArmorReuseButton",
		"PatchResourceViewerButton",
		"BisectButton",
		"TagManagementButton",
		"SettingsButton",
		string.Empty,
	];
	private readonly NavigationStore _navigationStore;
	private readonly SolidColorBrush _background;
	private readonly LocalizationService _localizationService;
	private readonly SettingsService _settingsService;
	private readonly ILogger<MainViewModel> _logger;
	private bool _disposed;
	private bool _isFirstRunTutorialVisible;
	private int _firstRunTutorialStep;
	private bool _replayFirstRunTutorialRequested;
	private PageViewModelBase? _observedPageViewModel;

	public MainViewModel(
		NavigationStore navigationStore,
		LocalizationService localizationService,
		SettingsService settingsService,
		ILogger<MainViewModel> logger)
	{
		_navigationStore = navigationStore;
		_localizationService = localizationService;
		_settingsService = settingsService;
		_logger = logger;
		_background = new SolidColorBrush(Color.FromScRgb(0.7f, 0, 0, 0));

		_navigationStore.Navigated += NavigationStore_Navigated;
		_settingsService.SettingsChanged += SettingsService_SettingsChanged;
		RefreshBackground();
		ObserveCurrentPageViewModel();
		WeakReferenceMessenger.Default.Register<ReplayFirstRunTutorialMessage>(this);
	}

	private void NavigationStore_Navigated(object? sender, EventArgs e)
	{
		ObserveCurrentPageViewModel();
		OnPropertyChanged(nameof(CurrentViewModel));
		OnPropertyChanged(nameof(Title));
	}

	private void SettingsService_SettingsChanged(object? sender, EventArgs e)
	{
		RefreshBackground();
	}

	private void RefreshBackground()
	{
		BackgroundImageSource = CreateBackgroundImageSource();
		OnPropertyChanged(nameof(BackgroundImageSource));
		ApplyCardOpacity();
		OnPropertyChanged(nameof(BackgroundImageOpacity));
		OnPropertyChanged(nameof(HasBackgroundImage));
	}

	private void ApplyCardOpacity()
	{
		// 防御性：SettingsService 未初始化时使用默认卡片不透明度 0.7
		float opacity = _settingsService.Initialized ? _settingsService.CardOpacity : 0.7f;
		ApplyCardOpacity(opacity);
	}

	/// <summary>
	/// 应用卡片半透明度到全局卡片背景 brush（设置页滑块实时调用）。
	/// </summary>
	internal static void ApplyCardOpacity(float opacity)
	{
		UpdateBrushAlpha("CardBackgroundBrush", opacity);
		UpdateBrushAlpha("ElevatedCardBackgroundBrush", opacity);
	}

	private static void UpdateBrushAlpha(string key, float opacity)
	{
		// 资源 brush 可能被 WPF 冻结（多处引用时自动 Freeze），不能直接改 Color；
		// 改为替换整个资源键，DynamicResource 引用会自动跟随新值。
		if (System.Windows.Application.Current.Resources[key] is SolidColorBrush brush)
		{
			var color = brush.Color;
			var updated = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), color.R, color.G, color.B));
			updated.Freeze();
			System.Windows.Application.Current.Resources[key] = updated;
		}
	}

	private ImageSource? CreateBackgroundImageSource()
	{
		// 防御性：SettingsService 未初始化时不加载背景图片，返回 null
		if (!_settingsService.Initialized)
			return null;

		if (_settingsService.BackgroundMode != BackgroundMode.Image)
			return null;

		var path = _settingsService.BackgroundImagePath;
		if (string.IsNullOrEmpty(path) || !File.Exists(path))
			return null;

		try
		{
			var image = new BitmapImage();
			image.BeginInit();
			image.CacheOption = BitmapCacheOption.OnLoad;
			image.UriSource = new Uri(Path.GetFullPath(path));
			image.EndInit();
			image.Freeze();
			return image;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to load background image: {Path}", path);
			return null;
		}
	}

	private void ObserveCurrentPageViewModel()
	{
		var current = _navigationStore.CurrentViewModel;
		if (ReferenceEquals(_observedPageViewModel, current))
		{
			TryShowFirstRunTutorial();
			return;
		}

		if (_observedPageViewModel is not null)
			_observedPageViewModel.PropertyChanged -= CurrentViewModel_PropertyChanged;

		_observedPageViewModel = current;
		if (_observedPageViewModel is not null)
			_observedPageViewModel.PropertyChanged += CurrentViewModel_PropertyChanged;

		TryShowFirstRunTutorial();
	}

	private void CurrentViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(DashboardPageViewModel.Initialized))
			TryShowFirstRunTutorial();
	}

	private void TryShowFirstRunTutorial()
	{
		if (_isFirstRunTutorialVisible)
			return;

		if (_settingsService.Initialized && _settingsService.FirstRunTutorialCompleted && !_replayFirstRunTutorialRequested)
			return;

		if (_navigationStore.CurrentViewModel is not DashboardPageViewModel { Initialized: true })
			return;

		_isFirstRunTutorialVisible = true;
		_replayFirstRunTutorialRequested = false;
		_firstRunTutorialStep = 0;
		NotifyFirstRunTutorialProperties();
	}

	private void ReplayFirstRunTutorial()
	{
		_replayFirstRunTutorialRequested = true;
		TryShowFirstRunTutorial();
	}

	public void Receive(ReplayFirstRunTutorialMessage message)
	{
		ReplayFirstRunTutorial();
	}

	[RelayCommand]
	private void NextTutorialStep()
	{
		if (_firstRunTutorialStep >= TutorialStepCount - 1)
			return;

		_firstRunTutorialStep++;
		NotifyFirstRunTutorialProperties();
	}

	[RelayCommand]
	private void PreviousTutorialStep()
	{
		if (_firstRunTutorialStep <= 0)
			return;

		_firstRunTutorialStep--;
		NotifyFirstRunTutorialProperties();
	}

	[RelayCommand]
	private void FinishFirstRunTutorial()
	{
		CompleteFirstRunTutorial();
	}

	[RelayCommand]
	private void SkipFirstRunTutorial()
	{
		CompleteFirstRunTutorial();
	}

	private void CompleteFirstRunTutorial()
	{
		if (!_isFirstRunTutorialVisible)
			return;

		_isFirstRunTutorialVisible = false;
		_replayFirstRunTutorialRequested = false;
		if (_settingsService.Initialized && !_settingsService.IsReadonly)
		{
			try
			{
				_settingsService.FirstRunTutorialCompleted = true;
				_ = SaveFirstRunTutorialSettingAsync();
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to mark first-run tutorial as completed");
			}
		}

		NotifyFirstRunTutorialProperties();
	}

	private async Task SaveFirstRunTutorialSettingAsync()
	{
		try
		{
			await _settingsService.SaveAsync();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to save first-run tutorial setting");
		}
	}

	private void NotifyFirstRunTutorialProperties()
	{
		OnPropertyChanged(nameof(IsFirstRunTutorialVisible));
		OnPropertyChanged(nameof(FirstRunTutorialStep));
		OnPropertyChanged(nameof(IsFirstRunTutorialFirstStep));
		OnPropertyChanged(nameof(IsFirstRunTutorialLastStep));
		OnPropertyChanged(nameof(IsFirstRunTutorialNotLastStep));
		OnPropertyChanged(nameof(FirstRunTutorialTargetName));
		OnPropertyChanged(nameof(FirstRunTutorialTitle));
		OnPropertyChanged(nameof(FirstRunTutorialDescription));
		OnPropertyChanged(nameof(FirstRunTutorialIcon));
		OnPropertyChanged(nameof(FirstRunTutorialStepText));
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

	/// <summary>
	/// 主窗口拖拽压缩包导入入口：已在 Dashboard 页面时直接导入，否则先导航到 Dashboard 再导入。
	/// </summary>
	internal void ImportArchives(string[] archivePaths)
	{
		if (archivePaths is null || archivePaths.Length == 0)
			return;

		if (_navigationStore.CurrentViewModel is DashboardPageViewModel dashboard)
		{
			dashboard.AddFilesCommand.Execute(archivePaths);
		}
		else
		{
			_navigationStore.Navigate<DashboardPageViewModel>(page => page.AddFilesCommand.Execute(archivePaths));
		}
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
			_settingsService.SettingsChanged -= SettingsService_SettingsChanged;
			WeakReferenceMessenger.Default.Unregister<ReplayFirstRunTutorialMessage>(this);
			if (_observedPageViewModel is not null)
				_observedPageViewModel.PropertyChanged -= CurrentViewModel_PropertyChanged;
		}

		_disposed = true;
	}
}

internal sealed class ReplayFirstRunTutorialMessage
{
}
