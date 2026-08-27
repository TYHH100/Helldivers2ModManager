using Helldivers2ModManager.ViewModels;
using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using ModelPreviewMesh = Helldivers2ModManager.Core.Preview.ModelPreviewMesh;
using TexturePreviewData = Helldivers2ModManager.Core.Preview.TexturePreviewData;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewTextureBrushTests
{
    [TestMethod]
    [DataRow(202UL, 202UL, DisplayName = "Selected texture belongs to mesh")]
    [DataRow(303UL, null, DisplayName = "Selected texture belongs to another mesh")]
    public void GetSelectedTextureIdForMesh_SelectedTexture_ReturnsOnlyWhenMeshReferencesIt(
        ulong selectedTextureId,
        ulong? expectedTextureId)
    {
        var mesh = new ModelPreviewMesh
        {
            PatchFile = "sample.patch_0",
            UnitId = 1,
            StreamIndex = 0,
            Positions = [],
            TriangleIndices = [],
            TextureIds = [101, 202]
        };

        var result = ModelPreviewPageViewModel.GetSelectedTextureIdForMesh(mesh, selectedTextureId);

        Assert.AreEqual(expectedTextureId, result);
    }

    [TestMethod]
    public void CreateMaterial_WithTexture_UsesAbsoluteAtlasCoordinates()
    {
        var image = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[] { 0, 0, 0, 255 },
            4);
        image.Freeze();

        var material = ModelPreviewPageViewModel.CreateMaterial(image);

        Assert.IsInstanceOfType(material, typeof(DiffuseMaterial));
        var diffuseMaterial = (DiffuseMaterial)material;
        Assert.IsInstanceOfType(diffuseMaterial.Brush, typeof(ImageBrush));
        var imageBrush = (ImageBrush)diffuseMaterial.Brush;
        Assert.AreEqual(new Rect(0, 0, 1, 1), imageBrush.Viewbox);
        Assert.AreEqual(BrushMappingMode.RelativeToBoundingBox, imageBrush.ViewboxUnits);
        Assert.AreEqual(new Rect(0, 0, 1, 1), imageBrush.Viewport);
        Assert.AreEqual(BrushMappingMode.Absolute, imageBrush.ViewportUnits);
        Assert.AreEqual(TileMode.Tile, imageBrush.TileMode);
    }

    [TestMethod]
    public void CreateMaterial_WithoutTexture_UsesVisibleNonBlackFallback()
    {
        var material = ModelPreviewPageViewModel.CreateMaterial(null);

        Assert.IsInstanceOfType(material, typeof(DiffuseMaterial));
        var diffuseMaterial = (DiffuseMaterial)material;
        Assert.IsInstanceOfType(diffuseMaterial.Brush, typeof(SolidColorBrush));
        var brush = (SolidColorBrush)diffuseMaterial.Brush;
        Assert.AreEqual(Color.FromRgb(184, 193, 202), brush.Color);
        Assert.AreEqual(1d, brush.Opacity);
        Assert.AreNotEqual(Colors.Black, brush.Color);
        Assert.IsTrue(brush.IsFrozen);
        Assert.IsTrue(material.IsFrozen);
    }

    [TestMethod]
    public void CreateModelBitmapSource_BgraWithTransparentPackedAlpha_UsesOpaqueBgr32()
    {
        var preview = new TexturePreviewData
        {
            Width = 2,
            Height = 1,
            BgraPixels =
            [
                0x11, 0x22, 0x33, 0x00,
                0x44, 0x55, 0x66, 0x01
            ],
            Description = "Albedo with packed non-opacity alpha"
        };

        var image = ModelPreviewPageViewModel.CreateModelBitmapSource(preview);

        Assert.IsInstanceOfType(image, typeof(BitmapSource));
        var bitmap = (BitmapSource)image;
        Assert.AreEqual(PixelFormats.Bgr32, bitmap.Format);
        Assert.AreEqual(3, bitmap.Format.Masks.Count);
        var pixels = new byte[8];
        bitmap.CopyPixels(pixels, 8, 0);
        CollectionAssert.AreEqual(preview.BgraPixels, pixels);
    }
}
