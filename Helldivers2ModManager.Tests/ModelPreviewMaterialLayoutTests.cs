using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Buffers.Binary;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewMaterialLayoutTests
{
    [TestMethod]
    public void TryReadUnitMaterialSections_BindsEachMeshSectionToItsOwnMaterialTextures()
    {
        var unit = new byte[0x340];
        WriteInt32(unit, 0x34, 0x250); // TransformInfoOffset
        WriteInt32(unit, 0x64, 0x100); // MeshInfoOffset
        WriteInt32(unit, 0x70, 0x80);  // MaterialsOffset

        WriteInt32(unit, 0x80, 2);
        WriteUInt32(unit, 0x84, 101);
        WriteUInt32(unit, 0x88, 202);
        WriteUInt64(unit, 0x8C, 1001);
        WriteUInt64(unit, 0x94, 2002);

        WriteInt32(unit, 0x100, 1);
        WriteInt32(unit, 0x104, 0x20);
        const int mesh = 0x120;
        WriteInt32(unit, mesh + 60, 3);     // StreamIndex
        WriteInt32(unit, mesh + 104, 2);    // NumMaterials
        WriteInt32(unit, mesh + 108, 0x80);
        WriteInt32(unit, mesh + 120, 2);    // NumSections
        WriteInt32(unit, mesh + 124, 0x88);
        WriteUInt32(unit, 0x1A0, 202);
        WriteUInt32(unit, 0x1A4, 101);

        WriteInt32(unit, 0x1A8, 0);         // MaterialIndex -> slot 202
        WriteUInt32(unit, 0x1AC, 10);       // VertexOffset
        WriteUInt32(unit, 0x1B0, 20);       // NumVertices
        WriteUInt32(unit, 0x1B4, 0);
        WriteUInt32(unit, 0x1B8, 6);
        WriteInt32(unit, 0x1C0, 1);         // MaterialIndex -> slot 101
        WriteUInt32(unit, 0x1C4, 10);
        WriteUInt32(unit, 0x1C8, 20);
        WriteUInt32(unit, 0x1CC, 6);
        WriteUInt32(unit, 0x1D0, 3);

        WriteInt32(unit, 0x250, 1); // One local transform followed by one matrix.
        const int matrix = 0x2A0;
        WriteSingle(unit, matrix + 0 * 4, 2);
        WriteSingle(unit, matrix + 5 * 4, 1);
        WriteSingle(unit, matrix + 10 * 4, 1);
        WriteSingle(unit, matrix + 12 * 4, 5);
        WriteSingle(unit, matrix + 13 * 4, 6);
        WriteSingle(unit, matrix + 14 * 4, 7);
        WriteSingle(unit, matrix + 15 * 4, 1);

        var layouts = PatchResourceInspectionService.TryReadUnitMaterialSections(
            unit,
            new Dictionary<ulong, ModelPreviewMaterialTextures>
            {
                [1001] = new([11], 11),
                [2002] = new([22, 23], 23)
            });

        var sections = layouts[3];
        Assert.AreEqual(2, sections.Count);
        CollectionAssert.AreEqual(new ulong[] { 22, 23 }, sections[0].TextureIds.ToArray());
        Assert.AreEqual((ulong?)23, sections[0].ColorTextureId);
        Assert.AreEqual((uint)10, sections[0].VertexOffset);
        Assert.AreEqual((uint)20, sections[0].VertexCount);
        Assert.AreEqual((uint)0, sections[0].IndexOffset);
        Assert.AreEqual((uint)6, sections[0].IndexCount);
        Assert.AreEqual((7f, 8f, 10f), sections[0].Transform.TransformPoint(1, 2, 3));
        CollectionAssert.AreEqual(new ulong[] { 11 }, sections[1].TextureIds.ToArray());
        Assert.AreEqual((ulong?)11, sections[1].ColorTextureId);
        Assert.AreEqual((uint)6, sections[1].IndexOffset);
        Assert.AreEqual((uint)3, sections[1].IndexCount);
        Assert.IsTrue(sections.All(section => !section.IsCullingBody));
    }

    [TestMethod]
    public void TryReadUnitMaterialSections_MarksDefaultMaterialMeshAsCullingBody()
    {
        var unit = new byte[0x220];
        WriteInt32(unit, 0x64, 0x100);
        WriteInt32(unit, 0x70, 0x80);
        WriteInt32(unit, 0x80, 0); // Unit has no explicit material mappings.

        WriteInt32(unit, 0x100, 1);
        WriteInt32(unit, 0x104, 0x20);
        const int mesh = 0x120;
        WriteInt32(unit, mesh + 60, 2);
        WriteInt32(unit, mesh + 104, 1);
        WriteInt32(unit, mesh + 108, 0x80);
        WriteInt32(unit, mesh + 120, 1);
        WriteInt32(unit, mesh + 124, 0x84);
        WriteUInt32(unit, 0x1A0, 155175220); // StingrayDefaultMaterial slot.
        WriteInt32(unit, 0x1A4, 0);
        WriteUInt32(unit, 0x1A8, 12);
        WriteUInt32(unit, 0x1AC, 439);
        WriteUInt32(unit, 0x1B0, 0);
        WriteUInt32(unit, 0x1B4, 2_280);

        var layouts = PatchResourceInspectionService.TryReadUnitMaterialSections(
            unit,
            new Dictionary<ulong, ModelPreviewMaterialTextures>());

        var section = layouts[2].Single();
        Assert.IsTrue(section.IsCullingBody);
        Assert.AreEqual(0, section.TextureIds.Count);
        Assert.AreEqual((uint)2_280, section.IndexCount);
    }

    [TestMethod]
    public void TryReadUnitMaterialSections_RepeatedLods_KeepsSmallestNonNegativeLodAndNegativeProxy()
    {
        var unit = new byte[0x900];
        WriteInt32(unit, 0x64, 0x100);
        WriteInt32(unit, 0x70, 0x80);
        WriteInt32(unit, 0x80, 1);
        WriteUInt32(unit, 0x84, 101);
        WriteUInt64(unit, 0x88, 1001);

        WriteInt32(unit, 0x100, 4);
        WriteInt32(unit, 0x104, 0x40);
        WriteInt32(unit, 0x108, 0x140);
        WriteInt32(unit, 0x10C, 0x240);
        WriteInt32(unit, 0x110, 0x340);
        ConfigureMeshInfo(unit, 0x140, lodIndex: 2, streamIndex: 0);
        ConfigureMeshInfo(unit, 0x240, lodIndex: 0, streamIndex: 0);
        ConfigureMeshInfo(unit, 0x340, lodIndex: 1, streamIndex: 0);
        ConfigureMeshInfo(unit, 0x440, lodIndex: -1, streamIndex: 0);

        var layouts = PatchResourceInspectionService.TryReadUnitMaterialSections(
            unit,
            new Dictionary<ulong, ModelPreviewMaterialTextures>
            {
                [1001] = new([11], 11)
            });

        var sections = layouts[0];
        CollectionAssert.AreEqual(new[] { 1, 3 }, sections.Select(section => section.MeshInfoIndex).ToArray());
        Assert.IsTrue(sections.All(section => section.ColorTextureId == 11));
    }

    [TestMethod]
    public void CreateSectionMesh_UsesMeshLocalIndicesAndAppliesUnitTransform()
    {
        var source = new ModelPreviewMesh
        {
            PatchFile = "sample.patch_0",
            UnitId = 7,
            StreamIndex = 2,
            Positions =
            [
                100, 100, 100,
                101, 100, 100,
                100, 101, 100,
                0, 0, 0,
                1, 0, 0,
                0, 1, 0
            ],
            TriangleIndices = [0, 1, 2]
        };
        var section = new ModelPreviewMaterialSection(
            MeshInfoIndex: 4,
            SectionIndex: 0,
            VertexOffset: 3,
            VertexCount: 3,
            IndexOffset: 0,
            IndexCount: 3,
            TextureIds: [42],
            ColorTextureId: 42,
            IsCullingBody: false,
            Transform: new ModelPreviewTransform(
                1, 0, 0, 10,
                0, 1, 0, 20,
                0, 0, 1, 30));

        var mesh = PatchResourceInspectionService.CreateSectionMesh(source, section);

        Assert.IsNotNull(mesh);
        Assert.AreEqual(4, mesh.MeshInfoIndex);
        Assert.AreEqual((uint)3, mesh.SourceVertexOffset);
        Assert.AreEqual((ulong?)42, mesh.ColorTextureId);
        CollectionAssert.AreEqual(
            new float[] { 10, 20, 30, 11, 20, 30, 10, 21, 30 },
            mesh.Positions);
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, mesh.TriangleIndices);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, sizeof(int)), value);

    private static void WriteUInt32(byte[] buffer, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, sizeof(uint)), value);

    private static void WriteUInt64(byte[] buffer, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, sizeof(ulong)), value);

    private static void WriteSingle(byte[] buffer, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, sizeof(float)), value);

    private static void ConfigureMeshInfo(byte[] unit, int meshOffset, int lodIndex, int streamIndex)
    {
        WriteInt32(unit, meshOffset + 56, lodIndex);
        WriteInt32(unit, meshOffset + 60, streamIndex);
        WriteInt32(unit, meshOffset + 104, 1);
        WriteInt32(unit, meshOffset + 108, 0x80);
        WriteInt32(unit, meshOffset + 120, 1);
        WriteInt32(unit, meshOffset + 124, 0x84);
        WriteUInt32(unit, meshOffset + 0x80, 101);
        WriteInt32(unit, meshOffset + 0x84, 0);
        WriteUInt32(unit, meshOffset + 0x88, 0);
        WriteUInt32(unit, meshOffset + 0x8C, 3);
        WriteUInt32(unit, meshOffset + 0x90, 0);
        WriteUInt32(unit, meshOffset + 0x94, 3);
    }
}
