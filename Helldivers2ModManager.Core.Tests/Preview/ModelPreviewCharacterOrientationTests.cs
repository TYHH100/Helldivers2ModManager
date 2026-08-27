using Helldivers2ModManager.Core.Preview;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Preview;

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
            CreateMesh(ModelPreviewCustomizationSlot.RightLeg, 0, 0, 0),
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
            CreateMesh(ModelPreviewCustomizationSlot.RightLeg, 0, 0, 0),
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
            CreateMesh(ModelPreviewCustomizationSlot.RightLeg, 0, 0, 0),
        ]);
        var prop = ModelPreviewCharacterOrientation.GetRequiredRotation(
        [
            CreateMesh(ModelPreviewCustomizationSlot.Unknown, 4, 0, 0),
            CreateMesh(ModelPreviewCustomizationSlot.Unknown, 0, 0, 0),
        ]);
        Assert.AreEqual(ModelPreviewPresentationRotation.None, alreadyYUp);
        Assert.AreEqual(ModelPreviewPresentationRotation.None, prop);
    }

    [TestMethod]
    public void GetRequiredRotation_UnlabeledCharacterWithDominantNegativeZ_MapsNegativeZToViewportUp()
    {
        var rotation = ModelPreviewCharacterOrientation.GetRequiredRotation(
        [
            CreateMesh(ModelPreviewCustomizationSlot.Unknown, 0, 0, -6),
            CreateMesh(ModelPreviewCustomizationSlot.Unknown, 0, 1, 0),
            CreateMesh(ModelPreviewCustomizationSlot.Unknown, 0, 0, 6),
        ]);
        Assert.AreEqual(ModelPreviewPresentationRotation.NegativeZToPositiveY, rotation);
    }

    [TestMethod]
    public void GetSuggestedFrontYaw_UsesRotationSpecificFacingAxis()
    {
        Assert.AreEqual(180d, ModelPreviewCharacterOrientation.GetSuggestedFrontYaw(ModelPreviewPresentationRotation.PositiveXToPositiveY));
        Assert.AreEqual(0d, ModelPreviewCharacterOrientation.GetSuggestedFrontYaw(ModelPreviewPresentationRotation.NegativeXToPositiveY));
        Assert.AreEqual(-90d, ModelPreviewCharacterOrientation.GetSuggestedFrontYaw(ModelPreviewPresentationRotation.PositiveZToPositiveY));
        Assert.AreEqual(90d, ModelPreviewCharacterOrientation.GetSuggestedFrontYaw(ModelPreviewPresentationRotation.NegativeZToPositiveY));
    }

    private static ModelPreviewMesh CreateMesh(ModelPreviewCustomizationSlot slot, float x, float y, float z) => new()
    {
        PatchFile = "synthetic.patch_0",
        UnitId = 1,
        StreamIndex = 0,
        CustomizationSlot = slot,
        Positions = [x, y, z, x, y, z, x, y, z],
        TriangleIndices = [0, 1, 2],
    };
}
