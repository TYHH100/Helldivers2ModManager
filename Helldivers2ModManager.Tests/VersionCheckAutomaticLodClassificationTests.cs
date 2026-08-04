using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class VersionCheckAutomaticLodClassificationTests
{
    private const string PatchFilePath = "synthetic.patch_0";

    [TestMethod]
    public void ClassifyAutomaticLodActions_StrongCustomSameMeshUnit_PreservesUnit()
    {
        var strongCustom = CreateAction(1, string.Empty, strongCustomModelSignal: true);
        var ordinaryA = CreateAction(2, "TORSO");
        var ordinaryB = CreateAction(3, "LEG");

        var classification = VersionCheckService.ClassifyAutomaticLodActions(
            [strongCustom, ordinaryA, ordinaryB]);

        Assert.IsFalse(strongCustom.MeshIdsDiffer);
        Assert.IsTrue(classification.StrongCustomUnitIds.Contains(strongCustom.FileId));
        Assert.IsTrue(classification.PreserveUnitIds.Contains(strongCustom.FileId));
        Assert.AreEqual(1, classification.PreserveUnitIds.Count);
    }

    [TestMethod]
    public void ClassifyAutomaticLodActions_SharedStrongCustomSignature_PreservesRelatedUnit()
    {
        var strongCustom = CreateAction(10, "SHARED-ARM", strongCustomModelSignal: true);
        var related = CreateAction(11, "SHARED-ARM");
        var unrelated = CreateAction(12, "TORSO");

        var classification = VersionCheckService.ClassifyAutomaticLodActions(
            [strongCustom, related, unrelated]);

        Assert.IsTrue(classification.PreserveUnitIds.Contains(strongCustom.FileId));
        Assert.IsTrue(classification.PreserveUnitIds.Contains(related.FileId));
        Assert.IsFalse(classification.PreserveUnitIds.Contains(unrelated.FileId));
        Assert.AreEqual(2, classification.PreserveUnitIds.Count);
    }

    [TestMethod]
    public void ClassifyAutomaticLodActions_SharedMeshIdsDifferSignature_PreservesRelatedUnit()
    {
        var meshReplacement = CreateAction(13, "SHARED-MESH", meshIdsDiffer: true);
        var related = CreateAction(14, "SHARED-MESH");
        var unrelated = CreateAction(15, "TORSO");

        var classification = VersionCheckService.ClassifyAutomaticLodActions(
            [meshReplacement, related, unrelated]);

        Assert.IsFalse(meshReplacement.StrongCustomModelSignal);
        Assert.IsTrue(classification.PreserveUnitIds.Contains(meshReplacement.FileId));
        Assert.IsTrue(classification.PreserveUnitIds.Contains(related.FileId));
        Assert.IsFalse(classification.PreserveUnitIds.Contains(unrelated.FileId));
        Assert.AreEqual(2, classification.PreserveUnitIds.Count);
    }

    [TestMethod]
    public void ClassifyAutomaticLodActions_StrongCustomSlot_PreservesAllBodyShapesAndMaterialLayers()
    {
        var slimModel = CreateAction(
            16,
            "SLIM-ARM",
            strongCustomModelSignal: true,
            bodyShape: ModelPreviewBodyShape.Slim,
            customizationSlot: ModelPreviewCustomizationSlot.LeftArm);
        var stockyModel = CreateAction(
            17,
            "STOCKY-ARM",
            bodyShape: ModelPreviewBodyShape.Stocky,
            customizationSlot: ModelPreviewCustomizationSlot.LeftArm);
        var smallMaterialLayer = CreateAction(
            18,
            "MATERIAL-LAYER",
            currentGpuSize: 15_616,
            referenceGpuSize: 251_136,
            bodyShape: ModelPreviewBodyShape.Any,
            customizationSlot: ModelPreviewCustomizationSlot.LeftArm);
        var unrelatedTorso = CreateAction(
            19,
            "TORSO",
            bodyShape: ModelPreviewBodyShape.Stocky,
            customizationSlot: ModelPreviewCustomizationSlot.Torso);

        var classification = VersionCheckService.ClassifyAutomaticLodActions(
            [slimModel, stockyModel, smallMaterialLayer, unrelatedTorso]);

        Assert.IsTrue(classification.PreserveUnitIds.Contains(slimModel.FileId));
        Assert.IsTrue(classification.PreserveUnitIds.Contains(stockyModel.FileId));
        Assert.IsTrue(classification.PreserveUnitIds.Contains(smallMaterialLayer.FileId));
        Assert.IsFalse(classification.PreserveUnitIds.Contains(unrelatedTorso.FileId));
        Assert.AreEqual(3, classification.PreserveUnitIds.Count);
    }

    [TestMethod]
    public void ClassifyAutomaticLodActions_LargeSameMeshReplacementBelowOldCutoff_IsStrongAndPreserved()
    {
        const uint currentGpuSize = 5_987_632;
        const uint referenceGpuSize = 401_000;
        const uint exactEightTimesReferenceGpuSize = currentGpuSize / 8;
        var isStrong = VersionCheckService.IsStrongAutomaticLodCustomModel(
            meshIdsDiffer: false,
            currentGpuSize,
            referenceGpuSize);
        var isStrongAtEightTimesReference = VersionCheckService.IsStrongAutomaticLodCustomModel(
            meshIdsDiffer: false,
            currentGpuSize,
            exactEightTimesReferenceGpuSize);
        var largeReplacement = CreateAction(
            20,
            "LEFT-ARM",
            currentGpuSize,
            referenceGpuSize,
            strongCustomModelSignal: isStrong);

        var classification = VersionCheckService.ClassifyAutomaticLodActions(
            [largeReplacement, CreateAction(21, "TORSO"), CreateAction(22, "LEG")]);

        Assert.IsTrue(currentGpuSize < 6U * 1024U * 1024U);
        Assert.IsTrue(currentGpuSize / (double)referenceGpuSize >= 8.0);
        Assert.IsTrue(isStrong);
        Assert.IsTrue(isStrongAtEightTimesReference);
        Assert.IsTrue(classification.StrongCustomUnitIds.Contains(largeReplacement.FileId));
        Assert.IsTrue(classification.PreserveUnitIds.Contains(largeReplacement.FileId));
    }

    [TestMethod]
    public void ClassifyAutomaticLodActions_OrdinaryGameLikeUnits_UseGameReference()
    {
        var actions = new[]
        {
            CreateAction(30, "ARM", currentGpuSize: 420_000, referenceGpuSize: 401_000),
            CreateAction(31, "TORSO", currentGpuSize: 390_000, referenceGpuSize: 405_000),
            CreateAction(32, "LEG", currentGpuSize: 410_000, referenceGpuSize: 400_000)
        };

        var classification = VersionCheckService.ClassifyAutomaticLodActions(actions);

        Assert.AreEqual(0, classification.StrongCustomUnitIds.Count);
        Assert.AreEqual(0, classification.PreserveUnitIds.Count);
        Assert.AreEqual(actions.Length, classification.AutomaticUnitIds.Count);
        Assert.IsTrue(actions.All(action =>
            !classification.PreserveUnitIds.Contains(action.FileId)));
    }

    private static AssistedUnitRepairAction CreateAction(
        long fileId,
        string meshSignature,
        uint currentGpuSize = 420_000,
        uint referenceGpuSize = 401_000,
        bool meshIdsDiffer = false,
        bool? strongCustomModelSignal = null,
        ModelPreviewBodyShape bodyShape = ModelPreviewBodyShape.Unknown,
        ModelPreviewCustomizationSlot customizationSlot = ModelPreviewCustomizationSlot.Unknown) => new()
    {
        PatchFilePath = PatchFilePath,
        FileId = fileId,
        CurrentGpuSize = currentGpuSize,
        ReferenceGpuSize = referenceGpuSize,
        MeshIdsDiffer = meshIdsDiffer,
        CurrentMeshSignature = meshSignature,
        StrongCustomModelSignal = strongCustomModelSignal ??
            VersionCheckService.IsStrongAutomaticLodCustomModel(
                meshIdsDiffer,
                currentGpuSize,
                referenceGpuSize),
        BodyShape = bodyShape,
        CustomizationSlot = customizationSlot,
        LodStrategy = AssistedLodStrategy.UseGameReference,
        LodDataDiffers = true
    };
}
