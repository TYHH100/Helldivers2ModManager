using Helldivers2ModManager.Core.Preview;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Preview;

[TestClass]
public sealed class ModelPreviewMeshSelectorTests
{
    [TestMethod]
    public void Select_HidesOnlyExtremeOutlier_AndKeepsItAvailableForManualInspection()
    {
        var meshes = Enumerable.Range(0, 4).Select(index => CreateMesh(index, 10)).Append(CreateMesh(4, 100)).ToArray();
        var outlier = meshes[4];
        var selection = ModelPreviewMeshSelector.Select(meshes);
        Assert.AreEqual(4, selection.VisibleMeshes.Count);
        Assert.AreEqual(1, selection.HiddenMeshCount);
        Assert.AreEqual(ModelPreviewMeshRenderStatus.HiddenLargeOutlier, outlier.RenderStatus);
    }

    [TestMethod]
    public void Select_DoesNotHideMeshes_WhenThereAreTooFewPeers()
    {
        var meshes = new[] { CreateMesh(0, 10), CreateMesh(1, 1000) };
        var selection = ModelPreviewMeshSelector.Select(meshes);
        Assert.AreEqual(2, selection.VisibleMeshes.Count);
        Assert.IsTrue(meshes.All(mesh => mesh.RenderStatus == ModelPreviewMeshRenderStatus.Visible));
    }

    [TestMethod]
    public void Select_DoesNotHideMesh_AtExactScaleBoundary()
    {
        var meshes = Enumerable.Range(0, 4).Select(index => CreateMesh(index, 10)).Append(CreateMesh(4, 80)).ToArray();
        var selection = ModelPreviewMeshSelector.Select(meshes);
        Assert.AreEqual(5, selection.VisibleMeshes.Count);
        Assert.AreEqual(ModelPreviewMeshRenderStatus.Visible, meshes[4].RenderStatus);
    }

    [TestMethod]
    public void Select_HidesLowComplexityProxyGeometry()
    {
        var proxy = CreateMesh(0, 10, vertexCount: 24, triangleCount: 12);
        var body = CreateMesh(1, 10);
        var selection = ModelPreviewMeshSelector.Select([proxy, body]);
        Assert.AreSame(body, selection.VisibleMeshes.Single());
        Assert.AreEqual(ModelPreviewMeshRenderStatus.HiddenProxyGeometry, proxy.RenderStatus);
    }

    [TestMethod]
    public void Select_HidesMaterialDefinedCullingBody_RegardlessOfGeometryShape()
    {
        var cullingBox = CreateMesh(0, 10, vertexCount: 926, triangleCount: 1544, isCullingBody: true);
        var body = CreateMesh(1, 10);
        var selection = ModelPreviewMeshSelector.Select([cullingBox, body]);
        Assert.AreSame(body, selection.VisibleMeshes.Single());
        Assert.AreEqual(ModelPreviewMeshRenderStatus.HiddenCullingBody, cullingBox.RenderStatus);
    }

    [TestMethod]
    public void Select_HidesRegularCollisionSphere_WithoutUsingTextureHeuristics()
    {
        var sphere = CreateSphereMesh(0);
        var body = CreateMesh(1, 10);
        var selection = ModelPreviewMeshSelector.Select([sphere, body]);
        Assert.AreSame(body, selection.VisibleMeshes.Single());
        Assert.AreEqual(ModelPreviewMeshRenderStatus.HiddenCollisionSphere, sphere.RenderStatus);
    }

    [TestMethod]
    public void Select_HidesRegularCollisionSphere_WhenVertexSamplesAreUneven()
    {
        var sphere = CreateSphereMesh(0, unevenLatitudeSampling: true);
        var body = CreateMesh(1, 10);
        var selection = ModelPreviewMeshSelector.Select([sphere, body]);
        Assert.AreSame(body, selection.VisibleMeshes.Single());
        Assert.AreEqual(ModelPreviewMeshRenderStatus.HiddenCollisionSphere, sphere.RenderStatus);
    }
    [TestMethod]
    public void Select_KeepsRoundedButNonSphericalMesh()
    {
        var roundedPart = CreateSphereMesh(0, zScale: 1.2f);
        var body = CreateMesh(1, 10);
        var selection = ModelPreviewMeshSelector.Select([roundedPart, body]);
        Assert.IsTrue(selection.VisibleMeshes.Contains(roundedPart));
        Assert.AreEqual(ModelPreviewMeshRenderStatus.Visible, roundedPart.RenderStatus);
    }

    [TestMethod]
    public void GetRenderMeshes_DoesNotIsolateAutoHiddenMesh_UnlessShowHiddenIsEnabled()
    {
        var sphere = CreateSphereMesh(0);
        var body = CreateMesh(1, 10);
        var meshes = new[] { sphere, body };
        var selection = ModelPreviewMeshSelector.Select(meshes);
        Assert.AreSame(body, ModelPreviewMeshSelector.GetRenderMeshes(selection, meshes, sphere, true, false).Single());
        Assert.AreSame(sphere, ModelPreviewMeshSelector.GetRenderMeshes(selection, meshes, sphere, true, true).Single());
    }

    private static ModelPreviewMesh CreateMesh(int streamIndex, float size, int vertexCount = 30, int triangleCount = 20, bool isCullingBody = false) => new()
    {
        PatchFile = "sample.patch_0",
        UnitId = 1,
        StreamIndex = streamIndex,
        Positions = Enumerable.Range(0, vertexCount).SelectMany(index => new[] { index * size, 0f, 0f }).ToArray(),
        TriangleIndices = Enumerable.Range(0, triangleCount * 3).Select(index => index % vertexCount).ToArray(),
        IsCullingBody = isCullingBody,
    };

    private static ModelPreviewMesh CreateSphereMesh(int streamIndex, float zScale = 1, bool unevenLatitudeSampling = false) => new()
    {
        PatchFile = "sample.patch_0",
        UnitId = 2,
        StreamIndex = streamIndex,
        Positions = Enumerable.Range(0, 439).SelectMany(index =>
        {
            var progress = (index + 0.5f) / 439f;
            var y = unevenLatitudeSampling ? 1f - 2f * progress * progress * progress : 1f - 2f * progress;
            var radius = MathF.Sqrt(1f - y * y);
            var angle = MathF.PI * (3f - MathF.Sqrt(5f)) * index;
            return new[] { radius * MathF.Cos(angle), y, radius * MathF.Sin(angle) * zScale };
        }).ToArray(),
        TriangleIndices = Enumerable.Range(0, 760 * 3).Select(index => index % 439).ToArray(),
    };
}
