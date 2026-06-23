using Helldivers2ModManager.Services;
using Helldivers2ModManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 排序方式枚举
/// </summary>
public enum SortMode
{
    Default,
    NameAsc,
    NameDesc,
    EnabledFirst,
    DisabledFirst
}

/// <summary>
/// 排序服务 —— 封装模组列表的排序逻辑
/// </summary>
[RegisterService(ServiceLifetime.Transient)]
internal sealed class SortService
{
    private readonly SettingsService _settingsService;

    public SortService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// 判断排序功能是否在设置中启用
    /// </summary>
    public bool IsSortingEnabled => _settingsService.Initialized && _settingsService.EnableSorting;

    /// <summary>
    /// 对模组列表应用排序
    /// </summary>
    public IEnumerable<ModViewModel> ApplySort(
        IEnumerable<ModViewModel> mods,
        SortMode sortMode)
    {
        if (!_settingsService.Initialized || !_settingsService.EnableSorting || sortMode == SortMode.Default)
            return mods;

        return sortMode switch
        {
            SortMode.NameAsc => mods.OrderBy(static vm => vm.Name),
            SortMode.NameDesc => mods.OrderByDescending(static vm => vm.Name),
            SortMode.EnabledFirst => mods.OrderByDescending(static vm => vm.Enabled),
            SortMode.DisabledFirst => mods.OrderBy(static vm => vm.Enabled),
            _ => mods
        };
    }

    /// <summary>
    /// 判断指定排序模式是否为活跃排序（非 Default）
    /// </summary>
    public bool IsActiveSort(SortMode sortMode) =>
        _settingsService.Initialized && _settingsService.EnableSorting && sortMode != SortMode.Default;
}
