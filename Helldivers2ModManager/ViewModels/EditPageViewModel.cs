using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Media;

namespace Helldivers2ModManager.ViewModels;

/// <summary>
/// 模组选项编辑页面（点击首页"编辑"按钮打开）。
/// 显示模组作者定义的自定义选项，支持切换启用/禁用和选择子选项。
/// </summary>
[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class EditPageViewModel : PageViewModelBase
{
	public override string Title => _localizationService["EditPage.Title"];

	public ModViewModel? EditMod => _editModStore.CurrentMod;

	private readonly NavigationStore _navStore;
	private readonly EditModStore _editModStore;
	private readonly ProfileService _profileService;
	private readonly SettingsService _settingsService;
	private readonly ModService _modService;
	private readonly LocalizationService _localizationService;
	private readonly ModGroupService _modGroupService;

	public EditPageViewModel(NavigationStore navStore, EditModStore editModStore,
		ProfileService profileService, SettingsService settingsService, ModService modService,
		LocalizationService localizationService, ModGroupService modGroupService)
	{
		_navStore = navStore;
		_editModStore = editModStore;
		_profileService = profileService;
		_settingsService = settingsService;
		_modService = modService;
		_localizationService = localizationService;
		_modGroupService = modGroupService;

		_localizationService.PropertyChanged += (_, _) =>
		{
			OnPropertyChanged(nameof(Title));
		};
	}

	[RelayCommand]
	async Task Done()
	{
		// 在退出编辑前保存当前 Mod 配置到数据库，避免导航回 Dashboard 时数据丢失
		if (!_settingsService.IsReadonly)
		{
			try
			{
				await _modGroupService.SaveSelectedGroupStateAsync(_modService.Mods);
				if (_modGroupService.SelectedGroup.IsDefault)
					await _profileService.SaveAsync(_settingsService, _modService.Mods);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"{_localizationService["EditPage.SaveFailed"]}{ex.Message}");
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
		WeakReferenceMessenger.Default.Send(new ImagePreviewShowMessage { ImageSource = imageSource });
	}

	[RelayCommand]
	void HideImagePreview()
	{
		WeakReferenceMessenger.Default.Send(new ImagePreviewHideMessage());
	}
}
