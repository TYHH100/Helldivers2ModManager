using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Buffers.Binary;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class VersionCheckMaterialParentMigrationTests
{
    [TestMethod]
    public void TryGetLegacyMaterialParentMigration_LegacyCharacterMaterial_UsesCurrentParent()
    {
        var material = CreateMaterial(
            0x54AE9CE1A8FAFE8BUL,
            [
                0x7CA0D044, 0xC985395A, 0xA72CB013, 0x479FB1EF, 0xDF3EE984,
                0xCAED6CD6, 0xD2F99D38, 0xE7BD9019, 0xD47DB28B, 0xFF2C91CC,
                0x736A0029, 0xF8E31D7B, 0xA59F5E11
            ]);

        var migrated = VersionCheckService.TryGetLegacyMaterialParentMigration(
            material,
            out var oldParent,
            out var newParent);

        Assert.IsTrue(migrated);
        Assert.AreEqual(0x54AE9CE1A8FAFE8BUL, oldParent);
        Assert.AreEqual(0x8F669F365F24594EUL, newParent);
    }

    [TestMethod]
    public void TryGetLegacyMaterialParentMigration_LegacyEmissiveMaterial_DoesNotChangeSchema()
    {
        var material = CreateLegacyEmissiveMaterial();

        var migrated = VersionCheckService.TryGetLegacyMaterialParentMigration(
            material,
            out var oldParent,
            out var newParent);

        Assert.IsFalse(migrated);
        Assert.AreEqual(0UL, oldParent);
        Assert.AreEqual(0UL, newParent);
    }

    [TestMethod]
    public void TryBuildLegacyEmissiveMaterialMigration_ExactLegacySchema_UsesCurrentFourInputLayout()
    {
        var material = CreateLegacyEmissiveMaterial(
            0x1111111111111111UL,
            0x2222222222222222UL,
            0x3333333333333333UL);

        var migrated = VersionCheckService.TryBuildLegacyEmissiveMaterialMigration(
            material,
            out var updated);

        Assert.IsTrue(migrated);
        Assert.AreEqual(480, updated.Length);
        Assert.AreEqual(0xC6042E3403385D40UL, BinaryPrimitives.ReadUInt64LittleEndian(updated.AsSpan(0x18)));
        Assert.AreEqual(4U, BinaryPrimitives.ReadUInt32LittleEndian(updated.AsSpan(0x40)));
        Assert.AreEqual(12U, BinaryPrimitives.ReadUInt32LittleEndian(updated.AsSpan(0x68)));
        Assert.AreEqual(56U, BinaryPrimitives.ReadUInt32LittleEndian(updated.AsSpan(0x78)));
        CollectionAssert.AreEqual(
            new uint[] { 0x1D57DCF3U, 0xCA6F2CF1U, 0x848BA63BU, 0xCBDE381BU },
            ReadUInt32s(updated, 0x88, 4));
        CollectionAssert.AreEqual(
            new ulong[] { 0x1111111111111111UL, 0x2222222222222222UL, 0x3333333333333333UL, 0x12D4692531C1FD35UL },
            ReadUInt64s(updated, 0x98, 4));

        Assert.AreEqual(1.0f, BinaryPrimitives.ReadSingleLittleEndian(updated.AsSpan(0x1A8 + 4)));
        Assert.AreEqual(0.5f, BinaryPrimitives.ReadSingleLittleEndian(updated.AsSpan(0x1A8 + 24)));
        Assert.AreEqual(0.144f, BinaryPrimitives.ReadSingleLittleEndian(updated.AsSpan(0x1A8 + 48)));
        Assert.AreEqual(1.0f, BinaryPrimitives.ReadSingleLittleEndian(updated.AsSpan(0x1A8 + 52)));
    }

    [TestMethod]
    public void TryBuildLegacyEmissiveMaterialMigration_UnexpectedVariableDescriptor_IsRejected()
    {
        var material = CreateLegacyEmissiveMaterial();
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(0xAC + 8), 0xFFFFFFFF);

        var migrated = VersionCheckService.TryBuildLegacyEmissiveMaterialMigration(
            material,
            out var updated);

        Assert.IsFalse(migrated);
        Assert.AreEqual(0, updated.Length);
    }

    [TestMethod]
    public void TryBuildLegacyEmissiveMaterialMigration_UnexpectedTextureSemantic_IsRejected()
    {
        var material = CreateLegacyEmissiveMaterial();
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(0x88), 0xFFFFFFFF);

        var migrated = VersionCheckService.TryBuildLegacyEmissiveMaterialMigration(
            material,
            out var updated);

        Assert.IsFalse(migrated);
        Assert.AreEqual(0, updated.Length);
    }

    [TestMethod]
    public void TryGetLegacyMaterialParentMigration_MismatchedSignature_DoesNotMigrate()
    {
        var material = CreateMaterial(
            0x54AE9CE1A8FAFE8BUL,
            [
                0x7CA0D044, 0xC985395A, 0xA72CB013, 0x479FB1EF, 0xDF3EE984,
                0xCAED6CD6, 0xD2F99D38, 0xE7BD9019, 0xD47DB28B, 0xFF2C91CC,
                0x736A0029, 0xF8E31D7B, 0x00000000
            ]);

        var migrated = VersionCheckService.TryGetLegacyMaterialParentMigration(
            material,
            out var oldParent,
            out var newParent);

        Assert.IsFalse(migrated);
        Assert.AreEqual(0UL, oldParent);
        Assert.AreEqual(0UL, newParent);
    }

    private static byte[] CreateMaterial(ulong parentMaterialId, uint[] textureSemantics)
    {
        var material = new byte[0x88 + textureSemantics.Length * sizeof(uint)];
        BinaryPrimitives.WriteUInt64LittleEndian(material.AsSpan(0x18), parentMaterialId);
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(0x40), (uint)textureSemantics.Length);
        for (var index = 0; index < textureSemantics.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                material.AsSpan(0x88 + index * sizeof(uint)),
                textureSemantics[index]);
        }

        return material;
    }

    private static byte[] CreateLegacyEmissiveMaterial(
        ulong normalTextureId = 0x810CBDF684A1B921UL,
        ulong emissiveTextureId = 0xFA3E32046E67DA4FUL,
        ulong baseColorTextureId = 0x36028315FE967A0CUL)
    {
        var material = new byte[512];
        BinaryPrimitives.WriteUInt32LittleEndian(material, 0x102);
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(8), 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(12), 0x1E8);
        BinaryPrimitives.WriteUInt64LittleEndian(material.AsSpan(0x18), 0xD3701FC725106C09UL);
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(0x40), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(0x68), 14);
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(0x78), 60);

        var semantics = new[] { 0x1D57DCF3U, 0xCA6F2CF1U, 0x848BA63BU };
        for (var index = 0; index < semantics.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(0x88 + index * 4), semantics[index]);
        var textureIds = new[] { normalTextureId, emissiveTextureId, baseColorTextureId };
        for (var index = 0; index < textureIds.Length; index++)
            BinaryPrimitives.WriteUInt64LittleEndian(material.AsSpan(0x94 + index * 8), textureIds[index]);

        var variables = new (uint Id, uint Offset, float Value)[]
        {
            (0xA3351311, 0, 1), (0x43695F7B, 4, 3), (0x64AAB07B, 8, 1),
            (0x6FD0B9E7, 12, 0), (0x60E7D2A1, 16, 1), (0x4A7CD0EF, 20, 0.5f),
            (0x4A6796C6, 24, 0), (0xBD16A396, 28, 1), (0x32C02400, 56, 80),
            (0xC012EFE1, 36, 0), (0xA83F44CD, 40, 1), (0x6DDBAE8F, 44, 1),
            (0x4B564F57, 48, 65535), (0x9ED04DA2, 52, 1)
        };
        for (var index = 0; index < variables.Length; index++)
        {
            var descriptorOffset = 0xAC + index * 20;
            BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(descriptorOffset + 8), variables[index].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(descriptorOffset + 12), variables[index].Offset);
            BinaryPrimitives.WriteInt32LittleEndian(material.AsSpan(0x1C4 + (int)variables[index].Offset),
                BitConverter.SingleToInt32Bits(variables[index].Value));
        }

        return material;
    }

    private static uint[] ReadUInt32s(byte[] data, int offset, int count)
    {
        var values = new uint[count];
        for (var index = 0; index < count; index++)
            values[index] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + index * 4));
        return values;
    }

    private static ulong[] ReadUInt64s(byte[] data, int offset, int count)
    {
        var values = new ulong[count];
        for (var index = 0; index < count; index++)
            values[index] = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset + index * 8));
        return values;
    }
}
