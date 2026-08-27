using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace Helldivers2ModManager.Core.Preview;

public sealed class ModelPreviewAnimationLibrary
{
    public required ulong BonesId { get; init; }
    public required ulong StateMachineId { get; init; }
    public required IReadOnlyList<uint> BoneHashes { get; init; }
    public required IReadOnlyList<ModelPreviewAnimationOption> Animations { get; init; }
}

public sealed class ModelPreviewAnimationOption
{
    public required ulong AnimationId { get; init; }
    public required ulong StateNameHash { get; init; }
    public required int LayerIndex { get; init; }
    public required ModelPreviewAnimationClip Clip { get; init; }
    public string DisplayName => ModelPreviewAnimationNames.GetDisplayName(AnimationId, StateNameHash);
}

public static class ModelPreviewAnimationNames
{
    private static readonly IReadOnlyDictionary<ulong, string> KnownNames = new Dictionary<ulong, string>
    {
        [25247180846449471] = "Prone Wounded Strafing Right(?)",
        [90655527730108336] = "Prone Pistol Aiming Left Breathing(Injured?)",
        [132594416690666555] = "Crawling to Standing",
        [174977385746011720] = "Crouch walk into Crouch rest",
        [216782799318029858] = "Crouch Rest to Prone",
        [358551434572852177] = "Prone, crawl backward and to the left",
        [358807758360480437] = "Downed state(?), crawl backwards",
        [404260333466794186] = "Standing, turning away and running",
        [432909579045845230] = "Sitting backward to prone crawl transition",
        [456372530071472393] = "Prone, looking right, stab with two hand",
        [474377013447375633] = "Crouch walk into Prone transition",
        [508500488933616914] = "Crouch, walking backwards to the left",
        [519512256433322988] = "Prone, looking backwards, holding two hand weapon",
        [571422367076691216] = "Standing, sprinting to the left"
    };

    public static string GetDisplayName(ulong animationId, ulong stateNameHash) =>
        KnownNames.TryGetValue(animationId, out var name)
            ? $"{name} / 0x{animationId:X16}"
            : $"0x{animationId:X16} / State 0x{stateNameHash:X16}";
}

public readonly record struct ModelPreviewAnimationReference(
    ulong AnimationId,
    ulong StateNameHash,
    int LayerIndex);

public static class ModelPreviewAnimationCompatibility
{
    public const int MinimumMatchingBones = 16;
    public const float MinimumBoneCoverage = 0.60f;

    public static bool IsCompatible(
        ModelPreviewSkeleton skeleton,
        ModelPreviewAnimationLibrary library)
    {
        if (skeleton.BonesId == library.BonesId &&
            (skeleton.StateMachineId == 0 || skeleton.StateMachineId == library.StateMachineId))
        {
            return true;
        }

        var transformHashes = skeleton.Bones
            .Select(static bone => bone.NameHash)
            .Where(static hash => hash != 0)
            .ToHashSet();
        var animationHashes = library.BoneHashes
            .Where(static hash => hash != 0)
            .ToHashSet();
        if (transformHashes.Count == 0 || animationHashes.Count == 0)
            return false;

        var matchingBones = animationHashes.Count(transformHashes.Contains);
        return matchingBones >= MinimumMatchingBones &&
               matchingBones >= animationHashes.Count * MinimumBoneCoverage &&
               matchingBones >= transformHashes.Count * MinimumBoneCoverage;
    }
}

public static class ModelPreviewAnimationLibraryParser
{
    private const int MaxBones = 4096;
    private const int MaxLayers = 64;
    private const int MaxStatesPerLayer = 4096;
    private const int MaxAnimationsPerState = 4096;

    public static IReadOnlyList<uint> ParseBoneHashes(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            throw new InvalidDataException("Bones resource is smaller than its header.");

        var boneCount = ReadCount(data, 0, MaxBones, "bone name");
        var lodCount = ReadCount(data, 4, MaxBones, "bone LOD");
        var hashesOffset = checked(8 + lodCount * sizeof(float));
        EnsureRange(data, hashesOffset, checked(boneCount * sizeof(uint)), "bone hash table");

        var hashes = new uint[boneCount];
        for (var index = 0; index < hashes.Length; index++)
            hashes[index] = BinaryPrimitives.ReadUInt32LittleEndian(data[(hashesOffset + index * sizeof(uint))..]);
        return hashes;
    }

