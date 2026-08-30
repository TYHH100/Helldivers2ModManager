using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// 部署排序测试：携带 HD2PhysBone 参数集的模组必须稳定置底（最后部署 = 同名资源链最高 index，
/// PhysBone 运行时 Lua patch 的 update 劫持链式包裹先应用的脚本），且与部署方向设置无关。
/// </summary>
[TestClass]
public sealed class DeploymentOrderHelperTests
{
    private string _tempRoot = string.Empty;

    [TestInitialize]
    public void TestInitialize()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "deployorder_tests_" + Guid.NewGuid().ToString("N"));
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
    public void Build_WithoutPhysBoneMods_KeepsSnapshotOrder()
    {
        var mods = new[] { CreateMod("a", physBone: false), CreateMod("b", physBone: false), CreateMod("c", physBone: false) };

        var result = Build(mods, useDeploymentOrder: false, deployBottomToTop: false);

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, result.Select(static m => m.Manifest.Name).ToArray());
    }

    [TestMethod]
    public void Build_PhysBoneModsStabilizedLast()
    {
        var mods = new[] { CreateMod("pb1", physBone: true), CreateMod("a", physBone: false), CreateMod("pb2", physBone: true), CreateMod("b", physBone: false) };

        var result = Build(mods, useDeploymentOrder: false, deployBottomToTop: false);

        CollectionAssert.AreEqual(new[] { "a", "b", "pb1", "pb2" }, result.Select(static m => m.Manifest.Name).ToArray());
    }

    [TestMethod]
    public void Build_PhysBoneStaysLast_AfterBottomToTopReversal()
    {
        var mods = new[] { CreateMod("pb", physBone: true), CreateMod("a", physBone: false), CreateMod("b", physBone: false) };

        var result = Build(mods, useDeploymentOrder: false, deployBottomToTop: true);

        // 反转只作用于普通模组：[a, b] -> [b, a]，PhysBone 仍在最后
        CollectionAssert.AreEqual(new[] { "b", "a", "pb" }, result.Select(static m => m.Manifest.Name).ToArray());
    }

    [TestMethod]
    public void Build_PhysBoneStaysLast_WithDeploymentOrderGuids()
    {
        var mods = new[] { CreateMod("pb", physBone: true), CreateMod("a", physBone: false), CreateMod("b", physBone: false) };
        var snapshot = ProfileSnapshot.Capture(1, Guid.NewGuid(), true, mods);
        var order = new[] { mods[1].Manifest.Guid, mods[0].Manifest.Guid, mods[2].Manifest.Guid };

        var result = DeploymentOrderHelper.BuildDeploymentMods(snapshot, useDeploymentOrder: true, order, deployBottomToTop: false);

        CollectionAssert.AreEqual(new[] { "a", "b", "pb" }, result.Select(static m => m.Manifest.Name).ToArray());
    }

    [TestMethod]
    public void Build_AllPhysBone_KeepsRelativeOrder()
    {
        var mods = new[] { CreateMod("pb1", physBone: true), CreateMod("pb2", physBone: true) };

        var result = Build(mods, useDeploymentOrder: false, deployBottomToTop: false);

        CollectionAssert.AreEqual(new[] { "pb1", "pb2" }, result.Select(static m => m.Manifest.Name).ToArray());
    }

    [TestMethod]
    public void ModData_IsPhysBoneMod_DetectsParamSet()
    {
        var withParams = CreateMod("with", physBone: true);
        var withoutParams = CreateMod("without", physBone: false);

        Assert.IsTrue(withParams.IsPhysBoneMod);
        Assert.IsFalse(withoutParams.IsPhysBoneMod);
    }

    private static ModData[] Build(ModData[] mods, bool useDeploymentOrder, bool deployBottomToTop)
    {
        var snapshot = ProfileSnapshot.Capture(1, Guid.NewGuid(), true, mods);
        return DeploymentOrderHelper.BuildDeploymentMods(snapshot, useDeploymentOrder, [], deployBottomToTop);
    }

    private ModData CreateMod(string name, bool physBone)
    {
        var dir = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(dir);
        if (physBone)
        {
            var paramDir = Path.Combine(dir, "HD2PhysBone", name);
            Directory.CreateDirectory(paramDir);
            File.WriteAllText(Path.Combine(paramDir, "hd2_spring_rig.bin"), "rig");
            File.WriteAllText(Path.Combine(paramDir, "hd2_ib_needle.bin"), "needle");
            File.WriteAllText(Path.Combine(paramDir, "lua_units.txt"), "units");
        }

        var manifest = new LegacyModManifest
        {
            Guid = Guid.NewGuid(),
            Name = name,
            Description = string.Empty,
        };
        return new ModData(new DirectoryInfo(dir), manifest);
    }
}
