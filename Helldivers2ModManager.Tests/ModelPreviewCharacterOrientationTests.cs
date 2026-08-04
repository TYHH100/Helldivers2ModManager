using Helldivers2ModManager.Models;
using Helldivers2ModManager.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows.Media.Media3D;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewCharacterOrientationTests
{
    [TestMethod]
    public void GetRequiredRotation_TorsoAboveLegsOnPositiveX_MapsPositiveXToViewportUp()
    {
        var rotation = ModelPreviewCharacterOrientation.GetRequiredRotation(
        [
            CreateMesh(ModelPreviewCustomizationSlot.Torso, 4, 0, 0),
            CreateMesh(ModelPreviewCustomizationSlot.LeftLeg, 0, 0, 0),
            CreateMesh(ModelPreviewCustomizationSlot.RightLeg, 0, 0, 0)
        ]);

        Assert.AreEqual(ModelPreviewPresentationRotation.PositiveXToPositiveY, rotation);
    }

    [TestMethod]
    public void GetRequiredRotation_TorsoAboveLegsOnNegativeZ_MapsNegativeZToViewportUp()
    {
        var rotation = ModelPreviewCharacterOrientation.GetRequiredRotation(
        [
            CreateMesh(ModelPreviewCustomizationSlot.Torso, 0, 0, -4),
            CreateMesh(ModelPreviewCustomizationSlot.LeftLeg, 0, 0, 0),
            CreateMesh(ModelPreviewCustomizationSlot.RightLeg, 0, 0, 0)
        ]);

        Assert.AreEqual(ModelPreviewPresentationRotation.NegativeZToPositiveY, rotation);
    }

    [TestMethod]
    public void GetRequiredRotation_AlreadyYUpOrMissingBodyParts_DoesNotRotate()
    {
        var alreadyYUp = ModelPreviewCharacterOrientation.GetRequiredRotation(
        [
            CreateMesh(ModelPreviewCustomizationSlot.Torso, 0, 4, 0),
            CreateMesh(ModelPreviewCustomizationSlot.LeftLeg, 0, 0, 0),
            CreateMesh(ModelPreviewCustomizationSlot.RightLeg, 0, 0, 0)
        ]);
        var propWithoutBodySlots = ModelPreviewCharacterOrientation.GetRequiredRotation(
        [
            CreateMesh(ModelPreviewCustomizationSlot.Unknown, 4, 0, 0),
            CreateMesh(ModelPreviewCustomizationSlot.Unknown, 0, 0, 0)
        ]);

        Assert.AreEqual(ModelPreviewPresentationRotation.None, alreadyYUp);
        Assert.AreEqual(ModelPreviewPresentationRotation.None, propWithoutBodySlots);
    }

    [TestMethod]
    public void GetRequiredRotation_AmbiguousTorsoToLegsDirection_DoesNotRotate()
    {
        var rotation = ModelPreviewCharacterOrientation.GetRequiredRotation(
        [
            CreateMesh(ModelPreviewCustomizationSlot.Torso, 3, 3, 0),
            CreateMesh(ModelPreviewCustomizationSlot.LeftLeg, 0, 0, 0),
            CreateMesh(ModelPreviewCustomizationSlot.RightLeg, 0, 0, 0)
        ]);

        Assert.AreEqual(ModelPreviewPresentationRotation.None, rotation);
    }

    [TestMethod]
    public void CreatePresentationTransform_PositiveZUp_MapsSourceUpToViewportY()
    {
        var transform = ModelPreviewPageViewModel.CreatePresentationTransform(
            new Vector3D(),
            ModelPreviewPresentationRotation.PositiveZToPositiveY);

        var transformedUp = transform.Transform(new Point3D(0, 0, 1));

        Assert.AreEqual(0d, transformedUp.X, 0.000001d);
        Assert.AreEqual(1d, transformedUp.Y, 0.000001d);
        Assert.AreEqual(0d, transformedUp.Z, 0.000001d);
    }

    private static ModelPreviewMesh CreateMesh(ModelPreviewCustomizationSlot slot, float x, float y, float z) => new()
    {
        PatchFile = "synthetic.patch_0",
        UnitId = 1,
        StreamIndex = 0,
        CustomizationSlot = slot,
        Positions = [x, y, z, x, y, z, x, y, z],
        TriangleIndices = [0, 1, 2]
    };
}