    public static IReadOnlyList<ModelPreviewAnimationReference> ParseStateMachineAnimations(ReadOnlySpan<byte> data)
    {
        const int headerSize = 76;
        if (data.Length < headerSize)
            throw new InvalidDataException("State machine resource is smaller than its header.");

        var declaredLayerCount = ReadCount(data, 4, MaxLayers, "state machine layer");
        var layerDataOffset = ReadOffset(data, 8, data.Length, "state machine layer table");
        EnsureRange(data, layerDataOffset, sizeof(uint), "state machine layer count");
        var layerCount = ReadCount(data, layerDataOffset, MaxLayers, "state machine layer");
        if (declaredLayerCount != 0 && layerCount != declaredLayerCount)
            throw new InvalidDataException("State machine layer counts do not match.");

        var layerOffsetsStart = checked(layerDataOffset + sizeof(uint));
        EnsureRange(data, layerOffsetsStart, checked(layerCount * sizeof(uint)), "state machine layer offsets");
        var references = new List<ModelPreviewAnimationReference>();
        for (var layerIndex = 0; layerIndex < layerCount; layerIndex++)
        {
            var relativeLayerOffset = ReadOffset(data, layerOffsetsStart + layerIndex * sizeof(uint), data.Length, "state machine layer");
            var layerOffset = checked(layerDataOffset + relativeLayerOffset);
            EnsureRange(data, layerOffset, 12, "state machine layer header");

            var stateCount = ReadCount(data, layerOffset + 8, MaxStatesPerLayer, "state");
            var stateOffsetsStart = checked(layerOffset + 12);
            EnsureRange(data, stateOffsetsStart, checked(stateCount * sizeof(uint)), "state offsets");
            for (var stateIndex = 0; stateIndex < stateCount; stateIndex++)
            {
                var relativeStateOffset = ReadOffset(data, stateOffsetsStart + stateIndex * sizeof(uint), data.Length, "state");
                var stateOffset = checked(layerOffset + relativeStateOffset);
                EnsureRange(data, stateOffset, 112, "state header");

                var stateNameHash = BinaryPrimitives.ReadUInt64LittleEndian(data[stateOffset..]);
                var animationCount = ReadCount(data, stateOffset + 12, MaxAnimationsPerState, "state animation");
                var relativeAnimationOffset = ReadOffset(data, stateOffset + 16, data.Length, "state animation table");
                var animationOffset = checked(stateOffset + relativeAnimationOffset);
                EnsureRange(data, animationOffset, checked(animationCount * sizeof(ulong)), "state animation IDs");
                for (var animationIndex = 0; animationIndex < animationCount; animationIndex++)
                {
                    var animationId = BinaryPrimitives.ReadUInt64LittleEndian(
                        data[(animationOffset + animationIndex * sizeof(ulong))..]);
                    references.Add(new ModelPreviewAnimationReference(animationId, stateNameHash, layerIndex));
                }
            }
        }

        return references
            .GroupBy(static reference => reference.AnimationId)
            .Select(static group => group.First())
            .ToArray();
    }

    private static int ReadCount(ReadOnlySpan<byte> data, int offset, int maximum, string description)
    {
        EnsureRange(data, offset, sizeof(uint), description);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        if (value > maximum)
            throw new InvalidDataException($"{description} count {value} is outside the supported range.");
        return (int)value;
    }

    private static int ReadOffset(ReadOnlySpan<byte> data, int offset, int length, string description)
    {
        EnsureRange(data, offset, sizeof(uint), description);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        if (value > int.MaxValue || value > length)
            throw new InvalidDataException($"{description} offset exceeds the resource boundary.");
        return (int)value;
    }

    private static void EnsureRange(ReadOnlySpan<byte> data, int offset, int size, string description)
    {
        if (offset < 0 || size < 0 || offset > data.Length || size > data.Length - offset)
            throw new InvalidDataException($"{description} exceeds the resource boundary.");
    }
}

