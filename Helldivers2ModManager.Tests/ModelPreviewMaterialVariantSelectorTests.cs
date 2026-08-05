using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewMaterialVariantSelectorTests
{
    [TestMethod]
    public void SelectPreferredVariants_SameGeometryDifferentIndexOffset_KeepsLargerColorTexture()
    {
        var small = CreateMesh(indexOffset: 0, indexCount: 24_294, colorTextureId: 1);
        var large = CreateMesh(indexOffset: 24_294, indexCount: 24_294, colorTextureId: 2);

        var selected = ModelPreviewMaterialVariantSelector.SelectPreferredVariants(
            [small, large],
            textureId => textureId == 1 ? 512L * 512 : 4096L * 4096);

        Assert.AreEqual(1, selected.Count);
        Assert.AreEqual((ulong?)2, selected[0].ColorTextureId);
    }

    [TestMethod]
    public void SelectPreferredVariants_DifferentIndexCounts_RetainsBothSections()
    {
        var first = CreateMesh(indexOffset: 0, indexCount: 24_294, colorTextureId: 1);
        var second = CreateMesh(indexOffset: 24_294, indexCount: 24_297, colorTextureId: 2);

        var selected = ModelPreviewMaterialVariantSelector.SelectPreferredVariants([first, second], _ => 1);

        Assert.AreEqual(2, selected.Count);
    }

    [TestMethod]
    public void IsBc7PureBlackPlaceholder_RepeatedSparseBlocks_ReturnsTrue()
    {
        var bytes = new byte[64];
        bytes[0] = 1;
        bytes[3] = 2;
        bytes[16] = 1;
        bytes[19] = 2;
        bytes[32] = 1;
        bytes[35] = 2;
        bytes[48] = 1;
        bytes[51] = 2;

        Assert.IsTrue(ModelPreviewMaterialVariantSelector.IsBc7PureBlackPlaceholder(bytes));
    }

    [TestMethod]
    public void IsBc7PureBlackPlaceholder_SparseSamplesWithDifferentLaterBlock_ReturnsFalse()
    {
        var bytes = new byte[64 * 5];
        for (var sample = 0; sample < 5; sample++)
        {
            bytes[sample * 64] = 1;
            bytes[sample * 64 + 3] = 2;
            bytes[sample * 64 + 16] = 1;
            bytes[sample * 64 + 19] = 2;
            bytes[sample * 64 + 32] = 1;
            bytes[sample * 64 + 35] = 2;
            bytes[sample * 64 + 48] = 1;
            bytes[sample * 64 + 51] = 2;
        }
        bytes[^1] = 9;

        Assert.IsFalse(ModelPreviewMaterialVariantSelector.IsBc7PureBlackPlaceholder(bytes));
    }

    [TestMethod]
    public void IsBc7PureBlackPlaceholder_DifferentOrDenseBlock_ReturnsFalse()
    {
        var different = new byte[64];
        different[0] = 1;
        different[16] = 2;
        var dense = Enumerable.Repeat((byte)1, 64).ToArray();

        Assert.IsFalse(ModelPreviewMaterialVariantSelector.IsBc7PureBlackPlaceholder(different));
        Assert.IsFalse(ModelPreviewMaterialVariantSelector.IsBc7PureBlackPlaceholder(dense));
    }

    [TestMethod]
    public void IsOpaqueBgraPureBlack_RequiresEveryPixelToBeOpaqueBlack()
    {
        Assert.IsTrue(ModelPreviewMaterialVariantSelector.IsOpaqueBgraPureBlack(
            [0, 0, 0, 255, 0, 0, 0, 255]));
        Assert.IsFalse(ModelPreviewMaterialVariantSelector.IsOpaqueBgraPureBlack(
            [0, 0, 0, 0]));
        Assert.IsFalse(ModelPreviewMaterialVariantSelector.IsOpaqueBgraPureBlack(
            [0, 0, 1, 255]));
    }

    [TestMethod]
    public void FilterPureBlackPlaceholders_NonBlackPeerInSameStream_RemovesOnlyPlaceholder()
    {
        var black = CreateMesh(indexOffset: 0, indexCount: 3, colorTextureId: 1);
        var color = CreateMesh(indexOffset: 3, indexCount: 6, colorTextureId: 2);

        var selected = ModelPreviewMaterialVariantSelector.FilterPureBlackPlaceholders(
            [black, color],
            new HashSet<ulong> { 1 });

        Assert.AreEqual(1, selected.Count);
        Assert.AreEqual((ulong?)2, selected[0].ColorTextureId);
    }

    [TestMethod]
    public void FilterPureBlackPlaceholders_OnlyColorInStream_RetainsSection()
    {
        var black = CreateMesh(indexOffset: 0, indexCount: 3, colorTextureId: 1);

        var selected = ModelPreviewMaterialVariantSelector.FilterPureBlackPlaceholders(
            [black],
            new HashSet<ulong> { 1 });

        Assert.AreEqual(1, selected.Count);
    }

    private static ModelPreviewMesh CreateMesh(uint indexOffset, uint indexCount, ulong colorTextureId) => new()
    {
        PatchFile = "sample.patch_0",
        UnitId = 1,
        StreamIndex = 0,
        MeshInfoIndex = 4,
        SourceVertexOffset = 16,
        SourceVertexCount = 4_953,
        SourceIndexOffset = indexOffset,
        SourceIndexCount = indexCount,
        Positions = [],
        TriangleIndices = [],
        MaterialId = 42,
        ColorTextureId = colorTextureId
    };
}
