using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewAnimationCompatibilityTests
{
    [TestMethod]
    public void SmallPartialRig_FullyCoveredByAnimation_IsCompatible()
    {
        // 头盔/头部部件 Unit 可能只带少数骨骼；只要动画几乎完整覆盖它们，
        // 部件就应跟随动画，而不是冻结在绑定姿态上。
        var skeletonHashes = new uint[] { 11, 12, 13, 14, 15 };
        var animationHashes = new uint[] { 11, 12, 13, 14, 15, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30 };
        var skeleton = CreateSkeleton(skeletonHashes);
        var library = CreateLibrary(animationHashes);

        Assert.IsTrue(
            ModelPreviewAnimationCompatibility.IsCompatible(skeleton, library),
            "A small head/helmet rig fully covered by the animation must follow it.");
    }

    [TestMethod]
    public void SmallPartialRig_MostlyUncovered_IsRejected()
    {
        var skeleton = CreateSkeleton([11, 12, 13, 14, 15]);
        var library = CreateLibrary([11, 12, 91, 92, 93, 94, 95, 96, 97, 98, 99]);

        Assert.IsFalse(
            ModelPreviewAnimationCompatibility.IsCompatible(skeleton, library),
            "A rig whose bones are mostly absent from the animation must not be animated.");
    }

    [TestMethod]
    public void PartialRig_MixedWithCustomBones_FollowsAnimation()
    {
        // 护甲部件/挂接件骨架是"锚骨 + 自定义骨"混合体：实测 15-21 骨的部件骨架
        // 对游戏动画哈希命中率只有 40-57%。未匹配的骨骼保持绑定姿态且蒙皮矩阵
        // 仍携带已动画祖先的变换，因此足够数量的匹配骨即可让部件跟随。
        var skeletonHashes = Enumerable.Range(1, 20).Select(static index => (uint)index).ToArray();
        var animationHashes = Enumerable.Range(1, 10)
            .Select(static index => (uint)index)
            .Concat(Enumerable.Range(100, 10).Select(static index => (uint)index))
            .ToArray();
        var skeleton = CreateSkeleton(skeletonHashes);
        var library = CreateLibrary(animationHashes);

        Assert.IsTrue(
            ModelPreviewAnimationCompatibility.IsCompatible(skeleton, library),
            "A partial rig with 10 matching anchor bones must be able to follow the animation.");
    }

    [TestMethod]
    public void Rig_WithFewerThanSixMatchingBones_IsRejected()
    {
        // 匹配骨少于 6 个的骨架与人物动画基本无关（武器/道具/生物），保持拒绝。
        var skeletonHashes = Enumerable.Range(1, 20).Select(static index => (uint)index).ToArray();
        var animationHashes = Enumerable.Range(1, 5)
            .Select(static index => (uint)index)
            .Concat(Enumerable.Range(100, 15).Select(static index => (uint)index))
            .ToArray();
        var skeleton = CreateSkeleton(skeletonHashes);
        var library = CreateLibrary(animationHashes);

        Assert.IsFalse(
            ModelPreviewAnimationCompatibility.IsCompatible(skeleton, library),
            "A rig with only 5 matching bones must stay rejected.");
    }

    [TestMethod]
    public void ZeroHashes_AreIgnored()
    {
        var skeleton = CreateSkeleton([0, 11, 12, 13, 14, 15]);
        var library = CreateLibrary([0, 11, 12, 13, 14, 15, 21, 22, 23, 24, 25, 26, 27, 28, 29]);

        Assert.IsFalse(
            ModelPreviewAnimationCompatibility.IsCompatible(
                CreateSkeleton(Array.Empty<uint>()),
                library),
            "A skeleton without usable hashes must be rejected.");
        Assert.IsTrue(ModelPreviewAnimationCompatibility.IsCompatible(skeleton, library));
    }

    private static ModelPreviewSkeleton CreateSkeleton(uint[] nameHashes)
    {
        var bones = new ModelPreviewSkeletonBone[nameHashes.Length];
        for (var index = 0; index < nameHashes.Length; index++)
            bones[index] = new ModelPreviewSkeletonBone(index - 1, nameHashes[index], System.Numerics.Matrix4x4.Identity);
        return new ModelPreviewSkeleton
        {
            BonesId = 0xA,
            StateMachineId = 0xB,
            Bones = bones
        };
    }

    private static ModelPreviewAnimationLibrary CreateLibrary(uint[] boneHashes) =>
        new()
        {
            BonesId = 0xC,
            StateMachineId = 0xD,
            BoneHashes = boneHashes,
            Animations = []
        };
}
