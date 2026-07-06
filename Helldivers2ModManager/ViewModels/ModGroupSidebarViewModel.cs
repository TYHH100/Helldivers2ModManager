using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class ModGroupSidebarViewModel : ObservableObject
{
	private readonly ILogger<ModGroupSidebarViewModel> _logger;
	private readonly ModGroupService _modGroupService;
	private readonly LocalizationService _localizationService;
	private Func<IEnumerable<ModData>> _getSelectedMods = static () => [];
	private Func<IEnumerable<ModData>> _getAllMods = static () => [];
	private Func<Guid, Task> _selectGroup = static _ => Task.CompletedTask;
	private Action _refresh = static () => { };

	[ObservableProperty]
	private bool _isOpen;

	public string ToggleGlyph => IsOpen ? "\uE76B" : "\uE76C";

	[ObservableProperty]
	private string _newGroupName = string.Empty;

	public ObservableCollection<ModGroup> Groups => _modGroupService.Groups;

	public ModGroup SelectedGroup => _modGroupService.SelectedGroup;

	public bool CanModifySelectedGroup => !SelectedGroup.IsDefault;

	public string SelectedGroupName => SelectedGroup.Name;

	public ModGroupSidebarViewModel(ILogger<ModGroupSidebarViewModel> logger, ModGroupService modGroupService, LocalizationService localizationService)
	{
		_logger = logger;
		_modGroupService = modGroupService;
		_localizationService = localizationService;
		_modGroupService.SelectedGroupChanged += (_, _) => RefreshSelectionProperties();
	}

	public void Configure(
		Func<IEnumerable<ModData>> getSelectedMods,
		Func<IEnumerable<ModData>> getAllMods,
		Func<Guid, Task> selectGroup,
		Action refresh)
	{
		_getSelectedMods = getSelectedMods;
		_getAllMods = getAllMods;
		_selectGroup = selectGroup;
		_refresh = refresh;
	}

	public void RefreshSelectionProperties()
	{
		OnPropertyChanged(nameof(SelectedGroup));
		OnPropertyChanged(nameof(CanModifySelectedGroup));
		OnPropertyChanged(nameof(SelectedGroupName));
		DeleteGroupCommand.NotifyCanExecuteChanged();
		RemoveSelectedModsCommand.NotifyCanExecuteChanged();
	}

	[RelayCommand]
	private void ToggleOpen()
	{
		IsOpen = !IsOpen;
	}

	partial void OnIsOpenChanged(bool value)
	{
		_modGroupService.IsSidebarOpen = value;
		OnPropertyChanged(nameof(ToggleGlyph));
	}

	[RelayCommand]
	private async Task SelectGroup(ModGroup? group)
	{
		if (group is null)
			return;

		try
		{
			await _selectGroup(group.Id);
			RefreshSelectionProperties();
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "切换分组失败");
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = ex.Message });
		}
	}

	[RelayCommand]
	private async Task CreateGroup()
	{
		try
		{
			var group = await _modGroupService.CreateGroupAsync(NewGroupName);
			NewGroupName = string.Empty;
			await _selectGroup(group.Id);
			RefreshSelectionProperties();
			_refresh();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "创建分组失败");
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = ex.Message });
		}
	}

	[RelayCommand]
	private async Task AddSelectedMods()
	{
		var target = SelectedGroup;
		if (target.IsDefault)
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["ModGroup.DefaultCannotAdd"] });
			return;
		}

		var selectedMods = _getSelectedMods().ToArray();
		if (selectedMods.Length == 0)
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["ModGroup.NoSelectedMods"] });
			return;
		}

		await _modGroupService.AddModsToGroupAsync(target.Id, selectedMods);
		_refresh();
	}

	[RelayCommand(CanExecute = nameof(CanModifySelectedGroup))]
	private async Task RemoveSelectedMods()
	{
		var selectedMods = _getSelectedMods().ToArray();
		if (selectedMods.Length == 0)
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["ModGroup.NoSelectedMods"] });
			return;
		}

		await _modGroupService.RemoveModsFromGroupAsync(SelectedGroup.Id, selectedMods);
		_refresh();
	}

	[RelayCommand(CanExecute = nameof(CanModifySelectedGroup))]
	private async Task DeleteGroup()
	{
		var group = SelectedGroup;
		WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
		{
			Title = _localizationService["ModGroup.DeleteTitle"],
			Message = _localizationService["ModGroup.DeleteConfirm"].Replace("{name}", group.Name),
			Confirm = async () =>
			{
				try
				{
					await _modGroupService.DeleteGroupAsync(group.Id);
					await _selectGroup(ModGroup.DefaultGroupId);
					RefreshSelectionProperties();
					_refresh();
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "删除分组失败");
					WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = ex.Message });
				}
			}
		});
		await Task.CompletedTask;
	}
}
