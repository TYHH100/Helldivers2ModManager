using System.IO;
using Helldivers2ModManager.Core.GameData;
using Helldivers2ModManager.Core.Localization;
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
    LocalizationCatalog localization,
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
            localization.GetString("Next.Tasks.VersionCheck"),
            string.Format(localization.GetString("Next.Tasks.VersionCheckingFormat"), mods.Count),
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

    private VersionCheckItem CreateItem(ModItem mod, ModVersionCheckResult? result)
    {
        if (result is null)
        {
            return new(mod.Id, mod.Name, ModVersionStatus.Unknown, 0, DateTimeOffset.Now, 0, 0, 0, 0, 0, localization.GetString("Next.VersionCheck.NotChecked"));
        }

        var analysis = result.DetailedAnalysis;
        var summary = result.Status switch
        {
            ModVersionStatus.Compatible => localization.GetString("Next.VersionCheck.Compatible"),
            ModVersionStatus.Incompatible => string.Format(
                localization.GetString("Next.VersionCheck.IncompatibleFormat"),
                result.UnitsMissingGameReference.Count),
            _ => localization.GetString("Next.VersionCheck.Undetermined"),
        };
        if (analysis is { HasBlockingStructuralIssues: true })
        {
            summary += string.Format(
                localization.GetString("Next.VersionCheck.StructureIssuesFormat"),
                analysis.CorruptedFileCount,
                analysis.WarningFileCount);
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
