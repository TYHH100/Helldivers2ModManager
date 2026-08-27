using System.IO;
using Helldivers2ModManager.Core.Analysis;
using Helldivers2ModManager.Core.GameData;
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
    TaskExecutionService tasks)
{
    public async Task<ArmorReuseScanResult> ScanEnabledAsync(
        IReadOnlyList<ModItem> mods,
        CancellationToken cancellationToken = default)
    {
        ArmorReuseScanResult? result = null;
        await tasks.RunAsync(
            "护甲复用扫描",
            "正在分析启用模组的护甲部件复用",
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
