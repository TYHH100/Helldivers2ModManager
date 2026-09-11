using Helldivers2ModManager.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Numerics;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ModelPreviewAnimationBindingTests
{
    [TestMethod]
    public void SampleSkinningTransforms_RootMotionAndAnimatedChild_KeepsRootAtBindPoseAndAppliesChildTrack()
    {
        const uint rootHash = 0x11111111;
        const uint childHash = 0x22222222;
        var skeleton = new ModelPreviewSkeleton
        {
            BonesId = 1,
            StateMachineId = 2,
            Bones =
            [
                new ModelPreviewSkeletonBone(-1, rootHash, Matrix4x4.Identity),
                new ModelPreviewSkeletonBone(0, childHash, Matrix4x4.CreateTranslation(0, 2, 0))
            ]
        };
        var clip = new ModelPreviewAnimationClip
        {
            AnimationId = 3,
            BoneCount = 2,
            LengthSeconds = 1,
            IsAdditive = false,
            InitialPoses =
            [
                ModelPreviewBonePose.Identity,
                new ModelPreviewBonePose(new Vector3(0, 2, 0), Quaternion.Identity, Vector3.One)
            ],
            Keyframes =
            [
                new ModelPreviewAnimationKeyframe(
                    0,
                    0.5f,
                    ModelPreviewAnimationChannel.Position,
                    new Vector3(25, 10, -8),
                    Quaternion.Identity,
                    Vector3.One),
                new ModelPreviewAnimationKeyframe(
                    0,
                    0.5f,
                    ModelPreviewAnimationChannel.Rotation,
                    Vector3.Zero,
                    Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2),
                    Vector3.One),
                new ModelPreviewAnimationKeyframe(
                    1,
                    0.5f,
                    ModelPreviewAnimationChannel.Position,
                    new Vector3(0, 4, 0),
                    Quaternion.Identity,
                    Vector3.One)
            ],
            Events = []
        };
        var binding = new ModelPreviewAnimationBinding(skeleton, [rootHash, childHash], clip);

        var transforms = binding.SampleSkinningTransforms(0.5f);

        Assert.AreEqual(2, transforms.Length);
        Assert.AreEqual(Matrix4x4.Identity, transforms[0], "Root motion must not rotate or translate preview geometry.");
        Assert.AreEqual(
            Matrix4x4.CreateTranslation(0, 2, 0),
            transforms[1],
            "Consuming root motion must not suppress the animated child track.");
    }

    [TestMethod]
    public void SampleSkinningTransforms_ClipReferencePoseMismatch_TransfersRelativeDeltaWithoutSourceReferenceAxis()
    {
        // 差异重定向现已始终启用：clip 初始姿态是源参考系，任何与骨架 rest 的差异
        // 都不得泄漏进最终蒙皮结果（此前的重定向分支只在 BonesId 不一致时启用）。
        const uint rootHash = 0x11111111;
        const uint childHash = 0x22222222;
        var sourceReferenceRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);
        var relativeRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 12);
        var relativePosition = new Vector3(0.25f, -0.5f, 0.75f);
        var skeleton = new ModelPreviewSkeleton
        {
            BonesId = 0xAAAAAAAAAAAAAAAA,
            StateMachineId = 2,
            Bones =
            [
                new ModelPreviewSkeletonBone(-1, rootHash, Matrix4x4.Identity),
                new ModelPreviewSkeletonBone(0, childHash, Matrix4x4.Identity)
            ]
        };
        var clip = new ModelPreviewAnimationClip
        {
            AnimationId = 3,
            BoneCount = 2,
            LengthSeconds = 1,
            IsAdditive = false,
            InitialPoses =
            [
                ModelPreviewBonePose.Identity,
                new ModelPreviewBonePose(Vector3.Zero, sourceReferenceRotation, Vector3.One)
            ],
            Keyframes =
            [
                new ModelPreviewAnimationKeyframe(
                    1,
                    0.5f,
                    ModelPreviewAnimationChannel.Position,
                    relativePosition,
                    Quaternion.Identity,
                    Vector3.One),
                new ModelPreviewAnimationKeyframe(
                    1,
                    0.5f,
                    ModelPreviewAnimationChannel.Rotation,
                    Vector3.Zero,
                    Quaternion.Multiply(relativeRotation, sourceReferenceRotation),
                    Vector3.One)
            ],
            Events = []
        };
        var binding = new ModelPreviewAnimationBinding(skeleton, [rootHash, childHash], clip);

        var transforms = binding.SampleSkinningTransforms(0.5f);
        var translatedOrigin = Vector3.Transform(Vector3.Zero, transforms[1]);
        var rotatedYAxis = Vector3.TransformNormal(Vector3.UnitY, transforms[1]);
        var expectedYAxis = Vector3.TransformNormal(
            Vector3.UnitY,
            Matrix4x4.CreateFromQuaternion(relativeRotation));

        Assert.AreEqual(relativePosition.X, translatedOrigin.X, 0.00001f);
        Assert.AreEqual(relativePosition.Y, translatedOrigin.Y, 0.00001f);
        Assert.AreEqual(relativePosition.Z, translatedOrigin.Z, 0.00001f);
        Assert.AreEqual(expectedYAxis.X, rotatedYAxis.X, 0.00001f);
        Assert.AreEqual(expectedYAxis.Y, rotatedYAxis.Y, 0.00001f);
        Assert.AreEqual(0f, rotatedYAxis.Z, 0.00001f, "The source skeleton's 90-degree X reference axis must not leak into the target skeleton.");
    }

    [TestMethod]
    public void SampleSkinningTransforms_SameBonesIdWithDifferentBindPose_ModRestMustNotLeakIntoSkinnedDelta()
    {
        // 核心回归：改模骨架常与游戏 Bones 资源共用 BonesId，但其 rest（绑定）姿态
        // 与 clip 源参考姿态不同。此前走绝对路径直接套用 clip 局部 TRS，骨架 rest
        // 与源参考系的偏差会整体泄漏——表现为关节反向弯曲、部件间漂移。差异重定向
        // 必须把蒙皮结果还原为纯动画差量（rest 旋转完全抵消）。
        const uint rootHash = 0x11111111;
        const uint childHash = 0x22222222;
        var modRestRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);
        var animationDeltaRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6);
        var skeleton = new ModelPreviewSkeleton
        {
            // 与游戏 Bones 资源相同的 ID：旧实现会因此走绝对路径
            BonesId = 0x1234567890ABCDEF,
            StateMachineId = 2,
            Bones =
            [
                new ModelPreviewSkeletonBone(-1, rootHash, Matrix4x4.Identity),
                new ModelPreviewSkeletonBone(0, childHash, Matrix4x4.CreateFromQuaternion(modRestRotation))
            ]
        };
        var clip = new ModelPreviewAnimationClip
        {
            AnimationId = 3,
            BoneCount = 2,
            LengthSeconds = 1,
            IsAdditive = false,
            // 源参考姿态：无旋转（游戏骨架 rest）。与目标骨架 rest 不一致。
            InitialPoses =
            [
                ModelPreviewBonePose.Identity,
                ModelPreviewBonePose.Identity
            ],
            Keyframes =
            [
                new ModelPreviewAnimationKeyframe(
                    1,
                    0.5f,
                    ModelPreviewAnimationChannel.Rotation,
                    Vector3.Zero,
                    animationDeltaRotation,
                    Vector3.One)
            ],
            Events = []
        };
        var binding = new ModelPreviewAnimationBinding(skeleton, [rootHash, childHash], clip);

        var transforms = binding.SampleSkinningTransforms(0.5f);

        var expected = Matrix4x4.CreateFromQuaternion(animationDeltaRotation);
        Assert.AreEqual(2, transforms.Length);
        Assert.AreEqual(Matrix4x4.Identity, transforms[0], "The pinned root must stay at its bind pose.");
        AreApproximatelyEqual(
            expected,
            transforms[1],
            0.0001f,
            "The mod rig's rest rotation must cancel out; only the animation delta may remain.");
    }

    [TestMethod]
    public void SampleSkinningTransforms_WithSourceSkeleton_WorldDeltaIgnoresTargetLocalFrame()
    {
        // 核心回归：模组骨架（目标）的骨骼局部轴与游戏骨架（源）不一致时，
        // 蒙皮差量必须取世界差量 G = S⁻¹·A，而不是把源局部旋转套到目标
        // 局部轴上（后者绕错轴——腿部反弯/手臂僵直的根因）。
        const uint rootHash = 0x11111111;
        const uint thighHash = 0x22222222;
        var sourceRestRotation = Quaternion.Identity;
        var animationRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var sourceSkeleton = new ModelPreviewSkeleton
        {
            BonesId = 0xAAAA,
            StateMachineId = 0xBBBB,
            Bones =
            [
                new ModelPreviewSkeletonBone(-1, rootHash, Matrix4x4.Identity),
                new ModelPreviewSkeletonBone(0, thighHash, Matrix4x4.CreateTranslation(0, 0, 1))
            ]
        };
        // 目标骨架：同一关节，但绑定位置与局部轴都不同（重导出骨架的典型情况）。
        var targetRest = Matrix4x4.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2) *
                         Matrix4x4.CreateTranslation(3, 0, 0);
        var targetSkeleton = new ModelPreviewSkeleton
        {
            BonesId = 0,
            StateMachineId = 0,
            Bones =
            [
                new ModelPreviewSkeletonBone(-1, rootHash, Matrix4x4.Identity),
                new ModelPreviewSkeletonBone(0, thighHash, targetRest)
            ]
        };
        var clip = new ModelPreviewAnimationClip
        {
            AnimationId = 3,
            BoneCount = 2,
            LengthSeconds = 1,
            IsAdditive = false,
            InitialPoses =
            [
                ModelPreviewBonePose.Identity,
                new ModelPreviewBonePose(new Vector3(0, 0, 1), sourceRestRotation, Vector3.One)
            ],
            Keyframes =
            [
                new ModelPreviewAnimationKeyframe(
                    1,
                    0.5f,
                    ModelPreviewAnimationChannel.Rotation,
                    Vector3.Zero,
                    animationRotation,
                    Vector3.One)
            ],
            Events = []
        };

        var binding = new ModelPreviewAnimationBinding(
            targetSkeleton, [rootHash, thighHash], clip, sourceSkeleton);
        var transforms = binding.SampleSkinningTransforms(0.5f);

        // 蒙皮差量 = 源骨骼的世界差量 S⁻¹·A，与目标局部轴无关。
        var sourceBind = Matrix4x4.CreateTranslation(0, 0, 1);
        var sourceWorld = Matrix4x4.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) *
                          Matrix4x4.CreateTranslation(0, 0, 1);
        Matrix4x4.Invert(sourceBind, out var inverseSourceBind);
        var expected = inverseSourceBind * sourceWorld;
        AreApproximatelyEqual(expected, transforms[1], 0.0001f,
            "The skinned delta must be the source bone's world delta (S⁻¹·A).");
    }

    private static void AreApproximatelyEqual(
        Matrix4x4 expected,
        Matrix4x4 actual,
        float tolerance,
        string message)
    {
        var expectedValues = new[]
        {
            expected.M11, expected.M12, expected.M13, expected.M14,
            expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34,
            expected.M41, expected.M42, expected.M43, expected.M44
        };
        var actualValues = new[]
        {
            actual.M11, actual.M12, actual.M13, actual.M14,
            actual.M21, actual.M22, actual.M23, actual.M24,
            actual.M31, actual.M32, actual.M33, actual.M34,
            actual.M41, actual.M42, actual.M43, actual.M44
        };
        for (var index = 0; index < expectedValues.Length; index++)
        {
            if (MathF.Abs(expectedValues[index] - actualValues[index]) >= tolerance)
            {
                Assert.Fail(
                    $"{message} Element {index}: expected {expectedValues[index]}, actual {actualValues[index]}. " +
                    $"Expected matrix: {expected}. Actual matrix: {actual}.");
            }
        }
    }
}
