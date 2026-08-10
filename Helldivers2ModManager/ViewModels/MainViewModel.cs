using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using System.Windows.Media;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class MainViewModel : ObservableObject, IDisposable
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

	private static readonly ProcessStartInfo s_helpStartInfo = new(@"https://teutinsa.github.io/hd2mm-site/index.html") { UseShellExecute = true };
	private static readonly ProcessStartInfo s_reportBugStartInfo = new(@"https://github.com/TYHH100/Helldivers2ModManager/issues") { UseShellExecute = true };
	private readonly NavigationStore _navigationStore;
	private readonly SolidColorBrush _background;
	private readonly LocalizationService _localizationService;
	private readonly SettingsService _settingsService;
	private readonly ILogger<MainViewModel> _logger;
	private bool _disposed;

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
	}

	private void NavigationStore_Navigated(object? sender, EventArgs e)
	{
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
			_settingsService.SettingsChanged -= SettingsService_SettingsChanged;
		}

		_disposed = true;
	}
}