public sealed class ModelPreviewAnimationBinding
{
    private readonly ModelPreviewSkeleton _skeleton;
    private readonly ModelPreviewAnimationClip _clip;
    private readonly int[] _animationBoneByTransform;
    private readonly Matrix4x4[] _restLocalTransforms;
    private readonly BoneTrack[] _tracks;
    private readonly bool _requiresRetargeting;

    public ModelPreviewAnimationBinding(
        ModelPreviewSkeleton skeleton,
        IReadOnlyList<uint> animationBoneHashes,
        ModelPreviewAnimationClip clip)
        : this(skeleton, animationBoneHashes, clip, skeleton.BonesId)
    {
    }

    public ModelPreviewAnimationBinding(
        ModelPreviewSkeleton skeleton,
        IReadOnlyList<uint> animationBoneHashes,
        ModelPreviewAnimationClip clip,
        ulong animationBonesId)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(animationBoneHashes);
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.BoneCount > animationBoneHashes.Count)
            throw new InvalidDataException("Animation has more bones than its Bones resource.");

        _skeleton = skeleton;
        _clip = clip;
        _requiresRetargeting = skeleton.BonesId != animationBonesId;
        _tracks = Enumerable.Range(0, clip.BoneCount).Select(static _ => new BoneTrack()).ToArray();
        foreach (var keyframe in clip.Keyframes)
        {
            if (keyframe.BoneIndex < 0 || keyframe.BoneIndex >= _tracks.Length)
                continue;
            _tracks[keyframe.BoneIndex].Add(keyframe);
        }

        var animationIndexByHash = animationBoneHashes
            .Take(clip.BoneCount)
            .Select(static (hash, index) => (hash, index))
            .GroupBy(static item => item.hash)
            .ToDictionary(static group => group.Key, static group => group.First().index);
        _animationBoneByTransform = new int[skeleton.Bones.Count];
        Array.Fill(_animationBoneByTransform, -1);
        for (var index = 0; index < skeleton.Bones.Count; index++)
            if (animationIndexByHash.TryGetValue(skeleton.Bones[index].NameHash, out var animationIndex))
                _animationBoneByTransform[index] = animationIndex;

        _restLocalTransforms = BuildRestLocalTransforms(skeleton.Bones);
    }

    public Matrix4x4[] SampleSkinningTransforms(float timeSeconds)
    {
        var time = _clip.LengthSeconds > 0
            ? Math.Clamp(timeSeconds % _clip.LengthSeconds, 0, _clip.LengthSeconds)
            : 0;
        var localTransforms = new Matrix4x4[_skeleton.Bones.Count];
        for (var transformIndex = 0; transformIndex < localTransforms.Length; transformIndex++)
        {
            var parentIndex = _skeleton.Bones[transformIndex].ParentIndex;
            if (parentIndex < 0 || parentIndex >= localTransforms.Length)
            {
                // Root translation/rotation is consumed by the game's character
                // controller. Applying it to preview geometry rotates or displaces the
                // entire model, so keep every hierarchy root in its bind pose.
                localTransforms[transformIndex] = _restLocalTransforms[transformIndex];
                continue;
            }

            var animationBoneIndex = _animationBoneByTransform[transformIndex];
            if (animationBoneIndex < 0)
            {
                localTransforms[transformIndex] = _restLocalTransforms[transformIndex];
                continue;
            }

            var initial = _clip.InitialPoses[animationBoneIndex];
            var track = _tracks[animationBoneIndex];
            var position = track.SamplePosition(initial.Position, time);
            var rotation = track.SampleRotation(initial.Rotation, time);
            var scale = _clip.IsAdditive
                ? Vector3.One
                : track.SampleScale(initial.Scale, time);
            var animatedLocal = CreatePoseTransform(position, rotation, scale);
            if (_clip.IsAdditive)
            {
                localTransforms[transformIndex] = _restLocalTransforms[transformIndex] * animatedLocal;
            }
            else if (_requiresRetargeting)
            {
                var initialLocal = CreatePoseTransform(initial.Position, initial.Rotation, initial.Scale);
                localTransforms[transformIndex] = Matrix4x4.Invert(initialLocal, out var inverseInitial)
                    ? _restLocalTransforms[transformIndex] * inverseInitial * animatedLocal
                    : _restLocalTransforms[transformIndex];
            }
            else
            {
                localTransforms[transformIndex] = animatedLocal;
            }
        }

        var currentGlobal = new Matrix4x4[localTransforms.Length];
        var visitState = new byte[localTransforms.Length];
        for (var index = 0; index < currentGlobal.Length; index++)
            ResolveGlobalTransform(index, localTransforms, currentGlobal, visitState);

        var skinningTransforms = new Matrix4x4[currentGlobal.Length];
        for (var index = 0; index < skinningTransforms.Length; index++)
        {
            skinningTransforms[index] = Matrix4x4.Invert(_skeleton.Bones[index].BindTransform, out var inverseBind)
                ? inverseBind * currentGlobal[index]
                : Matrix4x4.Identity;
        }
        return skinningTransforms;
    }

    private static Matrix4x4 CreatePoseTransform(Vector3 position, Quaternion rotation, Vector3 scale) =>
        Matrix4x4.CreateScale(scale) *
        Matrix4x4.CreateFromQuaternion(rotation) *
        Matrix4x4.CreateTranslation(position);

    private void ResolveGlobalTransform(
        int index,
        IReadOnlyList<Matrix4x4> localTransforms,
        Matrix4x4[] globalTransforms,
        byte[] visitState)
    {
        if (visitState[index] == 2)
            return;
        if (visitState[index] == 1)
        {
            globalTransforms[index] = _skeleton.Bones[index].BindTransform;
            visitState[index] = 2;
            return;
        }

        visitState[index] = 1;
        var parentIndex = _skeleton.Bones[index].ParentIndex;
        if (parentIndex >= 0 && parentIndex < globalTransforms.Length)
        {
            ResolveGlobalTransform(parentIndex, localTransforms, globalTransforms, visitState);
            globalTransforms[index] = localTransforms[index] * globalTransforms[parentIndex];
        }
        else
        {
            globalTransforms[index] = localTransforms[index];
        }
        visitState[index] = 2;
    }

    private static Matrix4x4[] BuildRestLocalTransforms(IReadOnlyList<ModelPreviewSkeletonBone> bones)
    {
        var transforms = new Matrix4x4[bones.Count];
        for (var index = 0; index < bones.Count; index++)
        {
            var parentIndex = bones[index].ParentIndex;
            transforms[index] = parentIndex >= 0 && parentIndex < bones.Count &&
                                Matrix4x4.Invert(bones[parentIndex].BindTransform, out var inverseParent)
                ? bones[index].BindTransform * inverseParent
                : bones[index].BindTransform;
        }
        return transforms;
    }

    private sealed class BoneTrack
    {
        private readonly List<(float Time, Vector3 Value)> _positions = [];
        private readonly List<(float Time, Quaternion Value)> _rotations = [];
        private readonly List<(float Time, Vector3 Value)> _scales = [];

        public void Add(ModelPreviewAnimationKeyframe keyframe)
        {
            switch (keyframe.Channel)
            {
                case ModelPreviewAnimationChannel.Position:
                    _positions.Add((keyframe.TimeSeconds, keyframe.Position));
                    break;
                case ModelPreviewAnimationChannel.Rotation:
                    _rotations.Add((keyframe.TimeSeconds, keyframe.Rotation));
                    break;
                case ModelPreviewAnimationChannel.Scale:
                    _scales.Add((keyframe.TimeSeconds, keyframe.Scale));
                    break;
            }
        }

        public Vector3 SamplePosition(Vector3 initial, float time) => SampleVector(_positions, initial, time);
        public Vector3 SampleScale(Vector3 initial, float time) => SampleVector(_scales, initial, time);

        public Quaternion SampleRotation(Quaternion initial, float time)
        {
            if (_rotations.Count == 0)
                return initial;
            var upper = FindUpperBound(_rotations, time);
            if (upper == 0)
                return Interpolate(initial, _rotations[0].Value, time, _rotations[0].Time, Quaternion.Slerp);
            if (upper >= _rotations.Count)
                return _rotations[^1].Value;
            var lower = _rotations[upper - 1];
            var next = _rotations[upper];
            return Interpolate(lower.Value, next.Value, time - lower.Time, next.Time - lower.Time, Quaternion.Slerp);
        }

        private static Vector3 SampleVector(List<(float Time, Vector3 Value)> values, Vector3 initial, float time)
        {
            if (values.Count == 0)
                return initial;
            var upper = FindUpperBound(values, time);
            if (upper == 0)
                return Interpolate(initial, values[0].Value, time, values[0].Time, Vector3.Lerp);
            if (upper >= values.Count)
                return values[^1].Value;
            var lower = values[upper - 1];
            var next = values[upper];
            return Interpolate(lower.Value, next.Value, time - lower.Time, next.Time - lower.Time, Vector3.Lerp);
        }

        private static int FindUpperBound<T>(List<(float Time, T Value)> values, float time)
        {
            var low = 0;
            var high = values.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (values[middle].Time <= time)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }

        private static T Interpolate<T>(T from, T to, float elapsed, float duration, Func<T, T, float, T> lerp) =>
            duration <= 0 ? to : lerp(from, to, Math.Clamp(elapsed / duration, 0, 1));
    }
}

