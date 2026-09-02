using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// 补丁链覆盖语义回归：部署把选中补丁按选项顺序重排为连续补丁链，同一 Unit FileID
/// 被多个补丁修改时，链中靠后的补丁才是生效版本。"移除/摘下"类选项（选项补丁用空壳或
/// 精简 Unit 覆盖本体补丁的完整网格）与"替换"类选项（头型三选一等）都依赖该语义；
/// 预览若把所有补丁的网格叠加，被移除的部件就永远消失不了。
/// 夹具：milltina 修女 替换tg-122（本体补丁含完整头部 Unit E79251F8F34D351A，
/// 头/去长发 与 头/去头盖 各含同一 Unit 的不同变体）。
/// </summary>
[TestClass]
[TestCategory("Fixture")]
public sealed class ModelPreviewPatchOverrideTests
{
    private const ulong HeadUnitId = 0xE79251F8F34D351AUL;
    private const string PatchFileName = "9ba626afa44a3aa3.patch_0";

    [TestMethod]
    public async Task PreviewModelAsync_LaterHeadVariant_ShadowsBaseHeadMeshes()
    {
        var modDirectory = CreateModDirectory();
        var basePath = Path.Combine(modDirectory.FullName, "本体", PatchFileName);
        var variantPath = Path.Combine(modDirectory.FullName, "头", "去长发", PatchFileName);
        Assert.IsTrue(File.Exists(basePath), "本体 fixture patch is missing.");
        Assert.IsTrue(File.Exists(variantPath), "去长发 fixture patch is missing.");

        var result = await new PatchResourceInspectionService().PreviewModelAsync(
            modDirectory,
            [new FileInfo(basePath), new FileInfo(variantPath)]);

        Assert.IsNull(result.Error, result.Error);
        var headMeshes = result.Meshes.Where(mesh => mesh.UnitId == HeadUnitId).ToArray();
        Assert.IsTrue(headMeshes.Length > 0, "The selected head variant must decode geometry.");
        Assert.IsTrue(
            headMeshes.All(mesh => mesh.PatchFile.EndsWith($"{Path.DirectorySeparatorChar}去长发{Path.DirectorySeparatorChar}{PatchFileName}", StringComparison.Ordinal) ||
                                   mesh.PatchFile.EndsWith($"\\去长发\\{PatchFileName}", StringComparison.Ordinal)),
            $"Head meshes must come from the later variant patch only. Sources: {string.Join(", ", headMeshes.Select(static mesh => mesh.PatchFile).Distinct())}");
        Assert.IsTrue(
            result.Meshes.Any(mesh => mesh.UnitId != HeadUnitId && mesh.PatchFile.Contains("本体", StringComparison.Ordinal)),
            "Base body units that no later patch touches must remain visible.");
    }

    [TestMethod]
    public async Task PreviewModelAsync_ReversedPatchOrder_ShadowsInTheOtherDirection()
    {
        var modDirectory = CreateModDirectory();
        var basePath = Path.Combine(modDirectory.FullName, "本体", PatchFileName);
        var variantPath = Path.Combine(modDirectory.FullName, "头", "去长发", PatchFileName);

        var result = await new PatchResourceInspectionService().PreviewModelAsync(
            modDirectory,
            [new FileInfo(variantPath), new FileInfo(basePath)]);

        Assert.IsNull(result.Error, result.Error);
        var headMeshes = result.Meshes.Where(mesh => mesh.UnitId == HeadUnitId).ToArray();
        Assert.IsTrue(headMeshes.Length > 0, "The base head geometry must decode.");
        Assert.IsTrue(
            headMeshes.All(mesh => mesh.PatchFile.Contains("本体", StringComparison.Ordinal)),
            $"With the base patch last, head meshes must come from it. Sources: {string.Join(", ", headMeshes.Select(static mesh => mesh.PatchFile).Distinct())}");
    }

    private static DirectoryInfo CreateModDirectory()
    {
        var path = Path.Combine(
            FindRepositoryRoot().FullName,
            "Test", "Mods", "Mods", "milltina 修女 替换tg-122");
        Assert.IsTrue(Directory.Exists(path), $"Fixture mod directory is missing: {path}");
        return new DirectoryInfo(path);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (DirectoryInfo? current = new(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return current;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root for the model-preview fixtures.");
    }
}
