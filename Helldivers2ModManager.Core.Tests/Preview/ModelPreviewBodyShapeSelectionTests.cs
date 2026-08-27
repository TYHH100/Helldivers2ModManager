using Helldivers2ModManager.Core.Preview;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Preview;

[TestClass]
public sealed class ModelPreviewBodyShapeSelectionTests
{
    [TestMethod]
    public void Filter_ReplacesOnlyTheSelectedShapeWithinSwitchableSlots_AndKeepsAnyLegs()
    {
        var stockyTorso = CreateMesh(ModelPreviewBodyShape.Stocky, ModelPreviewCustomizationSlot.Torso, 1);
        var slimTorso = CreateMesh(ModelPreviewBodyShape.Slim, ModelPreviewCustomizationSlot.Torso, 2);
        var leftLeg = CreateMesh(ModelPreviewBodyShape.Any, ModelPreviewCustomizationSlot.LeftLeg, 3);
        var rightLeg = CreateMesh(ModelPreviewBodyShape.Any, ModelPreviewCustomizationSlot.RightLeg, 4);
        var meshes = new[] { stockyTorso, slimTorso, leftLeg, rightLeg };

        var filtered = ModelPreviewBodyShapeSelection.Filter(
            meshes,
            [stockyTorso, slimTorso],
            showStockyBody: true);

        CollectionAssert.AreEquivalent(
            new[] { stockyTorso, leftLeg, rightLeg },
            filtered.ToArray());
    }

    [TestMethod]
    public void Filter_KeepsBothShapesWhenSelectedShapeHasNoRenderableMesh()
    {
        var stockyTorso = CreateMesh(ModelPreviewBodyShape.Stocky, ModelPreviewCustomizationSlot.Torso, 1);
        var slimTorso = CreateMesh(ModelPreviewBodyShape.Slim, ModelPreviewCustomizationSlot.Torso, 2);

        var filtered = ModelPreviewBodyShapeSelection.Filter(
            [stockyTorso, slimTorso],
            [],
            showStockyBody: true);

        CollectionAssert.AreEquivalent(new[] { stockyTorso, slimTorso }, filtered.ToArray());
    }

    [TestMethod]
    public void GetSwitchableSlots_RequiresBothBodyShapesInTheSameSlot()
    {
        var meshes = new[]
        {
            CreateMesh(ModelPreviewBodyShape.Stocky, ModelPreviewCustomizationSlot.Torso, 1),
            CreateMesh(ModelPreviewBodyShape.Slim, ModelPreviewCustomizationSlot.Torso, 2),
            CreateMesh(ModelPreviewBodyShape.Stocky, ModelPreviewCustomizationSlot.LeftShoulder, 3),
            CreateMesh(ModelPreviewBodyShape.Any, ModelPreviewCustomizationSlot.LeftLeg, 4)
        };

        var slots = ModelPreviewBodyShapeSelection.GetSwitchableSlots(meshes);

        CollectionAssert.AreEquivalent(
            new[] { ModelPreviewCustomizationSlot.Torso },
            slots.ToArray());
    }

    [TestMethod]
    public void GetSwitchableSlots_UsesModOwnedFormsWhenTheyAreOnlyHiddenAsLargeOutliers()
    {
        var stockyTorso = CreateMesh(ModelPreviewBodyShape.Stocky, ModelPreviewCustomizationSlot.Torso, 1);
        var slimTorso = CreateMesh(ModelPreviewBodyShape.Slim, ModelPreviewCustomizationSlot.Torso, 2);
        stockyTorso.RenderStatus = ModelPreviewMeshRenderStatus.HiddenLargeOutlier;
        slimTorso.RenderStatus = ModelPreviewMeshRenderStatus.HiddenLargeOutlier;

        var slots = ModelPreviewBodyShapeSelection.GetSwitchableSlots([stockyTorso, slimTorso]);

        CollectionAssert.AreEquivalent(
            new[] { ModelPreviewCustomizationSlot.Torso },
            slots.ToArray());
    }

    private static ModelPreviewMesh CreateMesh(
        ModelPreviewBodyShape bodyShape,
        ModelPreviewCustomizationSlot slot,
        int streamIndex) => new()
    {
        PatchFile = "sample.patch_0",
        UnitId = (ulong)streamIndex,
        StreamIndex = streamIndex,
        BodyShape = bodyShape,
        CustomizationSlot = slot,
        Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0],
        TriangleIndices = [0, 1, 2]
    };
}


