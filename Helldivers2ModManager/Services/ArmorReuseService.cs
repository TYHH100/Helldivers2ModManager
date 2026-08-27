using Helldivers2ModManager.Adapters;
using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using System.IO;

namespace Helldivers2ModManager.Services;

/// <summary>
/// Facade over the Core armor reuse scan. It preserves the legacy result model and
/// supplies the user-configured game data directory while the UI migration is staged.
/// </summary>
internal sealed class ArmorReuseService
{
    private readonly Core.Analysis.ArmorReuseService _armorReuseService;
    private readonly SettingsService _settingsService;

    public ArmorReuseService(
        Core.Analysis.ArmorReuseService armorReuseService,
        SettingsService settingsService)
    {
        _armorReuseService = armorReuseService;
        _settingsService = settingsService;
    }

    public async Task<ArmorReuseAnalysisResult> AnalyzeAsync(
        IReadOnlyList<Models.ModData> mods,
        CancellationToken cancellationToken = default)
    {
        var analysisMods = mods
            .Select((mod, index) => ArmorReuseMapper.Map(mod, index))
            .ToArray();
        var coreResult = await _armorReuseService.AnalyzeAsync(
            analysisMods,
            GetGameDataDirectory(),
            cancellationToken);
        return ArmorReuseMapper.Map(coreResult);
    }

    private DirectoryInfo? GetGameDataDirectory()
    {
        if (!_settingsService.Initialized || string.IsNullOrWhiteSpace(_settingsService.GameDirectory))
            return null;

        return new DirectoryInfo(Path.Combine(_settingsService.GameDirectory, "data"));
    }
}
