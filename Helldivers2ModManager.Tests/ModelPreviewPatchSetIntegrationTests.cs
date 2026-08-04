using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
[TestCategory("Fixture")]
public sealed class ModelPreviewPatchSetIntegrationTests
{
    [TestMethod]
    public async Task PreviewModelAsync_UsesOnlyTheSelectedPlumBodyPartsAndMaterialVariant()
    {
        var modDirectory = new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName,
            "Test", "Mods", "Mods", "【学園制服】Plum 替换 CW-9+CE-27+I-92"));
        var selectedRelativePaths = new[]
        {
            "Model/本体/9ba626afa44a3aa3.patch_7",
            "Material/A/9ba626afa44a3aa3.patch_0",
            "Model/衣服/9ba626afa44a3aa3.patch_10",
            "Model/弹挂/9ba626afa44a3aa3.patch_8",
            "Model/尾巴/9ba626afa44a3aa3.patch_12",
            "Model/袜子/9ba626afa44a3aa3.patch_11",
            "Model/鞋子/9ba626afa44a3aa3.patch_9"
        };
        var selectedFiles = selectedRelativePaths
            .Select(relativePath => new FileInfo(Path.Combine(modDirectory.FullName, relativePath)))
            .ToArray();

        var result = await new PatchResourceInspectionService().PreviewModelAsync(modDirectory, selectedFiles);

        Assert.AreEqual(selectedFiles.Length, result.PatchFileCount);
        Assert.IsTrue(result.Meshes.Count > 0, "The selected model parts should decode geometry.");
        Assert.IsTrue(result.Meshes.Any(mesh => mesh.TextureIds.Count > 0), "The selected Unit sections should resolve texture resources from Material/A.");
        Assert.IsTrue(result.Meshes.All(mesh => !mesh.PatchFile.StartsWith("Material/B", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(result.Textures.All(texture => !texture.PatchFile.StartsWith("Material/B", StringComparison.OrdinalIgnoreCase)));
        var selectedPaths = selectedRelativePaths.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.IsTrue(
            result.Meshes.All(mesh => selectedPaths.Contains(Normalize(mesh.PatchFile))),
            $"Unexpected decoded source: {string.Join(", ", result.Meshes.Select(mesh => mesh.PatchFile).Distinct())}");
        Assert.IsTrue(result.Meshes.Any(mesh => Normalize(mesh.PatchFile) == "Model\\本体\\9ba626afa44a3aa3.patch_7"));

        const ulong skinAndSockAlbedo = 0xFBA32BE2CB401D8D;
        const ulong clothingAndShoeAlbedo = 0xB8F1673E63130FCC;
        var socks = result.Meshes
            .Where(mesh => Normalize(mesh.PatchFile) == "Model\\袜子\\9ba626afa44a3aa3.patch_11" && mesh.TextureIds.Count > 0)
            .ToArray();
        var clothesAndShoes = result.Meshes
            .Where(mesh => Normalize(mesh.PatchFile) is
                "Model\\衣服\\9ba626afa44a3aa3.patch_10" or
                "Model\\鞋子\\9ba626afa44a3aa3.patch_9")
            .Where(static mesh => mesh.TextureIds.Count > 0)
            .ToArray();

        Assert.IsTrue(socks.Length > 0, "The selected sock Unit should expose textured mesh sections.");
        Assert.IsTrue(socks.All(mesh => mesh.ColorTextureId == skinAndSockAlbedo));
        Assert.IsTrue(clothesAndShoes.Length > 0, "The selected clothing and shoe Units should expose textured mesh sections.");
        Assert.IsTrue(clothesAndShoes.All(mesh => mesh.ColorTextureId == clothingAndShoeAlbedo));
        CollectionAssert.AreEquivalent(
            new[] { skinAndSockAlbedo, clothingAndShoeAlbedo },
            result.Meshes
                .Select(static mesh => mesh.ColorTextureId)
                .Where(static textureId => textureId.HasValue)
                .Select(static textureId => textureId!.Value)
                .Distinct()
                .ToArray());
    }

    [TestMethod]
    public async Task PreviewModelAsync_UsesTheCompleteConventionalPatchSetWithoutBackupFiles()
    {
        var modDirectory = new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName,
            "Test", "Mods", "Mods", "shinano Fuyukano替换EX-00原型X CM-10临床医师 A-9地狱伞兵 A-35侦察者_dd7d1fc0"));
        var selectedFiles = new[]
        {
            new FileInfo(Path.Combine(modDirectory.FullName, "9ba626afa44a3aa3.patch_12")),
            new FileInfo(Path.Combine(modDirectory.FullName, "9ba626afa44a3aa3.patch_13"))
        };

        var result = await new PatchResourceInspectionService().PreviewModelAsync(modDirectory, selectedFiles);

        Assert.AreEqual(2, result.PatchFileCount);
        Assert.IsTrue(result.Meshes.Count > 0, "The conventional complete patch set should decode geometry.");
        Assert.IsTrue(result.Meshes.All(mesh =>
            mesh.PatchFile is "9ba626afa44a3aa3.patch_12" or "9ba626afa44a3aa3.patch_13"));
        Assert.IsTrue(result.Textures.All(texture =>
            texture.PatchFile is "9ba626afa44a3aa3.patch_12" or "9ba626afa44a3aa3.patch_13"));
    }

    [TestMethod]
    public async Task PreviewModelAsync_HidesEvetteCullingMeshesByDefault()
    {
        var modDirectory = new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName,
            "Test", "Mods", "Mods", "6+伊薇特替换AC-2_55d48dca"));
        var patch = new FileInfo(Path.Combine(modDirectory.FullName, "9ba626afa44a3aa3.patch_6"));

        var result = await new PatchResourceInspectionService().PreviewModelAsync(modDirectory, [patch]);
        var selection = ModelPreviewMeshSelector.Select(result.Meshes);
        var cullingMeshes = result.Meshes
            .Where(mesh => mesh.RenderStatus == ModelPreviewMeshRenderStatus.HiddenCullingBody)
            .ToArray();

        Assert.IsTrue(result.Meshes.Count > 0);
        Assert.IsTrue(cullingMeshes.Length >= 6, "The fixture contains repeated MeshInfo culling bodies.");
        Assert.IsTrue(cullingMeshes.All(mesh => mesh.MeshInfoIndex >= 0));
        Assert.IsFalse(selection.VisibleMeshes.Any(mesh => mesh.IsCullingBody));
    }

    [TestMethod]
    public async Task PreviewModelAsync_UsesMeshInfoWindowsAndHidesCullingBodiesAcrossRealMods()
    {
        var repositoryRoot = FindRepositoryRoot().FullName;
        var samples = new[]
        {
            (Directory: Path.Combine(repositoryRoot, "Test", "Mods", "Mods", "ALLENES替换技术兵和医疗套_af7f5242"), Patch: "9ba626afa44a3aa3.patch_18"),
            (Directory: Path.Combine(repositoryRoot, "Test", "Mods", "Mods", "VRC_夏菲 替换 B-01系列_6cb08803", "蓝光"), Patch: "9ba626afa44a3aa3.patch_6")
        };

        foreach (var sample in samples)
        {
            var directory = new DirectoryInfo(sample.Directory);
            var result = await new PatchResourceInspectionService().PreviewModelAsync(
                directory,
                [new FileInfo(Path.Combine(directory.FullName, sample.Patch))]);
            var selection = ModelPreviewMeshSelector.Select(result.Meshes);
            var cullingBodies = result.Meshes.Where(mesh => mesh.IsCullingBody).ToArray();

            Assert.IsTrue(cullingBodies.Length > 0, $"Expected default-material proxies in {directory.Name}.");
            Assert.IsTrue(cullingBodies.All(mesh => mesh.RenderStatus == ModelPreviewMeshRenderStatus.HiddenCullingBody));
            Assert.IsTrue(cullingBodies.All(mesh => mesh.MeshInfoIndex >= 0));
            Assert.IsFalse(selection.VisibleMeshes.Any(mesh => mesh.IsCullingBody));
        }

        var vrcDirectory = new DirectoryInfo(samples[1].Directory);
        var vrcResult = await new PatchResourceInspectionService().PreviewModelAsync(
            vrcDirectory,
            [new FileInfo(Path.Combine(vrcDirectory.FullName, samples[1].Patch))]);
        var splitVisibleUnit = vrcResult.Meshes
            .Where(mesh => mesh.UnitId == 0xC82BCD4AAABB6CD3 && mesh.StreamIndex == 0 && !mesh.IsCullingBody)
            .OrderBy(mesh => mesh.SourceVertexOffset)
            .ToArray();

        Assert.AreEqual(1, splitVisibleUnit.Length);
        CollectionAssert.AreEqual(new uint[] { 0 }, splitVisibleUnit.Select(mesh => mesh.SourceVertexOffset).ToArray());
        Assert.IsTrue(splitVisibleUnit.All(mesh => mesh.VertexCount == 5_376));
    }

    [TestMethod]
    public async Task PreviewModelAsync_VrcTellRetainsLegLod0GeometryWithinPreviewCapacity()
    {
        var modDirectory = new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName,
            "Test", "Mods", "Mods", "715 VRC_Tell 替换RE-1861 肩章轻甲_1a9657a3"));
        var patch = new FileInfo(Path.Combine(modDirectory.FullName, "9ba626afa44a3aa3.patch_43"));

        var result = await new PatchResourceInspectionService().PreviewModelAsync(modDirectory, [patch]);
        var selection = ModelPreviewMeshSelector.Select(result.Meshes);
        ulong[] legUnitIds = [0x2E96BEB8DF711CD4, 0x8E4FA51D187AD933];
        var legMeshes = selection.VisibleMeshes
            .Where(mesh => legUnitIds.Contains(mesh.UnitId))
            .ToArray();

        Assert.IsNull(result.Error);
        Assert.AreEqual(1, result.PatchFileCount);
        Assert.IsTrue(result.SkippedStreams < 15, $"LOD filtering should reduce the previous 15 skipped streams, but {result.SkippedStreams} remain.");
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
        Console.WriteLine($"VRC Tell: meshes={result.Meshes.Count}, skipped={result.SkippedStreams}, legMeshes={legMeshes.Length}, X=[{minX}, {maxX}], Y=[{minY}, {maxY}], Z=[{minZ}, {maxZ}]");
        Assert.IsTrue(Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)) > 0.5f,
            $"Leg geometry should span a meaningful lower-body range; decoded bounds were X=[{minX}, {maxX}], Y=[{minY}, {maxY}], Z=[{minZ}, {maxZ}].");
    }

    [TestMethod]
    public async Task PreviewModelAsync_VrcMizukiBodySlotsNeedPresentationRotation()
    {
        var modDirectory = new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName,
            "Test", "Mods", "Mods", "VRC_瑞希 寄染赛车服 替换 CM-10全套 + EX00全套 +CM17头+无畏头_02508ace", "无尾巴"));
        var patch = new FileInfo(Path.Combine(modDirectory.FullName, "9ba626afa44a3aa3.patch_9"));

        var result = await new PatchResourceInspectionService().PreviewModelAsync(modDirectory, [patch]);
        var visibleMeshes = ModelPreviewMeshSelector.Select(result.Meshes).VisibleMeshes;
        var rotation = ModelPreviewCharacterOrientation.GetRequiredRotation(visibleMeshes);

        Assert.IsNull(result.Error, result.Error);
        Assert.IsTrue(visibleMeshes.Any(mesh => mesh.CustomizationSlot == ModelPreviewCustomizationSlot.Torso));
        Assert.IsTrue(visibleMeshes.Any(mesh => mesh.CustomizationSlot is ModelPreviewCustomizationSlot.LeftLeg or ModelPreviewCustomizationSlot.RightLeg));
        Assert.AreNotEqual(ModelPreviewPresentationRotation.None, rotation,
            "The real VRC Mizuki body slots should expose a clear non-Y-up presentation axis.");
        Console.WriteLine($"VRC Mizuki: visibleMeshes={visibleMeshes.Count}, presentationRotation={rotation}");
    }

    [TestMethod]
    public async Task PreviewModelAsync_DoesNotInjectBaseGameArmorForProxyUnits()
    {
        var modDirectory = new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName,
            "Test", "Mods", "Mods", "ALLENES替换技术兵和医疗套_af7f5242"));
        var result = await new PatchResourceInspectionService().PreviewModelAsync(
            modDirectory,
            [new FileInfo(Path.Combine(modDirectory.FullName, "9ba626afa44a3aa3.patch_18"))]);

        Assert.IsNull(result.Error, result.Error);
        Assert.IsFalse(result.Meshes.Any(mesh => mesh.PatchFile.StartsWith("[Game]/", StringComparison.Ordinal)),
            "A replacement preview must not inject original game armor as visible geometry.");
        Assert.IsTrue(result.Meshes.Any(mesh => mesh.CustomizationSlot == ModelPreviewCustomizationSlot.LeftLeg &&
                                                mesh.BodyShape == ModelPreviewBodyShape.Any));
        Assert.IsTrue(result.Meshes.Any(mesh => mesh.CustomizationSlot == ModelPreviewCustomizationSlot.RightLeg &&
                                                mesh.BodyShape == ModelPreviewBodyShape.Any));

        var selection = ModelPreviewMeshSelector.Select(result.Meshes);
        var switchableSlots = ModelPreviewBodyShapeSelection.GetSwitchableSlots(
            result.Meshes,
            selection.VisibleMeshes);
        Assert.AreEqual(0, switchableSlots.Count,
            "Proxy-only body shapes must not expose a switch that would require original game armor.");
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

    private static string Normalize(string path) => path.Replace('/', '\\');

}
