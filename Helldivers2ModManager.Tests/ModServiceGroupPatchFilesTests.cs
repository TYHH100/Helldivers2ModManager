using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModServiceGroupPatchFilesTests
{
    [TestMethod]
    public void GroupPatchFiles_MixedNamesAndCompanions_PreservesTripletSemantics()
    {
        const string a = "aaaaaaaaaaaaaaaa";
        const string b = "bbbbbbbbbbbbbbbb";
        var files = new[]
        {
            new FileInfo($@"C:\mods\{a}.patch_0"),
            new FileInfo($@"C:\mods\{a}.patch_0.gpu_resources"),
            new FileInfo($@"C:\mods\{a}.patch_0.stream"),
            new FileInfo($@"C:\mods\{a}.patch_1"),
            new FileInfo($@"C:\mods\{b}.patch_2"),
            new FileInfo($@"C:\mods\{b}.patch_2.stream"),
        };

        var groups = ModService.GroupPatchFiles(files);

        // 两个 name 都存在
        Assert.AreEqual(2, groups.Count);
        Assert.IsTrue(groups.ContainsKey(a));
        Assert.IsTrue(groups.ContainsKey(b));

        // indexes 是所有文件的 index 并集（0、1、2），每个 name 都生成等长列表
        Assert.AreEqual(3, groups[a].Count);
        Assert.AreEqual(3, groups[b].Count);

        // a 的 index 0：patch + gpu + stream 三元组齐全
        var a0 = groups[a][0];
        Assert.IsNotNull(a0.Patch);
        Assert.IsNotNull(a0.GpuResources);
        Assert.IsNotNull(a0.Stream);
        Assert.AreEqual($"{a}.patch_0", a0.Patch!.Name);
        Assert.AreEqual($"{a}.patch_0.gpu_resources", a0.GpuResources!.Name);
        Assert.AreEqual($"{a}.patch_0.stream", a0.Stream!.Name);

        // a 的 index 1：只有主补丁
        var a1 = groups[a][1];
        Assert.IsNotNull(a1.Patch);
        Assert.IsNull(a1.GpuResources);
        Assert.IsNull(a1.Stream);

        // a 的 index 2：b 拥有该 index，a 无文件 → 空 triplet（部署时以空文件占位）
        var a2 = groups[a][2];
        Assert.IsNull(a2.Patch);
        Assert.IsNull(a2.GpuResources);
        Assert.IsNull(a2.Stream);

        // b 的 index 2：patch + stream
        var b2 = groups[b][2];
        Assert.IsNotNull(b2.Patch);
        Assert.IsNotNull(b2.Stream);
        Assert.IsNull(b2.GpuResources);
    }

    [TestMethod]
    public void GroupPatchFiles_CompanionWithoutMainPatch_StillProducesTripletEntry()
    {
        const string a = "aaaaaaaaaaaaaaaa";
        var files = new[]
        {
            new FileInfo($@"C:\mods\{a}.patch_3.gpu_resources"),
            new FileInfo($@"C:\mods\{a}.patch_3.stream"),
        };

        var groups = ModService.GroupPatchFiles(files);

        Assert.AreEqual(1, groups.Count);
        var triplet = groups[a][0];
        Assert.IsNull(triplet.Patch, "缺少主补丁时 Patch 保持 null（部署时创建空占位文件）。");
        Assert.IsNotNull(triplet.GpuResources);
        Assert.IsNotNull(triplet.Stream);
    }

    [TestMethod]
    public void GroupPatchFiles_UnrelatedNamesAreIgnored()
    {
        const string a = "aaaaaaaaaaaaaaaa";
        var files = new[]
        {
            new FileInfo($@"C:\mods\{a}.patch_0"),
            new FileInfo(@"C:\mods\readme.txt"),
            new FileInfo(@"C:\mods\9BA6.patch_1"), // 大写资源 ID 不匹配小写正则
        };

        var groups = ModService.GroupPatchFiles(files);

        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual(1, groups[a].Count);
        Assert.IsNotNull(groups[a][0].Patch);
    }
}
