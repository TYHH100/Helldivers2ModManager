using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// 删除模组后的补丁链补洞（compact）测试：游戏按 patch_0..N 连续读取，
/// 遇到第一个空洞即停止，删除中间补丁后必须把高于被删位的文件依次左移。
/// </summary>
[TestClass]
public sealed class ModServicePatchChainCompactTests
{
    private const string BaseName = "0123456789abcdef";

    private string _tempRoot = string.Empty;

    [TestInitialize]
    public void TestInitialize()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "patchchain_tests_" + Guid.NewGuid().ToString("N"));
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
    public void Plan_ContiguousChain_ReturnsNoMoves()
    {
        var moves = ModService.PlanPatchChainCompact([0, 1, 2, 3]);

        Assert.AreEqual(0, moves.Count);
    }

    [TestMethod]
    public void Plan_MiddleHoles_MovesFillFromLowest()
    {
        var moves = ModService.PlanPatchChainCompact([0, 1, 3, 4, 7]);

        CollectionAssert.AreEqual(new[] { (3, 2), (4, 3), (7, 4) }, moves);
    }

    [TestMethod]
    public void Plan_FirstSlotMissing_MovesStartAtZero()
    {
        var moves = ModService.PlanPatchChainCompact([1, 2]);

        CollectionAssert.AreEqual(new[] { (1, 0), (2, 1) }, moves);
    }

    [TestMethod]
    public void Plan_EmptyAndDuplicateInput_AreHandled()
    {
        Assert.AreEqual(0, ModService.PlanPatchChainCompact([]).Count);

        // 重复 index 去重后等价于单元素链 {5}，应左移到 0
        CollectionAssert.AreEqual(new[] { (5, 0) }, ModService.PlanPatchChainCompact([5, 5, 5]));
    }

    [TestMethod]
    public void Plan_MovesAreAscendingBySourceIndex()
    {
        // 升序执行是正确性前提：目标位要么本来就是空洞，要么已被前一次移动腾出
        var moves = ModService.PlanPatchChainCompact([0, 2, 5, 9, 10]);

        var sources = moves.Select(static m => m.FromIndex).ToArray();
        Assert.IsTrue(sources.SequenceEqual(sources.OrderBy(static i => i)));
    }

    [TestMethod]
    public void EnumeratePatchChainIndexes_GroupsByDeployedIndex()
    {
        var dataDir = new DirectoryInfo(_tempRoot);
        Write($"{BaseName}.patch_0", "a");
        Write($"{BaseName}.patch_2.gpu_resources", "b");
        Write($"{BaseName}.patch_2.stream", "c");
        Write("ffffffffffffffff.patch_0", "other base");
        Write("unrelated.txt", "x");

        var indexes = ModService.EnumeratePatchChainIndexes(dataDir, BaseName);

        CollectionAssert.AreEquivalent(new[] { 0, 2 }, indexes.ToArray());
    }

    [TestMethod]
    public async Task Compact_FillsHolesAndKeepsSidecarsAndContent()
    {
        var dataDir = new DirectoryInfo(_tempRoot);
        Write($"{BaseName}.patch_0", "p0");
        Write($"{BaseName}.patch_1.gpu_resources", "g1");
        Write($"{BaseName}.patch_3", "p3");
        Write($"{BaseName}.patch_3.gpu_resources", "g3");
        Write($"{BaseName}.patch_3.stream", "s3");
        Write($"{BaseName}.patch_5", "p5");

        await ModService.CompactPatchChainAsync(dataDir, BaseName, NullLogger.Instance);

        AssertFiles(
            ($"{BaseName}.patch_0", "p0"),
            ($"{BaseName}.patch_1.gpu_resources", "g1"),
            ($"{BaseName}.patch_2", "p3"),
            ($"{BaseName}.patch_2.gpu_resources", "g3"),
            ($"{BaseName}.patch_2.stream", "s3"),
            ($"{BaseName}.patch_3", "p5"));
        AssertFilesNotExist(
            $"{BaseName}.patch_1",
            $"{BaseName}.patch_3.gpu_resources",
            $"{BaseName}.patch_3.stream",
            $"{BaseName}.patch_4",
            $"{BaseName}.patch_5");
    }

    [TestMethod]
    public async Task Compact_ContiguousChain_IsNoOp()
    {
        var dataDir = new DirectoryInfo(_tempRoot);
        Write($"{BaseName}.patch_0", "p0");
        Write($"{BaseName}.patch_1", "p1");

        await ModService.CompactPatchChainAsync(dataDir, BaseName, NullLogger.Instance);

        AssertFiles(
            ($"{BaseName}.patch_0", "p0"),
            ($"{BaseName}.patch_1", "p1"));
    }

    private void Write(string fileName, string content)
    {
        File.WriteAllText(Path.Combine(_tempRoot, fileName), content, Encoding.UTF8);
    }

    private void AssertFiles(params (string Name, string Content)[] files)
    {
        foreach (var (name, content) in files)
            Assert.AreEqual(content, File.ReadAllText(Path.Combine(_tempRoot, name)), $"content of {name}");
    }

    private void AssertFilesNotExist(params string[] names)
    {
        foreach (var name in names)
            Assert.IsFalse(File.Exists(Path.Combine(_tempRoot, name)), $"{name} should not exist");
    }
}
