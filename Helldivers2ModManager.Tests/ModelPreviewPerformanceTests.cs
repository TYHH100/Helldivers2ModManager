using System.Reflection;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
[TestCategory("Fixture")]
public sealed class ModelPreviewPerformanceTests
{
    private const ulong LargeTextureId = 0x38DA9EB5A01A0564;

    [TestMethod]
    public async Task PreviewModelAsync_MetadataFirstPassSkipsGpuDiagnosticsAndPreservesFixtureResults()
    {
        var (modDirectory, patchFiles) = GetVrcTellFixture();
        var metadataOnly = await InspectMetadataOnlyAsync(modDirectory, patchFiles);
        var normalInspection = await new PatchResourceInspectionService().InspectAsync(modDirectory, patchFiles);
        var preview = await new PatchResourceInspectionService().PreviewModelAsync(modDirectory, patchFiles);

        Assert.IsTrue(metadataOnly.TocEntries.Count > 0, "The first pass must preserve the Patch TOC used for material lookup.");
        Assert.AreEqual(11, metadataOnly.Textures.Count, "The metadata pass must preserve all real texture metadata.");
        Assert.AreEqual(0, metadataOnly.GpuStreams.Count, "The metadata pass must not create diagnostic GPU stream samples.");
        Assert.IsTrue(normalInspection.GpuStreams.Count > 0, "The fixture must contain GPU diagnostics so a zero metadata-pass count is meaningful.");
        Assert.AreEqual(metadataOnly.TocEntries.Count, normalInspection.TocEntries.Count);
        Assert.AreEqual(metadataOnly.Textures.Count, normalInspection.Textures.Count);

        Assert.IsNull(preview.Error);
        Assert.AreEqual(32, preview.Meshes.Count);
        Assert.AreEqual(11, preview.Textures.Count);
        Assert.AreEqual(0, preview.SkippedStreams);
        Assert.IsTrue(preview.Meshes.Any(mesh => mesh.UnitId == 0x2E96BEB8DF711CD4));
        Assert.IsTrue(preview.Meshes.Any(mesh => mesh.UnitId == 0x8E4FA51D187AD933));
    }

    [TestMethod]
    public async Task PreviewTextureAsync_RealLargeTexture_RespectsCallerPixelLimit()
    {
        const int maxPreviewPixels = 65_536;
        var (modDirectory, patchFiles) = GetVrcTellFixture();
        var service = new PatchResourceInspectionService();
        var inspection = await service.InspectAsync(modDirectory, patchFiles);
        var texture = inspection.Textures.Single(texture => texture.TextureId == LargeTextureId);

        var preview = await service.PreviewTextureAsync(modDirectory, texture, maxPreviewPixels);

        Assert.IsNotNull(preview);
        Assert.IsTrue(preview.Width > 0 && preview.Height > 0);
        Assert.IsTrue(preview.Width < texture.Width && preview.Height < texture.Height);
        Assert.IsTrue((long)preview.Width * preview.Height <= maxPreviewPixels,
            $"Returned {preview.Width} x {preview.Height} exceeds the caller limit of {maxPreviewPixels:N0} pixels.");
        Assert.IsNotNull(preview.BgraPixels);
        Assert.AreEqual(checked(preview.Width * preview.Height * 4), preview.BgraPixels.Length);
        StringAssert.Contains(preview.Description, "mip");
    }

    private static async Task<PatchResourceInspectionResult> InspectMetadataOnlyAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles)
    {
        var method = typeof(PatchResourceInspectionService).GetMethod(
            "InspectAsync",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(DirectoryInfo), typeof(IReadOnlyList<FileInfo>), typeof(bool)],
            modifiers: null);
        Assert.IsNotNull(method, "The performance regression test requires the metadata-only inspection seam.");
        var task = method.Invoke(null, [modDirectory, patchFiles, false]) as Task<PatchResourceInspectionResult>;
        Assert.IsNotNull(task);
        return await task;
    }

    private static (DirectoryInfo Directory, FileInfo[] PatchFiles) GetVrcTellFixture()
    {
        var directory = new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName,
            "Test", "Mods", "Mods", "715 VRC_Tell 替换RE-1861 肩章轻甲_1a9657a3"));
        return (directory, [new FileInfo(Path.Combine(directory.FullName, "9ba626afa44a3aa3.patch_43"))]);
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
