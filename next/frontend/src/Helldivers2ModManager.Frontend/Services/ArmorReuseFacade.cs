using System.IO;
using Helldivers2ModManager.Core.Analysis;
using Helldivers2ModManager.Core.GameData;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Frontend.Models;

namespace Helldivers2ModManager.Frontend.Services;

public sealed record ArmorReuseScanResult(
    int ScannedModCount,
    int ScannedPatchCount,
    int ScannedUnitCount,
    IReadOnlyList<ArmorReuseRecord> Records);

public sealed class ArmorReuseFacade(
    ArmorReuseService service,
    VersionCheckFacade versionCheck,
    LocalizationCatalog localization,
    TaskExecutionService tasks)
{
    public async Task<ArmorReuseScanResult> ScanEnabledAsync(
        IReadOnlyList<ModItem> mods,
        CancellationToken cancellationToken = default)
    {
        ArmorReuseScanResult? result = null;
        await tasks.RunAsync(
            localization.GetString("Next.Tasks.ArmorReuseScan"),
            localization.GetString("Next.Tasks.ArmorReuseScanning"),
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
                    analysis.Records);
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
