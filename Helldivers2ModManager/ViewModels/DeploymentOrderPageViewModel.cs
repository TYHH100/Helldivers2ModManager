using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GongSolutions.Wpf.DragDrop;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Windows;

namespace Helldivers2ModManager.ViewModels;

/// <summary>
/// 部署顺序编辑页面 ViewModel
/// 用户可以在该页面通过拖拽或按钮调整模组的部署顺序，
/// 并展开模组以调整其选项/子选项的部署顺序
/// </summary>
[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class DeploymentOrderPageViewModel : PageViewModelBase, IDropTarget
{
    public override string Title => _localizationService["DeploymentOrderPage.Title"];

    /// <summary>
    /// 部署顺序列表中的项（扁平列表，模组+选项+子选项）
    /// </summary>
    public ObservableCollection<DeploymentOrderItem> Items { get; } = [];

    [ObservableProperty]
    private DeploymentOrderItem? _selectedItem;

    public bool CanMoveToTop => Items.Any(i => i.IsSelected && i.ItemType == DeploymentItemType.Mod && Items.IndexOf(i) > 0);

    public bool CanMoveToBottom => Items.Any(i => i.IsSelected && i.ItemType == DeploymentItemType.Mod && Items.IndexOf(i) < Items.Count - 1);

    /// <summary>
    /// 当前部署方向说明
    /// </summary>
    public string OrderDescription => _settingsService.DeployBottomToTop
        ? _localizationService["DeploymentOrderPage.OrderDescBottomUp"]
        : _localizationService["DeploymentOrderPage.OrderDescTopDown"];

    private readonly ILogger<DeploymentOrderPageViewModel> _logger;
    private readonly ModService _modService;
    private readonly SettingsService _settingsService;
    private readonly ProfileSaveCoordinator _profileSaveCoordinator;
    private readonly NavigationStore _navigationStore;
    private readonly LocalizationService _localizationService;

    public DeploymentOrderPageViewModel(
        ILogger<DeploymentOrderPageViewModel> logger,
        ModService modService,
        SettingsService settingsService,
        ProfileSaveCoordinator profileSaveCoordinator,
        NavigationStore navigationStore,
        LocalizationService localizationService)
    {
        _logger = logger;
        _modService = modService;
        _settingsService = settingsService;
        _profileSaveCoordinator = profileSaveCoordinator;
        _navigationStore = navigationStore;
        _localizationService = localizationService;

        _localizationService.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(OrderDescription));
        };

        if (!_settingsService.Initialized || !_settingsService.UseDeploymentOrder)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                _navigationStore.Navigate<DashboardPageViewModel>();
            });
            return;
        }

        LoadItems();
    }

    /// <summary>
    /// 从 SettingsService 加载部署顺序列表
    /// </summary>
    private void LoadItems()
    {
        Items.CollectionChanged -= Items_CollectionChanged;
        foreach (var item in Items)
        {
            item.PropertyChanged -= Item_PropertyChanged;
        }
        Items.Clear();

        var modDict = _modService.Initialized
            ? _modService.Mods.ToDictionary(static m => m.Manifest.Guid)
            : [];

        if (_settingsService.Initialized)
        {
            foreach (var guid in _settingsService.DeploymentOrderGuids)
            {
                if (modDict.TryGetValue(guid, out var mod))
                {
                    var item = new DeploymentOrderItem(mod.Manifest.Guid, mod.Manifest.Name);
                    item.ItemType = DeploymentItemType.Mod;
                    item.PropertyChanged += Item_PropertyChanged;
                    Items.Add(item);
                }
                else
                {
                    var item = new DeploymentOrderItem(guid, _localizationService["DeploymentOrderPage.DeletedModPlaceholder"]);
                    item.PropertyChanged += Item_PropertyChanged;
                    Items.Add(item);
                }
                modDict.Remove(guid);
            }

            foreach (var mod in _modService.Mods)
            {
                if (modDict.ContainsKey(mod.Manifest.Guid))
                {
                    var item = new DeploymentOrderItem(mod.Manifest.Guid, mod.Manifest.Name);
                    item.ItemType = DeploymentItemType.Mod;
                    item.PropertyChanged += Item_PropertyChanged;
                    Items.Add(item);
                }
            }
        }

        Items.CollectionChanged += Items_CollectionChanged;
    }

    private void Item_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeploymentOrderItem.IsSelected))
        {
            OnPropertyChanged(nameof(CanMoveToTop));
            OnPropertyChanged(nameof(CanMoveToBottom));
        }
    }

    /// <summary>
    /// 展开或折叠模组功能已禁用
    /// </summary>
    [RelayCommand]
    void ToggleExpand(DeploymentOrderItem? item)
    {
        // 已禁用，不再展开显示选项和子选项
    }

    private void Items_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        SyncToSettings();
        OnPropertyChanged(nameof(CanMoveToTop));
        OnPropertyChanged(nameof(CanMoveToBottom));
    }

    /// <summary>
    /// 将 Items 中的 Mod 级别项的顺序同步回 SettingsService.DeploymentOrderGuids
    /// </summary>
    private void SyncToSettings()
    {
        if (!_settingsService.Initialized) return;
        _settingsService.DeploymentOrderGuids.Clear();
        foreach (var item in Items)
        {
            if (item.ItemType == DeploymentItemType.Mod)
            {
                _settingsService.DeploymentOrderGuids.Add(item.Guid);
            }
        }
    }

    partial void OnSelectedItemChanged(DeploymentOrderItem? value)
    {
        OnPropertyChanged(nameof(CanMoveToTop));
        OnPropertyChanged(nameof(CanMoveToBottom));
    }

    // ========== 按钮操作 ==========

    [RelayCommand]
    void MoveToTop()
    {
        var selected = Items.Where(i => i.IsSelected && i.ItemType == DeploymentItemType.Mod)
                            .OrderBy(i => Items.IndexOf(i))
                            .ToList();

        if (selected.Count == 0) return;

        foreach (var item in selected)
        {
            var index = Items.IndexOf(item);
            if (index <= 0) continue;
            Items.Move(index, 0);
        }
    }

    [RelayCommand]
    void MoveToBottom()
    {
        var selected = Items.Where(i => i.IsSelected && i.ItemType == DeploymentItemType.Mod)
                            .OrderByDescending(i => Items.IndexOf(i))
                            .ToList();

        if (selected.Count == 0) return;

        foreach (var item in selected)
        {
            var index = Items.IndexOf(item);
            if (index >= Items.Count - 1) continue;
            Items.Move(index, Items.Count - 1);
        }
    }

    /// <summary>
    /// 用当前所有已安装的模组（按 Dashboard 顺序）填充部署顺序列表
    /// </summary>
    [RelayCommand]
    void InitFromCurrent()
    {
        if (!_modService.Initialized || !_settingsService.Initialized) return;

        var existingGuids = new HashSet<Guid>(Items.Where(i => i.ItemType == DeploymentItemType.Mod).Select(static i => i.Guid));
        var added = 0;

        var dashboardOrder = _profileSaveCoordinator.GetCurrentOrder();
        IEnumerable<ModData> orderedMods;

        if (dashboardOrder is { Count: > 0 })
        {
            var modDict = _modService.Mods.ToDictionary(static m => m.Manifest.Guid);
            var temp = new List<ModData>();

            foreach (var guid in dashboardOrder)
            {
                if (modDict.TryGetValue(guid, out var mod))
                {
                    temp.Add(mod);
                    modDict.Remove(guid);
                }
            }
            temp.AddRange(modDict.Values);
            orderedMods = temp;
        }
        else
        {
            orderedMods = _modService.Mods;
        }

        foreach (var mod in orderedMods)
        {
            if (!existingGuids.Contains(mod.Manifest.Guid))
            {
                var item = new DeploymentOrderItem(mod.Manifest.Guid, mod.Manifest.Name);
                item.ItemType = DeploymentItemType.Mod;
                Items.Add(item);
                added++;
            }
        }

        if (added > 0)
        {
            _logger.LogInformation("Added {} mods to deployment order (synced from Dashboard)", added);
        }
    }

    [RelayCommand]
    void ClearOrder()
    {
        Items.Clear();
        _logger.LogInformation("Deployment order cleared");
    }

    [RelayCommand]
    void Back()
    {
        _navigationStore.Navigate<DashboardPageViewModel>();
    }

    // ========== 多选操作 ==========

    [RelayCommand]
    void InvertSelection()
    {
        foreach (var item in Items)
        {
            item.IsSelected = !item.IsSelected;
        }
    }

    [RelayCommand]
    void SelectAll()
    {
        foreach (var item in Items)
        {
            item.IsSelected = true;
        }
    }

    [RelayCommand]
    void DeselectAll()
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }
    }

    // ========== 拖拽排序（IDropTarget） ==========

    void IDropTarget.DragOver(IDropInfo dropInfo)
    {
        new DefaultDropHandler().DragOver(dropInfo);
    }

    void IDropTarget.Drop(IDropInfo dropInfo)
    {
        if (dropInfo?.Data is not DeploymentOrderItem sourceItem)
        {
            new DefaultDropHandler().Drop(dropInfo);
            return;
        }

        if (sourceItem.ItemType != DeploymentItemType.Mod)
        {
            return;
        }

        // 获取所有选中项（含当前拖拽项），按原始位置排序
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Contains(sourceItem) && selected.Count > 1)
        {
            var sortedSelected = selected.OrderBy(i => Items.IndexOf(i)).ToList();
            var targetIdx = dropInfo.InsertIndex;

            foreach (var item in sortedSelected.AsEnumerable().Reverse())
                Items.Remove(item);

            var firstRemovedIdx = Items.IndexOf(sortedSelected[0]);
            if (firstRemovedIdx == -1)
            {
                var beforeCount = sortedSelected.Count(item => Items.IndexOf(item) < targetIdx);
                targetIdx -= beforeCount;
            }

            targetIdx = Math.Clamp(targetIdx, 0, Items.Count);

            for (int i = 0; i < sortedSelected.Count; i++)
                Items.Insert(targetIdx + i, sortedSelected[i]);
        }
        else
        {
            new DefaultDropHandler().Drop(dropInfo);
        }
    }
}
