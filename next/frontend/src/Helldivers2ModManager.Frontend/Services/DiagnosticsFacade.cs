using System.IO;
using Helldivers2ModManager.Core.GameData;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Repair;
using Helldivers2ModManager.Core.Versioning;
using Helldivers2ModManager.Frontend.Models;

namespace Helldivers2ModManager.Frontend.Services;

public sealed record DiagnosticsStatus(
    int ModCount,
    int EnabledCount,
    string StorageDirectory,
    string GameDirectory,
    bool GameDataAvailable,
    bool UseSymbolicLinks);

public sealed record RepairPlanItem(
    Guid ModId,
    string ModName,
    BatchRepairState State,
    string StateText,
    string Message,
    int MetadataActionCount,
    int AssistedActionCount,
    int CompanionRecoveryCount,
    BatchRepairItem Source);

public sealed class DiagnosticsFacade(
    ModLibraryService library,
    ApplicationSettingsService settings,
    PatchStructureAnalyzer analyzer,
    GameArchiveService gameArchive,
    VersionCheckFacade versionCheck,
    ConflictAnalysisFacade conflicts,
    ArmorReuseFacade armorReuse,
    LocalizationCatalog localization,
    TaskExecutionService tasks)
{
    private readonly Func<DirectoryInfo?> _gameDataDirectoryProvider = () =>
    {
        if (string.IsNullOrWhiteSpace(settings.Current.GameDirectory))
        {
            return null;
        }

        var directory = new DirectoryInfo(Path.Combine(settings.Current.GameDirectory, "data"));
        return directory.Exists ? directory : null;
    };

    public async Task<DiagnosticsStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await library.LoadAsync(cancellationToken).ConfigureAwait(false);
        var gameData = _gameDataDirectoryProvider();
        return new(
            result.Mods.Count,
            result.Mods.Count(mod => mod.IsEnabled),
            settings.Current.StorageDirectory,
            settings.Current.GameDirectory,
            gameData is not null && File.Exists(Path.Combine(gameData.FullName, "bundles.nxa")),
            settings.Current.UseSymbolicLinks);
    }

    public Task<IReadOnlyList<VersionCheckItem>> CheckVersionsAsync(IReadOnlyList<ModItem> mods, CancellationToken token = default) =>
        versionCheck.CheckAllAsync(mods, token);

    public Task<ConflictScanResult> ScanConflictsAsync(IReadOnlyList<ModItem> mods, CancellationToken token = default) =>
        conflicts.ScanEnabledAsync(mods, token);

    public Task<ArmorReuseScanResult> ScanArmorReuseAsync(IReadOnlyList<ModItem> mods, CancellationToken token = default) =>
        armorReuse.ScanEnabledAsync(mods, token);

    public async Task<IReadOnlyList<RepairPlanItem>> CreateRepairPlanAsync(CancellationToken cancellationToken = default)
    {
        var result = await library.LoadAsync(cancellationToken).ConfigureAwait(false);
        var metadataRepair = new MetadataRepairService(analyzer);
        var assistedRepair = new AssistedRepairService(metadataRepair, analyzer, gameArchive, _gameDataDirectoryProvider);
        var companionRecovery = _gameDataDirectoryProvider() is null ? null : new CompanionRecoveryService(gameArchive, analyzer);
        var batchRepair = new BatchRepairService(
            metadataRepair,
            assistedRepair,
            analyzer,
            companionRecovery,
            _gameDataDirectoryProvider);
        IReadOnlyList<BatchRepairItem>? plannedItems = null;
        await tasks.RunAsync(
            localization.GetString("Next.Tasks.RepairPlan"),
            string.Format(localization.GetString("Next.Tasks.AnalyzeModsFormat"), result.Mods.Count),
            async (_, token) =>
            {
                plannedItems = await batchRepair.CreatePlanAsync(
                    [.. result.Mods.Select(mod => (mod.Id, mod.Directory))],
                    token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        return [.. plannedItems!.Select(item =>
        {
            var name = result.Mods.FirstOrDefault(mod => mod.Id == item.ModId)?.Name ?? item.ModId.ToString("D");
            return new RepairPlanItem(
                item.ModId,
                name,
                item.State,
                item.State.ToString(),
                item.Message,
                item.MetadataActionCount,
                item.AssistedActionCount,
                item.CompanionRecoveryCount,
                item);
        })];
    }

    public async Task<IReadOnlyList<BatchRepairItem>> ExecuteRepairsAsync(
        IReadOnlyList<BatchRepairItem> items,
        CancellationToken cancellationToken = default)
    {
        var metadataRepair = new MetadataRepairService(analyzer);
        var assistedRepair = new AssistedRepairService(metadataRepair, analyzer, gameArchive, _gameDataDirectoryProvider);
        var companionRecovery = _gameDataDirectoryProvider() is null ? null : new CompanionRecoveryService(gameArchive, analyzer);
        var batchRepair = new BatchRepairService(
            metadataRepair,
            assistedRepair,
            analyzer,
            companionRecovery,
            _gameDataDirectoryProvider);
        IReadOnlyList<BatchRepairItem>? repaired = null;
        await tasks.RunAsync(
            localization.GetString("Next.Tasks.RepairExecute"),
            string.Format(localization.GetString("Next.Tasks.RepairingFormat"), items.Count),
            async (_, token) => repaired = (await batchRepair.RepairAsync(items, token).ConfigureAwait(false)).Items,
            cancellationToken).ConfigureAwait(false);
        return repaired!;
    }
}
