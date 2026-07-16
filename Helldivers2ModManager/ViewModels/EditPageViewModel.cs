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
	private readonly ProfileSaveCoordinator _profileSaveCoordinator;
	private readonly ModService _modService;
	private readonly LocalizationService _localizationService;

	public EditPageViewModel(NavigationStore navStore, EditModStore editModStore,
		ProfileSaveCoordinator profileSaveCoordinator, ModService modService,
		LocalizationService localizationService)
	{
		_navStore = navStore;
		_editModStore = editModStore;
		_profileSaveCoordinator = profileSaveCoordinator;
		_modService = modService;
		_localizationService = localizationService;

		_localizationService.PropertyChanged += (_, _) =>
		{
			OnPropertyChanged(nameof(Title));
		};
	}

	[RelayCommand]
	async Task Done()
	{
		// 在退出编辑前保存当前 Mod 配置到数据库，避免导航回 Dashboard 时数据丢失
		try
		{
			await _profileSaveCoordinator.SaveCurrentAsync(_modService.Mods);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"{_localizationService["EditPage.SaveFailed"]}{ex.Message}");
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
