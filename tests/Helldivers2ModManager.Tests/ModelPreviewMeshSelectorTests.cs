using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewMeshSelectorTests
{
    [TestMethod]
    public void Select_HidesOnlyExtremeOutlier_AndKeepsItAvailableForManualInspection()
    {
        var normalMeshes = Enumerable.Range(0, 4)
            .Select(index => CreateMesh(index, 10))
            .ToArray();
        var outlier = CreateMesh(4, 100);
        var meshes = normalMeshes.Append(outlier).ToArray();

        var selection = ModelPreviewMeshSelector.Select(meshes);

        Assert.AreEqual(4, selection.VisibleMeshes.Count);
        Assert.AreEqual(1, selection.HiddenMeshCount);
        Assert.AreEqual(ModelPreviewMeshRenderStatus.HiddenLargeOutlier, outlier.RenderStatus);
        CollectionAssert.DoesNotContain(selection.VisibleMeshes.ToList(), outlier);
        Assert.AreSame(outlier, meshes.Single(mesh => mesh.StreamIndex == 4));
    }

    [TestMethod]
    public void Select_DoesNotHideMeshes_WhenThereAreTooFewPeers()
    {
        var meshes = new[] { CreateMesh(0, 10), CreateMesh(1, 1000) };

        var selection = ModelPreviewMeshSelector.Select(meshes);

        Assert.AreEqual(2, selection.VisibleMeshes.Count);
        Assert.AreEqual(0, selection.HiddenMeshCount);
        Assert.IsTrue(meshes.All(mesh => mesh.RenderStatus == ModelPreviewMeshRenderStatus.Visible));
    }

    [TestMethod]
    public void Select_DoesNotHideMesh_AtExactScaleBoundary()
    {
        var normalMeshes = Enumerable.Range(0, 4)
            .Select(index => CreateMesh(index, 10))
            .ToArray();
        var boundaryMesh = CreateMesh(4, 80);
        var meshes = normalMeshes.Append(boundaryMesh).ToArray();

        var selection = ModelPreviewMeshSelector.Select(meshes);

        Assert.AreEqual(5, selection.VisibleMeshes.Count);
        Assert.AreEqual(0, selection.HiddenMeshCount);
        Assert.AreEqual(ModelPreviewMeshRenderStatus.Visible, boundaryMesh.RenderStatus);
    }

    [TestMethod]
    public void Select_HidesLowComplexityProxyGeometry()
    {
        var proxy = CreateMesh(0, 10, vertexCount: 24, triangleCount: 12);
        var body = CreateMesh(1, 10);

        var selection = ModelPreviewMeshSelector.Select([proxy, body]);

        Assert.AreSame(body, selection.VisibleMeshes.Single());
        Assert.AreEqual(ModelPreviewMeshRenderStatus.HiddenProxyGeometry, proxy.RenderStatus);
        Assert.AreEqual(1, selection.HiddenMeshCount);
    }

    [TestMethod]
    public void Select_HidesMaterialDefinedCullingBody_RegardlessOfGeometryShape()
    {
        var cullingBox = CreateMesh(0, 10, vertexCount: 926, triangleCount: 1_544, isCullingBody: true);
        var body = CreateMesh(1, 10);

        var selection = ModelPreviewMeshSelector.Select([cullingBox, body]);

        Assert.AreSame(body, selection.VisibleMeshes.Single());
        Assert.AreEqual(ModelPreviewMeshRenderStatus.HiddenCullingBody, cullingBox.RenderStatus);
        Assert.AreEqual(1, selection.HiddenMeshCount);
    }

    [TestMethod]
    public void Select_HidesRegularCollisionSphere_WithoutUsingTextureHeuristics()
    {
        var sphere = CreateSphereMesh(0);
        var body = CreateMesh(1, 10);

        var selection = ModelPreviewMeshSelector.Select([sphere, body]);

        Assert.AreSame(body, selection.VisibleMeshes.Single());
        Assert.AreEqual(ModelPreviewMeshRenderStatus.HiddenCollisionSphere, sphere.RenderStatus);
        Assert.AreEqual(1, selection.HiddenMeshCount);
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
        var selection = ModelPreviewMeshSelector.Select([sphere, body]);

        var defaultRenderSet = ModelPreviewMeshSelector.GetRenderMeshes(selection, [sphere, body], sphere, isolateSelectedMesh: true, showFilteredMeshes: false);
        var explicitInspectionSet = ModelPreviewMeshSelector.GetRenderMeshes(selection, [sphere, body], sphere, isolateSelectedMesh: true, showFilteredMeshes: true);

        CollectionAssert.AreEqual(new[] { body }, defaultRenderSet.ToArray());
        CollectionAssert.AreEqual(new[] { sphere }, explicitInspectionSet.ToArray());
    }

    private static ModelPreviewMesh CreateMesh(
        int streamIndex,
        float size,
        int vertexCount = 30,
        int triangleCount = 20,
        bool isCullingBody = false) => new()
    {
        PatchFile = "sample.patch_0",
        UnitId = 1,
        StreamIndex = streamIndex,
        Positions = Enumerable.Range(0, vertexCount)
            .SelectMany(index => new[] { (float)index * size, 0f, 0f })
            .ToArray(),
        TriangleIndices = Enumerable.Range(0, triangleCount * 3)
            .Select(index => index % vertexCount)
            .ToArray(),
        IsCullingBody = isCullingBody
    };

    private static ModelPreviewMesh CreateSphereMesh(int streamIndex, float zScale = 1, bool unevenLatitudeSampling = false) => new()
    {
        PatchFile = "sample.patch_0",
        UnitId = 2,
        StreamIndex = streamIndex,
        Positions = Enumerable.Range(0, 439)
            .SelectMany(index =>
            {
                var progress = (index + 0.5f) / 439f;
                var y = unevenLatitudeSampling
                    ? 1f - 2f * progress * progress * progress
                    : 1f - 2f * progress;
                var radius = MathF.Sqrt(1f - y * y);
                var angle = MathF.PI * (3f - MathF.Sqrt(5f)) * index;
                return new[] { radius * MathF.Cos(angle), y, radius * MathF.Sin(angle) * zScale };
            })
            .ToArray(),
        TriangleIndices = Enumerable.Range(0, 760 * 3)
            .Select(index => index % 439)
            .ToArray()
    };
}
