using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// 刚性挂接蒙皮回归：护甲模组的装饰部件（弹挂、尾巴、徽章、袜子、单三角 sprite
/// 等）的顶点流没有可解码的蒙皮数据，此前被解析为静态网格、动画时冻在原地
/// （"身体只有部分部位在动"）。修复后它们按 MeshInfo 的挂接骨骼做单骨骼全权
/// 重蒙皮。本测试用仓库内真实模组夹具锁定"解析出的预览网格全部可蒙皮"。
/// </summary>
[TestClass]
public sealed class ModelPreviewAttachedSkinningTests
{
    private const string FixtureModName = "【学園制服】Plum 替换 CW-9+CE-27+I-92";

    [TestMethod]
    public async Task AllPreviewMeshes_AreSkinnable()
    {
        var modDirectory = FindFixtureModDirectory();
        if (modDirectory is null)
        {
            Assert.Inconclusive($"Fixture mod '{FixtureModName}' is not present under Test/Mods/Mods.");
        }

        var patchFiles = modDirectory
            .GetFiles("*", SearchOption.AllDirectories)
            .Where(static file => file.Name.Contains(".patch_", StringComparison.OrdinalIgnoreCase) &&
                                  !file.Name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase) &&
                                  !file.Name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.IsTrue(patchFiles.Length > 0, "The fixture mod must contain patch files.");

        var preview = await new PatchResourceInspectionService()
            .PreviewModelAsync(modDirectory, patchFiles);
        Assert.IsNull(preview.Error, preview.Error);
        Assert.IsTrue(preview.Meshes.Count > 0, "The fixture mod must decode meshes.");

        // 旧解析器在该夹具上只有 17/73 网格带蒙皮；其余装饰部件全部冻结。
        var unskinned = preview.Meshes
            .Where(static mesh => mesh.Skinning is null)
            .Select(static mesh => $"{mesh.UnitIdText}/stream {mesh.StreamIndex}")
            .ToArray();
        Assert.AreEqual(
            0, unskinned.Length,
            "Every preview mesh must be skinnable (attached meshes included). Unskinned: " +
            string.Join(", ", unskinned.Take(8)));

        // 部件骨架（非 71 骨本体）必须出现，否则该夹具不再覆盖挂接件场景。
        Assert.IsTrue(
            preview.Meshes.Any(static mesh => mesh.Skinning!.Skeleton.Bones.Count is > 0 and < 71),
            "The fixture must include attached-part skeletons for this regression to be meaningful.");

        foreach (var mesh in preview.Meshes)
        {
            var skinning = mesh.Skinning!;
            Assert.IsTrue(
                skinning.IsValidForVertexCount(mesh.VertexCount),
                $"Skinning arrays must match the vertex count for {mesh.DisplayName}.");
            Assert.IsTrue(
                skinning.TransformIndices.Any(static transformIndex => transformIndex >= 0),
                $"Every mesh must have at least one resolved transform index: {mesh.DisplayName}.");
            Assert.IsTrue(
                skinning.TransformIndices.All(transformIndex =>
                    transformIndex < 0 || transformIndex < skinning.Skeleton.Bones.Count),
                $"Transform indices must stay inside the skeleton: {mesh.DisplayName}.");
        }
    }

    private static DirectoryInfo? FindFixtureModDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Helldivers2ModManager.sln")))
            directory = directory.Parent;
        if (directory is null)
            return null;

        var path = Path.Combine(directory.FullName, "Test", "Mods", "Mods", FixtureModName);
        return Directory.Exists(path) ? new DirectoryInfo(path) : null;
    }
}
