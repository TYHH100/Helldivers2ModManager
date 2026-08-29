using System.IO;
using Helldivers2ModManager.Core.Analysis;
using Helldivers2ModManager.Core.GameData;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Versioning;
using Helldivers2ModManager.Frontend.Models;

namespace Helldivers2ModManager.Frontend.Services;

public sealed record ConflictScanResult(
    int ScannedModCount,
    int ScannedPatchCount,
    int ScannedUnitCount,
    int DefiniteConflictCount,
    IReadOnlyList<ConflictDisplayItem> Conflicts);

public sealed record ConflictDisplayItem(
    long UnitId,
    string FriendlyName,
    bool IsDefinite,
    string Winner,
    IReadOnlyList<string> Participants);

public sealed class ConflictAnalysisFacade(
    PatchStructureAnalyzer analyzer,
    GameArchiveService gameArchive,
    VersionCheckFacade versionCheck,
    LocalizationCatalog localization,
    TaskExecutionService tasks)
{
    public async Task<ConflictScanResult> ScanEnabledAsync(
        IReadOnlyList<ModItem> mods,
        CancellationToken cancellationToken = default)
    {
        var service = new ModConflictService(analyzer, gameArchive);
        ConflictScanResult? result = null;
        await tasks.RunAsync(
            localization.GetString("Next.Tasks.ConflictScan"),
            localization.GetString("Next.Tasks.ConflictScanning"),
            async (_, token) =>
            {
                var enabled = mods.Where(mod => mod.IsEnabled)
                    .OrderBy(mod => mod.SortOrder)
                    .Select((mod, index) => new AnalysisMod(
                        mod.Id,
                        mod.Name,
                        true,
                        index,
                        mod.Directory,
                        mod.Source.Manifest,
                        "",
                        mod.EnabledOptions,
                        mod.SelectedOptions))
                    .ToArray();
                var analysis = await service.AnalyzeAsync(enabled, versionCheck.ResolveGameDataDirectory(), token)
                    .ConfigureAwait(false);
                result = new(
                    analysis.ScannedModCount,
                    analysis.ScannedPatchCount,
                    analysis.ScannedUnitCount,
                    analysis.DefiniteConflictCount,
                    [.. analysis.Conflicts.Select(CreateDisplayItem)]);
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    private static ConflictDisplayItem CreateDisplayItem(ConflictRecord record)
    {
        var winner = record.Winner;
        return new(
            record.UnitId,
            string.IsNullOrWhiteSpace(record.FriendlyName) ? record.OriginalName : record.FriendlyName,
            record.IsDefiniteConflict,
            $"{winner.ModName} → {winner.PatchFileName}",
            [.. record.Participants.Select(participant =>
                $"{participant.ModName}（{participant.PatchFileName}, v{participant.Version}）")]);
    }
}
