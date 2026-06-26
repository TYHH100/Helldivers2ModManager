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
    public override string Title => "部署顺序";

    /// <summary>
    /// 部署顺序列表中的项（扁平列表，模组+选项+子选项）
    /// </summary>
    public ObservableCollection<DeploymentOrderItem> Items { get; } = [];

    [ObservableProperty]
    private DeploymentOrderItem? _selectedItem;

    public bool CanMoveUp => SelectedItem is not null
        && Items.IndexOf(SelectedItem) > 0
        && GetTopOfLevel(Items.IndexOf(SelectedItem)) < Items.IndexOf(SelectedItem);

    public bool CanMoveDown => SelectedItem is not null
        && Items.IndexOf(SelectedItem) < Items.Count - 1
        && GetBottomOfLevel(Items.IndexOf(SelectedItem)) > Items.IndexOf(SelectedItem);

    /// <summary>
    /// 当前部署方向说明
    /// </summary>
    public string OrderDescription => _settingsService.DeployBottomToTop
        ? "当前部署方向：从下到上（列表底部的模组优先部署）"
        : "当前部署方向：从上到下（列表顶部的模组优先部署）";

    private readonly ILogger<DeploymentOrderPageViewModel> _logger;
    private readonly ModService _modService;
    private readonly SettingsService _settingsService;
    private readonly ProfileService _profileService;
    private readonly NavigationStore _navigationStore;

    public DeploymentOrderPageViewModel(
        ILogger<DeploymentOrderPageViewModel> logger,
        ModService modService,
        SettingsService settingsService,
        ProfileService profileService,
        NavigationStore navigationStore)
    {
        _logger = logger;
        _modService = modService;
        _settingsService = settingsService;
        _profileService = profileService;
        _navigationStore = navigationStore;

        LoadItems();
    }

    /// <summary>
    /// 从 SettingsService 加载部署顺序列表
    /// </summary>
    private void LoadItems()
    {
        Items.CollectionChanged -= Items_CollectionChanged;
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
                    Items.Add(item);
                }
                else
                {
                    Items.Add(new DeploymentOrderItem(guid, "[已删除的模组]"));
                }
                modDict.Remove(guid);
            }

            // 添加不在 DeploymentOrderGuids 中的模组
            foreach (var mod in _modService.Mods)
            {
                if (modDict.ContainsKey(mod.Manifest.Guid))
                {
                    var item = new DeploymentOrderItem(mod.Manifest.Guid, mod.Manifest.Name);
                    item.ItemType = DeploymentItemType.Mod;
                    Items.Add(item);
                }
            }
        }

        Items.CollectionChanged += Items_CollectionChanged;
    }

    /// <summary>
    /// 获取指定模组的选项列表（仅 V1 清单有 Options）
    /// </summary>
    private static IReadOnlyList<ModOption>? GetModOptions(ModData mod)
    {
        if (mod.Manifest is V1ModManifest v1Man)
            return v1Man.Options;
        return null;
    }

    /// <summary>
    /// 展开或折叠模组以显示/隐藏其选项
    /// </summary>
    [RelayCommand]
    void ToggleExpand(DeploymentOrderItem? item)
    {
        if (item is null || item.ItemType != DeploymentItemType.Mod)
            return;

        if (item.IsExpanded)
        {
            CollapseMod(item);
        }
        else
        {
            ExpandMod(item);
        }
    }

    /// <summary>
    /// 展开模组：在 Items 中插入该模组的选项（及子选项）
    /// </summary>
    private void ExpandMod(DeploymentOrderItem modItem)
    {
        var modData = _modService.GetModByGuid(modItem.Guid);
        if (modData is null)
            return;

        var options = GetModOptions(modData);
        if (options is null || options.Count == 0)
            return;

        var modIndex = Items.IndexOf(modItem);
        if (modIndex < 0)
            return;

        // 获取自定义选项顺序，如果没有则使用默认顺序
        var optOrder = _settingsService.OptionOrders.TryGetValue(modItem.Guid, out var order)
            ? order
            : Enumerable.Range(0, options.Count).ToArray();

        int insertPos = modIndex + 1;

        for (int optIdx = 0; optIdx < optOrder.Length; optIdx++)
        {
            var origIndex = optOrder[optIdx];
            if (origIndex < 0 || origIndex >= options.Count)
                continue;

            var opt = options[origIndex];
            var optItem = new DeploymentOrderItem(modItem.Guid, $"  ▸ {opt.Name}")
            {
                ItemType = DeploymentItemType.Option,
                ParentModGuid = modItem.Guid,
                OriginalIndex = origIndex,
            };
            Items.Insert(insertPos++, optItem);

            // 插入子选项
            if (opt.SubOptions is { Count: > 0 } subs)
            {
                // 获取自定义子选项顺序
                var subOrder = _settingsService.SubOptionOrders.TryGetValue(modItem.Guid, out var subOrderDict)
                    && subOrderDict.TryGetValue(origIndex, out var subArr)
                    ? subArr
                    : Enumerable.Range(0, subs.Count).ToArray();

                for (int subIdx = 0; subIdx < subOrder.Length; subIdx++)
                {
                    var subOrigIndex = subOrder[subIdx];
                    if (subOrigIndex < 0 || subOrigIndex >= subs.Count)
                        continue;

                    var sub = subs[subOrigIndex];
                    var subItem = new DeploymentOrderItem(modItem.Guid, $"    ▪ {sub.Name}")
                    {
                        ItemType = DeploymentItemType.SubOption,
                        ParentModGuid = modItem.Guid,
                        ParentOptionIndex = origIndex,
                        OriginalIndex = subOrigIndex,
                    };
                    Items.Insert(insertPos++, subItem);
                }
            }
        }

        modItem.IsExpanded = true;
    }

    /// <summary>
    /// 折叠模组：从 Items 中移除该模组下所有选项和子选项
    /// </summary>
    private void CollapseMod(DeploymentOrderItem modItem)
    {
        // 保存当前选项/子选项顺序到 SettingsService
        SaveOptionOrderForMod(modItem);

        var modIndex = Items.IndexOf(modItem);
        if (modIndex < 0)
            return;

        // 从模组后开始删除，直到遇到下一个 Mod 级别项或列表末尾
        int i = modIndex + 1;
        while (i < Items.Count && Items[i].ItemType != DeploymentItemType.Mod)
        {
            Items.RemoveAt(i);
        }

        modItem.IsExpanded = false;
    }

    /// <summary>
    /// 将当前展开的选项/子选项顺序保存到 SettingsService
    /// </summary>
    private void SaveOptionOrderForMod(DeploymentOrderItem modItem)
    {
        var modIndex = Items.IndexOf(modItem);
        if (modIndex < 0)
            return;

        // 收集该模组下的所有选项和子选项
        var optionItems = new List<DeploymentOrderItem>();
        var subOptionItems = new List<DeploymentOrderItem>();

        for (int i = modIndex + 1; i < Items.Count; i++)
        {
            var item = Items[i];
            if (item.ItemType == DeploymentItemType.Mod)
                break;
            if (item.ItemType == DeploymentItemType.Option)
                optionItems.Add(item);
            else if (item.ItemType == DeploymentItemType.SubOption)
                subOptionItems.Add(item);
        }

        // 保存选项顺序
        if (optionItems.Count > 0)
        {
            _settingsService.OptionOrders[modItem.Guid] = optionItems
                .Select(static o => o.OriginalIndex)
                .ToArray();
        }

        // 保存子选项顺序（按选项分组）
        if (subOptionItems.Count > 0)
        {
            var subDict = new Dictionary<int, int[]>();
            foreach (var sub in subOptionItems)
            {
                if (!subDict.ContainsKey(sub.ParentOptionIndex))
                {
                    var subsForOption = subOptionItems
                        .Where(s => s.ParentOptionIndex == sub.ParentOptionIndex)
                        .Select(static s => s.OriginalIndex)
                        .ToArray();
                    subDict[sub.ParentOptionIndex] = subsForOption;
                }
            }
            _settingsService.SubOptionOrders[modItem.Guid] = subDict;
        }
    }

    private void Items_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        SyncToSettings();
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
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
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
    }

    // ========== 按钮操作 ==========

    [RelayCommand]
    void MoveUp()
    {
        if (SelectedItem is null) return;
        var index = Items.IndexOf(SelectedItem);
        if (index <= 0) return;

        // 检查上一项是否在同一层级范围
        var prevIndex = GetPreviousSiblingIndex(index);
        if (prevIndex < 0) return;

        Items.Move(index, prevIndex);
    }

    [RelayCommand]
    void MoveDown()
    {
        if (SelectedItem is null) return;
        var index = Items.IndexOf(SelectedItem);
        if (index >= Items.Count - 1) return;

        // 检查下一项是否在同一层级范围
        var nextIndex = GetNextSiblingIndex(index);
        if (nextIndex < 0) return;

        Items.Move(index, nextIndex);
    }

    [RelayCommand]
    void MoveToTop()
    {
        if (SelectedItem is null) return;
        var index = Items.IndexOf(SelectedItem);
        if (index <= 0) return;

        var topIndex = GetTopOfLevel(index);
        if (topIndex < 0 || topIndex >= index) return;

        Items.Move(index, topIndex);
    }

    [RelayCommand]
    void MoveToBottom()
    {
        if (SelectedItem is null) return;
        var index = Items.IndexOf(SelectedItem);
        if (index >= Items.Count - 1) return;

        var bottomIndex = GetBottomOfLevel(index);
        if (bottomIndex < 0 || bottomIndex <= index) return;

        Items.Move(index, bottomIndex);
    }

    /// <summary>
    /// 获取同一层级中上一项的索引
    /// </summary>
    private int GetPreviousSiblingIndex(int currentIndex)
    {
        if (currentIndex <= 0) return -1;

        var currentItem = Items[currentIndex];
        for (int i = currentIndex - 1; i >= 0; i--)
        {
            if (IsSameLevel(Items[i], currentItem))
                return i;
            // 如果遇到更高级别的项则停止
            if (GetLevel(Items[i]) < GetLevel(currentItem))
                break;
        }
        return -1;
    }

    /// <summary>
    /// 获取同一层级中下一项的索引
    /// </summary>
    private int GetNextSiblingIndex(int currentIndex)
    {
        if (currentIndex >= Items.Count - 1) return -1;

        var currentItem = Items[currentIndex];
        for (int i = currentIndex + 1; i < Items.Count; i++)
        {
            if (IsSameLevel(Items[i], currentItem))
                return i;
            // 如果遇到更高级别的项则停止
            if (GetLevel(Items[i]) <= GetLevel(currentItem))
                break;
        }
        return -1;
    }

    /// <summary>
    /// 获取当前层级的最顶部索引
    /// </summary>
    private int GetTopOfLevel(int currentIndex)
    {
        if (currentIndex <= 0) return 0;

        var currentItem = Items[currentIndex];
        var currentLevel = GetLevel(currentItem);

        // 对于 Mod 层级，顶部就是 0
        if (currentLevel == 0) return 0;

        // 对于 Option 和 SubOption，需要找到所属 Mod 后的第一个同级项
        for (int i = currentIndex - 1; i >= 0; i--)
        {
            var level = GetLevel(Items[i]);
            if (level < currentLevel)
                return i + 1;
        }
        return 0;
    }

    /// <summary>
    /// 获取当前层级的最底部索引
    /// </summary>
    private int GetBottomOfLevel(int currentIndex)
    {
        if (currentIndex >= Items.Count - 1) return Items.Count - 1;

        var currentItem = Items[currentIndex];
        var currentLevel = GetLevel(currentItem);

        for (int i = currentIndex + 1; i < Items.Count; i++)
        {
            var level = GetLevel(Items[i]);
            if (level <= currentLevel)
                return i - 1;
        }
        return Items.Count - 1;
    }

    /// <summary>
    /// 判断两项是否在同一层级（类型相同且父级相同）
    /// </summary>
    private static bool IsSameLevel(DeploymentOrderItem a, DeploymentOrderItem b)
    {
        if (a.ItemType != b.ItemType) return false;
        return a.ItemType switch
        {
            DeploymentItemType.Mod => true,
            DeploymentItemType.Option => a.ParentModGuid == b.ParentModGuid,
            DeploymentItemType.SubOption => a.ParentModGuid == b.ParentModGuid && a.ParentOptionIndex == b.ParentOptionIndex,
            _ => false,
        };
    }

    /// <summary>
    /// 获取层级深度（Mod=0, Option=1, SubOption=2）
    /// </summary>
    private static int GetLevel(DeploymentOrderItem item)
    {
        return item.ItemType switch
        {
            DeploymentItemType.Mod => 0,
            DeploymentItemType.Option => 1,
            DeploymentItemType.SubOption => 2,
            _ => 0,
        };
    }

    /// <summary>
    /// 用当前所有已安装的模组（按 Dashboard 顺序）填充部署顺序列表
    /// </summary>
    [RelayCommand]
    void InitFromCurrent()
    {
        if (!_modService.Initialized || !_settingsService.Initialized) return;

        // 先折叠所有已展开的模组
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].ItemType == DeploymentItemType.Mod && Items[i].IsExpanded)
            {
                CollapseMod(Items[i]);
            }
        }

        var existingGuids = new HashSet<Guid>(Items.Where(i => i.ItemType == DeploymentItemType.Mod).Select(static i => i.Guid));
        var added = 0;

        var dashboardOrder = _profileService.GetCurrentOrder();
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
        // 折叠所有已展开的模组以保存选项顺序
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].ItemType == DeploymentItemType.Mod && Items[i].IsExpanded)
            {
                CollapseMod(Items[i]);
            }
        }
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

        // 获取所有选中项（含当前拖拽项），按原始位置排序
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Contains(sourceItem) && selected.Count > 1)
        {
            // 限制：只允许同层级的多选拖拽
            var sameLevel = selected.Where(i => IsSameLevel(i, sourceItem)).ToList();
            if (sameLevel.Count <= 1)
            {
                new DefaultDropHandler().Drop(dropInfo);
                return;
            }

            var sortedSelected = sameLevel.OrderBy(i => Items.IndexOf(i)).ToList();
            var targetIdx = dropInfo.InsertIndex;

            // 从集合中移除所有选中项（倒序删除以保持索引正确）
            foreach (var item in sortedSelected.AsEnumerable().Reverse())
                Items.Remove(item);

            // 如果目标索引位于删除区域之后，需修正插入位置
            var firstRemovedIdx = Items.IndexOf(sortedSelected[0]);
            if (firstRemovedIdx == -1)
            {
                var beforeCount = sortedSelected.Count(item => Items.IndexOf(item) < targetIdx);
                targetIdx -= beforeCount;
            }

            targetIdx = Math.Clamp(targetIdx, 0, Items.Count);

            // 按原始顺序插入
            for (int i = 0; i < sortedSelected.Count; i++)
                Items.Insert(targetIdx + i, sortedSelected[i]);
        }
        else
        {
            // 单项目拖拽 — 检查是否在同一层级内
            var dropIndex = dropInfo.InsertIndex;
            var targetItem = dropIndex < Items.Count ? Items[dropIndex] : null;

            if (targetItem is not null && !IsSameLevel(sourceItem, targetItem))
            {
                // 不允许跨层级拖拽，但允许拖到同层级的边界
                // 找到最近的同层级位置
                var adjustedIdx = FindClosestSameLevelIndex(sourceItem, dropIndex);
                if (adjustedIdx < 0)
                    return;

                // 手动执行移动
                var srcIdx = Items.IndexOf(sourceItem);
                if (srcIdx < 0) return;
                if (srcIdx == adjustedIdx) return;

                Items.Move(srcIdx, adjustedIdx);
                return;
            }

            new DefaultDropHandler().Drop(dropInfo);
        }
    }

    /// <summary>
    /// 查找离目标索引最近的同层级位置
    /// </summary>
    private int FindClosestSameLevelIndex(DeploymentOrderItem item, int targetIndex)
    {
        var itemLevel = GetLevel(item);

        // 扫描 targetIndex 附近的项，找到同层级项可插入的位置
        for (int i = targetIndex; i < Items.Count; i++)
        {
            if (GetLevel(Items[i]) == itemLevel && IsSameLevel(Items[i], item))
                return i;
            if (GetLevel(Items[i]) < itemLevel)
                break;
        }

        for (int i = targetIndex - 1; i >= 0; i--)
        {
            if (GetLevel(Items[i]) == itemLevel && IsSameLevel(Items[i], item))
                return i + 1;
            if (GetLevel(Items[i]) < itemLevel)
                return i + 1;
        }

        return -1;
    }
}
