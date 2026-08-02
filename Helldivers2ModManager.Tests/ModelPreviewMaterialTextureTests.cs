using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewMaterialTextureTests
{
    [TestMethod]
    public void TryReadMaterialTextures_ParallelSemanticTable_PreservesAlbedoEmissiveBinding()
    {
        const ulong normalTextureId = 0x32D2FA947BA6AC30;
        const ulong maskTextureId = 0x08DA923A9F943D02;
        const ulong emissiveTextureId = 0x19DA923A9F943D03;
        const ulong albedoTextureId = 0xFBA32BE2CB401D8D;
        var material = new byte[0x140];
        WriteInt32(material, 0x40, 4);
        WriteUInt32(material, 0x88, 0xF5C97D31); // NormalMap
        WriteUInt32(material, 0x8C, 0xE97A4617); // BaseMask
        WriteUInt32(material, 0x90, 0x4DC19F08); // EmissiveMap
        WriteUInt32(material, 0x94, 0xE67AC0C7); // AlbedoEmissive
        WriteUInt64(material, 0x98, normalTextureId);
        WriteUInt64(material, 0xA0, maskTextureId);
        WriteUInt64(material, 0xA8, emissiveTextureId);
        WriteUInt64(material, 0xB0, albedoTextureId);

        var textures = PatchResourceInspectionService.TryReadMaterialTextures(
            material,
            new HashSet<ulong> { normalTextureId, maskTextureId, emissiveTextureId, albedoTextureId });

        Assert.IsNotNull(textures);
        CollectionAssert.AreEqual(
            new[] { normalTextureId, maskTextureId, emissiveTextureId, albedoTextureId },
            textures.TextureIds.ToArray());
        Assert.AreEqual(albedoTextureId, textures.ColorTextureId);
        CollectionAssert.AreEqual(new[] { normalTextureId }, textures.TexturesByRole![ModelPreviewTextureRole.Normal].ToArray());
        CollectionAssert.AreEqual(new[] { maskTextureId }, textures.TexturesByRole[ModelPreviewTextureRole.Mask].ToArray());
        CollectionAssert.AreEqual(new[] { emissiveTextureId }, textures.TexturesByRole[ModelPreviewTextureRole.Emissive].ToArray());
        CollectionAssert.AreEqual(new[] { albedoTextureId }, textures.TexturesByRole[ModelPreviewTextureRole.BaseColor].ToArray());
    }

    [TestMethod]
    public void FindPreferredTextureId_ResolvedAlbedo_PrecedesLargerGrayscaleColorCandidate()
    {
        const ulong grayscaleTextureId = 0x08DA923A9F943D02;
        const ulong albedoTextureId = 0xFBA32BE2CB401D8D;
        var mesh = new ModelPreviewMesh
        {
            PatchFile = "sample.patch_0",
            UnitId = 1,
            StreamIndex = 0,
            Positions = [],
            TriangleIndices = [],
            TextureIds = [grayscaleTextureId, albedoTextureId],
            ColorTextureId = albedoTextureId
        };
        var loadedTextures = new Dictionary<ulong, ModelPreviewPageViewModel.LoadedTexturePreview>
        {
            [grayscaleTextureId] = new(new DrawingImage(), TexturePreviewRole.ColorCandidate, 4096L * 4096),
            // Deliberately give the resolved albedo the least favorable fallback
            // classification and size. Its Material semantic must still take precedence.
            [albedoTextureId] = new(new DrawingImage(), TexturePreviewRole.LikelyNormalMap, 1)
        };

        var selectedTextureId = ModelPreviewPageViewModel.FindPreferredTextureId(mesh, loadedTextures);

        Assert.AreEqual(albedoTextureId, selectedTextureId);
    }

    [TestMethod]
    public void CreateMaterial_AutomaticMode_ComposesBaseColorAndEmissiveInputs()
    {
        const ulong baseColorId = 1;
        const ulong emissiveId = 2;
        var mesh = new ModelPreviewMesh
        {
            PatchFile = "sample.patch_0",
            UnitId = 1,
            StreamIndex = 0,
            Positions = [],
            TriangleIndices = [],
            TextureIds = [baseColorId, emissiveId],
            MaterialTextures = new ModelPreviewMaterialTextureSet(
                new Dictionary<ModelPreviewTextureRole, IReadOnlyList<ulong>>
                {
                    [ModelPreviewTextureRole.BaseColor] = [baseColorId],
                    [ModelPreviewTextureRole.Emissive] = [emissiveId]
                },
                [baseColorId, emissiveId],
                baseColorId)
        };
        var loadedTextures = new Dictionary<ulong, ModelPreviewPageViewModel.LoadedTexturePreview>
        {
            [baseColorId] = new(new DrawingImage(), TexturePreviewRole.ColorCandidate, 4),
            [emissiveId] = new(new DrawingImage(), TexturePreviewRole.Unknown, 4)
        };

        var material = ModelPreviewPageViewModel.CreateMaterial(mesh, loadedTextures, true, null);

        var diffuse = (DiffuseMaterial)material;
        var brush = (DrawingBrush)diffuse.Brush;
        var drawing = (DrawingGroup)brush.Drawing;
        Assert.AreEqual(2, drawing.Children.Count);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, sizeof(int)), value);

    private static void WriteUInt32(byte[] buffer, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, sizeof(uint)), value);

    private static void WriteUInt64(byte[] buffer, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, sizeof(ulong)), value);
}
