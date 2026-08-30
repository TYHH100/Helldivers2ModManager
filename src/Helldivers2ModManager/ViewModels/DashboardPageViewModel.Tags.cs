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
    void ApplyAll()
    {
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

    [RelayCommand]
    void DownloadFromNexus()
    {
        WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = _localizationService["DashboardPage.NexusDownloadInfo"] });
        
        _navStore.Value.Navigate<NexusDownloadPageViewModel>();
    }

    [RelayCommand]
    void ShowBackgroundTasks()
    {
        _navStore.Value.Navigate<BackgroundTasksPageViewModel>();
    }

    [RelayCommand]
    void EditModTags(ModViewModel modVm)
    {
        if (modVm == null || !_settingsService.Initialized)
            return;

        var selectedTagIds = modVm.Data.TagIds.ToList();
        var selectableTags = _settingsService.Tags.Select(t => new TagSelectionItem(t, selectedTagIds.Contains(t.Id))).ToList();

        WeakReferenceMessenger.Default.Send(new MessageBoxTagSelectionMessage
            {
                Title = _localizationService["DashboardPage.SetTags"],
                Message = _localizationService["DashboardPage.EditTagsMsg"],
                Tags = selectableTags,
                Confirm = (selectedTags) =>
                {
                    if (!_settingsService.IsReadonly)
                    {
                        modVm.Data.TagIds = selectedTags.Select(t => t.Tag.Id).ToList();
                        RequestProfileSave();
                        WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = _localizationService["DashboardPage.EditTagsUpdated"] });
                    }
                    else
                    {
                        WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["DashboardPage.BatchTagReadonly"] });
                    }
                }
            });
    }

    public IReadOnlyList<ModTag> AllTags => _settingsService.Initialized ? _settingsService.Tags : [];
    public IEnumerable<object> TagItems => _settingsService.Initialized ? _settingsService.Tags : [];

    // ===== 分隔符命令 =====

    /// <summary>
    /// 是否可以创建分隔符（分隔符功能必须在设置中启用）
    /// </summary>
    bool CanCreateSeparator() => ShowSeparator;

    /// <summary>
    /// 创建新的分隔符
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreateSeparator))]
    void CreateSeparator()
    {
        if (!_settingsService.Initialized || _settingsService.IsReadonly)
            return;

        var separator = new ModSeparator
        {
            Name = _localizationService["DashboardPage.DefaultSeparatorName"],
            Color = "#FF6200EE",
            IsExpanded = true,
            DisplayIndex = _orderedItems.Count
        };
        _settingsService.Separators.Add(separator);
        RebuildOrderedItems();
        _ = _settingsService.SaveAsync();
    }

    /// <summary>
    /// 重命名分隔符
    /// </summary>
    [RelayCommand]
    void RenameSeparator(ModSeparator separator)
    {
        if (separator == null || _settingsService.IsReadonly)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
        {
            Title = _localizationService["DashboardPage.RenameSeparatorTitle"],
            Message = _localizationService["DashboardPage.RenameSeparatorMsg"],
            MaxLength = 32,
            InitialText = separator.Name,
            Confirm = (newName) =>
            {
                if (string.IsNullOrWhiteSpace(newName))
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["DashboardPage.RenameSeparatorEmptyError"] });
                    return;
                }
                separator.Name = newName;
                OnPropertyChanged(nameof(Mods));
                _ = _settingsService.SaveAsync();
            }
        });
    }

    /// <summary>
    /// 更改分隔符颜色
    /// </summary>
    [RelayCommand]
    void ChangeSeparatorColor(ModSeparator separator)
    {
        if (separator == null || _settingsService.IsReadonly)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxColorPickerMessage
        {
            Title = _localizationService["DashboardPage.ChangeSeparatorColorTitle"],
            Message = $"{_localizationService["DashboardPage.ChangeSeparatorColorPrefix"]}{separator.Name}{_localizationService["DashboardPage.ChangeSeparatorColorSuffix"]}",
            CurrentColor = separator.Color,
            Confirm = (selectedColor) =>
            {
                separator.Color = selectedColor;
                OnPropertyChanged(nameof(Mods));
                _ = _settingsService.SaveAsync();
            }
        });
    }

    /// <summary>
    /// 删除分隔符
    /// </summary>
    [RelayCommand]
    void DeleteSeparator(ModSeparator separator)
    {
        if (separator == null || _settingsService.IsReadonly)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = _localizationService["DashboardPage.DeleteSeparatorHint"],
            Message = $"{_localizationService["DashboardPage.DeleteSeparatorPrefix"]}{separator.Name}{_localizationService["DashboardPage.DeleteSeparatorSuffix"]}",
            Confirm = () =>
            {
                _settingsService.Separators.Remove(separator);
                RebuildOrderedItems();
                _ = _settingsService.SaveAsync();
            }
        });
    }

    protected override void OnDispose()
    {
        _modService.ModAdded -= ModService_ModAdded;
        _modService.ModAdded -= OnModAdded;
        _modService.ModRemoved -= ModService_ModRemoved;
        _versionCheckVm.PropertyChanged -= VersionCheckVm_PropertyChanged;

        if (Initialized && !_settingsService.IsReadonly)
            _profileSaveCoordinator.RequestSave(CaptureProfileSnapshot());

        if (_mods is not null)
        {
            _mods.CollectionChanged -= Mods_CollectionChanged;
            foreach (var vm in _mods)
            {
                vm.OptionsChanged -= ModViewModel_OptionsChanged;
                vm.PropertyChanged -= ModViewModel_PropertyChanged;
                vm.VersionCheckRefreshed -= ModViewModel_VersionCheckRefreshed;
            }
            _orderedItems.Clear();
            _mods.Clear();
        }
    }
}
