using System.IO;
using Helldivers2ModManager.Core.GameData;
using Helldivers2ModManager.Core.Versioning;
using Helldivers2ModManager.Frontend.Models;

namespace Helldivers2ModManager.Frontend.Services;

public sealed record VersionCheckItem(
    Guid ModId,
    string ModName,
    ModVersionStatus Status,
    uint GameVersion,
    DateTimeOffset LastChecked,
    int UnitCount,
    int HealthyFiles,
    int WarningFiles,
    int CorruptedFiles,
    int MissingReferenceCount,
    string Summary);

public sealed class VersionCheckFacade(
    PatchStructureAnalyzer analyzer,
    GameArchiveService gameArchive,
    ApplicationSettingsService settings,
    TaskExecutionService tasks)
{
    public VersionCheckService CreateService() => new(
        analyzer,
        ResolveGameDataDirectory,
        gameArchive);

    public async Task<IReadOnlyList<VersionCheckItem>> CheckAllAsync(
        IReadOnlyList<ModItem> mods,
        CancellationToken cancellationToken = default)
    {
        var service = CreateService();
        IReadOnlyList<VersionCheckItem>? result = null;
        await tasks.RunAsync(
            "版本检查",
            $"正在检查 {mods.Count} 个模组",
            async (_, token) =>
            {
                var inputs = mods.Select(mod => new DiscoveredModInput(
                    mod.Id,
                    mod.Name,
                    mod.Directory)).ToArray();
                var results = await service.CheckAllModsAsync(inputs, token).ConfigureAwait(false);
                result = [.. mods.Select(mod => CreateItem(mod, results.GetValueOrDefault(mod.Id)))];
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    public DirectoryInfo? ResolveGameDataDirectory()
    {
        var gameDirectory = settings.Current.GameDirectory;
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return null;
        }

        var dataDirectory = new DirectoryInfo(Path.Combine(gameDirectory, "data"));
        return dataDirectory.Exists ? dataDirectory : null;
    }

    private static VersionCheckItem CreateItem(ModItem mod, ModVersionCheckResult? result)
    {
        if (result is null)
        {
            return new(mod.Id, mod.Name, ModVersionStatus.Unknown, 0, DateTimeOffset.Now, 0, 0, 0, 0, 0, "未检查");
        }

        var analysis = result.DetailedAnalysis;
        var summary = result.Status switch
        {
            ModVersionStatus.Compatible => "版本兼容",
            ModVersionStatus.Incompatible => $"版本不兼容；{result.UnitsMissingGameReference.Count} 个 Unit 缺少游戏引用",
            _ => "无法判定版本",
        };
        if (analysis is { HasBlockingStructuralIssues: true })
        {
            summary += $"；结构问题：{analysis.CorruptedFileCount} 损坏 / {analysis.WarningFileCount} 警告";
        }

        return new(
            mod.Id,
            mod.Name,
            result.Status,
            result.GameVersion,
            result.LastChecked,
            result.Units.Count,
            analysis?.HealthyFileCount ?? 0,
            analysis?.WarningFileCount ?? 0,
            analysis?.CorruptedFileCount ?? 0,
            result.UnitsMissingGameReference.Count,
            summary);
    }
}