public static class ModelPreviewCpuSkinner
{
    public static ModelPreviewSkinnedGeometry Skin(
        ModelPreviewMesh mesh,
        IReadOnlyList<Matrix4x4> skinningTransforms,
        bool skinNormals = true)
    {
        if (mesh.Skinning is not { } skinning || !skinning.IsValidForVertexCount(mesh.VertexCount))
            return new ModelPreviewSkinnedGeometry(mesh.Positions, mesh.Normals);

        var result = new float[mesh.Positions.Length];
        var normals = skinNormals &&
                      mesh.Normals is { Length: > 0 } &&
                      mesh.Normals.Length == mesh.Positions.Length
            ? new float[mesh.Normals.Length]
            : null;
        for (var vertexIndex = 0; vertexIndex < mesh.VertexCount; vertexIndex++)
        {
            var positionOffset = vertexIndex * 3;
            var source = new Vector3(
                mesh.Positions[positionOffset],
                mesh.Positions[positionOffset + 1],
                mesh.Positions[positionOffset + 2]);
            var sourceNormal = normals is not null && mesh.Normals is { } sourceNormals
                ? new Vector3(
                    sourceNormals[positionOffset],
                    sourceNormals[positionOffset + 1],
                    sourceNormals[positionOffset + 2])
                : Vector3.Zero;
            var influenceOffset = vertexIndex * ModelPreviewSkinningData.InfluencesPerVertex;
            var transformed = Vector3.Zero;
            var transformedNormal = Vector3.Zero;
            var totalWeight = 0f;
            for (var influence = 0; influence < ModelPreviewSkinningData.InfluencesPerVertex; influence++)
            {
                var transformIndex = skinning.TransformIndices[influenceOffset + influence];
                var weight = skinning.Weights[influenceOffset + influence];
                if (transformIndex < 0 || transformIndex >= skinningTransforms.Count || weight <= 0)
                    continue;
                var transform = skinningTransforms[transformIndex];
                transformed += Vector3.Transform(source, transform) * weight;
                if (normals is not null)
                    transformedNormal += Vector3.TransformNormal(sourceNormal, transform) * weight;
                totalWeight += weight;
            }

            if (totalWeight < 0.999f)
            {
                var remainder = Math.Clamp(1f - totalWeight, 0f, 1f);
                transformed += source * remainder;
                transformedNormal += sourceNormal * remainder;
            }
            result[positionOffset] = transformed.X;
            result[positionOffset + 1] = transformed.Y;
            result[positionOffset + 2] = transformed.Z;
            if (normals is not null)
            {
                transformedNormal = transformedNormal.LengthSquared() > 0.000001f
                    ? Vector3.Normalize(transformedNormal)
                    : sourceNormal;
                normals[positionOffset] = transformedNormal.X;
                normals[positionOffset + 1] = transformedNormal.Y;
                normals[positionOffset + 2] = transformedNormal.Z;
            }
        }
        return new ModelPreviewSkinnedGeometry(result, normals);
    }

    public static float[] SkinPositions(ModelPreviewMesh mesh, IReadOnlyList<Matrix4x4> skinningTransforms)
    {
        return Skin(mesh, skinningTransforms).Positions;
    }
}

public readonly record struct ModelPreviewSkinnedGeometry(
    float[] Positions,
    float[]? Normals);

