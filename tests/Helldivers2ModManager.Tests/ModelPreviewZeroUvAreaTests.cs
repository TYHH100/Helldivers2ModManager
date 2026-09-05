using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewZeroUvAreaTests
{
    [TestMethod]
    public void FilterZeroUvAreaTriangles_AllNormal_ReturnsOriginalInstance()
    {
        var coordinates = new[] { 0.0f, 0.0f, 0.5f, 0.0f, 0.0f, 0.5f };
        var triangleIndices = new[] { 0, 1, 2 };
        var positions = new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f };

        var kept = ModelPreviewPageViewModel.FilterZeroUvAreaTriangles(triangleIndices, coordinates, positions);

        Assert.AreSame(triangleIndices, kept);
    }

    [TestMethod]
    public void FilterZeroUvAreaTriangles_CoveredZeroAreaTriangle_Dropped()
    {
        // 正常大三角形 (0,0,0)-(10,0,0)-(0,10,0)；零面积 UV 三角形共面位于其内部
        // 且不共享顶点索引（复制皮肤特征）→ 丢弃。
        var coordinates = new[]
        {
            0.00f, 0.00f,   // v0
            0.50f, 0.00f,   // v1
            0.00f, 0.50f,   // v2
            0.25f, 0.25f,   // v3（零面积三角形的三个顶点）
            0.25f, 0.25f,   // v4
            0.25f, 0.25f,   // v5
        };
        var triangleIndices = new[] { 0, 1, 2, 3, 4, 5 };
        var positions = new[]
        {
            0f, 0f, 0f, 10f, 0f, 0f, 0f, 10f, 0f,
            1f, 0f, 0f, 3f, 0f, 0f, 1f, 4f, 0f,
        };

        var kept = ModelPreviewPageViewModel.FilterZeroUvAreaTriangles(triangleIndices, coordinates, positions);

        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, kept);
    }

    [TestMethod]
    public void FilterZeroUvAreaTriangles_UncoveredZeroAreaTriangle_Kept()
    {
        // maya 民主中甲场景：纯色部件（帽/裙/袜）的 UV 塌缩三角形是该区域唯一曲面，
        // 没有正常三角形覆盖它 → 必须保留，否则部件消失。
        var coordinates = new[]
        {
            0.00f, 0.00f,   // v0
            0.50f, 0.00f,   // v1
            0.00f, 0.50f,   // v2
            0.25f, 0.25f,   // v3（零面积三角形的三个顶点，远离正常曲面）
            0.25f, 0.25f,   // v4
            0.25f, 0.25f,   // v5
        };
        var triangleIndices = new[] { 0, 1, 2, 3, 4, 5 };
        var positions = new[]
        {
            0f, 0f, 0f, 10f, 0f, 0f, 0f, 10f, 0f,
            100f, 0f, 0f, 103f, 0f, 0f, 100f, 4f, 0f,
        };

        var kept = ModelPreviewPageViewModel.FilterZeroUvAreaTriangles(triangleIndices, coordinates, positions);

        CollectionAssert.AreEqual(triangleIndices, kept);
    }

    [TestMethod]
    public void FilterZeroUvAreaTriangles_CoveredSubTexelTriangle_Dropped()
    {
        // 次纹素三角形（跨度 > 0 但不足半个纹素）共面叠在正常三角形内部 → 丢弃。
        var coordinates = new[]
        {
            0.00f, 0.00f,
            0.50f, 0.00f,
            0.00f, 0.50f,
            0.2500f, 0.2500f,   // 次纹素三角形：跨度 1/4096
            0.2500f, 0.2501f,
            0.2501f, 0.2500f,
        };
        var triangleIndices = new[] { 0, 1, 2, 3, 4, 5 };
        var positions = new[]
        {
            0f, 0f, 0f, 10f, 0f, 0f, 0f, 10f, 0f,
            1f, 0f, 0f, 3f, 0f, 0f, 1f, 4f, 0f,
        };

        var kept = ModelPreviewPageViewModel.FilterZeroUvAreaTriangles(triangleIndices, coordinates, positions);

        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, kept);
    }

    [TestMethod]
    public void FilterZeroUvAreaTriangles_AdjacentZeroAreaTriangle_Kept()
    {
        // 与正常三角形共享顶点索引的零面积三角形属于同一连续曲面（非复制皮肤）→ 保留。
        var coordinates = new[]
        {
            0.00f, 0.00f,
            0.50f, 0.00f,
            0.00f, 0.50f,
            0.25f, 0.25f,   // 零面积三角形：复用 v1/v2 并带一个新顶点
            0.25f, 0.25f,
            0.25f, 0.25f,
        };
        var triangleIndices = new[] { 0, 1, 2, 1, 2, 3 };
        var positions = new[]
        {
            0f, 0f, 0f, 10f, 0f, 0f, 0f, 10f, 0f,
        };

        var kept = ModelPreviewPageViewModel.FilterZeroUvAreaTriangles(triangleIndices, coordinates, positions);

        CollectionAssert.AreEqual(triangleIndices, kept);
    }

    [TestMethod]
    public async Task PreviewModelAsync_MayaRealFixture_KeepsSolidColorCollapsedUvGeometry()
    {
        // maya 民主中甲（DP-40+DP-11）：纯色部件（帽、裙摆、袜面）的 UV 塌缩三角形
        // 是该区域唯一曲面。回归场景：过滤后的网格不得丢失这些三角形（此前
        // "零面积一律跳过"的实现导致帽子消失、下半身残缺、袜边撕裂）。
        var modDirectory = new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName,
            "Test", "Mods", "Mods", "maya-melee uniform改DP-40+DP-11民主中甲mod2024.12.1更新_0a3cf3d1"));
        var patch = new FileInfo(Path.Combine(modDirectory.FullName, "9ba626afa44a3aa3.patch_28"));

        var result = await new PatchResourceInspectionService().PreviewModelAsync(modDirectory, [patch]);
        Assert.IsNull(result.Error, result.Error);

        var (mesh, degenerateCount) = FindMeshWithMostDegenerateTriangles(result.Meshes);
        Assert.IsTrue(degenerateCount >= 1000, "Expected a solid-color mesh with many collapsed-UV triangles.");
        var kept = ModelPreviewPageViewModel.FilterZeroUvAreaTriangles(
            mesh.TriangleIndices, mesh.TextureCoordinates!, mesh.Positions);
        var keptDegenerate = CountDegenerateTriangles(kept, mesh.TextureCoordinates!);
        Assert.IsTrue(
            keptDegenerate >= degenerateCount * 0.9,
            $"Collapsed-UV geometry must survive the filter: kept {keptDegenerate} of {degenerateCount}.");
    }

    [TestMethod]
    public async Task PreviewModelAsync_VrcTellFixture_DropsDeathOverlayTriangles()
    {
        // 715 VRC_Tell：头部死亡覆盖层（~36% 零面积 + ~19% 次纹素）叠在正常曲面上方，
        // WPF 渲染成黑块盖住头发。回归场景：覆盖层三角形必须被过滤掉。
        var modDirectory = new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName,
            "Test", "Mods", "Mods", "715 VRC_Tell 替换RE-1861 肩章轻甲_1a9657a3"));
        var patch = new FileInfo(Path.Combine(modDirectory.FullName, "9ba626afa44a3aa3.patch_43"));

        var result = await new PatchResourceInspectionService().PreviewModelAsync(modDirectory, [patch]);
        Assert.IsNull(result.Error, result.Error);

        var (mesh, degenerateCount) = FindMeshWithMostDegenerateTriangles(result.Meshes);
        Assert.IsTrue(degenerateCount >= 1000, "Expected a death-overlay mesh with many collapsed-UV triangles.");
        var kept = ModelPreviewPageViewModel.FilterZeroUvAreaTriangles(
            mesh.TriangleIndices, mesh.TextureCoordinates!, mesh.Positions);
        var keptDegenerate = CountDegenerateTriangles(kept, mesh.TextureCoordinates!);
        Assert.IsTrue(
            keptDegenerate <= degenerateCount * 0.4,
            $"Death overlay must be filtered: kept {keptDegenerate} of {degenerateCount}.");
    }

    [TestMethod]
    public async Task PreviewModelAsync_SuomiRealFixture_FiltersBc1PureBlackPlaceholderSections()
    {
        // 索米圣诞装：每个身体部位的 MeshInfo 末尾都有一个引用 128x128 BC1 纯黑
        // 占位 Albedo 的整窗 section（实测最大 36950 三角形），最后绘制会盖住正常
        // 材质。BC1（format 71/72）纯黑占位必须与 BC7 一样被识别并过滤。
        var modDirectory = new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName,
            "Test", "Mods", "Mods", "索米 聖誕裝 替換O-3 MOD分享 20260507_1f993a67"));
        var patch = new FileInfo(Path.Combine(modDirectory.FullName, "9ba626afa44a3aa3.patch_0"));
        const ulong placeholderColorTextureId = 0xB80BA5835850B17E;

        var result = await new PatchResourceInspectionService().PreviewModelAsync(modDirectory, [patch]);

        Assert.IsNull(result.Error, result.Error);
        Assert.IsTrue(result.Meshes.Count > 0, "Expected decoded meshes.");
        Assert.IsFalse(result.Meshes.Any(mesh => mesh.ColorTextureId == placeholderColorTextureId),
            "The known 128px BC1 pure-black placeholder sections must not remain in the preview.");
        var visible = ModelPreviewMeshSelector.Select(result.Meshes).VisibleMeshes;
        Assert.IsTrue(visible.Count > 0);
    }

    private static (ModelPreviewMesh Mesh, int DegenerateCount) FindMeshWithMostDegenerateTriangles(
        IReadOnlyList<ModelPreviewMesh> meshes)
    {
        var best = (Mesh: meshes[0], DegenerateCount: 0);
        foreach (var mesh in meshes)
        {
            if (mesh.TextureCoordinates is not { Length: > 0 })
                continue;
            var count = CountDegenerateTriangles(mesh.TriangleIndices, mesh.TextureCoordinates);
            if (count > best.DegenerateCount)
                best = (mesh, count);
        }

        return best;
    }

    private static int CountDegenerateTriangles(int[] triangleIndices, float[] coordinates)
    {
        const float subTexelSpan = 1f / 2048;
        var count = 0;
        for (var triangle = 0; triangle < triangleIndices.Length / 3; triangle++)
        {
            var a = triangleIndices[triangle * 3];
            var b = triangleIndices[triangle * 3 + 1];
            var c = triangleIndices[triangle * 3 + 2];
            var ax = coordinates[a * 2];
            var ay = coordinates[a * 2 + 1];
            var span = Math.Max(Math.Max(Math.Abs(coordinates[b * 2] - ax), Math.Abs(coordinates[c * 2] - ax)),
                                Math.Max(Math.Abs(coordinates[b * 2 + 1] - ay), Math.Abs(coordinates[c * 2 + 1] - ay)));
            if (span == 0f || span <= subTexelSpan)
                count++;
        }

        return count;
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
