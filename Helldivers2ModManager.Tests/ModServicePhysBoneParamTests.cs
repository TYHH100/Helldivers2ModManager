using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// HD2PhysBone 参数集识别与参数目录名清洗测试。
/// </summary>
[TestClass]
public sealed class ModServicePhysBoneParamTests
{
    private const string FallbackName = "测试模组";

    private string _tempRoot = string.Empty;

    [TestInitialize]
    public void TestInitialize()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "physboneparam_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }

    [TestMethod]
    public void Sanitize_KeepsNormalName()
    {
        Assert.AreEqual("白银之城-侦探-CW9", ModService.SanitizePhysBoneDirName(" 白银之城-侦探-CW9 "));
    }

    [TestMethod]
    public void Sanitize_ReplacesInvalidCharsAndTrailingDots()
    {
        // 非法字符替换为 _；仅结尾的点/空格被裁剪（结尾的 _ 来自替换，保留）
        Assert.AreEqual("my_mod_v2_", ModService.SanitizePhysBoneDirName("my:mod<v2>.."));
    }

    [TestMethod]
    public void Sanitize_RejectsEmptyAndReservedPrefix()
    {
        Assert.AreEqual(string.Empty, ModService.SanitizePhysBoneDirName("   "));
        Assert.AreEqual(string.Empty, ModService.SanitizePhysBoneDirName("///"));
        // add-on 跳过 _/. 开头的目录
        Assert.AreEqual(string.Empty, ModService.SanitizePhysBoneDirName("_hidden"));
        Assert.AreEqual(string.Empty, ModService.SanitizePhysBoneDirName(".hidden"));
    }

    [TestMethod]
    public void Detect_FindsCompleteParamSet_WithNameFromLocalModJson()
    {
        var modDir = CreateModDir();
        var paramDir = Path.Combine(modDir, "HD2PhysBone", "KitName");
        WriteParamSet(paramDir);
        WriteModJson(paramDir, "KitName");

        var sets = ModService.DetectPhysBoneParamSets(new DirectoryInfo(modDir), FallbackName, "guidvalue");

        Assert.AreEqual(1, sets.Count);
        Assert.AreEqual("KitName", sets[0].DirName);
        Assert.AreEqual(paramDir, sets[0].ParamDir.FullName);
    }

    [TestMethod]
    public void Detect_FallsBackToRootModJson_ThenManifestName_ThenGuid()
    {
        // 1. 参数子目录无 mod.json、模组根目录有 → 用根目录的 name
        var modDir1 = CreateModDir();
        WriteParamSet(Path.Combine(modDir1, "params"));
        WriteModJson(modDir1, "RootJsonName");
        var sets1 = ModService.DetectPhysBoneParamSets(new DirectoryInfo(modDir1), FallbackName, "guidvalue");
        Assert.AreEqual("RootJsonName", sets1[0].DirName);

        // 2. 无任何 mod.json → 回退清单名称
        var modDir2 = CreateModDir();
        WriteParamSet(Path.Combine(modDir2, "params"));
        var sets2 = ModService.DetectPhysBoneParamSets(new DirectoryInfo(modDir2), FallbackName, "guidvalue");
        Assert.AreEqual(FallbackName, sets2[0].DirName);

        // 3. 清单名称也是保留前缀 → 回退 GUID
        var modDir3 = CreateModDir();
        WriteParamSet(Path.Combine(modDir3, "params"));
        var sets3 = ModService.DetectPhysBoneParamSets(new DirectoryInfo(modDir3), "_bad", "guidvalue");
        Assert.AreEqual("guidvalue", sets3[0].DirName);
    }

    [TestMethod]
    public void Detect_IgnoresIncompleteParamSets()
    {
        var modDir = CreateModDir();
        // 只有 rig，缺 ib_needle / lua_units → 不是参数集
        var dir = Path.Combine(modDir, "params");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hd2_spring_rig.bin"), "x");

        var sets = ModService.DetectPhysBoneParamSets(new DirectoryInfo(modDir), FallbackName, "guidvalue");

        Assert.AreEqual(0, sets.Count);
    }

    [TestMethod]
    public void Detect_FindsMultipleIndependentSets()
    {
        var modDir = CreateModDir();
        WriteParamSet(Path.Combine(modDir, "HD2PhysBone", "setA"));
        WriteModJson(Path.Combine(modDir, "HD2PhysBone", "setA"), "setA");
        WriteParamSet(Path.Combine(modDir, "HD2PhysBone", "setB"));
        WriteModJson(Path.Combine(modDir, "HD2PhysBone", "setB"), "setB");

        var sets = ModService.DetectPhysBoneParamSets(new DirectoryInfo(modDir), FallbackName, "guidvalue");

        Assert.AreEqual(2, sets.Count);
        CollectionAssert.AreEquivalent(new[] { "setA", "setB" }, sets.Select(static s => s.DirName).ToArray());
    }

    [TestMethod]
    public void TypeDetection_ParamSet_YieldsPhysBonePrimaryType()
    {
        var modDir = CreateModDir();
        WriteParamSet(Path.Combine(modDir, "HD2PhysBone", "KitName"));

        var result = new ModTypeDetectionService(NullLogger<ModTypeDetectionService>.Instance)
            .Detect(new DirectoryInfo(modDir));

        Assert.AreEqual(ModType.PhysBone, result.Type);
        Assert.IsTrue(result.Types.Contains(ModType.PhysBone));
    }

    private string CreateModDir()
    {
        var dir = Path.Combine(_tempRoot, "mod_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteParamSet(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var name in ModService.PhysBoneParamFileNames)
            File.WriteAllText(Path.Combine(dir, name), name, Encoding.UTF8);
    }

    private static void WriteModJson(string dir, string name)
    {
        File.WriteAllText(Path.Combine(dir, "mod.json"), $"{{ \"name\": \"{name}\" }}", Encoding.UTF8);
    }
}
