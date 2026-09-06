using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewForceDecodeBudgetTests
{
    [TestMethod]
    public void TryAddMesh_DefaultBudget_RejectsMeshBeyondGlobalLimits()
    {
        var result = new ModelPreviewResult();
        var first = result.TryAddMesh(CreateMesh(300_000, 500_000));
        var second = result.TryAddMesh(CreateMesh(300_000, 500_000));
        var third = result.TryAddMesh(CreateMesh(300_000, 500_000));

        Assert.IsTrue(first);
        Assert.IsTrue(second);
        Assert.IsFalse(third);
        Assert.AreEqual(2, result.Meshes.Count);
        Assert.IsTrue(result.IsAtCapacity);
    }

    [TestMethod]
    public void TryAddMesh_ForceDecodeOversizedStreams_RaisesGlobalLimitsAndAdmitsSameMeshes()
    {
        var result = new ModelPreviewResult { ForceDecodeOversizedStreams = true };
        for (var index = 0; index < 3; index++)
            Assert.IsTrue(result.TryAddMesh(CreateMesh(300_000, 500_000)), $"Mesh {index} must be admitted in force mode.");

        Assert.IsFalse(result.IsAtCapacity);
    }

    [TestMethod]
    public void IsAtCapacity_ForceDecodeOversizedStreams_KeepsABudgetInsteadOfRemovingIt()
    {
        var result = new ModelPreviewResult { ForceDecodeOversizedStreams = true };

        // 强制模式只是放宽预算（8 倍），不是取消上限：仍然必须存在 admission control。
        // 1k 顶点 + 2k 三角形（6k 索引）的网格在 24M 强制索引预算下恰好容纳 4000 个。
        for (var index = 0; index < 4_000; index++)
            Assert.IsTrue(result.TryAddMesh(CreateMesh(1_000, 2_000)), $"Mesh {index} must be admitted in force mode.");
        Assert.IsFalse(result.TryAddMesh(CreateMesh(1_000, 2_000)));

        Assert.AreEqual(4_000, result.Meshes.Count);
        Assert.IsTrue(result.IsAtCapacity);
    }

    private static ModelPreviewMesh CreateMesh(int vertexCount, int triangleCount)
    {
        var triangleIndices = new int[triangleCount * 3];
        for (var index = 0; index < triangleIndices.Length; index++)
            triangleIndices[index] = index % vertexCount;

        return new ModelPreviewMesh
        {
            PatchFile = "synthetic.patch_0",
            UnitId = 1,
            StreamIndex = 0,
            Positions = new float[vertexCount * 3],
            TriangleIndices = triangleIndices
        };
    }
}
