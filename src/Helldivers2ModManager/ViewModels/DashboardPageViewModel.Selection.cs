using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GongSolutions.Wpf.DragDrop;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Nexus;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SharpSevenZip;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MessageBox = Helldivers2ModManager.Components.MessageBox;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class DashboardPageViewModel
{
    [RelayCommand]
    void SelectAll()
    {
        foreach (var vm in _modGroupService.FilterModViewModels(_mods))
            vm.IsSelected = true;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionCountText));
    }

    [RelayCommand]
    void DeselectAll()
    {
        foreach (var vm in _modGroupService.FilterModViewModels(_mods))
            vm.IsSelected = false;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionCountText));
    }

    [RelayCommand]
    void ToggleModSelection(ModViewModel vm)
    {
        vm.IsSelected = !vm.IsSelected;
    }

    [RelayCommand]
    void InvertSelection()
    {
        ApplyInvertSelection(_modGroupService.FilterModViewModels(_mods).ToList());
    }

    /// <summary>
    /// Shift+单击范围选择：选中过滤视图中 anchor 与 target 之间的所有 Mod。
    /// additive=true（按住 Ctrl）时保留原有选择，否则先清空。
    /// 锚点不在当前过滤视图（被搜索/分组过滤）时退化为仅选中 target。
    /// </summary>
    internal void SelectRange(ModViewModel anchor, ModViewModel target, bool additive)
    {
        ApplyRangeSelection(_modGroupService.FilterModViewModels(_mods).ToList(), anchor, target, additive);
    }

    /// <summary>
    /// 范围选择的纯逻辑（可单元测试）：在可见列表上把 anchor..target 之间的项设为选中。
    /// </summary>
    internal static void ApplyRangeSelection(IList<ModViewModel> visible, ModViewModel anchor, ModViewModel target, bool additive)
    {
        var anchorIndex = visible.IndexOf(anchor);
        var targetIndex = visible.IndexOf(target);

        if (!additive)
        {
            foreach (var item in visible)
                item.IsSelected = false;
        }

        if (anchorIndex < 0 || targetIndex < 0)
        {
            target.IsSelected = true;
            return;
        }

        var low = Math.Min(anchorIndex, targetIndex);
        var high = Math.Max(anchorIndex, targetIndex);
        for (int i = low; i <= high; i++)
            visible[i].IsSelected = true;
    }

    /// <summary>
    /// 反选纯逻辑（可单元测试）：翻转可见列表的所有选中状态。
    /// </summary>
    internal static void ApplyInvertSelection(IList<ModViewModel> visible)
    {
        foreach (var item in visible)
            item.IsSelected = !item.IsSelected;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    Task BatchDelete()
    {
        var selected = _modGroupService.FilterModViewModels(_mods).Where(static vm => vm.IsSelected).ToArray();
        if (selected.Length == 0)
            return Task.CompletedTask;

        var deleteMessage = _settingsService.DeleteToRecycleBin
            ? _localizationService["DashboardPage.RecycleBinConfirm"]
            : _localizationService["DashboardPage.PermanentDeleteConfirm"];

        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = _localizationService["DashboardPage.BatchDeleteTitle"],
            Message = $"{_localizationService["DashboardPage.BatchDeleteConfirm"].Replace("{count}", selected.Length.ToString())}{deleteMessage}",
            Confirm = async () =>
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
                {
                    Title = _localizationService["DashboardPage.BatchDeleteProgress"],
                    Message = _localizationService["SettingsPage.PleaseWait"]
                });

                try
                {
                    foreach (var vm in selected)
                    {
                        vm.IsSelected = false;
                        await _modService.RemoveAsync(vm.Data);
                        vm.Dispose();
                    }

                    // 批量删除后同步更新数据库：直接删除这些模组对应的记录
                    if (!_settingsService.IsReadonly)
                    {
                        var guids = selected.Select(static vm => vm.Guid).ToList();
                        await _profileService.DeleteEnabledDataAsync(_settingsService.StorageDirectory, guids);
                        await _modGroupService.RemoveModsFromAllGroupsAsync(guids);
                        // 同时删除这些模组的版本检测记录
                        foreach (var guid in guids)
                            await _versionCheckRepository.DeleteByGuidAsync(_settingsService.StorageDirectory, guid);
                    }

                    WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, _localizationService["DashboardPage.BatchDeleteFailed2"]);
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
                    {
                        Message = $"{_localizationService["DashboardPage.BatchDeleteFailed"]}{ex.Message}"
                    });
                }

                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectionCountText));
            }
        });

        return Task.CompletedTask;
    }

    [RelayCommand]
    void BatchEnable()
    {
        foreach (var vm in _modGroupService.FilterModViewModels(_mods).Where(static vm => vm.IsSelected))
            vm.Enabled = true;
    }

    [RelayCommand]
    void BatchDisable()
    {
        foreach (var vm in _modGroupService.FilterModViewModels(_mods).Where(static vm => vm.IsSelected))
            vm.Enabled = false;
    }

    /// <summary>
    /// 批量打标签 —— 为所有选中的模组统一设置标签
    /// </summary>
    [RelayCommand]
    void BatchAddTags()
    {
        var selected = _modGroupService.FilterModViewModels(_mods).Where(static vm => vm.IsSelected).ToArray();
        if (selected.Length == 0 || !_settingsService.Initialized)
            return;

        // 使用第一个选中模组的标签作为初始选择状态（方便用户基于现有标签增减）
        var initialTagIds = selected[0].Data.TagIds.ToList();
        var selectableTags = _settingsService.Tags.Select(t => new TagSelectionItem(t, initialTagIds.Contains(t.Id))).ToList();

        WeakReferenceMessenger.Default.Send(new MessageBoxTagSelectionMessage
        {
            Title = _localizationService["DashboardPage.BatchTagTitle"],
            Message = $"{_localizationService["DashboardPage.BatchTagPrefix"]}{selected.Length}{_localizationService["DashboardPage.BatchTagSuffix"]}",
            Tags = selectableTags,
            Confirm = (selectedTags) =>
            {
                if (_settingsService.IsReadonly)
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["DashboardPage.BatchTagReadonly"] });
                    return;
                }

                var newTagIds = selectedTags.Select(static t => t.Tag.Id).ToList();
                foreach (var vm in selected)
                {
                    vm.Data.TagIds = newTagIds;
                }
                RequestProfileSave();
                WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = $"{_localizationService["DashboardPage.BatchTagUpdatedPrefix"]}{selected.Length}{_localizationService["DashboardPage.BatchTagUpdatedSuffix"]}" });
            }
        });
    }

    [RelayCommand]
    void AddModsToGroup(ModViewModel? source = null)
    {
        if (!_settingsService.Initialized)
            return;

        var selected = source is not null && !source.IsSelected
            ? [source]
            : _modGroupService.FilterModViewModels(_mods).Where(static vm => vm.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["ModGroup.NoSelectedMods"] });
            return;
        }

        var groups = _modGroupService.Groups.Where(static group => !group.IsDefault).ToArray();
        if (groups.Length == 0)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["ModGroup.NoCustomGroups"] });
            return;
        }

        // 与设置标签一致的多选交互：预勾选所有选中模组都已加入的分组，
        // 确认后按勾选结果覆盖这些模组的分组集合（可一次加入/移出多个分组）。
        var selectedGuids = selected.Select(static vm => vm.Guid).ToHashSet();
        var items = groups
            .Select(group => new ModGroupSelectionItem(group, group.ModGuids.Count > 0 && group.ModGuids.All(selectedGuids.Contains)))
            .ToList();

        WeakReferenceMessenger.Default.Send(new MessageBoxGroupSelectionMessage
        {
            Title = _localizationService["ModGroup.AddToGroup"],
            Message = _localizationService["ModGroup.AddToGroupsMessage"].Replace("{count}", selected.Length.ToString()),
            Groups = items,
            Confirm = selectedGroups =>
            {
                var picked = selectedGroups.Where(static item => item.IsSelected).Select(static item => item.Group).ToArray();
                _ = SetModsToGroupsAsync(picked, selected);
            }
        });
    }
}
