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
            CreateMesh(ModelPreviewCustomizationSlot.Torso, 4, 0, 0, copies: 50),
            CreateMesh(ModelPreviewCustomizationSlot.LeftLeg, 0, 0, 0, copies: 50),
            CreateMesh(ModelPreviewCustomizationSlot.RightLeg, 0, 0, 0, copies: 50)
        ]);

        Assert.AreEqual(ModelPreviewPresentationRotation.PositiveXToPositiveY, rotation);
    }

    [TestMethod]
    public void GetRequiredRotation_TorsoAboveLegsOnNegativeZ_MapsNegativeZToViewportUp()
    {
        var rotation = ModelPreviewCharacterOrientation.GetRequiredRotation(
        [
            CreateMesh(ModelPreviewCustomizationSlot.Torso, 0, 0, -4, copies: 50),
            CreateMesh(ModelPreviewCustomizationSlot.LeftLeg, 0, 0, 0, copies: 50),
            CreateMesh(ModelPreviewCustomizationSlot.RightLeg, 0, 0, 0, copies: 50)
        ]);

        Assert.AreEqual(ModelPreviewPresentationRotation.NegativeZToPositiveY, rotation);
    }

    [TestMethod]
    public void GetRequiredRotation_AlreadyYUpOrMissingBodyParts_DoesNotRotate()
    {
        var alreadyYUp = ModelPreviewCharacterOrientation.GetRequiredRotation(
        [
            CreateMesh(ModelPreviewCustomizationSlot.Torso, 0, 4, 0, copies: 50),
            CreateMesh(ModelPreviewCustomizationSlot.LeftLeg, 0, 0, 0, copies: 50),
            CreateMesh(ModelPreviewCustomizationSlot.RightLeg, 0, 0, 0, copies: 50)
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
    public void GetRequiredRotation_UnlabeledCharacterWithDominantNegativeZ_MapsNegativeZToViewportUp()
    {
        var rotation = ModelPreviewCharacterOrientation.GetRequiredRotation(
        [
            CreateMesh(ModelPreviewCustomizationSlot.Unknown, 0, 0, -6),
            CreateMesh(ModelPreviewCustomizationSlot.Unknown, 0, 1, 0),
            CreateMesh(ModelPreviewCustomizationSlot.Unknown, 0, 0, 6)
        ]);

        Assert.AreEqual(ModelPreviewPresentationRotation.NegativeZToPositiveY, rotation);
    }

    [TestMethod]
    public void GetRequiredRotation_ZUpBodyWithSpreadArms_UsesZAxisNotBounds()
    {
        // 回归（2026-09-11）：护甲选项筛选后的躯干子集里，T-pose 张开的手臂把 X
        // 跨度抬高到与 Z 相当（安德莉亚 x 1.96 vs z 1.97），bounds 比较把身高轴
        // 翻成 X 导致模型横躺。顶点标准差（0.21 vs 0.43）必须压过 bounds。
        var mesh = CreateMesh(ModelPreviewCustomizationSlot.Unknown, 0, 0, 0);
        var positions = new List<float>();
        // 躯干+腿：大量顶点沿 Z 均匀分布（0.0 到 1.9）。
        for (var i = 0; i <= 60; i++)
            positions.AddRange([0f, 0f, i / 60f * 1.9f]);
        // 张开的手臂：少量顶点位于 X 两端。
        positions.AddRange([-0.98f, 1.6f, 1.5f, 0.98f, 1.6f, 1.5f]);
        var spreadArmMesh = new ModelPreviewMesh
        {
            PatchFile = "synthetic.patch_0",
            UnitId = 2,
            StreamIndex = 0,
            CustomizationSlot = ModelPreviewCustomizationSlot.Unknown,
            Positions = positions.ToArray(),
            TriangleIndices = [0, 1, 2]
        };

        var rotation = ModelPreviewCharacterOrientation.GetRequiredRotation(
        [
            mesh,
            spreadArmMesh,
            CreateMesh(ModelPreviewCustomizationSlot.Unknown, 0, 0, 0.5f)
        ]);

        Assert.AreEqual(ModelPreviewPresentationRotation.PositiveZToPositiveY, rotation);
    }

    [TestMethod]
    public void GetSuggestedFrontYaw_UsesRotationSpecificFacingAxis()
    {
        Assert.AreEqual(180d, ModelPreviewCharacterOrientation.GetSuggestedFrontYaw(ModelPreviewPresentationRotation.PositiveXToPositiveY));
        Assert.AreEqual(0d, ModelPreviewCharacterOrientation.GetSuggestedFrontYaw(ModelPreviewPresentationRotation.NegativeXToPositiveY));
        Assert.AreEqual(-90d, ModelPreviewCharacterOrientation.GetSuggestedFrontYaw(ModelPreviewPresentationRotation.PositiveZToPositiveY));
        Assert.AreEqual(90d, ModelPreviewCharacterOrientation.GetSuggestedFrontYaw(ModelPreviewPresentationRotation.NegativeZToPositiveY));
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
    public void GetRequiredRotation_TorsoOnlyXAxis_DoesNotRotate()
    {
        // X/Y 主导的资源（武器、机甲、载具）不做呈现旋转：保持原始朝向比猜一个
        // "立正"更不容易错（实测"充满power的机甲"被 X 翻转误伤）。
        var rotation = ModelPreviewCharacterOrientation.GetRequiredRotation(
        [
            CreateMesh(ModelPreviewCustomizationSlot.Torso, 4, 0, 0, copies: 50),
            CreateMesh(ModelPreviewCustomizationSlot.Unknown, 0, 0, 0, copies: 50),
            CreateMesh(ModelPreviewCustomizationSlot.Unknown, 0, 0, 0, copies: 50),
            CreateMesh(ModelPreviewCustomizationSlot.Unknown, 0, 0, 0, copies: 50)
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

    private static ModelPreviewMesh CreateMesh(ModelPreviewCustomizationSlot slot, float x, float y, float z, int copies = 1)
    {
        var positions = new List<float>(copies * 9);
        var indices = new List<int>(copies * 3);
        for (var copy = 0; copy < copies; copy++)
        {
            positions.AddRange([x, y, z, x, y, z, x, y, z]);
            var baseIndex = copy * 3;
            indices.AddRange([baseIndex, baseIndex + 1, baseIndex + 2]);
        }

        return new ModelPreviewMesh
        {
            PatchFile = "synthetic.patch_0",
            UnitId = 1,
            StreamIndex = 0,
            CustomizationSlot = slot,
            Positions = positions.ToArray(),
            TriangleIndices = indices.ToArray()
        };
    }
}
