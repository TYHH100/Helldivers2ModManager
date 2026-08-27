using Helldivers2ModManager.Core.Preview;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Buffers.Binary;

namespace Helldivers2ModManager.Core.Tests.Preview;

[TestClass]
public sealed class MaterialTextureReaderTests
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
        WriteUInt32(material, 0x88, 0xF5C97D31);
        WriteUInt32(material, 0x8c, 0xE97A4617);
        WriteUInt32(material, 0x90, 0x4DC19F08);
        WriteUInt32(material, 0x94, 0xE67AC0C7);
        WriteUInt64(material, 0x98, normalTextureId);
        WriteUInt64(material, 0xa0, maskTextureId);
        WriteUInt64(material, 0xa8, emissiveTextureId);
        WriteUInt64(material, 0xb0, albedoTextureId);

        var textures = MaterialTextureReader.TryReadMaterialTextures(
            material,
            new HashSet<ulong> { normalTextureId, maskTextureId, emissiveTextureId, albedoTextureId });

        Assert.IsNotNull(textures);
        CollectionAssert.AreEqual(
            new[] { normalTextureId, maskTextureId, emissiveTextureId, albedoTextureId },
            textures.TextureIds.ToArray());
        Assert.AreEqual(albedoTextureId, textures.ColorTextureId);
        CollectionAssert.AreEqual(
            new uint[] { 0xF5C97D31, 0xE97A4617, 0x4DC19F08, 0xE67AC0C7 },
            textures.Inputs!.Select(static input => input.SemanticId).ToArray());
        CollectionAssert.AreEqual(new[] { normalTextureId }, textures.TexturesByRole![ModelPreviewTextureRole.Normal].ToArray());
        CollectionAssert.AreEqual(new[] { maskTextureId }, textures.TexturesByRole[ModelPreviewTextureRole.Mask].ToArray());
        CollectionAssert.AreEqual(new[] { emissiveTextureId }, textures.TexturesByRole[ModelPreviewTextureRole.Emissive].ToArray());
        CollectionAssert.AreEqual(new[] { albedoTextureId }, textures.TexturesByRole[ModelPreviewTextureRole.BaseColor].ToArray());
    }

    [TestMethod]
    public void TryReadMaterialTextures_CharacterMaterialSemantics_ClassifiesOnlyRenderableRoles()
    {
        const ulong normalTextureId = 1;
        const ulong mraTextureId = 2;
        const ulong emissiveTextureId = 3;
        const ulong albedoTextureId = 4;
        const ulong opacityTextureId = 5;
        var material = new byte[0x140];
        WriteInt32(material, 0x40, 5);
        WriteUInt32(material, 0x88, 0xCAED6CD6);
        WriteUInt32(material, 0x8c, 0x756F6FA6);
        WriteUInt32(material, 0x90, 0xCA6F2CF1);
        WriteUInt32(material, 0x94, 0xFF2C91CC);
        WriteUInt32(material, 0x98, 0xCBDE381B);
        WriteUInt64(material, 0x9c, normalTextureId);
        WriteUInt64(material, 0xa4, mraTextureId);
        WriteUInt64(material, 0xac, emissiveTextureId);
        WriteUInt64(material, 0xb4, albedoTextureId);
        WriteUInt64(material, 0xbc, opacityTextureId);

        var textures = MaterialTextureReader.TryReadMaterialTextures(
            material,
            new HashSet<ulong> { normalTextureId, mraTextureId, emissiveTextureId, albedoTextureId, opacityTextureId });

        Assert.IsNotNull(textures);
        Assert.AreEqual(albedoTextureId, textures.ColorTextureId);
        CollectionAssert.AreEqual(new[] { normalTextureId }, textures.TexturesByRole![ModelPreviewTextureRole.Normal].ToArray());
        CollectionAssert.AreEqual(new[] { mraTextureId, opacityTextureId }, textures.TexturesByRole[ModelPreviewTextureRole.Mask].ToArray());
        CollectionAssert.AreEqual(new[] { emissiveTextureId }, textures.TexturesByRole[ModelPreviewTextureRole.Emissive].ToArray());
        CollectionAssert.AreEqual(new[] { albedoTextureId }, textures.TexturesByRole[ModelPreviewTextureRole.BaseColor].ToArray());
    }

    private static void WriteInt32(byte[] buffer, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, sizeof(int)), value);

    private static void WriteUInt32(byte[] buffer, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, sizeof(uint)), value);

    private static void WriteUInt64(byte[] buffer, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, sizeof(ulong)), value);
}
