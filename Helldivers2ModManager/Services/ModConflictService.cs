using System.IO;
using System.Security.Cryptography;
using System.Text;
using Helldivers2ModManager.Adapters;
using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Services;

/// <summary>
/// Legacy-result facade over the Core conflict scanner. The cache key intentionally
/// keeps its historical serialization so existing cached results remain valid during M10.
/// </summary>
internal sealed class ModConflictService
{
    private readonly Core.Analysis.ModConflictService _conflictService;
    private readonly SettingsService _settingsService;

    public ModConflictService(
        Core.Analysis.ModConflictService conflictService,
        SettingsService settingsService)
    {
        _conflictService = conflictService;
        _settingsService = settingsService;
    }

    public string BuildCacheKey(IReadOnlyList<ModData> deploymentMods)
    {
        var builder = new StringBuilder(deploymentMods.Count * 96 + 32);
        builder.Append("conflict-cache-v3|");

        for (var index = 0; index < deploymentMods.Count; index++)
        {
            var mod = deploymentMods[index];
            builder.Append(index).Append('|');
            builder.Append(mod.Manifest.Guid.ToString("N")).Append('|');
            builder.Append(mod.Manifest.Version).Append('|');
            builder.Append(mod.Directory.LastWriteTimeUtc.Ticks).Append('|');
            AppendBoolArray(builder, mod.EnabledOptions);
            builder.Append('|');
            AppendIntArray(builder, mod.SelectedOptions);
            builder.Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public async Task<ModConflictAnalysisResult> AnalyzeAsync(
        IReadOnlyList<ModData> deploymentMods,
        CancellationToken cancellationToken = default)
    {
        var analysisMods = deploymentMods
            .Select((mod, index) => ArmorReuseMapper.Map(mod, index))
            .ToArray();
        var result = await _conflictService.AnalyzeAsync(
            analysisMods,
            GetGameDataDirectory(),
            cancellationToken);
        return ConflictAnalysisMapper.Map(result);
    }

    private DirectoryInfo? GetGameDataDirectory()
    {
        if (!_settingsService.Initialized || string.IsNullOrWhiteSpace(_settingsService.GameDirectory))
            return null;

        return new DirectoryInfo(Path.Combine(_settingsService.GameDirectory, "data"));
    }

    private static void AppendBoolArray(StringBuilder builder, bool[] values)
    {
        builder.Append('[');
        foreach (var value in values)
            builder.Append(value ? '1' : '0').Append(',');
        builder.Append(']');
    }

    private static void AppendIntArray(StringBuilder builder, int[] values)
    {
        builder.Append('[');
        foreach (var value in values)
            builder.Append(value).Append(',');
        builder.Append(']');
    }
}
