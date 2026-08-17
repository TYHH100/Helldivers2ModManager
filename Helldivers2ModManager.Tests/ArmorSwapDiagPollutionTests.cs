using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// 污染护甲换甲问题诊断：B-01 有 4 个变体包（body + 头盔各 4），
/// 当前实现只替换用户选中的那一个 body 包 → 其余变体残留原版/污染。
/// </summary>
[TestClass]
public sealed class ArmorSwapDiagPollutionTests
{
    private static readonly string[] B01BodyPackages =
    [
        "58e4bd4b2278d15c", "562a45e9bc984eb9", "6cd3d55c05d4eac1", "519cc1ec2eb56e1d"
    ];

    [TestMethod]
    public async Task Diagnose_B01Variations()
    {
        var (service, inspection, versionCheck) = CreateServices();

        // 1. 4 个 B-01 body 包各自的 Unit 分布
        foreach (var packageId in B01BodyPackages)
        {
            var units = await versionCheck.ResolveGameArmorUnitsAsync([packageId]);
            Console.WriteLine($"=== B-01 body package {packageId}:");
            if (!units.TryGetValue(packageId, out var unitIds))
            {
                Console.WriteLine("  (not found)");
                continue;
            }
            foreach (var unitId in unitIds.OrderBy(static id => id))
            {
                var main = await versionCheck.ReadGameUnitMainDataAsync(unitId);
                var structure = main is null
                    ? null
                    : PatchResourceInspectionService.ParseArmorSwapUnitStructure(main, unitId);
                Console.WriteLine(structure is null
                    ? $"  0x{unitId:X16} (unparseable, main={main?.Length ?? -1}B)"
                    : $"  0x{unitId:X16} slot={structure.Slot} shape={structure.BodyShape} cust={structure.HasCustomizationInfo} main={structure.MainData.Length}B");
            }
        }

        // 2. 夏菲（B-01 污染源）覆盖的 FileId 归属哪些游戏包
        var xiafei = new ModData(
            new DirectoryInfo(Path.Combine(FindRepositoryRoot().FullName, "Test", "Mods", "Mods",
                "VRC_夏菲 替换 B-01系列_6cb08803")),
            ModManifest.DeserializeFromDirectory(new DirectoryInfo(Path.Combine(
                FindRepositoryRoot().FullName, "Test", "Mods", "Mods",
                "VRC_夏菲 替换 B-01系列_6cb08803"))));
        var xiafeiAnalysis = await service.AnalyzeSourceModAsync(xiafei);
        Console.WriteLine($"=== 夏菲 groups: {xiafeiAnalysis.Groups.Count}");
        foreach (var group in xiafeiAnalysis.Groups)
        {
            Console.WriteLine($"  group '{group.DisplayName}' ({group.ArmorId}): {group.Units.Count} units");
            foreach (var u in group.Units.OrderBy(static u => (ulong)u.FileId))
                Console.WriteLine($"    0x{u.FileId:X16} slot={u.Slot} shape={u.BodyShape} gpu={u.GpuSize}");
        }

        // 3. LoadTargetArmorAsync 选中任一 B-01 包时应聚合全部 4 个变体包 + 4 个头盔包
        var target = await service.LoadTargetArmorAsync(B01BodyPackages[0]);
        Console.WriteLine($"=== LoadTargetArmorAsync({B01BodyPackages[0]}): {target?.Units.Count ?? -1} units");
        Assert.IsNotNull(target);
        var packageIds = target.Units.Select(static u => u.PackageId).Distinct().OrderBy(static id => id).ToArray();
        Console.WriteLine($"=== packages in skeleton: {packageIds.Length}");
        foreach (var id in packageIds)
            Console.WriteLine($"  {id}: {target.Units.Count(u => u.PackageId == id)} units");
        // 4 个 body 变体包 + 4 个头盔包
        Assert.AreEqual(8, packageIds.Length, "B-01 骨架应聚合 4 body + 4 helmet 包");
        // 各变体独有 Unit 必须在骨架中
        Assert.IsTrue(target.Units.Any(static u => u.FileId == unchecked((long)0x970CEF3AE566058CUL)), "缺少 v2 独有 RightShoulder");
        Assert.IsTrue(target.Units.Any(static u => u.FileId == unchecked((long)0xD9B56CE4130B8C54UL)), "缺少 v3 独有 Torso");
        Assert.IsTrue(target.Units.Any(static u => u.FileId == unchecked((long)0xA810E3416CAFFCF6UL)), "缺少 v4 独有 Torso");

        // 4. 嘉然 → B-01(v1) 配对覆盖诊断
        var jaran = new ModData(
            new DirectoryInfo(Path.Combine(FindRepositoryRoot().FullName, "Test", "Mods", "Mods",
                "8 嘉然 生日礼服 替换 DP-00 “战术”民主中甲_3e2f99ef")),
            ModManifest.DeserializeFromDirectory(new DirectoryInfo(Path.Combine(
                FindRepositoryRoot().FullName, "Test", "Mods", "Mods",
                "8 嘉然 生日礼服 替换 DP-00 “战术”民主中甲_3e2f99ef"))));
        var jaranAnalysis = await service.AnalyzeSourceModAsync(jaran);
        Console.WriteLine($"=== 嘉然 directory views: {jaranAnalysis.DirectoryViews.Count}");
        foreach (var view in jaranAnalysis.DirectoryViews)
        {
            var legs = view.Units.Where(static u => u.Slot == ModelPreviewCustomizationSlot.LeftLeg).ToArray();
            Console.WriteLine($"  dir '{view.RelativeDirectory}': {view.Units.Count} units, LeftLeg={legs.Length}");
            foreach (var leg in legs)
                Console.WriteLine($"    0x{leg.FileId:X16} shape={leg.BodyShape} gpu={leg.GpuSize}");
        }
        var jaranGroup = jaranAnalysis.Groups.FirstOrDefault();
        Console.WriteLine($"=== 嘉然 groups: {jaranAnalysis.Groups.Count} first='{jaranGroup?.DisplayName}' units={jaranGroup?.Units.Count}");
        if (jaranGroup is not null)
        {
            foreach (var u in jaranGroup.Units.OrderBy(static u => (ulong)u.FileId))
                Console.WriteLine($"  src 0x{u.FileId:X16} slot={u.Slot} shape={u.BodyShape} gpu={u.GpuSize} cust={u.HasCustomizationInfo}");
        }
        if (jaranGroup is not null && target is not null)
        {
            var outDir = await service.GenerateArmorSwapModAsync(jaran, jaranAnalysis, jaranGroup, target);
            try
            {
                var patchPath = Path.Combine(outDir, "9ba626afa44a3aa3.patch_0");
                var entries = await inspection.ReadPatchEntriesAsync(new FileInfo(patchPath));
                var unitEntries = entries.Where(static e => e.TypeId == 0xE0A48D0BE9A7453FUL).ToArray();
                var unitIds = unitEntries.Select(static e => unchecked((long)e.FileId)).ToHashSet();
                Console.WriteLine($"=== 嘉然->B-01 output units: {unitIds.Count}");
                var uncovered = target.Units.Where(u => !unitIds.Contains(u.FileId)).ToArray();
                Console.WriteLine($"=== uncovered target units ({uncovered.Length}):");
                foreach (var u in uncovered.OrderBy(static u => (ulong)u.FileId))
                    Console.WriteLine($"  0x{u.FileId:X16} slot={u.Slot} shape={u.BodyShape} helmetPkg={u.IsFromHelmetPackage}");
                // 全变体全覆盖：游戏随机到任何 Variation 都不残留原版/污染内容
                Assert.AreEqual(0, uncovered.Length, "存在未覆盖的目标 Unit（原版/污染残留）");

                // 逐包层分配验证：v3 独有 Torso Stocky 应拿嘉然最大的 Torso Stocky
                // 层（0x2EC9ECBD91F29291, 6154680B 裙子），v2 独有 LeftLeg 应拿
                // 剩余的空气层（0xC475E062CA5B77A6, 15584B）而不是重复主层。
                var gpuSizes = unitEntries.ToDictionary(
                    static e => unchecked((long)e.FileId), static e => e.GpuSize);
                var v3Torso = unchecked((long)0xD9B56CE4130B8C54UL);
                var v4Torso = unchecked((long)0xA810E3416CAFFCF6UL);
                var v2LeftLeg = unchecked((long)0x40B23D9FAEB7F3DAUL);
                foreach (var (id, gpu) in gpuSizes.OrderBy(static p => (ulong)p.Key))
                {
                    var tu = target.Units.First(u => u.FileId == id);
                    if (tu.Slot is ModelPreviewCustomizationSlot.LeftLeg or ModelPreviewCustomizationSlot.Torso)
                        Console.WriteLine($"  map 0x{id:X16} pkg={tu.PackageId} slot={tu.Slot} shape={tu.BodyShape} gpu={gpu}");
                }
                Console.WriteLine($"  v3 Torso 0xD9B56CE4130B8C54 gpu={gpuSizes.GetValueOrDefault(v3Torso)}");
                Console.WriteLine($"  v4 Torso 0xA810E3416CAFFCF6 gpu={gpuSizes.GetValueOrDefault(v4Torso)}");
                Console.WriteLine($"  v2 LeftLeg 0x40B23D9FAEB7F3DA gpu={gpuSizes.GetValueOrDefault(v2LeftLeg)}");
                Assert.AreEqual(6154680u, gpuSizes[v3Torso], "v3 独有 Torso 应映射嘉然主裙层");
                Assert.AreEqual(6154680u, gpuSizes[v4Torso], "v4 独有 Torso 应映射嘉然主裙层");
                Assert.AreEqual(15584u, gpuSizes[v2LeftLeg], "v2 独有 LeftLeg 应映射剩余空气层");
            }
            finally
            {
                Directory.Delete(outDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Diagnose_SeresToB01_Coverage()
    {
        var (service, inspection, _) = CreateServices();
        var seres = new ModData(
            new DirectoryInfo(Path.Combine(FindRepositoryRoot().FullName, "Test", "Mods", "Mods",
                "瑟瑞斯-瞬刻斑斓水色替换SR-24+“街头侦察兵”+++修复_4bfa8e88")),
            ModManifest.DeserializeFromDirectory(new DirectoryInfo(Path.Combine(
                FindRepositoryRoot().FullName, "Test", "Mods", "Mods",
                "瑟瑞斯-瞬刻斑斓水色替换SR-24+“街头侦察兵”+++修复_4bfa8e88"))));
        var analysis = await service.AnalyzeSourceModAsync(seres);
        var group = analysis.Groups.FirstOrDefault();
        Console.WriteLine($"=== 瑟瑞斯 groups: {analysis.Groups.Count} first='{group?.DisplayName}' units={group?.Units.Count}");
        if (group is null)
            return;
        foreach (var u in group.Units.OrderBy(static u => u.Slot).ThenBy(static u => (ulong)u.FileId))
            Console.WriteLine($"  src 0x{u.FileId:X16} slot={u.Slot} shape={u.BodyShape} gpu={u.GpuSize}");

        var target = await service.LoadTargetArmorAsync(B01BodyPackages[0]);
        Assert.IsNotNull(target);
        await DumpCoverageAsync(service, inspection, seres, analysis, group, target, "瑟瑞斯->B-01");

        // 瑟瑞斯 -> CW-9（用户截图场景：原版肩甲/胸甲/臂甲残留）
        var cw9 = await service.LoadTargetArmorAsync("78c4f1839dea282d");
        Assert.IsNotNull(cw9);
        Console.WriteLine($"=== CW-9 skeleton: {cw9.Units.Count} units");
        foreach (var u in cw9.Units.OrderBy(static u => u.Slot).ThenBy(static u => (ulong)u.FileId))
            Console.WriteLine($"  tgt 0x{u.FileId:X16} pkg={u.PackageId} slot={u.Slot} shape={u.BodyShape} cust={u.HasCustomizationInfo} helmetPkg={u.IsFromHelmetPackage}");
        await DumpCoverageAsync(service, inspection, seres, analysis, group, cw9, "瑟瑞斯->CW-9");

        // 白银之城-侦探-CW9：用户指出其头盔是空气隐藏网格，校准空气尺寸阈值
        var baiyin = new ModData(
            new DirectoryInfo(Path.Combine(FindRepositoryRoot().FullName, "Test", "Mods", "Mods",
                "白银之城-侦探-CW9_f390e084")),
            ModManifest.DeserializeFromDirectory(new DirectoryInfo(Path.Combine(
                FindRepositoryRoot().FullName, "Test", "Mods", "Mods",
                "白银之城-侦探-CW9_f390e084"))));
        var baiyinAnalysis = await service.AnalyzeSourceModAsync(baiyin);
        Console.WriteLine($"=== 白银之城 groups: {baiyinAnalysis.Groups.Count}");
        foreach (var g in baiyinAnalysis.Groups)
        {
            Console.WriteLine($"  group '{g.DisplayName}': {g.Units.Count} units");
            foreach (var u in g.Units.OrderBy(static u => (ulong)u.FileId))
                Console.WriteLine($"    0x{u.FileId:X16} slot={u.Slot} shape={u.BodyShape} gpu={u.GpuSize} cust={u.HasCustomizationInfo}");
        }
    }

    private static async Task DumpCoverageAsync(
        ArmorSwapService service,
        PatchResourceInspectionService inspection,
        ModData sourceMod,
        ArmorSwapSourceAnalysis analysis,
        ArmorSwapSourceGroup group,
        ArmorSwapTargetArmor target,
        string label)
    {
        var outDir = await service.GenerateArmorSwapModAsync(sourceMod, analysis, group, target);
        try
        {
            var patchPath = Path.Combine(outDir, "9ba626afa44a3aa3.patch_0");
            var entries = await inspection.ReadPatchEntriesAsync(new FileInfo(patchPath));
            var unitIds = entries.Where(static e => e.TypeId == 0xE0A48D0BE9A7453FUL)
                .Select(static e => unchecked((long)e.FileId)).ToHashSet();
            Console.WriteLine($"=== {label} output units: {unitIds.Count} / target {target.Units.Count}");
            var uncovered = target.Units.Where(u => !unitIds.Contains(u.FileId)).ToArray();
            Console.WriteLine($"=== uncovered target units ({uncovered.Length}):");
            foreach (var u in uncovered.OrderBy(static u => u.Slot).ThenBy(static u => (ulong)u.FileId))
                Console.WriteLine($"  0x{u.FileId:X16} pkg={u.PackageId} slot={u.Slot} shape={u.BodyShape} helmetPkg={u.IsFromHelmetPackage}");
            Assert.AreEqual(0, uncovered.Length, $"{label} 存在未覆盖的目标 Unit（原版残留）");

            // 空气填充验证：瑟瑞斯->CW-9 的未分类肩甲/胸甲/臂甲附件应被空气网格覆盖
            if (label.Contains("CW-9", StringComparison.OrdinalIgnoreCase))
            {
                var gpuSizes = entries.ToDictionary(
                    static e => unchecked((long)e.FileId), static e => e.GpuSize);
                var uncBySlot = target.Units
                    .GroupBy(static u => u.Slot)
                    .ToDictionary(static g => g.Key, static g => g.Count());
                Console.WriteLine("=== CW-9 target slots:");
                foreach (var (slot, count) in uncBySlot.OrderBy(static p => p.Key))
                    Console.WriteLine($"  {slot}: {count}");
            }
        }
        finally
        {
            Directory.Delete(outDir, recursive: true);
        }
    }

    private static (ArmorSwapService Service, PatchResourceInspectionService Inspection, VersionCheckService VersionCheck) CreateServices()
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
        return (service, inspection, versionCheck);
    }

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
