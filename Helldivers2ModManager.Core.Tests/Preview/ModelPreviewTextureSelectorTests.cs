using Helldivers2ModManager.Core.Preview;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Preview;

[TestClass]
public sealed class ModelPreviewTextureSelectorTests
{
    [TestMethod]
    public void SelectAutomaticTextureIds_SemanticMaterial_KeepsBaseColorAndEmissiveOnly()
    {
        const ulong baseColorId = 1;
        const ulong normalId = 2;
        const ulong maskId = 3;
        const ulong emissiveId = 4;
        var mesh = CreateMesh(
            textureIds: [baseColorId, normalId, maskId, emissiveId],
            colorTextureId: baseColorId,
            materialTextures: new ModelPreviewMaterialTextureSet(
                new Dictionary<ModelPreviewTextureRole, IReadOnlyList<ulong>>
                {
                    [ModelPreviewTextureRole.BaseColor] = [baseColorId],
                    [ModelPreviewTextureRole.Normal] = [normalId],
                    [ModelPreviewTextureRole.Mask] = [maskId],
                    [ModelPreviewTextureRole.Emissive] = [emissiveId]
                },
                [baseColorId, normalId, maskId, emissiveId],
                baseColorId));

        var selected = ModelPreviewTextureSelector.SelectAutomaticTextureIds([mesh], 16);

        CollectionAssert.AreEqual(new[] { baseColorId, emissiveId }, selected.ToArray());
    }

    [TestMethod]
    public void SelectAutomaticTextureIds_LegacyMaterial_UsesBoundedTextureIdFallback()
    {
        var mesh = CreateMesh(textureIds: [11UL, 12UL, 13UL]);

        var selected = ModelPreviewTextureSelector.SelectAutomaticTextureIds([mesh], 2);

        CollectionAssert.AreEqual(new[] { 11UL, 12UL }, selected.ToArray());
    }

    [TestMethod]
    public void FindPreferredTextureId_ResolvedAlbedo_PrecedesLargerGrayscaleColorCandidate()
    {
        const ulong grayscaleTextureId = 0x08DA923A9F943D02;
        const ulong albedoTextureId = 0xFBA32BE2CB401D8D;
        var mesh = CreateMesh(
            textureIds: [grayscaleTextureId, albedoTextureId],
            colorTextureId: albedoTextureId);
        var loadedTextures = new Dictionary<ulong, TexturePreviewCandidate>
        {
            [grayscaleTextureId] = new(TexturePreviewRole.ColorCandidate, 4096L * 4096),
            [albedoTextureId] = new(TexturePreviewRole.LikelyNormalMap, 1)
        };

        var selected = ModelPreviewTextureSelector.FindPreferredTextureId(mesh, loadedTextures);

        Assert.AreEqual((ulong?)albedoTextureId, selected);
    }

    private static ModelPreviewMesh CreateMesh(
        ulong[]? textureIds = null,
        ulong? colorTextureId = null,
        ModelPreviewMaterialTextureSet? materialTextures = null) => new()
    {
        PatchFile = "sample.patch_0",
        UnitId = 1,
        StreamIndex = 0,
        Positions = [],
        TriangleIndices = [],
        TextureIds = textureIds ?? [],
        ColorTextureId = colorTextureId,
        MaterialTextures = materialTextures ?? ModelPreviewMaterialTextureSet.Empty
    };
}
