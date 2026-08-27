using Helldivers2ModManager.Core.Preview;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Preview;

[TestClass]
[TestCategory("Fixture")]
public sealed class PatchResourceInspectorIntegrationTests
{
    [TestMethod]
    public async Task PreviewModelAsync_VrcTellRetainsLegLod0GeometryWithinPreviewCapacity()
    {
        var modDirectory = new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName,
            "Test", "Mods", "Mods", "715 VRC_Tell 替换RE-1861 肩章轻甲_1a9657a3"));
        if (!modDirectory.Exists)
            Assert.Inconclusive("Local model-preview fixture is not installed.");

        var patch = new FileInfo(Path.Combine(modDirectory.FullName, "9ba626afa44a3aa3.patch_43"));
        var result = await new PatchResourceInspector().PreviewModelAsync(modDirectory, [patch]);
        var selection = ModelPreviewMeshSelector.Select(result.Meshes);
        ulong[] legUnitIds = [0x2E96BEB8DF711CD4, 0x8E4FA51D187AD933];
        var legMeshes = selection.VisibleMeshes
            .Where(mesh => legUnitIds.Contains(mesh.UnitId))
            .ToArray();

        Assert.IsNull(result.Error, result.Error);
        Assert.AreEqual(1, result.PatchFileCount);
        Assert.IsTrue(result.SkippedStreams < 15);
        foreach (var unitId in legUnitIds)
        {
            var unitMeshes = legMeshes.Where(mesh => mesh.UnitId == unitId).ToArray();
            Assert.IsTrue(unitMeshes.Length > 0, $"LOD0 leg Unit 0x{unitId:X16} should decode visible geometry.");
            Assert.IsTrue(unitMeshes.Any(mesh => mesh.TextureIds.Count > 0 && mesh.HasTextureCoordinates));
            Assert.IsTrue(unitMeshes.All(mesh => mesh.VertexCount > 0 && mesh.TriangleCount > 0));
        }

        var minX = legMeshes.SelectMany(mesh => mesh.Positions.Where((_, index) => index % 3 == 0)).Min();
        var maxX = legMeshes.SelectMany(mesh => mesh.Positions.Where((_, index) => index % 3 == 0)).Max();
        var minY = legMeshes.SelectMany(mesh => mesh.Positions.Where((_, index) => index % 3 == 1)).Min();
        var maxY = legMeshes.SelectMany(mesh => mesh.Positions.Where((_, index) => index % 3 == 1)).Max();
        var minZ = legMeshes.SelectMany(mesh => mesh.Positions.Where((_, index) => index % 3 == 2)).Min();
        var maxZ = legMeshes.SelectMany(mesh => mesh.Positions.Where((_, index) => index % 3 == 2)).Max();

        Assert.IsTrue(Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)) > 0.5f);
        Assert.IsTrue(result.PreviewVertexCount <= 1_000_000);
        Assert.IsTrue(result.PreviewIndexCount <= 3_000_000);
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
