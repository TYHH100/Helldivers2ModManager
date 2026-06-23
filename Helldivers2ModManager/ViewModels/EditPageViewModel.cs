using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.ViewModels;

/// <summary>
/// 模组选项编辑页面（点击首页"编辑"按钮打开）。
/// 显示模组作者定义的自定义选项，支持切换启用/禁用和选择子选项。
/// </summary>
[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class EditPageViewModel : PageViewModelBase
{
	public override string Title => "Mod Options";

	public ModViewModel? EditMod => _editModStore.CurrentMod;

	[ObservableProperty]
	private ImageSource? _previewImageSource;

	[ObservableProperty]
	private Visibility _imagePreviewVisibility = Visibility.Collapsed;

	private readonly NavigationStore _navStore;
	private readonly EditModStore _editModStore;
	private readonly ProfileService _profileService;
	private readonly SettingsService _settingsService;
	private readonly ModService _modService;

	public EditPageViewModel(NavigationStore navStore, EditModStore editModStore,
		ProfileService profileService, SettingsService settingsService, ModService modService)
	{
		_navStore = navStore;
		_editModStore = editModStore;
		_profileService = profileService;
		_settingsService = settingsService;
		_modService = modService;
	}

	[RelayCommand]
	async Task Done()
	{
		// 在退出编辑前保存当前 Mod 配置到数据库，避免导航回 Dashboard 时数据丢失
		if (!_settingsService.IsReadonly)
		{
			try
			{
				await _profileService.SaveAsync(_settingsService, _modService.Mods);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"保存 Mod 配置失败: {ex.Message}");
			}
		}

		_editModStore.CurrentMod = null;
		_navStore.Navigate<DashboardPageViewModel>();
	}

	[RelayCommand]
	void Cancel()
	{
		_editModStore.CurrentMod = null;
		_navStore.Navigate<DashboardPageViewModel>();
	}

	[RelayCommand]
	void ShowImagePreview(ImageSource imageSource)
	{
		PreviewImageSource = imageSource;
		ImagePreviewVisibility = Visibility.Visible;
	}

	[RelayCommand]
	void HideImagePreview()
	{
		ImagePreviewVisibility = Visibility.Hidden;
		PreviewImageSource = null;
	}
}
