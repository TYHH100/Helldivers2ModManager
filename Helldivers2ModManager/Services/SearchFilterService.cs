using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Services;

[RegisterService(ServiceLifetime.Transient)]
/// <summary>
/// 搜索和过滤服务 —— 负责模组列表的文本搜索、标签搜索和分组过滤
/// </summary>
internal sealed class SearchFilterService
{
    private readonly SettingsService _settingsService;

    public SearchFilterService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// 对模组列表应用搜索过滤（不含分组过滤，分组由调用方处理）
    /// </summary>
    public IEnumerable<ModViewModel> ApplySearchFilter(
        IEnumerable<ModViewModel> mods,
        string searchText)
    {
        if (string.IsNullOrEmpty(searchText) || !_settingsService.Initialized)
            return mods;

        var trimmed = searchText.Trim();
        if (trimmed.StartsWith("@"))
        {
            return ApplyTagSearch(mods, trimmed.Substring(1));
        }
        return ApplyNameSearch(mods, trimmed);
    }

    private IEnumerable<ModViewModel> ApplyTagSearch(IEnumerable<ModViewModel> mods, string tagName)
    {
        if (string.IsNullOrEmpty(tagName))
            return mods;

        // OrdinalIgnoreCase：模组名/标签通常为 ASCII 或中文（无大小写规则），
        // 序数比较远快于 InvariantCulture 的文化规则比较（搜索是防抖后的热路径）。
        return mods.Where(vm =>
            vm.Tags.Any(t => t.Name.Contains(tagName, StringComparison.OrdinalIgnoreCase)));
    }

    private IEnumerable<ModViewModel> ApplyNameSearch(IEnumerable<ModViewModel> mods, string searchText)
    {
        return mods.Where(vm =>
        {
            if (_settingsService.CaseSensitiveSearch)
                return vm.Name.Contains(searchText, StringComparison.Ordinal);
            return vm.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase);
        });
    }
}
