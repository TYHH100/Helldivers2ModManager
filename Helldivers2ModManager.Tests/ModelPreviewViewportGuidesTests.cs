using Helldivers2ModManager.Models;
using Helldivers2ModManager.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows.Media.Media3D;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewViewportGuidesTests
{
    [TestMethod]
    public void GetCameraDirection_RotatingAndTiltingCamera_ReportsAllViewportDirections()
    {
        Assert.AreEqual(
            ModelPreviewCameraDirection.Front,
            ModelPreviewViewportGuides.GetCameraDirection(0, 0, 0));
        Assert.AreEqual(
            ModelPreviewCameraDirection.Right,
            ModelPreviewViewportGuides.GetCameraDirection(90, 0, 0));
        Assert.AreEqual(
            ModelPreviewCameraDirection.Back,
            ModelPreviewViewportGuides.GetCameraDirection(180, 0, 0));
        Assert.AreEqual(
            ModelPreviewCameraDirection.Left,
            ModelPreviewViewportGuides.GetCameraDirection(-90, 0, 0));
        Assert.AreEqual(
            ModelPreviewCameraDirection.Top,
            ModelPreviewViewportGuides.GetCameraDirection(45, 0, 55));
        Assert.AreEqual(
            ModelPreviewCameraDirection.Bottom,
            ModelPreviewViewportGuides.GetCameraDirection(45, 0, -55));
    }

    [TestMethod]
    public void GetAxisGizmo_RotatingCamera_ReprojectsVisibleAndFacingAxes()
    {
        var lookingAlongPositiveX = ModelPreviewViewportGuides.GetAxisGizmo(0, 0);
        var lookingAlongForwardZ = ModelPreviewViewportGuides.GetAxisGizmo(-90, 0);

        Assert.IsTrue(lookingAlongPositiveX.X.ScreenLength < 0.000001d);
        Assert.IsTrue(lookingAlongPositiveX.X.Depth > 0.99d);
        Assert.IsTrue(lookingAlongPositiveX.Z.ScreenX > 0.99d);
        Assert.IsTrue(lookingAlongForwardZ.Z.ScreenLength < 0.000001d);
        Assert.IsTrue(lookingAlongForwardZ.Z.Depth > 0.99d);
        Assert.IsTrue(lookingAlongForwardZ.X.ScreenLength > 0.99d);
    }

    [TestMethod]
    public void GetAxisView_AxisAndOppositeSelection_ReturnsExpectedAxisAlignedPoses()
    {
        Assert.AreEqual(
            new ModelPreviewCameraPose(0, 0),
            ModelPreviewViewportGuides.GetAxisView(ModelPreviewGizmoAxis.X, opposite: false));
        Assert.AreEqual(
            new ModelPreviewCameraPose(180, 0),
            ModelPreviewViewportGuides.GetAxisView(ModelPreviewGizmoAxis.X, opposite: true));
        Assert.AreEqual(
            new ModelPreviewCameraPose(0, 90),
            ModelPreviewViewportGuides.GetAxisView(ModelPreviewGizmoAxis.Y, opposite: false));
        Assert.AreEqual(
            new ModelPreviewCameraPose(0, -90),
            ModelPreviewViewportGuides.GetAxisView(ModelPreviewGizmoAxis.Y, opposite: true));
        Assert.AreEqual(
            new ModelPreviewCameraPose(-90, 0),
            ModelPreviewViewportGuides.GetAxisView(ModelPreviewGizmoAxis.Z, opposite: false));
        Assert.AreEqual(
            new ModelPreviewCameraPose(90, 0),
            ModelPreviewViewportGuides.GetAxisView(ModelPreviewGizmoAxis.Z, opposite: true));
    }

    [TestMethod]
    public void GetCameraBasis_FullPitchTurn_KeepsOrthonormalScreenPlaneWithoutClamp()
    {
        foreach (var pitch in new[] { 0d, 90d, 180d, 270d, 360d })
        {
            var basis = ModelPreviewViewportGuides.GetCameraBasis(35, pitch);

            Assert.AreEqual(1d, basis.Forward.Length, 0.000001d, $"pitch={pitch}");
            Assert.AreEqual(1d, basis.Right.Length, 0.000001d, $"pitch={pitch}");
            Assert.AreEqual(1d, basis.Up.Length, 0.000001d, $"pitch={pitch}");
            Assert.AreEqual(0d, Vector3D.DotProduct(basis.Forward, basis.Right), 0.000001d, $"pitch={pitch}");
            Assert.AreEqual(0d, Vector3D.DotProduct(basis.Forward, basis.Up), 0.000001d, $"pitch={pitch}");
        }
    }

    [TestMethod]
    public void CreateGroundGridLayout_ValidAndInvalidBounds_ProducesBoundedFloorOrNoGrid()
    {
        var layout = ModelPreviewViewportGuides.CreateGroundGridLayout(
            new Rect3D(-2, 1, -3, 4, 8, 6));
        var empty = ModelPreviewViewportGuides.CreateGroundGridLayout(Rect3D.Empty);

        Assert.IsTrue(layout.HasGrid);
        Assert.AreEqual(0d, layout.CenterX, 0.000001d);
        Assert.AreEqual(0d, layout.CenterZ, 0.000001d);
        Assert.IsTrue(layout.FloorY < 1d);
        Assert.IsTrue(layout.HalfLineCount is >= 4 and <= 20);
        Assert.IsFalse(empty.HasGrid);
    }

    [TestMethod]
    public void GetTextureDecodePixelWidth_OriginalResolutionLeavesSourceUnscaled()
    {
        Assert.AreEqual(2048, ModelPreviewPageViewModel.GetTextureDecodePixelWidth(8192, useOriginalResolution: false));
        Assert.IsNull(ModelPreviewPageViewModel.GetTextureDecodePixelWidth(8192, useOriginalResolution: true));
    }
}
