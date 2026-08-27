using Helldivers2ModManager.Models;
using System.IO;

namespace Helldivers2ModManager.Adapters;

internal static class ArmorReuseMapper
{
    public static Core.Analysis.AnalysisMod Map(Models.ModData mod, int deploymentOrder)
    {
        var manifest = CoreManifestMapper.Map(mod.Manifest);
        return new(
            mod.Manifest.Guid,
            mod.Manifest.Name,
            mod.Enabled,
            deploymentOrder,
            new DirectoryInfo(mod.Directory.FullName),
            manifest,
            "",
            [.. mod.EnabledOptions],
            [.. mod.SelectedOptions]);
    }

    public static ArmorReuseAnalysisResult Map(Core.Analysis.ArmorReuseAnalysisResult result) => new()
    {
        ScannedModCount = result.ScannedModCount,
        ScannedPatchCount = result.ScannedPatchCount,
        ScannedUnitCount = result.ScannedUnitCount,
        Records = result.Records.Select(Map).ToArray(),
    };

    private static ArmorReuseRecord Map(Core.Analysis.ArmorReuseRecord record) => new()
    {
        ModGuid = record.ModId,
        ModName = record.ModName,
        SourceArmorId = record.SourceArmorId,
        SourceArmorName = record.SourceArmorName,
        ReusedBy = record.ReusedBy.Select(static target => new ArmorReuseTarget
        {
            ArmorId = target.ArmorId,
            ArmorName = target.ArmorName,
        }).ToArray(),
        SharedUnitCount = record.SharedUnitCount,
    };
}