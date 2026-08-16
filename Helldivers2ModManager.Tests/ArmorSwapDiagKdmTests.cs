using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ArmorSwapDiagKdmTests
{
    [TestMethod]
    public async Task Diagnose_Kdm500Skeleton()
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.InitDefault(@readonly: false);
        settings.GameDirectory = @"G:\AppData\SteamLibrary\steamapps\common\Helldivers 2";
        settings.TempDirectory = Path.Combine(Path.GetTempPath(), "hd2mm-diag");
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "Helldivers2ModManager", "Resources", "Language"));
        var inspection = new PatchResourceInspectionService();
        var versionCheck = new VersionCheckService(NullLogger<VersionCheckService>.Instance, settings, localization);
        var modService = new ModService(NullLogger<ModService>.Instance, null!, null!, null!, null!);
        var initializedField = typeof(ModService).GetField(
            "<Initialized>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        initializedField!.SetValue(modService, true);
        var service = new ArmorSwapService(
            NullLogger<ArmorSwapService>.Instance, modService, versionCheck, inspection, settings, localization);

        // 对比几个护甲的槽位分布
        foreach (var (armorId, label) in new[]
                 {
                     ("476488a5861d15e0", "KDM-500"),
                     ("78c4f1839dea282d", "CW-9 White Wolf"),
                     ("25fa0f1d426ad6bd", "AC-1 Dutiful"),
                 })
        {
            var target = await service.LoadTargetArmorAsync(armorId);
            Console.WriteLine($"=== {label} ({armorId}): {target?.Units.Count ?? -1} units");
            if (target is not null)
            {
                foreach (var group in target.Units.GroupBy(static u => u.Slot).OrderBy(static g => g.Key))
                {
                    var shapes = string.Join(",", group.Select(static u => u.BodyShape.ToString()).Distinct());
                    Console.WriteLine($"  {group.Key}: {group.Count()} [{shapes}]");
                }
            }
        }

        // milltina -> KDM-500 全局配对验证（Torso 多层）
        var milltina = LoadMilltinaMod();
        var analysis = await service.AnalyzeSourceModAsync(milltina);
        var sourceGroup = analysis.Groups.FirstOrDefault(g => g.SlotUnits.Any(static u => u.Slot == ModelPreviewCustomizationSlot.Torso));
        Console.WriteLine($"=== milltina source group: {sourceGroup?.DisplayName} units={sourceGroup?.Units.Count}");
        if (sourceGroup is not null)
        {
            foreach (var u in sourceGroup.Units.Where(static u => u.Slot == ModelPreviewCustomizationSlot.Torso).OrderBy(static u => (ulong)u.FileId))
                Console.WriteLine($"  Torso 0x{u.FileId:X16} shape={u.BodyShape} gpu={u.GpuSize} patch={Path.GetFileName(Path.GetDirectoryName(u.SourcePatchPath))}");
            var kdm = await service.LoadTargetArmorAsync("476488a5861d15e0");
            Console.WriteLine($"=== KDM-500 Torso targets:");
            foreach (var t in kdm!.SlotUnits.Where(static u => u.Slot == ModelPreviewCustomizationSlot.Torso).OrderBy(static u => (ulong)u.FileId))
                Console.WriteLine($"  Torso 0x{t.FileId:X16} shape={t.BodyShape}");

            // 修女 -> AC-1 生成验证（链珠目录应生成）
            settings.TempDirectory = Path.Combine(Path.GetTempPath(), "hd2mm-diag-out");
            var nun = new ModData(
                new DirectoryInfo(Path.Combine(FindRepositoryRoot().FullName, "Test", "Mods", "Mods", "milltina 修女 替换tg-122")),
                ModManifest.DeserializeFromDirectory(new DirectoryInfo(Path.Combine(
                    FindRepositoryRoot().FullName, "Test", "Mods", "Mods", "milltina 修女 替换tg-122"))));
            var nunAnalysis = await service.AnalyzeSourceModAsync(nun);
            var nunGroup = nunAnalysis.Groups.FirstOrDefault();
            var ac1 = await service.LoadTargetArmorAsync("25fa0f1d426ad6bd");
            Console.WriteLine($"=== nun groups: {nunAnalysis.Groups.Count} first={nunGroup?.DisplayName} units={nunGroup?.Units.Count}");
            if (nunGroup is not null && ac1 is not null)
            {
                var issues = await service.CheckCompatibilityAsync(nunGroup, ac1);
                Console.WriteLine($"=== nun->AC-1 issues: {issues.Count} errors={issues.Count(static i => i.IsError)}");
                var outDir = await service.GenerateArmorSwapModAsync(nun, nunAnalysis, nunGroup, ac1);
                try
                {
                    var dirs = Directory.GetDirectories(outDir).Select(Path.GetFileName).OrderBy(static n => n).ToArray();
                    Console.WriteLine($"=== nun->AC-1 output dirs: {string.Join(", ", dirs)}");
                }
                finally
                {
                    Directory.Delete(outDir, recursive: true);
                }
            }

            // 瑟瑞斯 -> CW-9 生成验证（CW-9 头盔 FileId 0x6D5011A2D2D35EB8 应出现在产物）
            var seres = new ModData(
                new DirectoryInfo(Path.Combine(FindRepositoryRoot().FullName, "Test", "Mods", "Mods",
                    "瑟瑞斯-瞬刻斑斓水色替换SR-24+\u201C街头侦察兵\u201D+++\u4FEE\u590D_4bfa8e88")),
                ModManifest.DeserializeFromDirectory(new DirectoryInfo(Path.Combine(
                    FindRepositoryRoot().FullName, "Test", "Mods", "Mods",
                    "瑟瑞斯-瞬刻斑斓水色替换SR-24+\u201C街头侦察兵\u201D+++\u4FEE\u590D_4bfa8e88"))));
            var seresAnalysis = await service.AnalyzeSourceModAsync(seres);
            var seresGroup = seresAnalysis.Groups.FirstOrDefault();
            var cw9 = await service.LoadTargetArmorAsync("78c4f1839dea282d");
            Console.WriteLine($"=== seres groups: {seresAnalysis.Groups.Count} first={seresGroup?.DisplayName} units={seresGroup?.Units.Count} cw9 units={cw9?.Units.Count} cw9 helmets={cw9?.HelmetUnits.Count}");
            if (seresGroup is not null && cw9 is not null)
            {
                var outDir2 = await service.GenerateArmorSwapModAsync(seres, seresAnalysis, seresGroup, cw9);
                try
                {
                    var patchPath = Path.Combine(outDir2, "9ba626afa44a3aa3.patch_0");
                    var entries = await inspection.ReadPatchEntriesAsync(new FileInfo(patchPath));
                    var unitIds = entries.Where(static e => e.TypeId == 0xE0A48D0BE9A7453FUL).Select(static e => e.FileId).ToArray();
                    Console.WriteLine($"=== seres->CW-9 units: {unitIds.Length} hasCw9Helmet={unitIds.Contains(0x6D5011A2D2D35EB8UL)}");
                    foreach (var id in unitIds.OrderBy(static id => id))
                        Console.WriteLine($"   0x{id:X16}");
                }
                finally
                {
                    Directory.Delete(outDir2, recursive: true);
                }
            }
        }
    }

    private static ModData LoadMilltinaMod() => new(
        new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName, "Test", "Mods", "Mods", "milltina 旗袍替换tg-8")),
        ModManifest.DeserializeFromDirectory(new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName, "Test", "Mods", "Mods", "milltina 旗袍替换tg-8"))));

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (DirectoryInfo? current = new(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return current;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
