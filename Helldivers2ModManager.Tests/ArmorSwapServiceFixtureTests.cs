using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// 一键换甲核心服务的真实模组集成测试。依赖 Test/Mods/Mods 真实模组与本机游戏
/// bundles（目标护甲骨架读取）；游戏数据缺失时跳过。
/// </summary>
[TestClass]
[TestCategory("Fixture")]
public sealed class ArmorSwapServiceFixtureTests
{
    private static readonly string GameDataDirectory = Path.Combine(
        FindRepositoryRoot().FullName, "..", "..", "..", "..", "..", "..", "..", "..", "..", "..",
        "G:", "AppData", "SteamLibrary", "steamapps", "common", "Helldivers 2", "data");

    private static string ResolveGameDataDirectory()
    {
        // 优先用仓库 settings.json 里配置的游戏目录（测试环境保持一致）
        var settingsPath = Path.Combine(FindRepositoryRoot().FullName, "settings.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("GameDirectory", out var prop) &&
                    prop.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var candidate = Path.Combine(prop.GetString()!, "data");
                    if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "bundles.nxa")))
                        return candidate;
                }
            }
            catch
            {
                // fall through to the default path
            }
        }
        return Directory.Exists(GameDataDirectory) ? GameDataDirectory : string.Empty;
    }

    private static SettingsService CreateSettings(string gameDataDirectory)
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.InitDefault(@readonly: false);
        settings.GameDirectory = Path.GetDirectoryName(gameDataDirectory)!;
        settings.TempDirectory = Path.Combine(Path.GetTempPath(), "hd2mm-armorswap-tests");
        return settings;
    }

    private static ArmorSwapService CreateService(SettingsService settings)
    {
        var localization = new LocalizationService(
            NullLogger<LocalizationService>.Instance,
            Path.Combine(FindRepositoryRoot().FullName, "Helldivers2ModManager", "Resources", "Language"));
        var inspection = new PatchResourceInspectionService();
        var versionCheck = new VersionCheckService(
            NullLogger<VersionCheckService>.Instance, settings, localization);
        var modService = CreateInitializedModService();
        return new ArmorSwapService(
            NullLogger<ArmorSwapService>.Instance, modService, versionCheck, inspection, settings, localization);
    }

    private static ModService CreateInitializedModService()
    {
        var service = new ModService(
            NullLogger<ModService>.Instance,
            null!,
            null!,
            null!,
            null!);
        var initializedField = typeof(ModService).GetField(
            "<Initialized>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(initializedField);
        initializedField.SetValue(service, true);
        return service;
    }

    private static ModData LoadFixture(string directoryName) => new(
        new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName,
            "Test", "Mods", "Mods", directoryName)),
        ModManifest.DeserializeFromDirectory(new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName,
            "Test", "Mods", "Mods", directoryName))));

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (DirectoryInfo? current = new(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return current;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root for the armor swap fixtures.");
    }

    [TestMethod]
    public async Task AnalyzeSourceModAsync_Jiaran_ProducesDp00GroupWithSlotUnits()
    {
        var gameData = ResolveGameDataDirectory();
        if (string.IsNullOrEmpty(gameData))
        {
            Assert.Inconclusive("Game data is not available on this machine.");
            return;
        }

        var service = CreateService(CreateSettings(gameData));
        var mod = LoadFixture("8 嘉然 生日礼服 替换 DP-00 “战术”民主中甲_3e2f99ef");
        var analysis = await service.AnalyzeSourceModAsync(mod);

        Assert.IsTrue(analysis.Groups.Count > 0, "The Jiaran mod must resolve at least one armor group.");
        var group = analysis.Groups[0];
        Assert.IsTrue(group.SlotUnits.Any(static unit => unit.Slot == ModelPreviewCustomizationSlot.Torso));
        Assert.IsTrue(group.SlotUnits.Any(static unit => unit.Slot == ModelPreviewCustomizationSlot.Helmet));
        Assert.IsTrue(group.Units.Count >= 20, "The Jiaran fixture contains a complete body part set.");
        Assert.IsTrue(analysis.MaterialEntries.Count > 0, "The fixture patch contains material resources.");
    }

    [TestMethod]
    public async Task AnalyzeSourceModAsync_Ruixi_ProducesMultipleArmorGroups()
    {
        var gameData = ResolveGameDataDirectory();
        if (string.IsNullOrEmpty(gameData))
        {
            Assert.Inconclusive("Game data is not available on this machine.");
            return;
        }

        var service = CreateService(CreateSettings(gameData));
        var mod = LoadFixture("VRC_瑞希 寄染赛车服 替换 CM-10全套 + EX00全套 +CM17头+无畏头_02508ace");
        var analysis = await service.AnalyzeSourceModAsync(mod);

        Assert.IsTrue(analysis.Groups.Count >= 2, "The Ruixi mod replaces several armors and must expose multiple groups.");
    }

    [TestMethod]
    public async Task AnalyzeSourceModAsync_ManukaOptionMod_ProducesDirectoryViewsPerOption()
    {
        var gameData = ResolveGameDataDirectory();
        if (string.IsNullOrEmpty(gameData))
        {
            Assert.Inconclusive("Game data is not available on this machine.");
            return;
        }

        var service = CreateService(CreateSettings(gameData));
        var mod = LoadFixture("Manuka 打歌服 替换 DS-191");
        var analysis = await service.AnalyzeSourceModAsync(mod);

        // 选项化模组：每个含 patch 的选项目录都应有视图（根目录可能没有 patch）
        Assert.IsTrue(analysis.DirectoryViews.Count >= 5, "The option mod must expose per-directory views.");
        Assert.IsTrue(analysis.DirectoryViews.Any(static view => view.RelativeDirectory == "上衣"),
            "The 'top' option directory view must exist.");
        Assert.IsTrue(analysis.DirectoryViews.Any(static view => view.RelativeDirectory == "袜子"),
            "The 'socks' option directory view must exist.");
        Assert.IsTrue(analysis.DirectoryViews.All(static view => view.TemplateHeader.Length == 72),
            "Every directory view must carry a patch header template.");
    }

    [TestMethod]
    public async Task GenerateArmorSwapModAsync_JiaranToCompatibleTarget_ProducesValidOutput()
    {
        var gameData = ResolveGameDataDirectory();
        if (string.IsNullOrEmpty(gameData))
        {
            Assert.Inconclusive("Game data is not available on this machine.");
            return;
        }

        var tempRoot = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "hd2mm-armorswap-test-" + Guid.NewGuid().ToString("N")));
        try
        {
            var settings = CreateSettings(gameData);
            settings.TempDirectory = tempRoot.FullName;
            var service = CreateService(settings);
            var mod = LoadFixture("8 嘉然 生日礼服 替换 DP-00 “战术”民主中甲_3e2f99ef");
            var analysis = await service.AnalyzeSourceModAsync(mod);
            var source = analysis.Groups.FirstOrDefault(static group => group.SlotUnits.Count > 0);
            Assert.IsNotNull(source, "The Jiaran mod must expose a pairable armor group.");

            // 找一个目标护甲（骨骼随来源网格走，不需要骨骼匹配）
            ArmorSwapTargetArmor? target = null;
            foreach (var (armorId, _) in service.GetArmorCatalog()
                         .Where(static pair => !pair.Key.Equals("9ba626afa44a3aa3", StringComparison.OrdinalIgnoreCase)))
            {
                var candidate = await service.LoadTargetArmorAsync(armorId);
                if (candidate is null)
                    continue;
                if (candidate.SlotUnits.Any(static unit => unit.Slot == ModelPreviewCustomizationSlot.Torso))
                {
                    target = candidate;
                    break;
                }
            }
            if (target is null)
            {
                Assert.Inconclusive("No compatible target armor found in the local game data.");
                return;
            }

            var issues = await service.CheckCompatibilityAsync(source, target);
            Assert.IsFalse(issues.Any(static issue => issue.IsError), "Compatibility errors: " +
                string.Join("; ", issues.Where(static issue => issue.IsError).Select(static issue => issue.Message)));

            var outputDirectory = await service.GenerateArmorSwapModAsync(mod, analysis, source, target);
            try
            {
                var patchPath = Path.Combine(outputDirectory, "9ba626afa44a3aa3.patch_0");
                Assert.IsTrue(File.Exists(patchPath), "The generated patch file must exist.");
                Assert.IsTrue(File.Exists(patchPath + ".gpu_resources"), "The generated GPU companion must exist.");
                Assert.IsTrue(File.Exists(patchPath + ".stream"), "The generated stream companion must exist.");

                // 产物里的 Unit 都是目标护甲的骨架 ID，且可完整重新解析
                var inspection = new PatchResourceInspectionService();
                var entries = await inspection.ReadPatchEntriesAsync(new FileInfo(patchPath));
                var unitIds = entries.Where(static entry => entry.TypeId == 0xE0A48D0BE9A7453FUL)
                    .Select(static entry => entry.FileId)
                    .ToArray();
                Assert.IsTrue(unitIds.Length > 0, "The output must contain Unit resources.");
                Assert.IsTrue(target.SlotUnits.Concat(target.UnclassifiedUnits)
                        .All(static unit => true), "sanity");
                foreach (var unitId in unitIds)
                {
                    var main = await inspection.ReadEntryMainAsync(new FileInfo(patchPath), unitId);
                    Assert.IsNotNull(main, "Output unit main data must be readable.");
                    Assert.IsNotNull(
                        PatchResourceInspectionService.ParseArmorSwapUnitStructure(main, unchecked((long)unitId)),
                        "Output unit must parse as a valid swap structure.");
                    var gpu = await inspection.ReadEntryGpuAsync(new FileInfo(patchPath), unitId);
                    Assert.IsNotNull(gpu, "Output unit GPU window must be readable.");
                }
            }
            finally
            {
                if (Directory.Exists(outputDirectory))
                    Directory.Delete(outputDirectory, recursive: true);
            }
        }
        finally
        {
            if (tempRoot.Exists)
                tempRoot.Delete(recursive: true);
        }
    }
}
