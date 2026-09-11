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
    public void LargeRig_BelowBothCoverageRules_IsRejected()
    {
        // 与修复前行为一致：20 根骨骼只命中 10 根，既不满足双向 60%+16 骨规则，
        // 也不满足小骨架全覆盖规则。
        var skeletonHashes = Enumerable.Range(1, 20).Select(static index => (uint)index).ToArray();
        var animationHashes = Enumerable.Range(1, 10)
            .Select(static index => (uint)index)
            .Concat(Enumerable.Range(100, 10).Select(static index => (uint)index))
            .ToArray();
        var skeleton = CreateSkeleton(skeletonHashes);
        var library = CreateLibrary(animationHashes);

        Assert.IsFalse(
            ModelPreviewAnimationCompatibility.IsCompatible(skeleton, library),
            "A large rig with only 50% coverage must stay rejected.");
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
