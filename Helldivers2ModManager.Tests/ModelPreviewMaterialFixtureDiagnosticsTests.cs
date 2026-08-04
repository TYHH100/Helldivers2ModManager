using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
[TestCategory("Fixture")]
public sealed class ModelPreviewMaterialFixtureDiagnosticsTests
{
    [DataTestMethod]
    [DataRow("白银之城-侦探-CW9_f390e084", "9ba626afa44a3aa3.patch_0")]
    [DataRow("2026年5月2日安德莉亚 替换fs37 sc15_93b0ae32", "9ba626afa44a3aa3.patch_103")]
    [DataRow("VRC_瑞希 寄染赛车服 替换 CM-10全套 + EX00全套 +CM17头+无畏头_02508ace\\无尾巴", "9ba626afa44a3aa3.patch_9")]
    public async Task PreviewModelAsync_RealFixture_ReportsSectionMaterialInputs(
        string modRelativePath,
        string patchName)
    {
        var modDirectory = new DirectoryInfo(Path.Combine(
            FindRepositoryRoot().FullName,
            "Test", "Mods", "Mods", modRelativePath));
        var patch = new FileInfo(Path.Combine(modDirectory.FullName, patchName));
        var service = new PatchResourceInspectionService();

        var result = await service.PreviewModelAsync(modDirectory, [patch]);
        var textureSizes = result.Textures.ToDictionary(
            static texture => texture.TextureId,
            static texture => $"{texture.Width}x{texture.Height}");

        Assert.IsNull(result.Error, result.Error);
        Assert.IsTrue(result.Meshes.Count > 0, $"Expected decoded meshes in {modRelativePath}.");
        var variants = result.Meshes
            .Where(static mesh => mesh.MaterialId.HasValue && mesh.ColorTextureId.HasValue)
            .GroupBy(static mesh => (mesh.UnitId, mesh.StreamIndex, mesh.MeshInfoIndex, mesh.SourceVertexOffset, mesh.VertexCount, mesh.TriangleCount))
            .Where(static group => group.Select(mesh => mesh.MaterialId).Distinct().Count() > 1)
            .ToArray();
        Assert.AreEqual(0, variants.Length,
            "Material variants with identical MeshInfo/vertex/index geometry must be reduced before rendering.");
        if (modRelativePath.StartsWith("白银之城", StringComparison.Ordinal))
        {
            const ulong placeholderColorTextureId = 0x6014B4C43AEBA392;
            const ulong preferredColorTextureId = 0x280EBD363C66358F;
            Assert.IsFalse(result.Meshes.Any(mesh => mesh.ColorTextureId == placeholderColorTextureId),
                "The known 512px pure-black material variant must not override the visible section.");
            Assert.IsTrue(result.Meshes.Any(mesh => mesh.ColorTextureId == preferredColorTextureId),
                "The matching 4096px visible material variant must remain in the preview.");
        }
        if (modRelativePath.StartsWith("2026年5月2日安德莉亚", StringComparison.Ordinal) ||
            modRelativePath.StartsWith("VRC_瑞希", StringComparison.Ordinal))
        {
            Assert.IsTrue(result.Meshes
                .SelectMany(mesh => mesh.MaterialTextures.Inputs ?? [])
                .Any(input => input.SemanticId == 0xFF2C91CC && input.Role == ModelPreviewTextureRole.BaseColor),
                "Character material AlbedoIridescence must be selected as a BaseColor input, not an unknown fallback.");
        }
        foreach (var variant in variants)
        {
            var descriptions = new List<string>();
            foreach (var mesh in variant.OrderByDescending(static mesh => mesh.ColorTextureId))
            {
                var colorTexture = result.Textures.Single(texture => texture.TextureId == mesh.ColorTextureId);
                var preview = await service.PreviewTextureAsync(modDirectory, colorTexture, maxPreviewPixels: 256);
                descriptions.Add(
                    $"Mat=0x{mesh.MaterialId:X16} Color=0x{mesh.ColorTextureId:X16}:{textureSizes[mesh.ColorTextureId!.Value]} Avg={GetAverageRgb(preview)}");
            }
            Console.WriteLine(
                $"{modDirectory.Name}: Unit=0x{variant.Key.UnitId:X16} St={variant.Key.StreamIndex} MI={variant.Key.MeshInfoIndex} " +
                $"VO={variant.Key.SourceVertexOffset} VC={variant.Key.VertexCount} Tri={variant.Key.TriangleCount} :: {string.Join(" | ", descriptions)}");
        }

        foreach (var material in result.Meshes
                     .Where(static mesh => mesh.MaterialId.HasValue)
                     .GroupBy(static mesh => mesh.MaterialId!.Value)
                     .OrderBy(static group => group.Key))
        {
            var representative = material.First();
            var inputs = string.Join(", ", representative.MaterialTextures.ByRole
                .OrderBy(static pair => pair.Key)
                .Select(pair => $"{pair.Key}=[{string.Join("/", pair.Value.Select(id => $"0x{id:X16}:{textureSizes.GetValueOrDefault(id, "?")}"))}]"));
            var semanticInputs = string.Join(", ", (representative.MaterialTextures.Inputs ?? [])
                .Select(input => $"0x{input.SemanticId:X8}=0x{input.TextureId:X16}"));
            var colorAverage = representative.ColorTextureId is ulong colorTextureId
                ? GetAverageRgb(await service.PreviewTextureAsync(
                    modDirectory,
                    result.Textures.Single(texture => texture.TextureId == colorTextureId),
                    maxPreviewPixels: 256))
                : "-";
            Console.WriteLine(
                $"{modDirectory.Name}: Material=0x{material.Key:X16} Meshes={material.Count()} " +
                $"Color={(representative.ColorTextureId is ulong id ? $"0x{id:X16}:{textureSizes[id]} Avg={colorAverage}" : "-")} {inputs} Inputs=[{semanticInputs}]");
        }

        Console.WriteLine($"{modDirectory.Name}: materialVariants={variants.Length}, materialBoundMeshes={result.Meshes.Count(mesh => mesh.MaterialId.HasValue)}");
    }

    private static string GetAverageRgb(TexturePreviewData? preview)
    {
        if (preview?.BgraPixels is not { Length: >= 4 } pixels)
            return "-";

        long red = 0;
        long green = 0;
        long blue = 0;
        var count = pixels.Length / 4;
        for (var offset = 0; offset < count * 4; offset += 4)
        {
            blue += pixels[offset];
            green += pixels[offset + 1];
            red += pixels[offset + 2];
        }

        return $"({red / count},{green / count},{blue / count})";
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
