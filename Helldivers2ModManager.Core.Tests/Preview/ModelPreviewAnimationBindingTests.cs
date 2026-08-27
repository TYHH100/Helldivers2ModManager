using Helldivers2ModManager.Core.Preview;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Numerics;

namespace Helldivers2ModManager.Core.Tests.Preview;

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
    public void SampleSkinningTransforms_DifferentBonesIds_RemovesSourceReferenceAxisAndKeepsRelativeDelta()
    {
        const uint rootHash = 0x11111111;
        const uint childHash = 0x22222222;
        const ulong targetBonesId = 0xAAAAAAAAAAAAAAAA;
        const ulong animationBonesId = 0xBBBBBBBBBBBBBBBB;
        var sourceReferenceRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);
        var relativeRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 12);
        var relativePosition = new Vector3(0.25f, -0.5f, 0.75f);
        var skeleton = new ModelPreviewSkeleton
        {
            BonesId = targetBonesId,
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
        var binding = new ModelPreviewAnimationBinding(
            skeleton,
            [rootHash, childHash],
            clip,
            animationBonesId);

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
}


